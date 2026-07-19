using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using CyberSnap.Native;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.Models;

namespace CyberSnap.Capture;

/// <summary>
/// Two-phase form: first shows fullscreen overlay for region selection,
/// then stays fullscreen but transparent during recording to show the
/// dashed border around the capture region plus a floating toolbar.
/// </summary>
public sealed partial class RecordingForm : Form
{
    private const int RecordingWarmupDelayMs = 260;

    /// <summary>Fires with (filePath, firstFrameBitmap). Caller must dispose the bitmap.</summary>
    public event Action<string, Bitmap?>? RecordingCompleted;
    public event Action<Exception>? RecordingFailed;
    public event Action? RecordingCancelled;

    /// <summary>Static reference to the current recording form for external stop control.</summary>
    public static RecordingForm? Current { get; private set; }

    private enum State { Selecting, PreRecording, Recording, Encoding }

    private Bitmap? _screenshot;
    private readonly Rectangle _virtualBounds;
    private State _state = State.Selecting;

    // Selection
    private bool _isDragging;
    private Point _dragStart;
    private Point _selectionCursor;
    private Rectangle _selection;

    // Handle resize/move during PreRecording (after drag-release, before START)
    private int _handleDragIdx = -1; // -1=none, 0=TL, 1=TR, 2=BL, 3=BR, 4=move
    private bool _isHandleDragging;
    private Point _handleDragOrigin;
    private Rectangle _handleDragStartRect;
    private static readonly int HandleSize = 10;
    private static int HandleSizeScaled => UiChrome.ScaleInt(HandleSize);

    // Recording
    private GifRecorder? _recorder;
    private VideoRecorder? _videoRecorder;
    private int _recordingStopRequested;
    private int _fps;
    private readonly int _maxDuration;
    private readonly Models.RecordingFormat _format;
    private readonly int _maxHeight;
    private readonly bool _showCursor;
    private readonly bool _recordMic;
    private readonly string? _micDeviceId;
    private readonly bool _recordDesktop;
    private readonly string? _desktopDeviceId;
#pragma warning disable CS0414 // Field assigned but never used
    private readonly bool _showMarginBorder = true; // dummy or keep
#pragma warning restore CS0414
    private readonly bool _showMagnifier;
    private readonly bool _openVideoTrimmerAfterCapture;
    private readonly Action<string>? _onGifEncodedForTrimmer;
    private readonly CaptureMagnifierHelper? _magHelper;
    private LiveSelectionAdornerForm? _selectionAdorner;
    private Rectangle _autoDetectRect;
    private bool _autoDetectActive;
    private StandaloneToolBanner? _banner;
    private CaptureEscapeKeyHook? _escapeHook;
    private System.Windows.Forms.Timer? _tickTimer;
    private readonly string _savePath;
    private RecordingControlBar? _controlBar;

    // Screen-relative selection (stays valid after phase change)
    private Rectangle _recordRegion; // in form coords, persisted

    private bool _isPaused;
    private DateTime? _pauseStartTime;
    private TimeSpan _totalPausedDuration;

    // TransparencyKey color - any color that won't appear in UI
    private static readonly Color TransKey = Color.FromArgb(1, 2, 3);

    // Cached GDI objects for paint
    private readonly Font _readoutFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
    private readonly Pen _borderPen = new(UiChrome.AccentColor, 2.0f) { DashStyle = DashStyle.Dash, DashPattern = new[] { 5f, 3f }, LineJoin = LineJoin.Miter };


