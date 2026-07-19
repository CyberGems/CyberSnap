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
using CyberSnap.UI;

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
    public event Action<ScrollingCaptureMode>? CaptureModeChanged;

    private enum State { Selecting, Capturing, Stitching, Done }

    private Bitmap? _screenshot;
    private readonly Rectangle _virtualBounds;
    private readonly bool _showCursor;
    private ScrollingCaptureMode _captureMode;
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
    private DateTime _lastAcceptedFrameTime;
    private static readonly TimeSpan AutoscrollNoProgressTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ManualScrollNoProgressTimeout = TimeSpan.FromMinutes(2);
    private enum FrameCaptureResult { Accepted, Pending, Duplicate, Rejected, Failed }

    // Control bar
    private CaptureControlBar? _controlBar;
    private System.Windows.Forms.Timer? _captureTimer;
    private CaptureEscapeKeyHook? _escapeHook;

    // Magnifier
    private readonly bool _showMagnifier;
    private readonly CaptureMagnifierHelper? _magHelper;
    private LiveSelectionAdornerForm? _selectionAdorner;
    private StandaloneToolBanner? _hintBanner;

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
        Cursor = CursorFactory.PrecisionCursor;
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
        RegisterCaptureOverlayExclusion();
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
        else if (_screenshot is not null)
        {
            var scrollLabel = LocalizationService.Translate("Scrolling capture") + ": ";
            var scrollAction = LocalizationService.Translate("Drag to select scrolling area")
                + " · " + LocalizationService.Translate("Right-click or Esc to cancel");
            _hintBanner = new StandaloneToolBanner(
                new BannerSegment[]
                {
                    new(scrollLabel, StandaloneToolBanner.LabelColor),
                    new(scrollAction, null), // theme accent
                },
                Screen.FromPoint(Cursor.Position).WorkingArea,
                Bounds,
                onInvalidate: () => Invalidate(),
                persistent: true,
                iconId: "scrollCapture");
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

    private void ShowEmptyAreaContextMenu(Point clickLocation)
    {
        if (!Services.SettingsService.LoadStatic()?.ConfirmBeforeExit ?? true)
        {
            Cancel();
            return;
        }

        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 220);
        menu.Font = UiChrome.ChromeFont(11.0f);

        var isSpanish = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);

        var cancelLabel = isSpanish ? "Cancelar captura por desplazamiento" : "Cancel scroll capture";
        var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "close", danger: true, iconSize: 24);
        cancelItem.Click += (_, _) => Cancel();
        menu.Items.Add(cancelItem);

        menu.Items.Add(new ToolStripSeparator());

        var closeLabel = isSpanish ? "Cerrar menú y continuar" : "Close menu and continue";
        var closeItem = WindowsMenuRenderer.Item(closeLabel, iconId: "close", iconSize: 24);
        closeItem.Click += (_, _) => menu.Close();
        menu.Items.Add(closeItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 220, itemHeight: 46);
        menu.Show(PointToScreen(clickLocation));
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
            if (_state == State.Capturing && _frameCount > 1)
                StopCapturing();
            else if (_state == State.Capturing)
                ShowEmptyAreaContextMenu(e.Location);
            else
                ShowEmptyAreaContextMenu(e.Location);
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
            DismissHintBanner();
            _isDragging = true;
            _dragStart = e.Location;
            _selectionCursor = e.Location;
            _selection = Rectangle.Empty;
            Invalidate(); // start selection dim; banner fades via its own region/timer
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
                Invalidate(); // full repaint for correct dim overlay
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
            {
                // Click without meaningful selection — revive instruction banner
                _hintBanner?.Revive();
                Invalidate();
            }
        }
    }

    // â”€â”€â”€ Control bar â€” starts capturing instantly (same as recording) â”€â”€

    private static readonly Color TransKey = Color.FromArgb(1, 2, 3);

    private static void PersistScrollingCaptureMode(ScrollingCaptureMode mode)
    {
        try
        {
            using var svc = new SettingsService();
            svc.Load();
            svc.Settings.ScrollingCaptureMode = mode;
            svc.Save();
            svc.FlushPendingWrites();
        }
        catch { /* settings may be locked */ }
    }

    /// <summary>
    /// Overlay chrome is excluded from capture via WDA_EXCLUDEFROMCAPTURE; never hide/show per frame.
    /// </summary>
    private static Rectangle ScrollingCaptureOverlayExclusionBounds() => Rectangle.Empty;

    private void RegisterCaptureOverlayExclusion()
    {
        if (!IsHandleCreated)
            return;

        CaptureWindowExclusion.SetLogicalBounds(Handle, ScrollingCaptureOverlayExclusionBounds);
    }

    /// <summary>
    /// Repaint overlay chrome after leaving the frozen screenshot/dim ready phase.
    /// </summary>
    private void InvalidateCaptureOverlay()
    {
        if (!IsHandleCreated)
            return;

        Invalidate(true);
        Update();
    }

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
        _controlBar.ModeChanged += mode =>
        {
            _captureMode = mode;
            PersistScrollingCaptureMode(mode);
            CaptureModeChanged?.Invoke(mode);
        };
        _controlBar.StartClicked += () =>
        {
            // User clicked START — begin capturing now
            _state = State.Capturing;
            ReleaseSelectionPreview(); // free screenshot memory now that capture has started
            Services.SoundService.PlayRecordStartSound();
            _controlBar?.TransitionToCapturing();
            BuildCaptureInputRegion();
            RegisterCaptureOverlayExclusion();
            InvalidateCaptureOverlay();
            FocusScrollTargetWindow();

            if (_captureMode == ScrollingCaptureMode.AssistAutoscroll)
            {
                _consecutiveDuplicates = 0;
            }

            _lastAcceptedFrameTime = DateTime.UtcNow;

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

        int interval = CaptureIntervalMs;
        _captureTimer = new System.Windows.Forms.Timer { Interval = interval };
        _captureTimer.Tick += (_, _) => HandleTimerTick();
        _captureTimer.Start();
    }

    private void HandleTimerTick()
    {
        var timeout = _captureMode == ScrollingCaptureMode.AssistAutoscroll
            ? AutoscrollNoProgressTimeout
            : ManualScrollNoProgressTimeout;
        if (DateTime.UtcNow - _lastAcceptedFrameTime > timeout)
        {
            StopCapturing();
            return;
        }

        if (_captureMode == ScrollingCaptureMode.AssistAutoscroll)
        {
            var result = CaptureFrame(forceAccept: false);
            if (result == FrameCaptureResult.Accepted)
            {
                _consecutiveDuplicates = 0;
                _lastAcceptedFrameTime = DateTime.UtcNow;
            }
            else if (result == FrameCaptureResult.Duplicate)
            {
                _consecutiveDuplicates++;
                if (_consecutiveDuplicates >= 3)
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
                _consecutiveDuplicates = 0;
                _lastAcceptedFrameTime = DateTime.UtcNow;
            }
            // In manual-scroll mode, unchanged frames just mean the user has not scrolled yet.
        }
    }

    private void FocusScrollTargetWindow()
    {
        int centerX = _screenRegion.Left + _screenRegion.Width / 2;
        int centerY = _screenRegion.Top + _screenRegion.Height / 2;
        User32.SetCursorPos(centerX, centerY);
        IntPtr targetHwnd = User32.WindowFromPoint(new User32.POINT(centerX, centerY));
        if (targetHwnd != IntPtr.Zero)
            User32.SetForegroundWindow(targetHwnd);
    }

    /// <summary>
    /// During capture, use a hollow frame so wheel/click input reaches the scrollable content below.
    /// </summary>
    private void BuildCaptureInputRegion()
    {
        if (_state != State.Capturing)
        {
            var fullRgn = Gdi32.CreateRectRgn(0, 0, Bounds.Width, Bounds.Height);
            User32.SetWindowRgn(Handle, fullRgn, true);
            Gdi32.DeleteObject(fullRgn);
            return;
        }

        const int RgnOr = 2;
        const int frameThickness = 15;
        var rgn = Gdi32.CreateRectRgn(0, 0, 0, 0);

        if (_selection.Width > 0 && _selection.Height > 0)
        {
            var topRgn = Gdi32.CreateRectRgn(
                _selection.Left - frameThickness, _selection.Top - frameThickness,
                _selection.Right + frameThickness, _selection.Top);
            var bottomRgn = Gdi32.CreateRectRgn(
                _selection.Left - frameThickness, _selection.Bottom,
                _selection.Right + frameThickness, _selection.Bottom + frameThickness);
            var leftRgn = Gdi32.CreateRectRgn(
                _selection.Left - frameThickness, _selection.Top,
                _selection.Left, _selection.Bottom);
            var rightRgn = Gdi32.CreateRectRgn(
                _selection.Right, _selection.Top,
                _selection.Right + frameThickness, _selection.Bottom);

            Gdi32.CombineRgn(rgn, rgn, topRgn, RgnOr);
            Gdi32.CombineRgn(rgn, rgn, bottomRgn, RgnOr);
            Gdi32.CombineRgn(rgn, rgn, leftRgn, RgnOr);
            Gdi32.CombineRgn(rgn, rgn, rightRgn, RgnOr);

            Gdi32.DeleteObject(topRgn);
            Gdi32.DeleteObject(bottomRgn);
            Gdi32.DeleteObject(leftRgn);
            Gdi32.DeleteObject(rightRgn);
        }

        User32.SetWindowRgn(Handle, rgn, true);
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
            _controlBar?.SetPartialFrameCount(_frameCount);
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
        _controlBar?.SetStatus(LocalizationService.Translate("Scroll capture stitching"));

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
            // Capturing phase: live desktop shows through; only draw the frame chrome.
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(TransKey);
            g.CompositingMode = CompositingMode.SourceOver;
        }

        if (_selection.Width > 2 && _selection.Height > 2)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var borderRect = Rectangle.Inflate(_selection, 2, 2);
            if (_state == State.Capturing)
                PaintCapturingFrame(g, borderRect);
            else
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

    /// <summary>
    /// Frame during capture: no semi-transparent glow that can trap the ready-phase dim snapshot.
    /// </summary>
    private static void PaintCapturingFrame(Graphics g, Rectangle borderRect)
    {
        var accent = UiChrome.AccentColor;
        using var borderPen = new Pen(accent, 1.5f)
        {
            DashStyle = DashStyle.Dash,
            DashPattern = new[] { 6f, 4f }
        };
        g.DrawRectangle(borderPen, borderRect);

        const int cornerLen = 12;
        const int cornerOffset = 3;
        using var cornerPen = new Pen(accent, 2f) { LineJoin = LineJoin.Miter };
        g.DrawLine(cornerPen, borderRect.X - cornerOffset, borderRect.Y - cornerOffset, borderRect.X - cornerOffset + cornerLen, borderRect.Y - cornerOffset);
        g.DrawLine(cornerPen, borderRect.X - cornerOffset, borderRect.Y - cornerOffset, borderRect.X - cornerOffset, borderRect.Y - cornerOffset + cornerLen);
        g.DrawLine(cornerPen, borderRect.Right + cornerOffset, borderRect.Y - cornerOffset, borderRect.Right + cornerOffset - cornerLen, borderRect.Y - cornerOffset);
        g.DrawLine(cornerPen, borderRect.Right + cornerOffset, borderRect.Y - cornerOffset, borderRect.Right + cornerOffset, borderRect.Y - cornerOffset + cornerLen);
        g.DrawLine(cornerPen, borderRect.X - cornerOffset, borderRect.Bottom + cornerOffset, borderRect.X - cornerOffset + cornerLen, borderRect.Bottom + cornerOffset);
        g.DrawLine(cornerPen, borderRect.X - cornerOffset, borderRect.Bottom + cornerOffset, borderRect.X - cornerOffset, borderRect.Bottom + cornerOffset - cornerLen);
        g.DrawLine(cornerPen, borderRect.Right + cornerOffset, borderRect.Bottom + cornerOffset, borderRect.Right + cornerOffset - cornerLen, borderRect.Bottom + cornerOffset);
        g.DrawLine(cornerPen, borderRect.Right + cornerOffset, borderRect.Bottom + cornerOffset, borderRect.Right + cornerOffset, borderRect.Bottom + cornerOffset - cornerLen);
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

        // Keep painting while fading out (Dismiss keeps the instance; opacity gates visibility).
        _hintBanner?.Render(g);
    }

    private void DismissHintBanner()
    {
        // Soft dismiss — keep the reference so the short fade can paint and so Revive()
        // still works after an aborted click/drag that never became a selection.
        _hintBanner?.Dismiss();
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
            _hintBanner?.Dispose();
            _hintBanner = null;
            _selectionAdorner?.Dispose();
            _selectionAdorner = null;
            _captureTimer?.Stop();
            _captureTimer?.Dispose();
            _controlBar?.Dispose();
            ClearPendingAutoFrame();
            ReleaseCaptureBitmaps();
            ReleaseSelectionPreview();
            _readoutFont.Dispose();
            if (IsHandleCreated)
                CaptureWindowExclusion.Unregister(Handle);
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
        public event Action<ScrollingCaptureMode>? ModeChanged;

        private static readonly Color ScrollAccent = Color.FromArgb(255, 0x07, 0x87, 0x88);
        private static readonly Color DoneAccent = Color.FromArgb(255, 0xBD, 0x70, 0x11);
        private static readonly Color DoneAccentHover = Color.FromArgb(255, 0xD4, 0x82, 0x18);
        private static readonly Color StartShineGlow = Color.FromArgb(168, 174, 184);
        private static readonly Color StartShineCore = Color.FromArgb(210, 215, 222);
        private const float StartShineThicknessScale = 0.55f;

        private static int BarWidth => UiChrome.ScaleInt(520);
        private static int BarHeight => UiChrome.ScaleInt(58);
        private static int PrimaryBtnHeight => UiChrome.ScaleInt(40);
        private static int SecondaryBtnSize => UiChrome.ScaleInt(38);
        private static int PrimaryBtnWidth => UiChrome.ScaleInt(76);
        private static int StartCancelGap => UiChrome.ScaleInt(8);
        private static int ModeComboWidth => UiChrome.ScaleInt(88);
        private static int ModeComboHeight => UiChrome.ScaleInt(30);
        private static int DotSize => UiChrome.ScaleInt(10);
        private static float CornerR => UiChrome.ScaledToolbarCornerRadius;

        private ScrollingCaptureMode _mode;
        private int _frameCount;
        private string? _statusOverride;
        private bool _isCapturing;
        private bool _modeComboHovered;
        private ContextMenuStrip? _modeMenu;

        private Rectangle _startBtnRect;
        private Rectangle _stopBtnRect;
        private Rectangle _cancelBtnRect;
        private Rectangle _manualFrameBtnRect;
        private Rectangle _modeComboRect;
        private Rectangle _phaseLabelRect;
        private Rectangle _statusRect;
        private Rectangle _recDotRect;
        private Rectangle? _hoveredRect;

        private readonly Font _statusFont = UiChrome.ChromeFont(10f, FontStyle.Bold);
        private readonly Font _hintFont = UiChrome.ChromeFont(8f, FontStyle.Regular);
        private readonly Font _phaseFont = CreatePhaseFont();
        private readonly Font _comboFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
        private readonly Font _startFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
        private WindowsToolTip? _chromeToolTip;
        private Rectangle? _tooltipAnchor;
        private readonly System.Windows.Forms.Timer _startShineTimer;
        private float _startShinePhase;

        private static readonly StringFormat _singleLineFmt = new()
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        private static readonly StringFormat _centerFmt = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        private static Font CreatePhaseFont()
        {
            return UiChrome.ChromeFont(11f, FontStyle.Bold);
        }

        public CaptureControlBar(Rectangle captureRegion, ScrollingCaptureMode mode)
        {
            _mode = mode == ScrollingCaptureMode.AssistAutoscroll
                ? ScrollingCaptureMode.AssistAutoscroll
                : ScrollingCaptureMode.Automatic;

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

            PositionAboveRegion(captureRegion);
            CalcLayout();
            ApplyRoundedChromeRegion();

            _startShineTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
            _startShineTimer.Tick += (_, _) => StartShineTick();
        }

        private void ApplyRoundedChromeRegion()
        {
            if (Width <= 0 || Height <= 0)
                return;

            Region?.Dispose();
            using var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, Width, Height), CornerR);
            Region = new Region(path);
        }

        private void PositionAboveRegion(Rectangle captureRegion)
        {
            var screen = Screen.FromRectangle(captureRegion);
            int tx = captureRegion.X + captureRegion.Width / 2 - BarWidth / 2;
            int ty = captureRegion.Y - BarHeight - UiChrome.ScaleInt(14);
            var edge = UiChrome.ScaleInt(4);
            if (ty < screen.Bounds.Top + edge)
                ty = captureRegion.Bottom + UiChrome.ScaleInt(14);
            if (tx < screen.Bounds.Left + edge) tx = screen.Bounds.Left + edge;
            if (tx + BarWidth > screen.Bounds.Right - edge) tx = screen.Bounds.Right - edge - BarWidth;
            Location = new Point(tx, ty);
        }

        private void CalcLayout()
        {
            int btnPad = WindowsDockRenderer.SurfacePadding;
            int btnGap = WindowsDockRenderer.ButtonSpacing;
            int gap = UiChrome.ScaleInt(8);
            int leftPad = UiChrome.ScaleInt(14);

            float centerY = BarHeight / 2f;
            int dotY = (int)(centerY - DotSize / 2f);
            _recDotRect = new Rectangle(leftPad, dotY, DotSize, DotSize);

            string phaseTextReady = PhaseLabel(false);
            string phaseTextCapturing = PhaseLabel(true);
            int phaseWidth = Math.Max(UiChrome.ScaleInt(96),
                Math.Max(MeasurePhaseLabelWidth(phaseTextReady), MeasurePhaseLabelWidth(phaseTextCapturing)));
            int phaseX = _recDotRect.Right + UiChrome.ScaleInt(8);
            _phaseLabelRect = new Rectangle(phaseX, 0, phaseWidth, BarHeight);

            int comboY = (BarHeight - ModeComboHeight) / 2;
            _modeComboRect = new Rectangle(_phaseLabelRect.Right + gap, comboY, ModeComboWidth, ModeComboHeight);

            int secY = (BarHeight - SecondaryBtnSize) / 2;
            int priY = (BarHeight - PrimaryBtnHeight) / 2;
            _cancelBtnRect = new Rectangle(BarWidth - btnPad - SecondaryBtnSize, secY, SecondaryBtnSize, SecondaryBtnSize);

            if (!_isCapturing)
            {
                _startBtnRect = new Rectangle(
                    _cancelBtnRect.X - btnGap - StartCancelGap - PrimaryBtnWidth, priY, PrimaryBtnWidth, PrimaryBtnHeight);
                _stopBtnRect = Rectangle.Empty;
                _manualFrameBtnRect = Rectangle.Empty;
            }
            else
            {
                _startBtnRect = Rectangle.Empty;
                _stopBtnRect = new Rectangle(
                    _cancelBtnRect.X - btnGap - StartCancelGap - PrimaryBtnWidth, priY, PrimaryBtnWidth, PrimaryBtnHeight);
                _manualFrameBtnRect = _mode == ScrollingCaptureMode.Manual
                    ? new Rectangle(_stopBtnRect.X - btnGap - SecondaryBtnSize, secY, SecondaryBtnSize, SecondaryBtnSize)
                    : Rectangle.Empty;
            }

            int firstBtnX = !_manualFrameBtnRect.IsEmpty ? _manualFrameBtnRect.X
                : !_startBtnRect.IsEmpty ? _startBtnRect.X
                : _stopBtnRect.X;
            int statusX = _modeComboRect.Right + gap;
            _statusRect = new Rectangle(statusX, 0, Math.Max(0, firstBtnX - gap - statusX), BarHeight);
        }

        private int MeasurePhaseLabelWidth(string text) =>
            TextRenderer.MeasureText(text, _phaseFont, new Size(int.MaxValue, BarHeight),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + UiChrome.ScaleInt(8);

        public void TransitionToCapturing()
        {
            _isCapturing = true;
            _frameCount = 0;
            _statusOverride = null;
            _startShineTimer.Stop();
            CalcLayout();
            Invalidate();
        }

        private void StartShineTick()
        {
            if (UI.Motion.Disabled || _isCapturing || _startBtnRect.IsEmpty)
            {
                _startShineTimer.Stop();
                return;
            }

            bool hovered = _hoveredRect == _startBtnRect;
            float delta = (float)(UiChrome.FrameIntervalMs / 2600.0) * (hovered ? 2f : 1f);
            _startShinePhase += delta;
            if (_startShinePhase >= 1f) _startShinePhase -= 1f;
            InvalidateStartShine();
        }

        public void Reposition(Rectangle captureRegion)
        {
            if (InvokeRequired) { BeginInvoke(() => Reposition(captureRegion)); return; }
            PositionAboveRegion(captureRegion);
            CalcLayout();
            Invalidate();
        }

        public void SetFrameCount(int count)
        {
            if (InvokeRequired) { BeginInvoke(() => SetFrameCount(count)); return; }
            _frameCount = count;
            _statusOverride = null;
            Invalidate(_statusRect);
        }

        public void SetPartialFrameCount(int count)
        {
            if (InvokeRequired) { BeginInvoke(() => SetPartialFrameCount(count)); return; }
            _frameCount = count;
            _statusOverride = FormatPartialFrameStatus(count);
            Invalidate(_statusRect);
        }

        public void SetStatus(string text)
        {
            if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
            _statusOverride = text;
            Invalidate(_statusRect);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var barRect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            var bgPath = WindowsDockRenderer.RoundedRect(barRect, CornerR);

            var glowRect = barRect;
            glowRect.Inflate(3f, 3f);
            using (var glowPath = WindowsDockRenderer.RoundedRect(glowRect, CornerR))
            using (var glowBrush = new SolidBrush(Color.FromArgb(25, ScrollAccent)))
                g.FillPath(glowBrush, glowPath);

            using (var micaBrush = new SolidBrush(Color.FromArgb(225, 12, 12, 16)))
                g.FillPath(micaBrush, bgPath);

            using (var bp = new Pen(Color.FromArgb(150, ScrollAccent), 1f))
                g.DrawPath(bp, bgPath);

            WindowsDockRenderer.PaintShadow(g, barRect, CornerR);

            float centerY = BarHeight / 2f;
            float dotX = _recDotRect.X;
            float dotY = centerY - DotSize / 2f;
            var dotRect = new RectangleF(dotX, dotY, DotSize, DotSize);

            if (!_isCapturing)
            {
                using var glowDot = new SolidBrush(Color.FromArgb(30, ScrollAccent));
                g.FillEllipse(glowDot, dotRect.X - 4, dotRect.Y - 4, DotSize + 8, DotSize + 8);
                using var dotBrush = new SolidBrush(Color.FromArgb(180, ScrollAccent));
                g.FillEllipse(dotBrush, dotRect);
            }
            else
            {
                double pulse = Math.Sin(Environment.TickCount / 250.0);
                float pa = (float)((pulse + 1.0) / 2.0);
                using var glowDot = new SolidBrush(Color.FromArgb((int)(30 + 40 * pa), ScrollAccent));
                g.FillEllipse(glowDot, dotRect.X - 4, dotRect.Y - 4, DotSize + 8, DotSize + 8);
                using var dotBrush = new SolidBrush(Color.FromArgb((int)(200 + 55 * pa), ScrollAccent));
                g.FillEllipse(dotBrush, dotRect);
            }

            using (var labelBrush = new SolidBrush(Color.FromArgb(220, ScrollAccent)))
            {
                var phaseRect = new RectangleF(_phaseLabelRect.X, centerY - UiChrome.ScaleFloat(8f),
                    _phaseLabelRect.Width, UiChrome.ScaleFloat(16f));
                g.DrawString(PhaseLabel(), _phaseFont, labelBrush, phaseRect, _singleLineFmt);
            }

            DrawModeCombo(g);

            var statusText = StatusDisplayText();
            if (!string.IsNullOrEmpty(statusText))
            {
                bool isHint = IsStatusHint();
                using var statusBrush = new SolidBrush(isHint ? UiChrome.SurfaceTextMuted : UiChrome.SurfaceTextPrimary);
                g.DrawString(statusText, isHint ? _hintFont : _statusFont, statusBrush, _statusRect, _singleLineFmt);
            }

            if (!_startBtnRect.IsEmpty)
            {
                DrawPrimaryTextBtn(g, _startBtnRect, LocalizationService.Translate("Start scrolling capture"),
                    _hoveredRect == _startBtnRect, ScrollAccent, Color.FromArgb(255, 0x0a, 0x9a, 0x9c),
                    withShine: true, shinePhase: _startShinePhase);
            }
            if (!_stopBtnRect.IsEmpty)
            {
                DrawPrimaryTextBtn(g, _stopBtnRect, LocalizationService.Translate("Scroll capture done"),
                    _hoveredRect == _stopBtnRect, DoneAccent, DoneAccentHover);
            }
            if (!_manualFrameBtnRect.IsEmpty)
                DrawIconBtn(g, _manualFrameBtnRect, "record", _hoveredRect == _manualFrameBtnRect, UiChrome.SurfaceTextPrimary);

            bool cancelHovered = _hoveredRect == _cancelBtnRect;
            var cancelColor = cancelHovered
                ? Color.FromArgb(255, 255, 80, 80)
                : UiChrome.SurfaceTextPrimary;
            DrawIconBtn(g, _cancelBtnRect, "close", cancelHovered, cancelColor);
        }

        private void DrawModeCombo(Graphics g)
        {
            var rect = new RectangleF(_modeComboRect.X, _modeComboRect.Y, _modeComboRect.Width, _modeComboRect.Height);
            bool enabled = !_isCapturing;
            bool hovered = enabled && _modeComboHovered;
            WindowsDockRenderer.PaintButton(g, rect, false, hovered, CornerR - 1f, ScrollAccent);

            using (var border = new Pen(Color.FromArgb(enabled ? 90 : 50, ScrollAccent), 1f))
            using (var path = WindowsDockRenderer.RoundedRect(rect, CornerR - 1f))
                g.DrawPath(border, path);

            int alpha = enabled ? hovered ? 255 : 220 : 120;
            using (var textBrush = new SolidBrush(Color.FromArgb(alpha, UiChrome.SurfaceTextPrimary)))
            {
                var textRect = new RectangleF(rect.X + UiChrome.ScaleFloat(8f), rect.Y,
                    rect.Width - UiChrome.ScaleFloat(22f), rect.Height);
                g.DrawString(ModeComboLabel(_mode), _comboFont, textBrush, textRect, _singleLineFmt);
            }

            float chevronX = rect.Right - UiChrome.ScaleFloat(14f);
            float chevronY = rect.Y + rect.Height / 2f;
            using var chevronPen = new Pen(Color.FromArgb(enabled ? 180 : 90, ScrollAccent), UiChrome.ScaleFloat(1.4f));
            g.DrawLine(chevronPen, chevronX - 3, chevronY - 2, chevronX, chevronY + 1);
            g.DrawLine(chevronPen, chevronX, chevronY + 1, chevronX + 3, chevronY - 2);
        }

        private void DrawPrimaryTextBtn(
            Graphics g, Rectangle rect, string text, bool hovered,
            Color normal, Color hoverFill, bool withShine = false, float shinePhase = 0f)
        {
            var rectF = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
            using var path = WindowsDockRenderer.RoundedRect(rectF, CornerR);
            using (var brush = new SolidBrush(hovered ? hoverFill : normal))
                g.FillPath(brush, path);
            using (var border = new Pen(Color.FromArgb(hovered ? 220 : 160, Color.White), 1f))
                g.DrawPath(border, path);

            using var textBrush = new SolidBrush(Color.White);
            g.DrawString(text, _startFont, textBrush, rectF, _centerFmt);

            if (withShine && !UI.Motion.Disabled)
            {
                var clipState = g.Save();
                g.ResetClip();
                WindowsDockRenderer.PaintBorderShine(
                    g, rectF, CornerR, shinePhase, StartShineGlow, StartShineCore, 1f, StartShineThicknessScale);
                g.Restore(clipState);
            }
        }

        private void InvalidateStartShine()
        {
            if (_startBtnRect.IsEmpty)
                return;

            int pad = UiChrome.ScaleInt(10);
            Invalidate(Rectangle.Inflate(_startBtnRect, pad, pad));
        }

        private void DrawIconBtn(Graphics g, Rectangle r, string iconId, bool hovered, Color iconColor)
        {
            bool filled = iconId == "stop";
            WindowsDockRenderer.PaintButton(g, r, filled, hovered);
            int alpha = filled ? 255 : hovered ? 240 : 200;
            WindowsDockRenderer.PaintIcon(g, iconId, r, Color.FromArgb(alpha, iconColor.R, iconColor.G, iconColor.B), filled);
        }

        private void ShowModeMenu()
        {
            if (_isCapturing) return;

            _modeMenu?.Close();

            var menu = WindowsMenuRenderer.Create(showImages: false, minWidth: ModeComboWidth + UiChrome.ScaleInt(24));
            _modeMenu = menu;

            var manualItem = WindowsMenuRenderer.Item("Manual", active: _mode == ScrollingCaptureMode.Automatic);
            manualItem.Click += (_, _) => QueueApplyMode(ScrollingCaptureMode.Automatic);
            var autoItem = WindowsMenuRenderer.Item("Auto", active: _mode == ScrollingCaptureMode.AssistAutoscroll);
            autoItem.Click += (_, _) => QueueApplyMode(ScrollingCaptureMode.AssistAutoscroll);
            menu.Items.Add(manualItem);
            menu.Items.Add(autoItem);
            menu.Show(this, new Point(_modeComboRect.Left, _modeComboRect.Bottom + UiChrome.ScaleInt(2)));
        }

        private void QueueApplyMode(ScrollingCaptureMode mode)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                _modeMenu?.Close();
                ApplyMode(mode);
            }));
        }

        private void ApplyMode(ScrollingCaptureMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            ModeChanged?.Invoke(mode);
            CalcLayout();
            Invalidate();
            Invalidate(_statusRect);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var prev = _hoveredRect;
            bool prevCombo = _modeComboHovered;

            _hoveredRect = HitTestInteractive(e.Location);
            _modeComboHovered = !_isCapturing && _modeComboRect.Contains(e.Location);

            Cursor = _hoveredRect != null || _modeComboHovered ? Cursors.Hand : Cursors.Default;

            if (_hoveredRect != prev)
            {
                if (prev.HasValue) Invalidate(prev.Value);
                if (_hoveredRect.HasValue) Invalidate(_hoveredRect.Value);
            }
            if (_modeComboHovered != prevCombo)
                Invalidate(_modeComboRect);

            UpdateToolTip(e.Location);
        }

        private Rectangle? HitTestInteractive(Point p)
        {
            if (!_startBtnRect.IsEmpty && _startBtnRect.Contains(p)) return _startBtnRect;
            if (!_stopBtnRect.IsEmpty && _stopBtnRect.Contains(p)) return _stopBtnRect;
            if (!_manualFrameBtnRect.IsEmpty && _manualFrameBtnRect.Contains(p)) return _manualFrameBtnRect;
            if (_cancelBtnRect.Contains(p)) return _cancelBtnRect;
            return null;
        }

        private void UpdateToolTip(Point location)
        {
            string? tip = null;
            Rectangle? anchor = null;

            if (!_isCapturing && _modeComboRect.Contains(location))
            {
                tip = _mode == ScrollingCaptureMode.AssistAutoscroll
                    ? LocalizationService.Translate("Automatically scroll and capture the window content.")
                    : LocalizationService.Translate("You scroll the content yourself. CyberSnap collects frames as they appear while you scroll.");
                anchor = _modeComboRect;
            }
            else if (!_startBtnRect.IsEmpty && _startBtnRect.Contains(location))
            {
                tip = LocalizationService.Translate("Scroll capture start tooltip");
                anchor = _startBtnRect;
            }
            else if (!_stopBtnRect.IsEmpty && _stopBtnRect.Contains(location))
            {
                tip = LocalizationService.Translate("Scroll capture stop tooltip");
                anchor = _stopBtnRect;
            }
            else if (!_manualFrameBtnRect.IsEmpty && _manualFrameBtnRect.Contains(location))
            {
                tip = LocalizationService.Translate("Scroll capture manual frame");
                anchor = _manualFrameBtnRect;
            }
            else if (_cancelBtnRect.Contains(location))
            {
                tip = LocalizationService.Translate("Scroll capture cancel tooltip");
                anchor = _cancelBtnRect;
            }

            if (tip == null || anchor == null)
            {
                HideChromeToolTip();
                return;
            }

            if (_tooltipAnchor == anchor && _chromeToolTip?.Visible == true)
                return;

            _tooltipAnchor = anchor;
            _chromeToolTip ??= new WindowsToolTip();
            var screenAnchor = GetScreenBounds(anchor.Value);
            _chromeToolTip.ShowNear(this, tip, screenAnchor, above: true);
        }

        private Rectangle GetScreenBounds(Rectangle clientRect)
        {
            var origin = PointToScreen(Point.Empty);
            return new Rectangle(origin.X + clientRect.X, origin.Y + clientRect.Y, clientRect.Width, clientRect.Height);
        }

        private void HideChromeToolTip()
        {
            _tooltipAnchor = null;
            _chromeToolTip?.Hide();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredRect != null)
            {
                var p = _hoveredRect.Value;
                _hoveredRect = null;
                Invalidate(p);
            }
            if (_modeComboHovered)
            {
                _modeComboHovered = false;
                Invalidate(_modeComboRect);
            }
            HideChromeToolTip();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!_isCapturing && _modeComboRect.Contains(e.Location))
            {
                ShowModeMenu();
                return;
            }
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
            if (!_isCapturing && key == Keys.Enter)
            {
                StartClicked?.Invoke();
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
            CaptureWindowExclusion.SetLogicalBounds(Handle, static () => Rectangle.Empty);
            ApplyRoundedChromeRegion();
            try
            {
                Dwm.TrySetWindowCornerPreference(Handle, Dwm.DWMWCP_ROUND);
                Dwm.TrySetImmersiveDarkMode(Handle, UiChrome.IsDark);
            }
            catch { /* optional DWM polish */ }

            if (!UI.Motion.Disabled && !_isCapturing)
                _startShineTimer.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!IsHandleCreated)
                return;

            if (Visible && !UI.Motion.Disabled && !_isCapturing)
                _startShineTimer.Start();
            else
                _startShineTimer.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _startShineTimer.Stop();
                _startShineTimer.Dispose();
                _modeMenu?.Dispose();
                _modeMenu = null;
                _chromeToolTip?.Dispose();
                _chromeToolTip = null;
                _statusFont.Dispose();
                _hintFont.Dispose();
                _phaseFont.Dispose();
                _comboFont.Dispose();
                _startFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private string StatusDisplayText()
        {
            if (!string.IsNullOrEmpty(_statusOverride))
                return _statusOverride;
            if (_isCapturing && _frameCount > 0)
                return FormatFrameStatus(_frameCount);
            if (!_isCapturing)
            {
                return _mode == ScrollingCaptureMode.AssistAutoscroll
                    ? LocalizationService.Translate("Scroll capture ready hint auto")
                    : LocalizationService.Translate("Scroll capture ready hint manual");
            }

            return string.Empty;
        }

        private bool IsStatusHint() =>
            string.IsNullOrEmpty(_statusOverride) && !_isCapturing;

        private static string PhaseLabel(bool capturing) =>
            capturing
                ? LocalizationService.Translate("Scroll capture capturing")
                : LocalizationService.Translate("Scroll capture ready");

        private string PhaseLabel() => PhaseLabel(_isCapturing);

        private static string ModeComboLabel(ScrollingCaptureMode mode) =>
            mode == ScrollingCaptureMode.AssistAutoscroll
                ? LocalizationService.Translate("Auto")
                : LocalizationService.Translate("Manual");

        private static string FormatFrameStatus(int count) =>
            count == 1
                ? LocalizationService.Translate("1 frame")
                : string.Format(LocalizationService.Translate("{0} frames"), count);

        private static string FormatPartialFrameStatus(int count) =>
            string.Format(LocalizationService.Translate("Scroll capture partial frames"), count);
    }
}
