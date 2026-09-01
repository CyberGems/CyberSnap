using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Native;
using CyberSnap.Services;
using CyberSnap.UI;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace CyberSnap.Capture;

/// <summary>
/// WPF replacement for the GDI+ RecordingControlBar. Floating recording control bar
/// with ready phase (Start + FPS + Cancel) and recording phase (Pause + Stop + Cancel).
/// </summary>
public sealed partial class RecordingControlBarWindow : Window
{
    // ── Events (match the GDI+ RecordingControlBar surface) ──
    public event Action? StartClicked;
    public event Action? StopClicked;
    public event Action? CancelClicked;
    public event Action? PauseClicked;
    public event Action<int>? FpsChanged;
    public event Action<bool>? SendToTrimmerChanged;

    // ── Accent colors ──
    private static Color DoneAccent =>
        UiChrome.IsGray
            ? ToMediaColor(UiChrome.AccentColor)
            : Color.FromArgb(255, 0xBD, 0x70, 0x11);
    private static Color DoneAccentHover =>
        UiChrome.IsGray
            ? Color.FromArgb(
                255,
                (byte)Math.Min(255, UiChrome.AccentColor.R + 28),
                (byte)Math.Min(255, UiChrome.AccentColor.G + 28),
                (byte)Math.Min(255, UiChrome.AccentColor.B + 28))
            : Color.FromArgb(255, 0xD4, 0x82, 0x18);
    private static readonly Color CancelHoverColor = Color.FromArgb(255, 255, 80, 80);
    private static readonly Color StopRed = Color.FromArgb(255, 229, 72, 77);
    private static readonly Color StopRedHot = Color.FromArgb(255, 255, 96, 100);
    private const long LowDiskWarnBytes = 200L * 1024 * 1024;

    // ── Bar dimensions (100% DPI baseline, scaled via UiScale.LayoutTransform) ──
    private const double BarWidth = 668;
    private const double BarHeight = 58;

    // ── State ──
    private readonly Models.RecordingFormat _format;
    private readonly Color _accent;
    private readonly Color _accentHover;
    private readonly bool _supportsPause;

    private int _fps;
    private bool _sendToTrimmer;
    private bool _isRecording;
    private bool _isPaused;
    private bool _isEncoding;
    private TimeSpan _elapsed;

    // ── Output path (size · free-disk meter; polled at 1 Hz, not the pulse timer) ──
    private readonly string _outputPath;
    private readonly DriveInfo? _outputDrive;

    // ── Timers ──
    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _storageTimer;
    private Storyboard? _shineStoryboard;
    private long _lastUsedBytes = -1;
    private long _lastFreeBytes = -1;

    // ── FPS menu ──
    private ContextMenu? _fpsMenu;

    // ── Positioning ──
    private System.Drawing.Rectangle _lastCaptureRegion;

    /// <summary>The WinForms RecordingForm that owns this bar; keeps the bar above the overlay.</summary>
    public System.Windows.Forms.Form? OwnerWinFormsForm { get; set; }

    /// <summary>True while the user is dragging/resizing the selection; bar hides to not obstruct.</summary>
    private bool _isDragInProgress;

    /// <summary>True while the user is dragging the bar itself.</summary>
    private bool _isBarDragging;

    /// <summary>Once the user moves the bar, auto-anchoring to the capture region stops.</summary>
    private bool _userPositioned;

    private System.Drawing.Point _dragCursorStart;
    private System.Drawing.Point _dragWindowStart;

    public RecordingControlBarWindow(
        System.Drawing.Rectangle captureRegion,
        Models.RecordingFormat format,
        int fps,
        bool sendToTrimmer,
        string outputPath)
    {
        Theme.Refresh();
        _format = format;
        _fps = NormalizeFps(format, fps);
        _sendToTrimmer = sendToTrimmer;
        _supportsPause = format != Models.RecordingFormat.GIF;
        _outputPath = outputPath ?? "";
        _outputDrive = TryGetDrive(_outputPath);

        // Accent: GIF keeps its format orange outside grayscale; MP4 follows the selection accent.
        _accent = format == Models.RecordingFormat.GIF
            ? ToMediaColor(UiChrome.GifAccentColor)
            : ToMediaColor(UiChrome.AccentColor);
        _accentHover = Color.FromArgb(
            255,
            (byte)Math.Min(255, _accent.R + 28),
            (byte)Math.Min(255, _accent.G + 28),
            (byte)Math.Min(255, _accent.B + 28));

        InitializeComponent();

        Height = BarHeight;

        // ── Chrome setup ──
        ConfigureShell();
        LoadIcons();
        HookHoverEffects();
        HookClickHandlers();

        // ── Rounded corners + no-activate + owner-window for z-order ──
        CyberSnapWindowChrome.ApplyRoundedCorners(this, UiChrome.ToolbarCornerRadius);
        SourceInitialized += (_, _) =>
        {
            PopupWindowHelper.ApplyNoActivateChrome(this);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                CaptureWindowExclusion.Apply(hwnd);

                // Owner the WPF bar to the WinForms RecordingForm overlay. Owned windows
                // always stay above their owner in z-order — this is what keeps the bar
                // visible over the dimmed overlay while the user drags the selection.
                if (OwnerWinFormsForm?.IsHandleCreated == true)
                    User32.SetWindowLongA(hwnd, User32.GWL_HWNDPARENT, unchecked((int)OwnerWinFormsForm.Handle));

                // Assert topmost once the window exists.
                User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                    User32.SWP_NOSIZE | User32.SWP_NOMOVE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
            }
        };

