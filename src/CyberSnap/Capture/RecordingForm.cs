using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using CyberSnap.Native;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;

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

    private enum State { Selecting, Recording, Encoding }

    private Bitmap? _screenshot;
    private readonly Rectangle _virtualBounds;
    private State _state = State.Selecting;

    // Selection
    private bool _isDragging;
    private Point _dragStart;
    private Point _selectionCursor;
    private Rectangle _selection;

    // Recording
    private GifRecorder? _recorder;
    private VideoRecorder? _videoRecorder;
    private int _recordingStopRequested;
    private readonly int _fps;
    private readonly int _maxDuration;
    private readonly Models.RecordingFormat _format;
    private readonly int _maxHeight;
    private readonly bool _showCursor;
    private readonly bool _recordMic;
    private readonly string? _micDeviceId;
    private readonly bool _recordDesktop;
    private readonly string? _desktopDeviceId;
    private readonly bool _showMagnifier;
    private readonly CaptureMagnifierHelper? _magHelper;
    private LiveSelectionAdornerForm? _selectionAdorner;
    private CaptureEscapeKeyHook? _escapeHook;
    private System.Windows.Forms.Timer? _tickTimer;
    private readonly string _savePath;
    private readonly ToolTip _tooltip = new();

    // Screen-relative selection (stays valid after phase change)
    private Rectangle _recordRegion; // in form coords, persisted

    // Toolbar (recording phase) - positioned relative to form
    private Rectangle _toolbarRect;
    private Rectangle _pauseBtn;
    private Rectangle _stopBtn;
    private Rectangle _discardBtn;
    private int _hoveredBtn = -1; // 0=pause, 1=stop, 2=discard
    private bool _isPaused;
    private DateTime? _pauseStartTime;
    private TimeSpan _totalPausedDuration;

    // TransparencyKey color - any color that won't appear in UI
    private static readonly Color TransKey = Color.FromArgb(1, 2, 3);

    // Cached GDI objects for paint
    private readonly Font _readoutFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
    private readonly Font _hintFont = UiChrome.ChromeFont(UiChrome.ChromeHintSize);
    private readonly SolidBrush _hintBrush = new(UiChrome.SurfaceTextMuted);
    private readonly Pen _borderPen = new(UiChrome.AccentColor, 2.0f) { DashStyle = DashStyle.Dash, DashPattern = new[] { 5f, 3f }, LineJoin = LineJoin.Miter };
    private readonly SolidBrush _cornerBrush = new(UiChrome.AccentColor);
    private readonly SolidBrush _dotBrush = new(Color.FromArgb(240, UiChrome.AccentColor.R, UiChrome.AccentColor.G, UiChrome.AccentColor.B));
    private readonly Pen _ringPen = new(Color.FromArgb(60, UiChrome.AccentColor.R, UiChrome.AccentColor.G, UiChrome.AccentColor.B), 2f);
    private readonly Font _timeFont = UiChrome.ChromeFont(UiChrome.ChromeTitleSize, FontStyle.Bold);
    private readonly SolidBrush _timeBrush = new(UiChrome.SurfaceTextPrimary);
    private readonly Font _encFont = UiChrome.ChromeFont(10f, FontStyle.Bold);
    private readonly SolidBrush _encTextBrush = new(UiChrome.SurfaceTextSecondary);
    private readonly SolidBrush _spinBrush = new(Color.FromArgb(200, UiChrome.AccentColor.R, UiChrome.AccentColor.G, UiChrome.AccentColor.B));

    public RecordingForm(Bitmap? screenshot, Rectangle virtualBounds, int fps, string savePath,
                         Models.RecordingFormat format = Models.RecordingFormat.GIF, int maxHeight = 0,
                         bool showCursor = false,
                         bool recordMic = false, string? micDeviceId = null,
                         bool recordDesktop = false, string? desktopDeviceId = null,
                         bool showMagnifier = false)
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
        Cursor = Cursors.Cross;
        BackColor = UiChrome.SurfaceWindowBackground;
        if (screenshot is null)
        {
            Opacity = 0.01;
            _selectionAdorner = new LiveSelectionAdornerForm(_virtualBounds, "Drag to select recording area");
        }
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

        _tooltip.InitialDelay = 400;
        _tooltip.ReshowDelay = 100;
        _tooltip.AutoPopDelay = 3000;
        _tooltip.UseAnimation = true;
        _tooltip.UseFading = true;
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

    private void CancelFromEscape()
    {
        if (_state == State.Recording)
        {
            DiscardRecording();
            return;
        }

        if (_state == State.Encoding)
            return;

        RecordingCancelled?.Invoke();
        Close();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_state == State.Selecting && e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            _dragStart = e.Location;
            _selectionCursor = e.Location;
            _selection = Rectangle.Empty;
            UpdateLiveSelectionAdorner();
        }
        else if (_state == State.Recording && e.Button == MouseButtons.Left)
        {
            if (_pauseBtn.Contains(e.Location) && _format != Models.RecordingFormat.GIF)
                TogglePause();
            else if (_stopBtn.Contains(e.Location))
                StopRecording();
            else if (_discardBtn.Contains(e.Location))
                DiscardRecording();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_state == State.Selecting)
        {
            if (_isDragging)
            {
                var oldSelection = _selection;
                var oldCursor = _selectionCursor;
                _selection = NormRect(_dragStart, e.Location);
                _selectionCursor = e.Location;
                UpdateLiveSelectionAdorner();
                InvalidateSelectionChrome(oldSelection, oldCursor, _selection, e.Location);
            }
            _magHelper?.Update(e.Location, this, _virtualBounds, _isDragging ? GetMagnifierAvoidBounds() : Rectangle.Empty);
            return;
        }
        if (_state == State.Recording)
        {
            int prev = _hoveredBtn;
            _hoveredBtn = _pauseBtn.Contains(e.Location) && _format != Models.RecordingFormat.GIF ? 0
                        : _stopBtn.Contains(e.Location) ? 1
                        : _discardBtn.Contains(e.Location) ? 2
                        : -1;
            Cursor = _hoveredBtn >= 0 ? Cursors.Hand : Cursors.Default;
            if (_hoveredBtn != prev) Invalidate(_toolbarRect);

            string tip = _hoveredBtn switch
            {
                0 => _isPaused ? "Resume recording" : "Pause recording",
                1 => "Stop and save recording",
                2 => "Discard recording",
                _ => ""
            };
            if (_tooltip.GetToolTip(this) != tip)
                _tooltip.SetToolTip(this, tip);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_state == State.Selecting && _isDragging && e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            _selection = NormRect(_dragStart, e.Location);
            _selectionCursor = e.Location;
            UpdateLiveSelectionAdorner();
            if (_selection.Width > 10 && _selection.Height > 10)
                StartRecording();
            else
            {
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
            SelectionFrameRenderer.DrawRectangle(g, _selection);
            SelectionSizeReadout.Draw(
                g,
                PointToClient(Cursor.Position),
                _selection,
                _hintFont,
                ClientRectangle,
                GetRecordingReadoutDetails());
        }
        else
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            string hint = "Drag to select recording area";
            var hintSz = g.MeasureString(hint, _hintFont);
            g.DrawString(hint, _hintFont, _hintBrush,
                Width / 2f - hintSz.Width / 2f, Height / 2f - hintSz.Height / 2f);
        }

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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var borderRect = Rectangle.Inflate(_recordRegion, 2, 2);

        // Neon glow behind the recording border
        var glowRect = borderRect;
        glowRect.Inflate(3, 3);
        using (var glowPen = new Pen(Color.FromArgb(50, UiChrome.AccentColor), 7f))
            g.DrawRectangle(glowPen, glowRect);

        // Accent dashed border
        g.DrawRectangle(_borderPen, borderRect);

        // HUD corner brackets
        const int cornerLen = 12;
        const int cornerOffset = 2;
        using (var cornerPen = new Pen(UiChrome.AccentColor, 2f) { LineJoin = LineJoin.Miter })
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

        var tbRectF = new RectangleF(_toolbarRect.X, _toolbarRect.Y, _toolbarRect.Width, _toolbarRect.Height);

        // ── Cyber-mica toolbar background ──
        float radius = UiChrome.ScaleFloat(5f);
        var bgPath = WindowsDockRenderer.RoundedRect(tbRectF, radius);
        var toolbarGlowRect = tbRectF;
        toolbarGlowRect.Inflate(3f, 3f);
        var glowPath = WindowsDockRenderer.RoundedRect(toolbarGlowRect, radius);

        // Subtle ambient glow behind the toolbar
        using (var glowBrush = new SolidBrush(Color.FromArgb(25, UiChrome.AccentColor.R, UiChrome.AccentColor.G, UiChrome.AccentColor.B)))
            g.FillPath(glowBrush, glowPath);

        // Mica-black fill
        using (var micaBrush = new SolidBrush(Color.FromArgb(225, 12, 12, 16)))
            g.FillPath(micaBrush, bgPath);

        // Accent border stroke
        using (var borderPen = new Pen(Color.FromArgb(150, UiChrome.AccentColor), 1f))
            g.DrawPath(borderPen, bgPath);

        // Shadow
        WindowsDockRenderer.PaintShadow(g, tbRectF, radius);

        var rawElapsed = _recorder?.Elapsed ?? _videoRecorder?.Elapsed ?? TimeSpan.Zero;
        var pausedNow = _isPaused && _pauseStartTime.HasValue ? DateTime.UtcNow - _pauseStartTime.Value : TimeSpan.Zero;
        var elapsed = rawElapsed - _totalPausedDuration - pausedNow;

        // ── REC indicator (dot + label) ──
        float dotX = _toolbarRect.X + 22;
        float centerY = _toolbarRect.Y + _toolbarRect.Height / 2f;
        float dotY = centerY - 5;
        double pulse = _isPaused ? 0 : Math.Sin(elapsed.TotalMilliseconds / 250.0);
        float pulseAlpha = (float)((pulse + 1.0) / 2.0);

        int glowAlpha = _isPaused ? 20 : (int)(30 + 40 * pulseAlpha);
        int dotAlpha = _isPaused ? 120 : (int)(200 + 55 * pulseAlpha);
        Color recColor = _isPaused ? UiChrome.SurfaceTextMuted : UiChrome.AccentColor;

        // Soft glow behind dot
        using (var glowBrush = new SolidBrush(Color.FromArgb(glowAlpha, recColor.R, recColor.G, recColor.B)))
            g.FillEllipse(glowBrush, dotX - 4, dotY - 4, 18, 18);
        using (var activeDotBrush = new SolidBrush(Color.FromArgb(dotAlpha, recColor.R, recColor.G, recColor.B)))
            g.FillEllipse(activeDotBrush, dotX, dotY, 10, 10);

        // REC / PAUSED label — vertically centered with the dot
        using (var recFont = UiChrome.ChromeFont(8f, FontStyle.Bold))
        using (var recBrush = new SolidBrush(_isPaused
            ? Color.FromArgb(180, UiChrome.SurfaceTextMuted)
            : Color.FromArgb(220, UiChrome.AccentColor)))
        {
            var recLabel = _isPaused ? "PAUSED" : "REC";
            var recRect = new RectangleF(dotX + 16, centerY - 8, 120, 16);
            using var recFormat = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Near,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(recLabel, recFont, recBrush, recRect, recFormat);
        }

        // Timer — starts after REC label so it never overlaps
        string time = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        float timeX = dotX + 120; // safely after REC/PAUSED label (PAUSED is the longest)
        var timeRect = new RectangleF(timeX, _toolbarRect.Y, _stopBtn.X - (timeX + 6), _toolbarRect.Height);
        using (var timeFormat = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        })
            g.DrawString(time, _timeFont, _timeBrush, timeRect, timeFormat);

        // Pause button (video only)
        if (_format != Models.RecordingFormat.GIF)
        {
            Color pauseColor = _hoveredBtn == 0 ? UiChrome.AccentColor : UiChrome.SurfaceTextPrimary;
            DrawIconBtn(g, _pauseBtn, _isPaused ? "play" : "pause", _hoveredBtn == 0, pauseColor, active: false);
        }

        // Stop button - red accent to indicate emergency/stop action
        Color stopColor = _hoveredBtn == 1 ? Color.FromArgb(255, 255, 80, 80) : Color.FromArgb(220, 255, 80, 80);
        DrawIconBtn(g, _stopBtn, "stop", _hoveredBtn == 1, stopColor, active: false);

        // Discard/Cancel button
        Color discardColor = _hoveredBtn == 2 ? UiChrome.AccentColor : UiChrome.SurfaceTextPrimary;
        DrawIconBtn(g, _discardBtn, "close", _hoveredBtn == 2, discardColor, active: false);

        if (_state == State.Encoding)
        {
            WindowsDockRenderer.PaintSurface(g, _toolbarRect);

            float spinX = _toolbarRect.X + 14;
            float spinY = _toolbarRect.Y + _toolbarRect.Height / 2f - 4;
            g.FillEllipse(_spinBrush, spinX, spinY, 8, 8);

            string encLabel = _format == Models.RecordingFormat.GIF ? "Encoding GIF..." : "Saving...";
            var encRect = new RectangleF(spinX + 16, _toolbarRect.Y, _toolbarRect.Width - 30, _toolbarRect.Height);
            using var encFormat = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(encLabel, _encFont, _encTextBrush, encRect, encFormat);
        }
    }

    private void DrawIconBtn(Graphics g, Rectangle rect, string iconId, bool hovered,
        Color iconColor, bool active)
    {
        WindowsDockRenderer.PaintButton(g, rect, active, hovered);
        int alpha = active ? 255 : hovered ? 240 : 200;
        WindowsDockRenderer.PaintIcon(g, iconId, rect, Color.FromArgb(alpha, iconColor.R, iconColor.G, iconColor.B), active);
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
        Models.RecordingFormat.WebM => "WebM",
        Models.RecordingFormat.MKV => "MKV",
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Current = null;
            CaptureWindowExclusion.Unregister(Handle);
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
            _hintFont.Dispose(); _hintBrush.Dispose();
            _borderPen.Dispose(); _cornerBrush.Dispose();
            _dotBrush.Dispose(); _ringPen.Dispose(); _timeFont.Dispose();
            _timeBrush.Dispose(); _encFont.Dispose();
            _encTextBrush.Dispose(); _spinBrush.Dispose();
        }
        base.Dispose(disposing);
    }

}
