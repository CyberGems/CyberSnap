using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Native;
using CyberSnap.Services;

namespace CyberSnap.Capture;

/// <summary>
/// Two-phase scrolling capture:
/// 1. User selects a region on a fullscreen overlay.
/// 2. Overlay hides and a floating control bar appears. User clicks Start,
///    then scrolls the content. Automatic mode captures useful stable scroll deltas;
///    manual mode captures only when the user presses the frame button.
///    User clicks Stop (or presses Escape) when done.
/// 3. Captured frames are stitched into a single tall image via overlap detection.
/// </summary>
public sealed partial class ScrollingCaptureForm : Form
{
    public event Action<Bitmap>? CaptureCompleted;
    public event Action? CaptureCancelled;
    public event Action<string>? CaptureFailed;

    private enum State { Selecting, Capturing, Stitching, Done }

    private Bitmap? _screenshot;
    private readonly Rectangle _virtualBounds;
    private readonly bool _showCursor;
    private readonly ScrollingCaptureMode _captureMode;
    private readonly Rectangle? _preSelectedRegion;
    private State _state = State.Selecting;

    // Selection
    private bool _isDragging;
    private Point _dragStart;
    private Point _selectionCursor;
    private Rectangle _selection;

    // Handle resize/move during ready phase (after drag-release, before START)
    private int _handleDragIndex = -1; // -1=none, 0=TL, 1=TR, 2=BL, 3=BR, 4=move
    private bool _isHandleDragging;
    private Point _handleDragOrigin;
    private Rectangle _handleDragStartRect;
    private static readonly int HandleSize = 10;

    // Capture
    private Rectangle _screenRegion;
    private const int CaptureIntervalMs = 100;
    private const int MatchStripHeight = 48;
    private const int MinimumAutoNewContentPixels = 24;
    private const double DuplicateThreshold = 0.985;
    private int _initialCaptureFailures;
    private string? _initialCaptureFailureMessage;
    private Bitmap? _pendingAutoFrame;
    private Bitmap? _stitchedResult;
    private Bitmap? _previousCapturedFrame;
    private int _frameCount;
    private int _bestMatchCount;
    private int _bestMatchIndex;
    private int _bestIgnoreBottomOffset;
    private int _consecutiveDuplicates;
    private int _consecutiveNonAccepted;
    private const int MaxConsecutiveNonAccepted = 8;
    private enum FrameCaptureResult { Accepted, Pending, Duplicate, Rejected, Failed }

    // Control bar
    private CaptureControlBar? _controlBar;
    private System.Windows.Forms.Timer? _captureTimer;
    private CaptureEscapeKeyHook? _escapeHook;

    // Magnifier
    private readonly bool _showMagnifier;
    private readonly CaptureMagnifierHelper? _magHelper;
    private LiveSelectionAdornerForm? _selectionAdorner;

    // Cached GDI objects for selection overlay
    private readonly Font _readoutFont = UiChrome.ChromeFont(9f, FontStyle.Bold);

    public ScrollingCaptureForm(Bitmap? screenshot, Rectangle virtualBounds, bool showCursor = false,
                                bool showMagnifier = false,
                                ScrollingCaptureMode captureMode = ScrollingCaptureMode.Automatic,
                                Rectangle? preSelectedRegion = null)
    {
        CyberSnap.UI.Theme.Refresh();
        _screenshot = screenshot;
        _virtualBounds = virtualBounds;
        _showCursor = showCursor;
        _captureMode = Enum.IsDefined(captureMode) ? captureMode : ScrollingCaptureMode.Automatic;
        _showMagnifier = showMagnifier;
        _preSelectedRegion = preSelectedRegion;
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
            _selectionAdorner = new LiveSelectionAdornerForm(_virtualBounds, "Drag to select scrolling area");
        }
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
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
        _escapeHook = CaptureEscapeKeyHook.Install(this, HandleEscape);