        // ── UI scale ──
        UiScale.ApplyToWindow(this, OuterShell, scaleWindowBounds: false);

        // ── Start button shine animation ──
        SetupShineAnimation();

        // ── Pulse timer for the format-badge live indicator ──
        _pulseTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(UiChrome.FrameIntervalMs)
        };
        _pulseTimer.Tick += (_, _) => UpdateFormatBadge();

        // ── Size · free-disk meter. Normal priority so the badge pulse (Render)
        //     cannot starve it; 250 ms so ffmpeg/NTFS size jumps show up quickly. ──
        _storageTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _storageTimer.Tick += (_, _) => RefreshStorageMeter();

        // ── Initial layout ──
        _lastCaptureRegion = captureRegion;
        UpdateLayoutVisibility();
        RefreshStorageMeter();
        _storageTimer.Start();

        // Position after the HWND exists so SetWindowPos works (physical pixels,
        // correct per-monitor DPI). SourceInitialized fires before Loaded.
        SourceInitialized += (_, _) => PositionAboveRegion(captureRegion);

        // Start shine when handle is created
        Loaded += (_, _) =>
        {
            if (!UI.Motion.Disabled && !_isRecording && !_isEncoding)
                StartShineAnimation();
        };
    }

    public int Fps => _fps;

    /// <summary>
    /// Hides the bar while the user drags/resizes the capture region so it doesn't
    /// obstruct the view; repositions and re-shows when the drag ends.
    /// </summary>
    public void SetDragInProgress(bool dragging)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetDragInProgress(dragging)); return; }
        _isDragInProgress = dragging;
        Opacity = dragging ? 0.0 : 1.0;
        IsHitTestVisible = !dragging;
    }

    // ══════════════════════════════════════════════════════════════
    //  Public API (mirrors GDI+ RecordingControlBar)
    // ══════════════════════════════════════════════════════════════

    public void TransitionToRecording()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(TransitionToRecording); return; }
        _isRecording = true;
        _isPaused = false;
        _isEncoding = false;
        _elapsed = TimeSpan.Zero;
        StopShineAnimation();
        if (!UI.Motion.Disabled)
            _pulseTimer.Start();
        UpdateLayoutVisibility();
        UpdateFormatBadge();
        UpdateStatusText();
        RefreshStorageMeter();
    }

    public void TransitionToEncoding()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(TransitionToEncoding); return; }
        _isEncoding = true;
        _isPaused = false;
        StopShineAnimation();
        _pulseTimer.Stop();
        UpdateLayoutVisibility();
        UpdateFormatBadge();
        UpdateStatusText();
        RefreshStorageMeter();
    }

    public void SetElapsed(TimeSpan elapsed)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetElapsed(elapsed)); return; }
        _elapsed = elapsed;
        if (_isRecording && !_isEncoding)
        {
            UpdateStatusText();
            RefreshStorageMeter();
        }
    }

    public void SetPaused(bool paused)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetPaused(paused)); return; }
        if (_isPaused == paused) return;
        _isPaused = paused;

        if (_isPaused)
            _pulseTimer.Stop();
        else if (_isRecording && !_isEncoding && !UI.Motion.Disabled)
            _pulseTimer.Start();

        UpdateFormatBadge();
        UpdatePrimaryButtonVisual();
        UpdateStopButtonVisual();
    }

    public void Reposition(System.Drawing.Rectangle captureRegion)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Reposition(captureRegion)); return; }
        _lastCaptureRegion = captureRegion;
        if (_userPositioned || _isBarDragging)
            return;
        PositionAboveRegion(captureRegion);
    }

    // ══════════════════════════════════════════════════════════════
    //  Positioning (per-monitor DPI aware)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Positions the bar centered horizontally on the capture region, BELOW its bottom
    /// edge (standardized for MP4/GIF). Uses physical pixels via SetWindowPos and
    /// re-asserts topmost so the overlay never hides the bar mid-drag.
    /// </summary>
    private void PositionAboveRegion(System.Drawing.Rectangle captureRegion)
    {
        var screen = System.Windows.Forms.Screen.FromRectangle(captureRegion);

        System.Drawing.Rectangle workArea;
        if (PopupWindowHelper.TryGetNativeMonitorInfo(screen, out _, out var nativeWork) && !nativeWork.IsEmpty)
            workArea = nativeWork;
        else
            workArea = screen.WorkingArea;

        var screenBounds = PopupWindowHelper.TryGetNativeMonitorInfo(screen, out var nativeBounds, out _)
            && !nativeBounds.IsEmpty
                ? nativeBounds
                : screen.Bounds;

        // Bar dimensions in physical pixels. Preferred: the real window rect (accounts
        // for SizeToContent + UiScale.LayoutTransform). Fallback: estimate from the
        // monitor's DPI scale when the handle isn't created yet.
        var scale = PopupWindowHelper.GetScaleForPoint(new System.Drawing.Point(
            captureRegion.X + captureRegion.Width / 2,
            captureRegion.Y));

        var hwndEarly = new WindowInteropHelper(this).Handle;
        int barWidthPhys, barHeightPhys;
        if (hwndEarly != IntPtr.Zero && User32.GetWindowRect(hwndEarly, out var wr))
        {
            barWidthPhys = wr.Right - wr.Left;
            barHeightPhys = wr.Bottom - wr.Top;
        }
        else
        {
            // LayoutTransform scales WPF units by BOTH UiScale.Current and the monitor
            // DPI, so physical size = logical * UiScale * dpiScale.
            barWidthPhys = (int)Math.Round(BarWidth * UiScale.Current * scale.X);
            barHeightPhys = (int)Math.Round(BarHeight * UiScale.Current * scale.Y);
        }

        int gap = (int)Math.Round(14 * scale.Y);
        int edge = (int)Math.Round(4 * scale.Y);

        // Centered horizontally on the region.
        int tx = captureRegion.X + (captureRegion.Width - barWidthPhys) / 2;

        // Default: BELOW the region's bottom edge.
        int ty = captureRegion.Bottom + gap;

        // If that goes past the working area bottom, flip ABOVE the region.
        if (ty + barHeightPhys > workArea.Bottom - edge)
            ty = captureRegion.Y - barHeightPhys - gap;

        // If still off the top, park at the bottom of the working area without
        // overlapping the region if possible.
        if (ty < screenBounds.Top + edge)
        {
            ty = workArea.Bottom - barHeightPhys - (int)Math.Round(16 * scale.Y);
            if (ty < screenBounds.Top + edge)
                ty = screenBounds.Top + edge;
        }

        // Clamp horizontally within the screen.
        if (tx < screenBounds.Left + edge) tx = screenBounds.Left + edge;
        if (tx + barWidthPhys > screenBounds.Right - edge) tx = screenBounds.Right - edge - barWidthPhys;

        MoveBarToPhysical(tx, ty);
        // If handle isn't ready, SourceInitialized re-invokes this and places it then.
    }

    private void MoveBarToPhysical(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // SWP_SHOWWINDOW + no NOZORDER flag → re-asserts HWND_TOPMOST each call,
        // so the bar stays above the fullscreen overlay even mid drag.
        User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, x, y, 0, 0,
            User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
    }

    // ══════════════════════════════════════════════════════════════
    //  Drag (chrome, not buttons — those mark MouseLeftButtonDown handled)
    // ══════════════════════════════════════════════════════════════

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || _isDragInProgress || _isBarDragging)
            return;
        if (!TryGetBarDragOrigin(out _dragCursorStart, out _dragWindowStart))
            return;

        _isBarDragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isBarDragging || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (!User32.GetCursorPos(out var cursor))
            return;

        int dx = cursor.X - _dragCursorStart.X;
        int dy = cursor.Y - _dragCursorStart.Y;
        if (!_userPositioned &&
            Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _userPositioned = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        int barWidth = (int)Math.Round(BarWidth * UiScale.Current);
        int barHeight = (int)Math.Round(BarHeight * UiScale.Current);
        if (hwnd != IntPtr.Zero && User32.GetWindowRect(hwnd, out var wr))
        {
            barWidth = wr.Width;
            barHeight = wr.Height;
        }

        int tx = _dragWindowStart.X + dx;
        int ty = _dragWindowStart.Y + dy;
        ClampToVirtualScreen(ref tx, ref ty, barWidth, barHeight);
        MoveBarToPhysical(tx, ty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isBarDragging)
            return;
        EndBarDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_isBarDragging)
            EndBarDrag();
    }

    private void EndBarDrag()
    {
        _isBarDragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    private bool TryGetBarDragOrigin(out System.Drawing.Point cursor, out System.Drawing.Point window)
    {
        cursor = default;
        window = default;
        if (!User32.GetCursorPos(out var pt))
            return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !User32.GetWindowRect(hwnd, out var wr))
            return false;

        cursor = new System.Drawing.Point(pt.X, pt.Y);
        window = new System.Drawing.Point(wr.Left, wr.Top);
        return true;
    }

    private static void ClampToVirtualScreen(ref int x, ref int y, int width, int height)
    {
        var vs = new System.Drawing.Rectangle(
            User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN),
            User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN),
            User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN),
            User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN));

        const int edge = 4;
        if (width < vs.Width - edge * 2)
            x = Math.Clamp(x, vs.Left + edge, vs.Right - edge - width);
        else
            x = vs.Left + edge;

        if (height < vs.Height - edge * 2)
            y = Math.Clamp(y, vs.Top + edge, vs.Bottom - edge - height);
        else
            y = vs.Top + edge;
    }

    // ══════════════════════════════════════════════════════════════
    //  Chrome / Visual Setup
    // ══════════════════════════════════════════════════════════════

    private void ConfigureShell()
    {
        // Use the same opaque surface, subtle border, and soft shadow tokens as the capture dock.
        Root.Background = Theme.Brush(ToMediaColor(UiChrome.SurfaceTier1));

        // Edge ring: quiet surface border; active accents belong to controls, not the whole shell.
        EdgeRing.Background = Theme.Brush(ToMediaColor(UiChrome.SurfaceBorder));
        EdgeRing.Padding = new Thickness(1);

        // Shadow
        ShadowPlate.Background = Theme.Brush(ToMediaColor(UiChrome.SurfaceTier1));
        ShadowPlate.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 3,
            Opacity = Theme.IsDark ? 0.34 : 0.16,
            Direction = 270,
            Color = Colors.Black,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
        };

        // A restrained accent halo mirrors the capture dock's edge treatment.
        OuterShell.Background = Theme.Brush(ToMediaColor(
            System.Drawing.Color.FromArgb(Theme.IsDark ? 18 : 12, _accent.R, _accent.G, _accent.B)));

        UpdateFormatBadge();
        UpdateFpsDivider();
    }

    private void UpdateFpsDivider()
    {
        var c = Theme.TextMuted;
        byte mid = Theme.IsDark ? (byte)72 : (byte)88;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(mid, c.R, c.G, c.B), 0.22));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(mid, c.R, c.G, c.B), 0.78));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        brush.Freeze();
        FpsDivider.Background = brush;
    }

    private static Color ToMediaColor(System.Drawing.Color color) =>
        Color.FromArgb(color.A, color.R, color.G, color.B);

    private static System.Drawing.Color ToDrawingColor(Color color) =>
        System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

    private void LoadIcons()
    {
        // Primary/Stop icons are set by UpdateLayoutVisibility() (UpdatePrimaryButtonVisual /
        // UpdateStopButtonVisual), which runs right after this in the constructor.
        UpdateTrimmerButtonIcon();
        UpdateCancelButtonIcon();
    }

    private static System.Drawing.Color IconNormal =>
        System.Drawing.Color.FromArgb(200, Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B);

    private void UpdateTrimmerButtonIcon()
    {
        var active = _sendToTrimmer;
        var c = active ? _accent : Theme.TextPrimary;
        var iconColor = System.Drawing.Color.FromArgb(active ? 255 : 200, c.R, c.G, c.B);
        TrimmerIcon.Source = FluentIcons.RenderWpf("scissors", iconColor, 44, active);
        // Active ring + accent wash so the ON state is unmistakable.
        TrimmerBtn.Background = active
            ? Theme.Brush(Color.FromArgb(38, _accent.R, _accent.G, _accent.B))
            : System.Windows.Media.Brushes.Transparent;
        TrimmerBtn.BorderBrush = active ? Theme.Brush(_accent) : System.Windows.Media.Brushes.Transparent;
        TrimmerBtn.BorderThickness = active ? new Thickness(1.5) : new Thickness(0);
    }

    private void UpdateCancelButtonIcon()
    {
        CancelIcon.Source = FluentIcons.RenderWpf("close", IconNormal, 32);
    }

    // ══════════════════════════════════════════════════════════════
    //  Hover Effects
    // ══════════════════════════════════════════════════════════════

    private void HookHoverEffects()
    {
        // ── Primary button (Record / Pause / Resume) hover ──
        PrimaryBtn.MouseEnter += (_, _) =>
        {
            if (PrimaryBtn.IsEnabled)
                PrimaryBtn.Background = Theme.Brush(Lighten(GetPrimaryFill(), 28));
        };
        PrimaryBtn.MouseLeave += (_, _) =>
        {
            if (PrimaryBtn.IsEnabled)
                PrimaryBtn.Background = Theme.Brush(GetPrimaryFill());
        };

        // ── Stop button hover: reddish wash, glyph stays red (never orange + white) ──
        StopBtn.MouseEnter += (_, _) =>
        {
            if (!StopBtn.IsEnabled)
                return;
            StopBtn.Background = Theme.Brush(Color.FromArgb(80, StopRed.R, StopRed.G, StopRed.B));
            StopGlyph.Background = Theme.Brush(StopRedHot);
        };
        StopBtn.MouseLeave += (_, _) =>
        {
            if (StopBtn.IsEnabled)
                SetStopEnabled(true);
        };

        // ── Trimmer button hover ──
        TrimmerBtn.MouseEnter += (_, _) =>
        {
            var c = _sendToTrimmer ? _accentHover : _accent;
            var iconColor = System.Drawing.Color.FromArgb(240, c.R, c.G, c.B);
            TrimmerIcon.Source = FluentIcons.RenderWpf("scissors", iconColor, 44, _sendToTrimmer);
            TrimmerBtn.Background = _sendToTrimmer
                ? Theme.Brush(Color.FromArgb(50, _accent.R, _accent.G, _accent.B))
                : Theme.Brush(Theme.AccentSubtle);
        };
        TrimmerBtn.MouseLeave += (_, _) =>
        {
            UpdateTrimmerButtonIcon(); // also restores the active ring/wash
        };

        // ── Cancel button hover ──
        CancelBtn.MouseEnter += (_, _) =>
        {
            var iconColor = System.Drawing.Color.FromArgb(255, CancelHoverColor.R, CancelHoverColor.G, CancelHoverColor.B);
            CancelIcon.Source = FluentIcons.RenderWpf("close", iconColor, 32);
            CancelBtn.Background = Theme.Brush(Color.FromArgb(40, 255, 80, 80));
        };
        CancelBtn.MouseLeave += (_, _) =>
        {
            UpdateCancelButtonIcon();
            CancelBtn.Background = System.Windows.Media.Brushes.Transparent;
        };

        // ── FPS combo hover ──
        FpsCombo.MouseEnter += (_, _) =>
        {
            if (!FpsCombo.IsEnabled)
                return;
            FpsCombo.Background = Theme.Brush(ToMediaColor(UiChrome.SurfaceHover));
            FpsComboLabel.Foreground = Theme.Brush(Theme.TextPrimary);
        };
        FpsCombo.MouseLeave += (_, _) =>
        {
            FpsCombo.Background = Theme.Brush(ToMediaColor(UiChrome.SurfacePill));
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  Click Handlers
    // ══════════════════════════════════════════════════════════════

    private void HookClickHandlers()
    {
        PrimaryBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_isRecording)
                PauseClicked?.Invoke();
            else
                StartClicked?.Invoke();
        };
        StopBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (StopBtn.IsEnabled)
                StopClicked?.Invoke();
        };
        CancelBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_isEncoding) CancelClicked?.Invoke(); };
        FpsCombo.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (FpsCombo.IsEnabled)
                ShowFpsMenu();
        };

        TrimmerBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _sendToTrimmer = !_sendToTrimmer;
            SendToTrimmerChanged?.Invoke(_sendToTrimmer);
            UpdateTrimmerButtonIcon(); // updates icon + ring + wash in one place
            UpdateTrimmerTooltip();
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  Layout State
    // ══════════════════════════════════════════════════════════════

    private void UpdateLayoutVisibility()
    {
        // All buttons stay visible between ready and recording (sizes are fixed) — only
        // their enabled state, icon and label change. This keeps the button group at a
        // constant width so nothing shifts when starting/pausing.
        if (_isEncoding)
        {
            SetPrimaryEnabled(false);
            SetStopEnabled(false);
            SetFpsEnabled(false);
            TrimmerBtn.IsEnabled = false;
            CancelBtn.IsEnabled = false;
        }
        else if (!_isRecording)
        {
            // Ready phase: Record enabled + FPS + Trimmer + Cancel (Stop disabled)
            SetPrimaryEnabled(true);
            SetStopEnabled(false);
            SetFpsEnabled(true);
            TrimmerBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
        else
        {
            // Recording phase: Pause/Resume enabled (MP4 only) + Stop + Trimmer + Cancel.
            // FPS stays in the layout (disabled) so Stop/Trimmer/Cancel do not shift.
            SetPrimaryEnabled(_supportsPause);
            SetStopEnabled(true);
            SetFpsEnabled(false);
            TrimmerBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }

        UpdatePrimaryButtonVisual();
        UpdateStopButtonVisual();
        UpdateStatusText();
        UpdateFpsComboVisual();
        UpdateFormatBadge();
        UpdateTooltips();
    }

    // ── Primary button: Record ⇄ Pause ⇄ Resume (icon + label swap, NEVER resizes) ──

    private void UpdatePrimaryButtonVisual()
    {
        string label;
        string iconId;

        if (!_isRecording)
        {
            // Ready: "Record" with the classic play icon
            label = LocalizationService.Translate("Record");
            iconId = "play";
        }
        else if (_isPaused)
        {
            // Paused: "Resume" with play icon
            label = LocalizationService.Translate("Resume");
            iconId = "play";
        }
        else
        {
            // Recording: "Pause" with pause bars icon
            label = LocalizationService.Translate("Pause");
            iconId = "pause";
        }

        var fill = GetPrimaryFill();
        PrimaryText.Text = label;
        PrimaryBtn.Background = Theme.Brush(fill);
        var primaryContentColor = GetButtonTextColor(fill);
        PrimaryText.Foreground = Theme.Brush(primaryContentColor);
        PrimaryBtn.Cursor = _isEncoding ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;

        // Use a dark foreground when the fill is a light silver/cyan, keeping icon and label readable.
        PrimaryIcon.Source = FluentIcons.RenderWpf(iconId, ToDrawingColor(primaryContentColor), 40);
        PrimaryIcon.Visibility = Visibility.Visible;
        UpdatePrimaryTooltip();
    }

    /// <summary>
    /// Ready → format accent. Recording/Pause → Stop's amber. Paused/Resume → green.
    /// </summary>
    private Color GetPrimaryFill()
    {
        if (!_isRecording)
            return _accent;
        if (_isPaused)
            return Color.FromArgb(255, 22, 163, 116);
        return DoneAccent;
    }

    private static Color Lighten(Color color, int amount) =>
        Color.FromArgb(
            255,
            (byte)Math.Min(255, color.R + amount),
            (byte)Math.Min(255, color.G + amount),
            (byte)Math.Min(255, color.B + amount));

    private static Color GetButtonTextColor(Color fill)
    {
        double luminance = (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) / 255.0;
        return luminance >= 0.58
            ? Color.FromRgb(7, 18, 28)
            : Colors.White;
    }

    private void SetPrimaryEnabled(bool enabled)
    {
        PrimaryBtn.IsEnabled = enabled;
        PrimaryBtn.Opacity = enabled ? 1.0 : 0.45;
    }

    // ── Stop button: always visible; red square icon; disabled in ready phase ──

    private void UpdateStopButtonVisual()
    {
        bool enabled = _isRecording && !_isEncoding;
        SetStopEnabled(enabled);
    }

    private void SetStopEnabled(bool enabled)
    {
        StopBtn.IsEnabled = enabled;
        StopBtn.Opacity = enabled ? 1.0 : 0.38;
        // Faint red wash when armed so the 40×40 hit target reads as a button.
        StopBtn.Background = enabled
            ? Theme.Brush(Color.FromArgb(42, StopRed.R, StopRed.G, StopRed.B))
            : System.Windows.Media.Brushes.Transparent;
        StopGlyph.Background = Theme.Brush(
            Color.FromArgb(enabled ? (byte)255 : (byte)170, StopRed.R, StopRed.G, StopRed.B));
    }

    private void UpdateFpsComboVisual()
    {
        FpsComboLabel.Text = $"{_fps} FPS";
        FpsComboLabel.Foreground = Theme.Brush(Theme.TextPrimary);
        FpsCombo.Background = Theme.Brush(ToMediaColor(UiChrome.SurfacePill));
        FpsCombo.BorderBrush = Theme.Brush(ToMediaColor(UiChrome.SurfaceBorderSubtle));
        FpsCombo.BorderThickness = new Thickness(1);
        FpsChevron.Fill = Theme.Brush(_accent);
    }

    private void SetFpsEnabled(bool enabled)
    {
        FpsCombo.IsEnabled = enabled;
        FpsCombo.Opacity = enabled ? 1.0 : 0.45;
        FpsCombo.Cursor = enabled
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        FpsCombo.Visibility = Visibility.Visible;
    }

    private void UpdateFormatBadge()
    {
        FormatBadgeText.Text = FormatLabel();

        Color fill;
        Color border;
        Color text;

        if (_isEncoding)
        {
            text = _accent;
            fill = Color.FromArgb(70, _accent.R, _accent.G, _accent.B);
            border = Color.FromArgb(150, _accent.R, _accent.G, _accent.B);
        }
        else if (!_isRecording)
        {
            text = _accent;
            fill = Color.FromArgb(40, _accent.R, _accent.G, _accent.B);
            border = Color.FromArgb(90, _accent.R, _accent.G, _accent.B);
        }
        else if (_isPaused)
        {
            var muted = Theme.TextMuted;
            text = muted;
            fill = Color.FromArgb(36, muted.R, muted.G, muted.B);
            border = Color.FromArgb(80, muted.R, muted.G, muted.B);
        }
        else
        {
            double pulse = UI.Motion.Disabled ? 1 : Math.Sin(Environment.TickCount / 250.0);
            float pa = (float)((pulse + 1.0) / 2.0);
            text = _accent;
            fill = Color.FromArgb((byte)(70 + 55 * pa), _accent.R, _accent.G, _accent.B);
            border = Color.FromArgb((byte)(120 + 80 * pa), _accent.R, _accent.G, _accent.B);
        }

        FormatBadgeText.Foreground = Theme.Brush(text);
        FormatBadge.Background = Theme.Brush(fill);
        FormatBadge.BorderBrush = Theme.Brush(border);
        FormatBadge.BorderThickness = new Thickness(1);
        UpdateFormatBadgeTooltip();
    }

    private string? _lastBadgeTipKey;

    private void UpdateFormatBadgeTooltip()
    {
        string stateKey;
        string stateText;
        if (_isEncoding)
        {
            stateKey = _format == Models.RecordingFormat.GIF ? "enc-gif" : "enc";
            stateText = LocalizationService.Translate(
                _format == Models.RecordingFormat.GIF ? "Encoding GIF..." : "Saving...");
        }
        else if (!_isRecording)
        {
            stateKey = "ready";
            stateText = LocalizationService.Translate("Recording ready");
        }
        else if (_isPaused)
        {
            stateKey = "paused";
            stateText = LocalizationService.Translate("Recording paused");
        }
        else
        {
            stateKey = "active";
            stateText = LocalizationService.Translate("Recording active");
        }

        if (stateKey == _lastBadgeTipKey)
            return;
        _lastBadgeTipKey = stateKey;
        FormatBadge.ToolTip = $"{FormatLabel()} · {stateText}";
    }

    private void UpdateStatusText()
    {
        if (_isEncoding)
        {
            StatusText.Text = _format == Models.RecordingFormat.GIF
                ? LocalizationService.Translate("Encoding GIF...")
                : LocalizationService.Translate("Saving...");
            StatusText.Foreground = Theme.Brush(Theme.TextMuted);
            StatusText.FontWeight = FontWeights.Normal;
            StatusText.FontSize = 13;
        }
        else
        {
            StatusText.Text = $"{(int)_elapsed.TotalMinutes:D2}:{_elapsed.Seconds:D2}";
            StatusText.Foreground = Theme.Brush(_isRecording ? Theme.TextPrimary : Theme.TextMuted);
            StatusText.FontWeight = FontWeights.Bold;
            StatusText.FontSize = 18;
        }
    }

    private void RefreshStorageMeter()
    {
        long used = TryReadOutputLength();
        if (used < 0)
            used = _lastUsedBytes >= 0 ? _lastUsedBytes : 0;

        long free = -1;
        try
        {
            if (_outputDrive is { IsReady: true })
                free = _outputDrive.AvailableFreeSpace;
        }
        catch
        {
            free = _lastFreeBytes;
        }

        // Free space jitters by kilobytes; snap so the label does not flicker.
        long freeCmp = free < 0 ? -1 : free & ~((1L << 20) - 1);
        if (used == _lastUsedBytes && freeCmp == _lastFreeBytes)
            return;
        _lastUsedBytes = used;
        _lastFreeBytes = freeCmp;

        long freeDisplay = freeCmp < 0 ? -1 : freeCmp;
        long usedDisplay = Math.Max(0, used);
        StorageText.Text = freeDisplay >= 0
            ? $"{FormatBytes(usedDisplay)} · {FormatBytes(freeDisplay)}"
            : FormatBytes(usedDisplay);

        bool lowDisk = freeDisplay >= 0 && freeDisplay < LowDiskWarnBytes;
        StorageText.Foreground = Theme.Brush(lowDisk ? StopRed : Theme.TextMuted);
        StorageText.Opacity = 1.0;
    }

    /// <summary>
    /// Size of a file another process is writing. FileShare.ReadWrite + handle Length
    /// tracks the writer's EOF; FileInfo.Length can lag on an open ffmpeg output.
    /// </summary>
    private long TryReadOutputLength()
    {
        if (_outputPath.Length == 0)
            return 0;
        try
        {
            using var fs = new FileStream(
                _outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return fs.Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
        catch
        {
            try
            {
                return new FileInfo(_outputPath).Length;
            }
            catch
            {
                return -1;
            }
        }
    }

    private static DriveInfo? TryGetDrive(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024)
            return $"{kb:0.0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return $"{mb:0.0} MB";
        double gb = mb / 1024.0;
        return gb < 10 ? $"{gb:0.00} GB" : $"{gb:0.0} GB";
    }

    // ══════════════════════════════════════════════════════════════
    //  Shine Animation (Start button)
    // ══════════════════════════════════════════════════════════════

    private void SetupShineAnimation()
    {
        // The shine is a TranslateTransform sweeping from left to right.
        // The gradient on StartShine is the visual; the transform animates its position.
        // The Border is wider than the button so it slides across cleanly.
    }

    private void StartShineAnimation()
    {
        if (UI.Motion.Disabled) return;

        StopShineAnimation();

        // The shine is a 60px-wide gradient band inside the PrimaryBtn's clipped grid.
        // We sweep its TranslateTransform.X from fully off-screen left to fully off-screen right.
        StartShine.Opacity = 1;
        StartShineTransform.X = -80;

        var animation = new DoubleAnimation
        {
            From = -80.0,
            To = 100.0,
            Duration = TimeSpan.FromMilliseconds(2600),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        _shineStoryboard = new Storyboard();
        _shineStoryboard.Children.Add(animation);
        Storyboard.SetTarget(animation, StartShineTransform);
        Storyboard.SetTargetProperty(animation, new PropertyPath("X"));
        _shineStoryboard.Begin();
    }

    private void StopShineAnimation()
    {
        _shineStoryboard?.Stop();
        _shineStoryboard = null;
        StartShine.Opacity = 0;
        StartShineTransform.X = -80;
    }

    // ══════════════════════════════════════════════════════════════
    //  FPS Menu
    // ══════════════════════════════════════════════════════════════

    private void ShowFpsMenu()
    {
        if (_isRecording || _isEncoding) return;

        _fpsMenu = new ContextMenu
        {
            Background = Theme.Brush(Theme.BgCard),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            HasDropShadow = true,
            PlacementTarget = FpsCombo,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        foreach (var option in GetFpsOptions(_format))
        {
            var item = new MenuItem
            {
                Header = $"{option} FPS",
                Foreground = Theme.Brush(Theme.TextPrimary),
                Background = System.Windows.Media.Brushes.Transparent,
                IsCheckable = false,
                IsChecked = _fps == option,
                FontSize = 12,
                FontWeight = _fps == option ? FontWeights.Bold : FontWeights.Normal,
                Padding = new Thickness(12, 6, 12, 6),
                MinWidth = 80,
            };

            int captured = option;
            item.Click += (_, _) => ApplyFps(captured);
            _fpsMenu.Items.Add(item);
        }

        _fpsMenu.IsOpen = true;
    }

    private void ApplyFps(int fps)
    {
        fps = NormalizeFps(_format, fps);
        if (_fps == fps) return;
        _fps = fps;
        FpsChanged?.Invoke(fps);
        UpdateFpsComboVisual();
    }

    /// <summary>
    /// GIF: 15 (default) / 30. Video: 15 / 24 / 30 / 60 (default 30).
    /// </summary>
    private static int[] GetFpsOptions(Models.RecordingFormat format) =>
        format == Models.RecordingFormat.GIF
            ? [15, 30]
            : [15, 24, 30, 60];

    private static int NormalizeFps(Models.RecordingFormat format, int fps)
    {
        var options = GetFpsOptions(format);
        if (Array.IndexOf(options, fps) >= 0)
            return fps;
        return format == Models.RecordingFormat.GIF ? 15 : 30;
    }

    // ══════════════════════════════════════════════════════════════
    //  Tooltips
    // ══════════════════════════════════════════════════════════════

    private void UpdateTooltips()
    {
        // FPS combo
        FpsCombo.ToolTip = string.Format(
            LocalizationService.Translate("Recording fps tooltip"), _fps);

        UpdatePrimaryTooltip();

        // Stop
        StopBtn.ToolTip = LocalizationService.Translate("Recording stop tooltip");

        StorageText.ToolTip = LocalizationService.Translate("Recording storage tooltip");
        _lastBadgeTipKey = null;
        UpdateFormatBadgeTooltip();

        // Trimmer
        UpdateTrimmerTooltip();

        // Cancel
        CancelBtn.ToolTip = LocalizationService.Translate(
            _isRecording ? "Recording discard tooltip" : "Recording cancel tooltip");
    }

    private void UpdatePrimaryTooltip()
    {
        string key;
        if (_isEncoding)
            key = _format == Models.RecordingFormat.GIF ? "Encoding GIF..." : "Saving...";
        else if (!_isRecording)
            key = "Recording start tooltip";
        else if (!_supportsPause)
            key = "Recording gif no pause tooltip";
        else if (_isPaused)
            key = "Recording resume tooltip";
        else
            key = "Recording pause tooltip";

        var text = LocalizationService.Translate(key);
        ToolTipService.SetToolTip(PrimaryBtn, null);
        ToolTipService.SetToolTip(PrimaryBtn, text);
    }

    private void UpdateTrimmerTooltip()
    {
        var tip = LocalizationService.Translate(_sendToTrimmer
            ? "Send to Trimmer is on"
            : "Send to Trimmer is off");
        tip += "\n" + LocalizationService.Translate(
            _format == Models.RecordingFormat.GIF
                ? "Open this GIF in the Trimmer when recording finishes"
                : "Open this video in the Trimmer when recording finishes");
        TrimmerBtn.ToolTip = tip;
    }

    // ══════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════

    private string FormatLabel() => _format switch
    {
        Models.RecordingFormat.MP4 => "MP4",
        _ => "GIF"
    };

    /// <summary>
    /// Thread-safe creation: builds the window on the WPF UI dispatcher if the caller
    /// is on a different thread (RecordingForm runs on a WinForms thread). WPF throws
    /// "cannot access Freezable because it is frozen" when constructing on the wrong thread.
    /// </summary>
    public static RecordingControlBarWindow Create(
        System.Drawing.Rectangle captureRegion,
        Models.RecordingFormat format,
        int fps,
        bool sendToTrimmer,
        System.Windows.Forms.Form ownerForm,
        string outputPath)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            var w = new RecordingControlBarWindow(captureRegion, format, fps, sendToTrimmer, outputPath);
            w.OwnerWinFormsForm = ownerForm;
            return w;
        }

        RecordingControlBarWindow? window = null;
        dispatcher.Invoke(() =>
        {
            window = new RecordingControlBarWindow(captureRegion, format, fps, sendToTrimmer, outputPath);
            window.OwnerWinFormsForm = ownerForm;
        });
        return window!;
    }

    /// <summary>Thread-safe Show: marshals to the window's own dispatcher.</summary>
    public void ShowSafely()
    {
        if (Dispatcher.CheckAccess()) { Show(); return; }
        Dispatcher.BeginInvoke(() =>
        {
            Show();
            // Re-assert topmost after Show so the bar stays above the overlay + trimmer.
            Dispatcher.BeginInvoke(() =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                        User32.SWP_NOSIZE | User32.SWP_NOMOVE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
            }, DispatcherPriority.Loaded);
        });
    }

    /// <summary>Thread-safe Close companion to Create.</summary>
    public void CloseSafely()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Close); return; }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopShineAnimation();
        _pulseTimer.Stop();
        _storageTimer.Stop();
        base.OnClosed(e);
    }
}