    public RecordingForm(Bitmap? screenshot, Rectangle virtualBounds, int fps, string savePath,
                         Models.RecordingFormat format = Models.RecordingFormat.GIF, int maxHeight = 0,
                         bool showCursor = false,
                         bool recordMic = false, string? micDeviceId = null,
                         bool recordDesktop = false, string? desktopDeviceId = null,
                         bool showMagnifier = false,
                         bool openVideoTrimmerAfterCapture = false,
                         Action<string>? onGifEncodedForTrimmer = null)
    {
        CyberSnap.UI.Theme.Refresh();
        _screenshot = screenshot;
        _virtualBounds = virtualBounds;
        _fps = fps;
        _maxDuration = 3600; // effectively unlimited - user stops manually
        _savePath = savePath;
        _format = format;
        _maxHeight = maxHeight;
        _showCursor = showCursor;
        _recordMic = recordMic;
        _micDeviceId = micDeviceId;
        _recordDesktop = recordDesktop;
        _desktopDeviceId = desktopDeviceId;
        _showMagnifier = showMagnifier;
        _openVideoTrimmerAfterCapture = openVideoTrimmerAfterCapture;
        _onGifEncodedForTrimmer = onGifEncodedForTrimmer;
        if (_showMagnifier && screenshot is not null)
        {
            _magHelper = new CaptureMagnifierHelper();
            _magHelper.CachePixelData(screenshot);
        }

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(virtualBounds.X, virtualBounds.Y, virtualBounds.Width, virtualBounds.Height);
        Cursor = CursorFactory.PrecisionCursor;
        BackColor = UiChrome.SurfaceWindowBackground;
        if (screenshot is null)
        {
            Opacity = 0.01;
            _selectionAdorner = new LiveSelectionAdornerForm(_virtualBounds, "Drag to select recording area");
        }
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

        // ── Banner (unified style with the capture-overlay tool banners) ──
        // Theme label for "MP4/GIF Recording:" + leading record icon, then theme-accent action.
        bool isMp4 = _format == Models.RecordingFormat.MP4;
        var label = LocalizationService.Translate(isMp4 ? "MP4 Recording" : "GIF Recording") + ": ";
        var action = LocalizationService.Translate("Click & drag to select area")
            + " · " + LocalizationService.Translate("Right-click or Esc to cancel");
        var iconId = isMp4 ? "record" : "recordGif";
        var bannerSegments = new BannerSegment[]
        {
            new(label, StandaloneToolBanner.LabelColor),
            new(action, null), // null = theme accent
        };
        var bannerWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        _banner = new StandaloneToolBanner(
            bannerSegments,
            bannerWorkingArea,
            Bounds,
            onInvalidate: () => Invalidate(),
            iconId: iconId);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CaptureWindowExclusion.Apply(this);
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW);
        User32.SetForegroundWindow(Handle);
        Activate();
        Focus();
        _escapeHook = CaptureEscapeKeyHook.Install(this, CancelFromEscape);
        _selectionAdorner?.Show(this);

        bool isDetectEnabled = false;
        try
        {
            var settings = SettingsService.LoadStatic();
            isDetectEnabled = settings != null && settings.WindowDetection != WindowDetectionMode.Off;
        }
        catch { }