        if (_preSelectedRegion.HasValue)
        {
            _selection = _preSelectedRegion.Value;
            ShowControlBar();
        }
        else
        {
            _selectionAdorner?.Show(this);
        }
    }

    // â”€â”€â”€ Input â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            HandleEscape();
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
            HandleEscape();
            return;
        }

        base.OnKeyDown(e);
    }

    private void HandleEscape()
    {
        if (_state == State.Capturing && _frameCount > 1)
            StopCapturing();
        else
            Cancel();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            HandleEscape();
            return;
        }

        // Handle resize/move during ready phase (bar visible, before START)
        if (_controlBar is not null && _state == State.Selecting && e.Button == MouseButtons.Left)
        {
            int hit = HitTestHandle(e.Location);
            if (hit >= 0)
            {
                _handleDragIndex = hit;
                _isHandleDragging = true;
                _handleDragOrigin = e.Location;
                _handleDragStartRect = _selection;
                return;
            }
            if (_selection.Contains(e.Location))
            {
                _handleDragIndex = 4; // move
                _isHandleDragging = true;
                _handleDragOrigin = e.Location;
                _handleDragStartRect = _selection;
                return;
            }
            return;
        }

        // Initial selection drag
        if (_state == State.Selecting && e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            _dragStart = e.Location;
            _selectionCursor = e.Location;
            _selection = Rectangle.Empty;
            Invalidate(); // immediately hide the hint banner
            UpdateLiveSelectionAdorner();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Handle resize/move during ready phase
        if (_isHandleDragging && _controlBar is not null)
        {
            ApplyHandleDrag(e.Location);
            return;
        }

        // Cursor feedback during ready phase
        if (_controlBar is not null && _state == State.Selecting)
        {
            int hit = HitTestHandle(e.Location);
            if (hit >= 0)
                Cursor = hit is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW;
            else if (_selection.Contains(e.Location))
                Cursor = Cursors.SizeAll;
            else
                Cursor = Cursors.Default;
            return;
        }

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
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        // Finish handle drag
        if (_isHandleDragging)
        {
            _isHandleDragging = false;
            _handleDragIndex = -1;
            Invalidate();
            UpdateControlBarPosition();
            return;
        }

        if (_state == State.Selecting && _isDragging && e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            _selection = NormRect(_dragStart, e.Location);
            _selectionCursor = e.Location;
            UpdateLiveSelectionAdorner();
            if (_selection.Width > 20 && _selection.Height > 20)
                ShowControlBar();
            else
                Invalidate();
        }
    }

    // â”€â”€â”€ Control bar â€” starts capturing instantly (same as recording) â”€â”€

    private static readonly Color TransKey = Color.FromArgb(1, 2, 3);

    private void ShowControlBar()
    {
        _magHelper?.Close();
        _selectionAdorner?.Close();
        _selectionAdorner?.Dispose();
        _selectionAdorner = null;
        _screenRegion = new Rectangle(
            _selection.X + _virtualBounds.X,
            _selection.Y + _virtualBounds.Y,
            _selection.Width, _selection.Height);

        // Switch to transparent overlay so the selection chrome remains visible
        // during the ready phase (user can still see and readjust the area).
        Opacity = 1;
        BackColor = TransKey;
        TransparencyKey = TransKey;
        Invalidate(); // repaint to show selection border

        _controlBar = new CaptureControlBar(_screenRegion, _captureMode);
        _controlBar.StartClicked += () =>
        {
            // User clicked START — begin capturing now
            _state = State.Capturing;
            ReleaseSelectionPreview(); // free screenshot memory now that capture has started
            Services.SoundService.PlayRecordStartSound();
            _controlBar?.TransitionToCapturing();

            if (_captureMode == ScrollingCaptureMode.AssistAutoscroll)
            {
                int centerX = _screenRegion.Left + _screenRegion.Width / 2;
                int centerY = _screenRegion.Top + _screenRegion.Height / 2;
                User32.SetCursorPos(centerX, centerY);
                IntPtr targetHwnd = User32.WindowFromPoint(new User32.POINT(centerX, centerY));
                if (targetHwnd != IntPtr.Zero)
                    User32.SetForegroundWindow(targetHwnd);
                _consecutiveDuplicates = 0;
            }

            CaptureFrame(forceAccept: true);

            if (_captureMode == ScrollingCaptureMode.AssistAutoscroll)
                User32.mouse_event(User32.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)-120), 0);

            if (_captureMode == ScrollingCaptureMode.Automatic || _captureMode == ScrollingCaptureMode.AssistAutoscroll)
                StartAutomaticTimer();
        };
        _controlBar.StopClicked += () => StopCapturing();
        _controlBar.CancelClicked += () => Cancel();
        _controlBar.ManualFrameClicked += () => CaptureFrame(forceAccept: true);
        _controlBar.Show();
        // Ensure the bar is on top of the dimmed-screenshot background
        Native.User32.SetWindowPos(_controlBar.Handle, Native.User32.HWND_TOPMOST,
            0, 0, 0, 0, Native.User32.SWP_NOMOVE | Native.User32.SWP_NOSIZE | Native.User32.SWP_SHOWWINDOW);
    }

    private void StartAutomaticTimer()
    {
        if (_captureTimer is not null)
            return;

        int interval = _captureMode == ScrollingCaptureMode.AssistAutoscroll ? 150 : CaptureIntervalMs;
        _captureTimer = new System.Windows.Forms.Timer { Interval = interval };
        _captureTimer.Tick += (_, _) => HandleTimerTick();
        _captureTimer.Start();
    }

    private void HandleTimerTick()
    {
        if (_captureMode == ScrollingCaptureMode.AssistAutoscroll)
        {
            var result = CaptureFrame(forceAccept: false);
            if (result == FrameCaptureResult.Accepted)
            {
                _consecutiveDuplicates = 0;
                _consecutiveNonAccepted = 0;
            }
            else if (result == FrameCaptureResult.Duplicate)
            {
                _consecutiveDuplicates++;
                _consecutiveNonAccepted++;
                if (_consecutiveDuplicates >= 3 || _consecutiveNonAccepted >= MaxConsecutiveNonAccepted)
                {
                    StopCapturing();
                    return;
                }
            }
            else
            {
                _consecutiveNonAccepted++;
                if (_consecutiveNonAccepted >= MaxConsecutiveNonAccepted)
                {
                    StopCapturing();
                    return;
                }
            }

            // Send next scroll event
            User32.mouse_event(User32.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)-120), 0);
        }
        else
        {
            var result = CaptureFrame(forceAccept: false);
            if (result == FrameCaptureResult.Accepted)
            {
                _consecutiveNonAccepted = 0;
            }
            else
            {
                _consecutiveNonAccepted++;
                if (_consecutiveNonAccepted >= MaxConsecutiveNonAccepted)
                {
                    StopCapturing();
                    return;
                }
            }
        }
    }

    private void StopAutomaticTimer()
    {
        _captureTimer?.Stop();
        _captureTimer?.Dispose();
        _captureTimer = null;
    }

    private FrameCaptureResult CaptureFrame(bool forceAccept)
    {
        try
        {
            var frame = ScreenCapture.CaptureRegion(_screenRegion, _showCursor);

            if (forceAccept || _captureMode == ScrollingCaptureMode.Manual)
            {
                ClearPendingAutoFrame();
                return TryAcceptFrame(frame, forceAccept);
            }

            return ProcessAutomaticFrame(frame);
        }
        catch (Exception ex)
        {
            // Capture can fail transiently; skip this tick.
            // If we never captured a frame at all, surface a failure instead of a silent cancel.
            if (_frameCount == 0 && _state == State.Capturing)
            {
                _initialCaptureFailures++;
                if (string.IsNullOrWhiteSpace(_initialCaptureFailureMessage))
                    _initialCaptureFailureMessage = string.IsNullOrWhiteSpace(ex.Message)
                        ? "Unable to capture this region."
                        : ex.Message;

                // After a few consecutive failures, stop and report a failure to the user.
                if (_initialCaptureFailures >= 3)
                    Fail(_initialCaptureFailureMessage);
            }

            return FrameCaptureResult.Failed;
        }
    }

    private FrameCaptureResult ProcessAutomaticFrame(Bitmap frame)
    {
        if (_previousCapturedFrame is not null && AreFramesDuplicate(_previousCapturedFrame, frame))
        {
            ClearPendingAutoFrame();
            frame.Dispose();
            return FrameCaptureResult.Duplicate;
        }

        if (ShouldKeepFrame(frame, forceAccept: false))
        {
            ClearPendingAutoFrame();
            return TryAcceptFrame(frame, forceAccept: false);
        }

        _pendingAutoFrame?.Dispose();
        _pendingAutoFrame = frame;
        return FrameCaptureResult.Pending;
    }

    private FrameCaptureResult TryAcceptFrame(Bitmap frame, bool forceAccept)
    {
        if (_stitchedResult is null)
        {
            AcceptFirstFrame(frame);
            return FrameCaptureResult.Accepted;
        }

        if (_previousCapturedFrame is not null && AreFramesDuplicate(_previousCapturedFrame, frame))
        {
            frame.Dispose();
            return FrameCaptureResult.Duplicate;
        }

        var match = TryAppendScrollingFrame(_stitchedResult, frame, _bestMatchCount, _bestMatchIndex, _bestIgnoreBottomOffset);
        if (!match.Success)
        {
            frame.Dispose();
            return FrameCaptureResult.Rejected;
        }

        int minimumNewContent = forceAccept || _captureMode == ScrollingCaptureMode.Manual
            ? 1
            : Math.Max(MinimumAutoNewContentPixels, frame.Height / 20);
        if (match.NewContentHeight < minimumNewContent)
        {
            match.Image?.Dispose();
            frame.Dispose();
            return FrameCaptureResult.Rejected;
        }

        bool usedBestGuess = match.UsedBestGuess;
        if (!usedBestGuess)
        {
            _bestMatchCount = Math.Max(_bestMatchCount, match.MatchCount);
            _bestMatchIndex = match.MatchIndex;
            _bestIgnoreBottomOffset = match.IgnoreBottomOffset;
        }

        _stitchedResult.Dispose();
        _stitchedResult = match.Image;
        ReplacePreviousCapturedFrame(frame);
        _frameCount++;
        if (usedBestGuess)
            _controlBar?.SetStatus($"Auto: {_frameCount} frames (partial)");
        else
            _controlBar?.SetFrameCount(_frameCount);
        return FrameCaptureResult.Accepted;
    }

    private void AcceptFirstFrame(Bitmap frame)
    {
        _stitchedResult = (Bitmap)frame.Clone();
        ReplacePreviousCapturedFrame(frame);
        _frameCount = 1;
        _controlBar?.SetFrameCount(_frameCount);
    }

    private void ReplacePreviousCapturedFrame(Bitmap frame)
    {
        _previousCapturedFrame?.Dispose();
        _previousCapturedFrame = frame;
    }

    private bool ShouldKeepFrame(Bitmap frame, bool forceAccept)
    {
        if (_stitchedResult is null)
            return true;

        if (_previousCapturedFrame is not null && AreFramesDuplicate(_previousCapturedFrame, frame))
            return false;

        var match = TryFindScrollingAppend(_stitchedResult, frame, _bestMatchCount, _bestMatchIndex, _bestIgnoreBottomOffset);
        if (!match.Success)
            return false;

        int minimumNewContent = forceAccept || _captureMode == ScrollingCaptureMode.Manual
            ? 1
            : Math.Max(MinimumAutoNewContentPixels, frame.Height / 20);
        return match.NewContentHeight >= minimumNewContent;
    }

    private void StopCapturing()
    {
        StopAutomaticTimer();
        TryAcceptPendingAutoFrame();
        ClearPendingAutoFrame();
        Services.SoundService.PlayRecordStopSound();

        _state = State.Stitching;
        _controlBar?.SetStatus("Stitching...");

        FinishCapture();
    }

    private void FinishCapture()
    {
        _controlBar?.Close();
        _controlBar?.Dispose();
        _controlBar = null;

        if (_stitchedResult is null)
        {
            Fail(_initialCaptureFailureMessage ?? "No frames captured.");
            return;
        }

        if (_frameCount <= 1)
        {
            var singleFrame = _stitchedResult;
            _stitchedResult = null;
            ReleaseCaptureBitmaps();
            CaptureCompleted?.Invoke(singleFrame);
            _state = State.Done;
            Close();
            return;
        }

        var stitched = _stitchedResult;
        _stitchedResult = null;
        ReleaseCaptureBitmaps();

        if (stitched != null)
        {
            CaptureCompleted?.Invoke(stitched);
        }
        else
        {
            CaptureCancelled?.Invoke();
        }
        _state = State.Done;
        Close();
    }

    private void Fail(string message)
    {
        if (_state == State.Done) return;

        try
        {
            StopAutomaticTimer();
            ClearPendingAutoFrame();
        }
        catch { }

        try
        {
            _controlBar?.SetStatus("Capture failed");
        }
        catch { }

        try { _controlBar?.Close(); } catch { }
        try { _controlBar?.Dispose(); } catch { }
        _controlBar = null;

        ReleaseCaptureBitmaps();

        try { CaptureFailed?.Invoke(message); } catch { }
        try { CaptureCancelled?.Invoke(); } catch { }
        _state = State.Done;
        try { Close(); } catch { }
    }

    private void Cancel()
    {
        StopAutomaticTimer();
        ClearPendingAutoFrame();
        _controlBar?.Close();
        _controlBar?.Dispose();
        _controlBar = null;
        ReleaseCaptureBitmaps();
        CaptureCancelled?.Invoke();
        _state = State.Done;
        Close();
    }

    // â”€â”€â”€ Paint â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_state == State.Selecting && _controlBar is null)
            PaintSelectionPhase(e.Graphics);
        else if (_state == State.Selecting || _state == State.Capturing)
            PaintReadyOrCapturingPhase(e.Graphics);
    }

    /// <summary>
    /// Paints the dimmed screenshot background with selection border and handles.
    /// During ready phase, uses the screenshot so every pixel captures mouse
    /// input (no click-through). During capturing, uses TransKey transparency.
    /// </summary>
    private void PaintReadyOrCapturingPhase(Graphics g)
    {
        if (_state == State.Selecting && _screenshot is not null)
        {
            // Ready phase: draw screenshot at full brightness
            g.DrawImage(_screenshot, ClientRectangle,
                new Rectangle(0, 0, _screenshot.Width, _screenshot.Height),
                GraphicsUnit.Pixel);

            // Dim everything EXCEPT the selection area (same technique as RegionOverlayForm)
            if (_selection.Width > 2 && _selection.Height > 2)
            {
                var state = g.Save();
                g.ExcludeClip(_selection);
                using var dimBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0));
                g.FillRectangle(dimBrush, ClientRectangle);
                g.Restore(state);
            }
        }
        else
        {
            // Capturing phase: transparent overlay, just the border
            g.Clear(TransKey);
        }

        if (_selection.Width > 2 && _selection.Height > 2)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var borderRect = Rectangle.Inflate(_selection, 2, 2);
            SelectionFrameRenderer.DrawRectangle(g, borderRect, fill: false);

            // Draw resize handles when in ready phase (not yet capturing)
            if (_state == State.Selecting && _controlBar is not null)
            {
                var handles = GetHandleRects();
                foreach (var h in handles)
                    WindowsHandleRenderer.Paint(g, h);
            }
        }
    }

    // ── Handle resize/move helpers ──

    private static int HandleSizeScaled => UiChrome.ScaleInt(HandleSize);

    private Rectangle[] GetHandleRects()
    {
        int hs = HandleSizeScaled;
        int h2 = hs / 2;
        var r = _selection;
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

        Rectangle next = _handleDragIndex switch
        {
            0 => Rectangle.FromLTRB(r.Left + dx, r.Top + dy, r.Right, r.Bottom),
            1 => Rectangle.FromLTRB(r.Left, r.Top + dy, r.Right + dx, r.Bottom),
            2 => Rectangle.FromLTRB(r.Left + dx, r.Top, r.Right, r.Bottom + dy),
            3 => Rectangle.FromLTRB(r.Left, r.Top, r.Right + dx, r.Bottom + dy),
            4 => new Rectangle(r.Left + dx, r.Top + dy, r.Width, r.Height), // move
            _ => r
        };

        // Enforce minimum size
        if (next.Width < 20) next.Width = 20;
        if (next.Height < 20) next.Height = 20;

        // Clamp within client bounds
        next.X = Math.Max(0, Math.Min(next.X, ClientSize.Width - next.Width));
        next.Y = Math.Max(0, Math.Min(next.Y, ClientSize.Height - next.Height));

        if (next == _selection) return;

        var old = _selection;
        _selection = next;
        Invalidate(Rectangle.Union(
            InflateForRepaint(old, 16).IsEmpty ? old : Rectangle.Union(InflateForRepaint(old, 16), old),
            InflateForRepaint(next, 16).IsEmpty ? next : Rectangle.Union(InflateForRepaint(next, 16), next)));
    }

    private void UpdateControlBarPosition()
    {
        if (_controlBar is null || _controlBar.IsDisposed) return;
        _screenRegion = new Rectangle(
            _selection.X + _virtualBounds.X,
            _selection.Y + _virtualBounds.Y,
            _selection.Width, _selection.Height);
        _controlBar.Reposition(_screenRegion);
        // Bring the control bar back to front — clicking the main form to drag
        // a handle activates it and puts it above the bar in TopMost z-order.
        Native.User32.SetWindowPos(_controlBar.Handle, Native.User32.HWND_TOPMOST,
            0, 0, 0, 0, Native.User32.SWP_NOMOVE | Native.User32.SWP_NOSIZE | Native.User32.SWP_SHOWWINDOW);
    }

    private void PaintSelectionPhase(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.AssumeLinear;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (_screenshot is null)
            g.Clear(UiChrome.SurfaceWindowBackground);
        else
            g.DrawImage(_screenshot, ClientRectangle,
                new Rectangle(0, 0, _screenshot.Width, _screenshot.Height),
                GraphicsUnit.Pixel);

        if (_selection.Width > 2 && _selection.Height > 2)
        {
            SelectionFrameRenderer.DrawRectangle(g, _selection);
        }
        else if (!_isDragging)
        {
            string hint = "Drag to select scrolling area";
            // Center the hint on each individual monitor instead of the
            // combined virtual desktop so the text doesn't get split across
            // monitor boundaries on multi-monitor setups.
            DrawHintPerMonitor(g, hint);
        }
    }

    /// <summary>
    /// Draws an elegant hint banner at the bottom of each screen. The banner
    /// matches the toolbar widget style: SurfacePill background, SurfaceBorderSubtle
    /// outline, and SurfaceTextPrimary text. Disappears as soon as the user
    /// starts dragging.
    /// </summary>
    private void DrawHintPerMonitor(Graphics g, string hint)
    {
        const int padX = 18;
        const int padY = 10;
        const int bottomMargin = 100;
        const float cornerR = 8f;

        using var hintFont = UiChrome.ChromeFont(14f, FontStyle.Bold);
        var hintSz = g.MeasureString(hint, hintFont);
        float bgW = hintSz.Width + padX * 2;
        float bgH = hintSz.Height + padY * 2;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Match the toolbar widget: SurfaceTier1 + accent border
        var tier1 = UiChrome.SurfaceTier1;
        var bgColor = Color.FromArgb(245, tier1.R, tier1.G, tier1.B);
        using var bgBrush = new SolidBrush(bgColor);

        // Border from widget color picker: #078788 teal/cyan
        using var borderPen = new Pen(Color.FromArgb(255, 0x07, 0x87, 0x88), 1.5f);

        // Text from widget color picker: #A0B4D2
        using var textBrush = new SolidBrush(Color.FromArgb(255, 0xA0, 0xB4, 0xD2));

        foreach (var screen in Screen.AllScreens)
        {
            var screenRect = screen.Bounds;
            int screenCenterX = screenRect.Left + screenRect.Width / 2 - _virtualBounds.X;
            int screenBottomY = screenRect.Bottom - _virtualBounds.Y - bottomMargin;

            float bgX = screenCenterX - bgW / 2f;
            float bgY = screenBottomY - bgH;

            // Rounded background — same fill as the toolbar widget
            using var bgPath = GetRoundedRectPath(bgX, bgY, bgW, bgH, cornerR);
            g.FillPath(bgBrush, bgPath);

            // Subtle border — same as toolbar dividers
            g.DrawPath(borderPen, bgPath);

            // Text centered in the pill
            g.DrawString(hint, hintFont, textBrush,
                bgX + padX, bgY + padY);
        }

        g.SmoothingMode = oldMode;
    }

    private static GraphicsPath GetRoundedRectPath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void UpdateLiveSelectionAdorner()
    {
        if (_selectionAdorner is null)
            return;

        _selectionAdorner.SetSelection(_selection, PointToClient(Cursor.Position));
    }

    private Rectangle GetMagnifierAvoidBounds()
    {
        if (_selection.Width <= 2 || _selection.Height <= 2)
            return Rectangle.Empty;

        var readoutBounds = SelectionSizeReadout.GetBounds(
            PointToClient(Cursor.Position),
            _selection,
            _readoutFont,
            ClientRectangle);
        return readoutBounds.IsEmpty
            ? _selection
            : Rectangle.Union(_selection, InflateForRepaint(readoutBounds, 8));
    }

    private void InvalidateSelectionChrome(Rectangle oldSelection, Point oldCursor, Rectangle newSelection, Point newCursor)
    {
        var oldDirty = GetSelectionChromeBounds(oldSelection, oldCursor);
        var newDirty = GetSelectionChromeBounds(newSelection, newCursor);

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
            ClientRectangle);
        if (!readoutBounds.IsEmpty)
            dirty = Rectangle.Union(dirty, InflateForRepaint(readoutBounds, 10));

        return dirty;
    }

    private static Rectangle NormRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static Rectangle InflateForRepaint(Rectangle rect, int pad = 8)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return Rectangle.Empty;
        rect.Inflate(pad, pad);
        return rect;
    }

    private void ReleaseCaptureBitmaps()
    {
        _stitchedResult?.Dispose();
        _stitchedResult = null;
        _previousCapturedFrame?.Dispose();
        _previousCapturedFrame = null;
        _frameCount = 0;
        _bestMatchCount = 0;
        _bestMatchIndex = 0;
        _bestIgnoreBottomOffset = 0;
    }

    private void ClearPendingAutoFrame()
    {
        _pendingAutoFrame?.Dispose();
        _pendingAutoFrame = null;
    }

    private void TryAcceptPendingAutoFrame()
    {
        if (_pendingAutoFrame is null)
            return;

        var frame = _pendingAutoFrame;
        _pendingAutoFrame = null;
        TryAcceptFrame(frame, forceAccept: false);
    }

    private void ReleaseSelectionPreview()
    {
        _screenshot?.Dispose();
        _screenshot = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _magHelper?.Dispose();
            _escapeHook?.Dispose();
            _escapeHook = null;
            _selectionAdorner?.Dispose();
            _selectionAdorner = null;
            _captureTimer?.Stop();
            _captureTimer?.Dispose();
            _controlBar?.Dispose();
            ClearPendingAutoFrame();
            ReleaseCaptureBitmaps();
            ReleaseSelectionPreview();
            _readoutFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Floating control bar that appears near the selected region
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    /// <summary>
    /// Floating control bar styled identically to the MP4/GIF recording bars
    /// with a teal accent tint. Two-phase: Ready (START) and Capturing (STOP).
    /// Positions centered above the capture region like the recording bars.
    /// </summary>
    private sealed class CaptureControlBar : Form
    {
        public event Action? StartClicked;
        public event Action? StopClicked;
        public event Action? CancelClicked;
        public event Action? ManualFrameClicked;

        private static readonly Color ScrollAccent = Color.FromArgb(255, 0x07, 0x87, 0x88);

        private static int BarWidth => UiChrome.ScaleInt(400);
        private static int BarHeight => WindowsDockRenderer.SurfaceHeight;
        private static float CornerR => UiChrome.ScaleFloat(5f);

        private readonly ScrollingCaptureMode _mode;
        private int _frameCount;
        private string _status;
        private bool _isCapturing;

        private Rectangle _startBtnRect;
        private Rectangle _stopBtnRect;
        private Rectangle _cancelBtnRect;
        private Rectangle _manualFrameBtnRect;
        private Rectangle? _hoveredBtn;
        private Rectangle _statusRect;
        private Rectangle _recDotRect;

        private readonly Font _statusFont = UiChrome.ChromeFont(10f, FontStyle.Bold);
        private readonly Font _dotFont = UiChrome.ChromeFont(8f, FontStyle.Bold);
        private static readonly StringFormat _statusFmt = new()
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        public CaptureControlBar(Rectangle captureRegion, ScrollingCaptureMode mode)
        {
            _mode = mode;
            _isCapturing = false;
            _status = mode == ScrollingCaptureMode.AssistAutoscroll ? "Autoscroll  —  ready"
                : mode == ScrollingCaptureMode.Automatic ? "Auto  —  ready" : "Manual  —  ready";

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(BarWidth, BarHeight);
            BackColor = Color.FromArgb(1, 2, 3);
            TransparencyKey = BackColor;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Default;

            // Position centered above the capture region, same logic as RecordingForm
            var screen = Screen.FromRectangle(captureRegion);
            int tx = captureRegion.X + captureRegion.Width / 2 - BarWidth / 2;
            int ty = captureRegion.Y - BarHeight - UiChrome.ScaleInt(14);
            var edge = UiChrome.ScaleInt(4);
            // If bar doesn't fit above, place it just below the region instead
            if (ty < screen.Bounds.Top + edge)
                ty = captureRegion.Bottom + UiChrome.ScaleInt(14);
            if (tx < screen.Bounds.Left + edge) tx = screen.Bounds.Left + edge;
            if (tx + BarWidth > screen.Bounds.Right - edge) tx = screen.Bounds.Right - edge - BarWidth;
            Location = new Point(tx, ty);

            CalcLayout();
        }

        private void CalcLayout()
        {
            int btnPad = WindowsDockRenderer.SurfacePadding;
            int btnSize = WindowsDockRenderer.IconButtonSize;
            int btnGap = WindowsDockRenderer.ButtonSpacing;
            int btnY = (BarHeight - btnSize) / 2;

            _recDotRect = new Rectangle(UiChrome.ScaleInt(16), btnY + 4, 10, 10);
            _cancelBtnRect = new Rectangle(BarWidth - btnPad - btnSize, btnY, btnSize, btnSize);

            if (!_isCapturing)
            {
                _startBtnRect = new Rectangle(_cancelBtnRect.X - btnGap - btnSize, btnY, btnSize, btnSize);
                _stopBtnRect = Rectangle.Empty;
                _manualFrameBtnRect = Rectangle.Empty;
            }
            else
            {
                _startBtnRect = Rectangle.Empty;
                _stopBtnRect = new Rectangle(_cancelBtnRect.X - btnGap - btnSize, btnY, btnSize, btnSize);
                _manualFrameBtnRect = _mode == ScrollingCaptureMode.Manual
                    ? new Rectangle(_stopBtnRect.X - btnGap - btnSize, btnY, btnSize, btnSize)
                    : Rectangle.Empty;
            }

            int firstBtnX = !_manualFrameBtnRect.IsEmpty ? _manualFrameBtnRect.X
                : !_startBtnRect.IsEmpty ? _startBtnRect.X : _stopBtnRect.X;
            _statusRect = new Rectangle(UiChrome.ScaleInt(16), 0, firstBtnX - UiChrome.ScaleInt(24), BarHeight);
        }

        public void TransitionToCapturing()
        {
            _isCapturing = true;
            CalcLayout();
            Invalidate();
        }

        public void Reposition(Rectangle captureRegion)
        {
            if (InvokeRequired) { BeginInvoke(() => Reposition(captureRegion)); return; }
            var screen = Screen.FromRectangle(captureRegion);
            int tx = captureRegion.X + captureRegion.Width / 2 - BarWidth / 2;
            int ty = captureRegion.Y - BarHeight - UiChrome.ScaleInt(14);
            var edge = UiChrome.ScaleInt(4);
            // If bar doesn't fit above, place it just below the region instead
            if (ty < screen.Bounds.Top + edge)
                ty = captureRegion.Bottom + UiChrome.ScaleInt(14);
            if (tx < screen.Bounds.Left + edge) tx = screen.Bounds.Left + edge;
            if (tx + BarWidth > screen.Bounds.Right - edge) tx = screen.Bounds.Right - edge - BarWidth;
            Location = new Point(tx, ty);
            CalcLayout();
            Invalidate();
        }

        public void SetFrameCount(int count)
        {
            if (InvokeRequired) { BeginInvoke(() => SetFrameCount(count)); return; }
            _frameCount = count;
            _status = FormatFrameStatus(count);
            Invalidate(_statusRect);
        }

        public void SetStatus(string text)
        {
            if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
            _status = text;
            Invalidate(_statusRect);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var barRect = new RectangleF(0, 0, Width, Height);
            var bgPath = WindowsDockRenderer.RoundedRect(barRect, CornerR);

            // Teal ambient glow (matching RecordingForm style)
            var glowRect = barRect;
            glowRect.Inflate(3f, 3f);
            using (var glowPath = WindowsDockRenderer.RoundedRect(glowRect, CornerR))
            using (var glowBrush = new SolidBrush(Color.FromArgb(25, ScrollAccent)))
                g.FillPath(glowBrush, glowPath);

            // Mica-black fill
            using (var micaBrush = new SolidBrush(Color.FromArgb(225, 12, 12, 16)))
                g.FillPath(micaBrush, bgPath);

            // Teal accent border
            using (var bp = new Pen(Color.FromArgb(150, ScrollAccent), 1f))
                g.DrawPath(bp, bgPath);

            // Shadow
            WindowsDockRenderer.PaintShadow(g, barRect, CornerR);

            // ── REC dot ──
            if (!_isCapturing)
            {
                using (var glowDot = new SolidBrush(Color.FromArgb(30, ScrollAccent)))
                    g.FillEllipse(glowDot, _recDotRect.X - 4, _recDotRect.Y - 4, 18, 18);
                using (var dotBrush = new SolidBrush(Color.FromArgb(180, ScrollAccent)))
                    g.FillEllipse(dotBrush, _recDotRect);
            }
            else
            {
                double pulse = Math.Sin(Environment.TickCount / 250.0);
                float pa = (float)((pulse + 1.0) / 2.0);
                using (var glowDot = new SolidBrush(Color.FromArgb((int)(30 + 40 * pa), ScrollAccent)))
                    g.FillEllipse(glowDot, _recDotRect.X - 4, _recDotRect.Y - 4, 18, 18);
                using (var dotBrush = new SolidBrush(Color.FromArgb((int)(200 + 55 * pa), ScrollAccent)))
                    g.FillEllipse(dotBrush, _recDotRect);
            }

            // READY / CAPTURING label
            var label = _isCapturing ? "CAPTURING" : "READY";
            using (var labelBrush = new SolidBrush(Color.FromArgb(220, ScrollAccent)))
            {
                var labelRect = new RectangleF(_recDotRect.Right + 8, 0, 105, BarHeight);
                g.DrawString(label, _dotFont, labelBrush, labelRect,
                    new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near });
            }

            // Status text
            var statusX = _recDotRect.Right + UiChrome.ScaleInt(90);
            var statusRect = new RectangleF(statusX, 0, _statusRect.Width - (statusX - _statusRect.X), BarHeight);
            using (var statusBrush = new SolidBrush(UiChrome.SurfaceTextPrimary))
                g.DrawString(_status, _statusFont, statusBrush, statusRect, _statusFmt);

            // ── Buttons ──
            if (!_startBtnRect.IsEmpty)
                DrawIconBtn(g, _startBtnRect, "play", _hoveredBtn == _startBtnRect, ScrollAccent);
            if (!_stopBtnRect.IsEmpty)
            {
                var sc = _hoveredBtn == _stopBtnRect
                    ? Color.FromArgb(255, 255, 80, 80) : Color.FromArgb(220, 255, 80, 80);
                DrawIconBtn(g, _stopBtnRect, "stop", _hoveredBtn == _stopBtnRect, sc);
            }
            if (!_manualFrameBtnRect.IsEmpty)
                DrawIconBtn(g, _manualFrameBtnRect, "record", _hoveredBtn == _manualFrameBtnRect, UiChrome.SurfaceTextPrimary);
            DrawIconBtn(g, _cancelBtnRect, "close", _hoveredBtn == _cancelBtnRect, UiChrome.SurfaceTextPrimary);
        }

        private void DrawIconBtn(Graphics g, Rectangle r, string iconId, bool hovered, Color iconColor)
        {
            bool filled = iconId == "stop";
            WindowsDockRenderer.PaintButton(g, r, filled, hovered);
            int alpha = filled ? 255 : hovered ? 240 : 200;
            WindowsDockRenderer.PaintIcon(g, iconId, r, Color.FromArgb(alpha, iconColor.R, iconColor.G, iconColor.B), filled);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle? prev = _hoveredBtn;
            _hoveredBtn = !_startBtnRect.IsEmpty && _startBtnRect.Contains(e.Location) ? _startBtnRect
                : !_stopBtnRect.IsEmpty && _stopBtnRect.Contains(e.Location) ? _stopBtnRect
                : !_manualFrameBtnRect.IsEmpty && _manualFrameBtnRect.Contains(e.Location) ? _manualFrameBtnRect
                : _cancelBtnRect.Contains(e.Location) ? _cancelBtnRect : null;
            Cursor = _hoveredBtn != null ? Cursors.Hand : Cursors.Default;
            if (_hoveredBtn != prev)
            {
                if (prev.HasValue) Invalidate(prev.Value);
                if (_hoveredBtn.HasValue) Invalidate(_hoveredBtn.Value);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredBtn != null) { var p = _hoveredBtn.Value; _hoveredBtn = null; Invalidate(p); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!_startBtnRect.IsEmpty && _startBtnRect.Contains(e.Location))
                StartClicked?.Invoke();
            else if (!_stopBtnRect.IsEmpty && _stopBtnRect.Contains(e.Location))
                StopClicked?.Invoke();
            else if (!_manualFrameBtnRect.IsEmpty && _manualFrameBtnRect.Contains(e.Location))
                ManualFrameClicked?.Invoke();
            else if (_cancelBtnRect.Contains(e.Location))
                CancelClicked?.Invoke();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Escape)
            {
                if (!_isCapturing || _frameCount <= 1) CancelClicked?.Invoke();
                else StopClicked?.Invoke();
                return true;
            }
            if (_isCapturing && _mode == ScrollingCaptureMode.Manual && (key == Keys.Space || key == Keys.Enter))
            {
                ManualFrameClicked?.Invoke();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CaptureWindowExclusion.Apply(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _statusFont.Dispose(); _dotFont.Dispose(); }
            base.Dispose(disposing);
        }

        private string FormatFrameStatus(int count)
        {
            string label = count == 1 ? "frame" : "frames";
            return _mode == ScrollingCaptureMode.AssistAutoscroll ? $"Autoscroll: {count} {label}"
                : _mode == ScrollingCaptureMode.Automatic ? $"Auto: {count} {label}"
                : $"Manual: {count} {label}";
        }
    }
}