        if (isDetectEnabled)
        {
            WindowDetector.RegisterIgnoredWindow(Handle);
            if (_selectionAdorner != null)
                WindowDetector.RegisterIgnoredWindow(_selectionAdorner.Handle);

            WindowDetector.ClearSnapshot();
            Task.Run(() => WindowDetector.SnapshotWindows(new Rectangle(_virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height)));
        }
    }

    // â”€â”€â”€ Selection phase â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            CancelFromEscape();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CancelFromEscape();
            return;
        }

        base.OnKeyDown(e);
    }

    private void ShowEmptyAreaContextMenu(Point clickLocation)
    {
        if (!Services.SettingsService.LoadStatic()?.ConfirmBeforeExit ?? true)
        {
            CancelFromEscape();
            return;
        }

        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 220);
        menu.Font = UiChrome.ChromeFont(11.0f);

        var isSpanish = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);
        bool isMp4 = _format == Models.RecordingFormat.MP4;

        var cancelLabel = isMp4
            ? (isSpanish ? "Cancelar captura MP4" : "Cancel MP4 capture")
            : (isSpanish ? "Cancelar captura GIF" : "Cancel GIF capture");
        var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "signOut", danger: true, iconSize: 24);
        cancelItem.Click += (_, _) => CancelFromEscape();
        menu.Items.Add(cancelItem);

        menu.Items.Add(new ToolStripSeparator());

        var closeLabel = isSpanish ? "Cerrar menú y continuar" : "Close menu and continue";
        var closeItem = WindowsMenuRenderer.Item(closeLabel, iconId: "close", iconSize: 24);
        closeItem.Click += (_, _) => menu.Close();
        menu.Items.Add(closeItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 220, itemHeight: 46);
        menu.Show(PointToScreen(clickLocation));
    }

    private void CancelFromEscape()
    {
        if (_state == State.Recording)
        {
            DiscardRecording();
            return;
        }

        if (_state == State.Encoding)
            return;

        CloseControlBar();
        RecordingCancelled?.Invoke();
        Close();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            if (_state == State.Recording)
                StopRecording();
            else if (_state == State.PreRecording)
                ShowEmptyAreaContextMenu(e.Location);
            else
                ShowEmptyAreaContextMenu(e.Location);
            return;
        }

        if (_state == State.Selecting && e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            _dragStart = e.Location;
            _selectionCursor = e.Location;
            if (_autoDetectActive && !_autoDetectRect.IsEmpty)
            {
                _selection = _autoDetectRect;
            }
            else
            {
                _selection = Rectangle.Empty;
            }
            _banner?.Dismiss();
            UpdateLiveSelectionAdorner();
        }
        else if (_state == State.PreRecording && e.Button == MouseButtons.Left)
        {
            int hit = HitTestHandle(e.Location);
            if (hit >= 0)
            {
                _handleDragIdx = hit;
                _isHandleDragging = true;
                _handleDragOrigin = e.Location;
                _handleDragStartRect = _recordRegion;
                return;
            }
            if (_recordRegion.Contains(e.Location))
            {
                _handleDragIdx = 4; // move
                _isHandleDragging = true;
                _handleDragOrigin = e.Location;
                _handleDragStartRect = _recordRegion;
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Handle resize/move during PreRecording
        if (_isHandleDragging && _state == State.PreRecording)
        {
            ApplyHandleDrag(e.Location);
            return;
        }

        if (_state == State.Selecting)
        {
            // Don't revive while dragging — Dismiss on mouse-down must stick until the selection ends.
            if (!_isDragging && _banner != null && _banner.ContainsCursor(e.Location))
                _banner.Revive();

            if (_isDragging)
            {
                var oldSelection = _selection;
                var oldCursor = _selectionCursor;
                _selection = NormRect(_dragStart, e.Location);
                _selectionCursor = e.Location;
                UpdateLiveSelectionAdorner();
                Invalidate(); // full repaint for correct dim overlay
            }
            else
            {
                bool isDetectEnabled = false;
                try
                {
                    var settings = SettingsService.LoadStatic();
                    isDetectEnabled = settings != null && settings.WindowDetection != WindowDetectionMode.Off;
                }
                catch { }

                if (isDetectEnabled)
                {
                    var rect = WindowDetector.GetDetectionRectAtPoint(e.Location, _virtualBounds, WindowDetectionMode.WindowOnly);
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        if (rect != _autoDetectRect)
                        {
                            _autoDetectRect = rect;
                            _autoDetectActive = true;
                            Invalidate();
                        }
                    }
                    else
                    {
                        if (_autoDetectActive)
                        {
                            _autoDetectRect = Rectangle.Empty;
                            _autoDetectActive = false;
                            Invalidate();
                        }
                    }
                }
            }
            _magHelper?.Update(e.Location, this, _virtualBounds, _isDragging ? GetMagnifierAvoidBounds() : Rectangle.Empty);
            return;
        }
        if (_state == State.PreRecording)
        {
            int hit = HitTestHandle(e.Location);
            if (hit >= 0)
                Cursor = hit is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW;
            else if (_recordRegion.Contains(e.Location))
                Cursor = Cursors.SizeAll;
            else
                Cursor = Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        // Finish handle drag in PreRecording
        if (_isHandleDragging)
        {
            _isHandleDragging = false;
            _handleDragIdx = -1;
            RebuildRecordingSurface();
            Invalidate();
            return;
        }

        if (_state == State.Selecting && _isDragging && e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            var dragSelection = NormRect(_dragStart, e.Location);
            if (dragSelection.Width > 10 && dragSelection.Height > 10)
            {
                _selection = dragSelection;
                _selectionCursor = e.Location;
                UpdateLiveSelectionAdorner();
                PrepareRecording();
            }
            else if (_autoDetectActive && !_autoDetectRect.IsEmpty)
            {
                _selection = _autoDetectRect;
                _selectionCursor = e.Location;
                UpdateLiveSelectionAdorner();
                PrepareRecording();
            }
            else
            {
                // Click without meaningful selection — revive instruction banner
                _banner?.Revive();
                Invalidate();
            }
        }
    }

    // â”€â”€â”€ Paint â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        if (_state == State.Selecting)
            PaintSelectionPhase(g);
        else
            PaintRecordingPhase(g);
    }

    private void PaintSelectionPhase(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.AssumeLinear;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var screenshot = _screenshot;
        if (screenshot is null)
            g.Clear(UiChrome.SurfaceWindowBackground);
        else
            g.DrawImage(screenshot, ClientRectangle,
                new Rectangle(0, 0, screenshot.Width, screenshot.Height),
                GraphicsUnit.Pixel);

        if (_selection.Width > 2 && _selection.Height > 2)
        {
            // Dim everything outside the selection area (same as RegionOverlayForm)
            var state = g.Save();
            g.ExcludeClip(_selection);
            using var dimBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0));
            g.FillRectangle(dimBrush, ClientRectangle);
            g.Restore(state);

            SelectionFrameRenderer.DrawRectangle(g, _selection);
            SelectionSizeReadout.Draw(
                g,
                _selectionCursor,
                _selection,
                _readoutFont,
                ClientRectangle,
                GetRecordingReadoutDetails());
        }
        else if (_autoDetectActive && !_autoDetectRect.IsEmpty)
        {
            SelectionFrameRenderer.DrawAutoDetectRectangle(g, _autoDetectRect);
        }

        // Always paint last so a short dismiss-fade can finish even mid-drag.
        _banner?.Render(g);
    }

    private void UpdateLiveSelectionAdorner()
    {
        if (_selectionAdorner is null)
            return;

        _selectionAdorner.SetSelection(_selection, PointToClient(Cursor.Position), GetRecordingReadoutDetails());
    }

    private void PaintRecordingPhase(Graphics g)
    {
        g.Clear(TransKey);

        // During PreRecording, show dimmed screenshot outside the recording region
        if (_state == State.PreRecording && _screenshot is not null)
        {
            g.DrawImage(_screenshot, ClientRectangle,
                new Rectangle(0, 0, _screenshot.Width, _screenshot.Height),
                GraphicsUnit.Pixel);
            if (_recordRegion.Width > 2 && _recordRegion.Height > 2)
            {
                var state = g.Save();
                g.ExcludeClip(_recordRegion);
                using var dimBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0));
                g.FillRectangle(dimBrush, ClientRectangle);
                g.Restore(state);
            }
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var accentColor = _format == Models.RecordingFormat.GIF ? Color.FromArgb(255, 140, 0) : UiChrome.AccentColor;
        _borderPen.Color = accentColor;

        var borderRect = Rectangle.Inflate(_recordRegion, 2, 2);

        // Neon glow behind the recording border
        var glowRect = borderRect;
        glowRect.Inflate(3, 3);
        using (var glowPen = new Pen(Color.FromArgb(50, accentColor), 7f))
            g.DrawRectangle(glowPen, glowRect);

        // Accent dashed border
        g.DrawRectangle(_borderPen, borderRect);

        // HUD corner brackets
        const int cornerLen = 12;
        const int cornerOffset = 2;
        using (var cornerPen = new Pen(accentColor, 2f) { LineJoin = LineJoin.Miter })
        {
            g.DrawLine(cornerPen, borderRect.X - cornerOffset, borderRect.Y - cornerOffset, borderRect.X - cornerOffset + cornerLen, borderRect.Y - cornerOffset);
            g.DrawLine(cornerPen, borderRect.X - cornerOffset, borderRect.Y - cornerOffset, borderRect.X - cornerOffset, borderRect.Y - cornerOffset + cornerLen);

            g.DrawLine(cornerPen, borderRect.Right + cornerOffset, borderRect.Y - cornerOffset, borderRect.Right + cornerOffset - cornerLen, borderRect.Y - cornerOffset);
            g.DrawLine(cornerPen, borderRect.Right + cornerOffset, borderRect.Y - cornerOffset, borderRect.Right + cornerOffset, borderRect.Y - cornerOffset + cornerLen);

            g.DrawLine(cornerPen, borderRect.X - cornerOffset, borderRect.Bottom + cornerOffset, borderRect.X - cornerOffset + cornerLen, borderRect.Bottom + cornerOffset);
            g.DrawLine(cornerPen, borderRect.X - cornerOffset, borderRect.Bottom + cornerOffset, borderRect.X - cornerOffset, borderRect.Bottom + cornerOffset - cornerLen);

            g.DrawLine(cornerPen, borderRect.Right + cornerOffset, borderRect.Bottom + cornerOffset, borderRect.Right + cornerOffset - cornerLen, borderRect.Bottom + cornerOffset);
            g.DrawLine(cornerPen, borderRect.Right + cornerOffset, borderRect.Bottom + cornerOffset, borderRect.Right + cornerOffset, borderRect.Bottom + cornerOffset - cornerLen);
        }

        // Draw resize handles during PreRecording (before START is clicked)
        if (_state == State.PreRecording)
        {
            var handles = GetHandleRects();
            foreach (var h in handles)
                WindowsHandleRenderer.Paint(g, h);
        }
    }

    private static Rectangle NormRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static Rectangle InflateForRepaint(Rectangle rect, int pad = 8)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return Rectangle.Empty;

        rect.Inflate(pad, pad);
        return rect;
    }

    private string GetRecordingFormatLabel() => _format switch
    {
        Models.RecordingFormat.MP4 => "MP4",
        _ => "GIF"
    };

    private string[] GetRecordingReadoutDetails()
        => [$"{GetRecordingFormatLabel()}  {_fps} FPS"];

    private Rectangle GetMagnifierAvoidBounds()
    {
        if (_selection.Width <= 2 || _selection.Height <= 2)
            return Rectangle.Empty;

        var readoutBounds = SelectionSizeReadout.GetBounds(
            PointToClient(Cursor.Position),
            _selection,
            _readoutFont,
            ClientRectangle,
            GetRecordingReadoutDetails());
        return readoutBounds.IsEmpty
            ? _selection
            : Rectangle.Union(_selection, InflateForRepaint(readoutBounds, 8));
    }

    private void InvalidateSelectionChrome(Rectangle oldSelection, Point oldCursor, Rectangle newSelection, Point newCursor)
    {
        var oldDirty = GetSelectionChromeBounds(oldSelection, oldCursor);
        var newDirty = GetSelectionChromeBounds(newSelection, newCursor);

        // Include the full selection rects so the dim overlay (which covers
        // everything outside the selection) redraws correctly on shrink.
        if (oldSelection.Width > 2) oldDirty = oldDirty.IsEmpty ? oldSelection : Rectangle.Union(oldDirty, oldSelection);
        if (newSelection.Width > 2) newDirty = newDirty.IsEmpty ? newSelection : Rectangle.Union(newDirty, newSelection);

        if (!oldDirty.IsEmpty && !newDirty.IsEmpty)
            Invalidate(Rectangle.Union(oldDirty, newDirty));
        else if (!oldDirty.IsEmpty)
            Invalidate(oldDirty);
        else if (!newDirty.IsEmpty)
            Invalidate(newDirty);
    }

    private Rectangle GetSelectionChromeBounds(Rectangle selection, Point cursor)
    {
        if (selection.Width <= 2 || selection.Height <= 2)
            return Rectangle.Empty;

        var dirty = InflateForRepaint(selection, 16);
        var readoutBounds = SelectionSizeReadout.GetBounds(
            cursor,
            selection,
            _readoutFont,
            ClientRectangle,
            GetRecordingReadoutDetails());
        if (!readoutBounds.IsEmpty)
            dirty = Rectangle.Union(dirty, InflateForRepaint(readoutBounds, 10));

        return dirty;
    }

    // ── Handle resize/move helpers (PreRecording phase) ──

    private Rectangle[] GetHandleRects()
    {
        int hs = HandleSizeScaled;
        int h2 = hs / 2;
        var r = _recordRegion;
        return new[]
        {
            new Rectangle(r.Left - h2, r.Top - h2, hs, hs),       // 0 TL
            new Rectangle(r.Right - h2, r.Top - h2, hs, hs),      // 1 TR
            new Rectangle(r.Left - h2, r.Bottom - h2, hs, hs),    // 2 BL
            new Rectangle(r.Right - h2, r.Bottom - h2, hs, hs),   // 3 BR
        };
    }

    private int HitTestHandle(Point p)
    {
        var handles = GetHandleRects();
        for (int i = 0; i < handles.Length; i++)
        {
            var hr = handles[i];
            hr.Inflate((WindowsHandleRenderer.HitSize - hr.Width) / 2,
                        (WindowsHandleRenderer.HitSize - hr.Height) / 2);
            if (hr.Contains(p)) return i;
        }
        return -1;
    }

    private void ApplyHandleDrag(Point current)
    {
        int dx = current.X - _handleDragOrigin.X;
        int dy = current.Y - _handleDragOrigin.Y;
        var r = _handleDragStartRect;

        var next = _handleDragIdx switch
        {
            0 => Rectangle.FromLTRB(r.Left + dx, r.Top + dy, r.Right, r.Bottom),
            1 => Rectangle.FromLTRB(r.Left, r.Top + dy, r.Right + dx, r.Bottom),
            2 => Rectangle.FromLTRB(r.Left + dx, r.Top, r.Right, r.Bottom + dy),
            3 => Rectangle.FromLTRB(r.Left, r.Top, r.Right + dx, r.Bottom + dy),
            4 => new Rectangle(r.Left + dx, r.Top + dy, r.Width, r.Height),
            _ => r
        };

        if (next.Width < 20) next.Width = 20;
        if (next.Height < 20) next.Height = 20;
        next.X = Math.Max(0, Math.Min(next.X, ClientSize.Width - next.Width));
        next.Y = Math.Max(0, Math.Min(next.Y, ClientSize.Height - next.Height));

        if (next == _recordRegion) return;
        _recordRegion = next;
        BuildHollowRegion(); // keep region in sync during drag
        Invalidate();
    }

    private void RebuildRecordingSurface()
    {
        BuildHollowRegion();
        CaptureWindowExclusion.SetLogicalBounds(Handle, GetRecordingChromeScreenBounds);
        UpdateControlBarPosition();
        Invalidate();
    }

    private void UpdateControlBarPosition()
    {
        if (_controlBar is null || _controlBar.IsDisposed)
            return;

        var screenRegion = new Rectangle(
            _recordRegion.X + _virtualBounds.X,
            _recordRegion.Y + _virtualBounds.Y,
            _recordRegion.Width,
            _recordRegion.Height);
        _controlBar.Reposition(screenRegion);
        // Bring the control bar back to front — resizing the overlay can steal TopMost z-order.
        User32.SetWindowPos(_controlBar.Handle, User32.HWND_TOPMOST,
            0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW);
    }

    private void CloseControlBar()
    {
        try { _controlBar?.Close(); } catch { /* ignore */ }
        try { _controlBar?.Dispose(); } catch { /* ignore */ }
        _controlBar = null;
    }

    private void BuildHollowRegion()
    {
        // During PreRecording, use a full region so the dimmed screenshot
        // is visible and clicks are captured everywhere (handles + center-drag).
        if (_state == State.PreRecording)
        {
            var fullRgn = Native.Gdi32.CreateRectRgn(0, 0, Bounds.Width, Bounds.Height);
            Native.User32.SetWindowRgn(Handle, fullRgn, true);
            Native.Gdi32.DeleteObject(fullRgn);
            return;
        }

        // During Recording, use a hollow frame so the interior is capturable.
        // The floating control bar is a separate excluded window.
        const int RGN_OR = 2;
        const int frameThickness = 15; // covers border + glow + handles
        var rgn = Native.Gdi32.CreateRectRgn(0, 0, 0, 0);

        if (_recordRegion.Width > 0 && _recordRegion.Height > 0)
        {
            var topRgn = Native.Gdi32.CreateRectRgn(
                _recordRegion.Left - frameThickness, _recordRegion.Top - frameThickness,
                _recordRegion.Right + frameThickness, _recordRegion.Top);
            var bottomRgn = Native.Gdi32.CreateRectRgn(
                _recordRegion.Left - frameThickness, _recordRegion.Bottom,
                _recordRegion.Right + frameThickness, _recordRegion.Bottom + frameThickness);
            var leftRgn = Native.Gdi32.CreateRectRgn(
                _recordRegion.Left - frameThickness, _recordRegion.Top,
                _recordRegion.Left, _recordRegion.Bottom);
            var rightRgn = Native.Gdi32.CreateRectRgn(
                _recordRegion.Right, _recordRegion.Top,
                _recordRegion.Right + frameThickness, _recordRegion.Bottom);

            Native.Gdi32.CombineRgn(rgn, rgn, topRgn, RGN_OR);
            Native.Gdi32.CombineRgn(rgn, rgn, bottomRgn, RGN_OR);
            Native.Gdi32.CombineRgn(rgn, rgn, leftRgn, RGN_OR);
            Native.Gdi32.CombineRgn(rgn, rgn, rightRgn, RGN_OR);

            Native.Gdi32.DeleteObject(topRgn);
            Native.Gdi32.DeleteObject(bottomRgn);
            Native.Gdi32.DeleteObject(leftRgn);
            Native.Gdi32.DeleteObject(rightRgn);
        }

        Native.User32.SetWindowRgn(Handle, rgn, true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Current = null;
            WindowDetector.UnregisterIgnoredWindow(Handle);
            if (_selectionAdorner != null)
                WindowDetector.UnregisterIgnoredWindow(_selectionAdorner.Handle);
            WindowDetector.ClearSnapshot();

            CaptureWindowExclusion.Unregister(Handle);
            CloseControlBar();
            _escapeHook?.Dispose();
            _escapeHook = null;
            _tickTimer?.Dispose();
            _recorder?.Dispose();
            _videoRecorder?.Dispose();
            _magHelper?.Dispose();
            _selectionAdorner?.Dispose();
            _selectionAdorner = null;
            _screenshot?.Dispose();
            _screenshot = null;
            _readoutFont.Dispose();
            _borderPen.Dispose();
        }
        base.Dispose(disposing);
    }

}
