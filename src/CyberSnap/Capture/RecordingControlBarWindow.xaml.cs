using System.Drawing;
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
    private static readonly Color DoneAccent = Color.FromArgb(255, 0xBD, 0x70, 0x11);
    private static readonly Color DoneAccentHover = Color.FromArgb(255, 0xD4, 0x82, 0x18);
    private static readonly Color CancelHoverColor = Color.FromArgb(255, 255, 80, 80);

    // ── Bar dimensions (100% DPI baseline, scaled via UiScale.LayoutTransform) ──
    private const double BarWidth = 598;
    private const double BarHeight = 64;

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

    // ── Timers ──
    private readonly DispatcherTimer _pulseTimer;
    private Storyboard? _shineStoryboard;

    // ── FPS menu ──
    private ContextMenu? _fpsMenu;

    // ── Positioning ──
    private System.Drawing.Rectangle _lastCaptureRegion;

    /// <summary>The WinForms RecordingForm that owns this bar; keeps the bar above the overlay.</summary>
    public System.Windows.Forms.Form? OwnerWinFormsForm { get; set; }

    /// <summary>True while the user is dragging/resizing the selection; bar hides to not obstruct.</summary>
    private bool _isDragInProgress;

    public RecordingControlBarWindow(
        System.Drawing.Rectangle captureRegion,
        Models.RecordingFormat format,
        int fps,
        bool sendToTrimmer)
    {
        _format = format;
        _fps = NormalizeFps(format, fps);
        _sendToTrimmer = sendToTrimmer;
        _supportsPause = format != Models.RecordingFormat.GIF;

        // Accent: GIF = orange, MP4 = theme accent
        _accent = format == Models.RecordingFormat.GIF
            ? Color.FromArgb(255, 140, 0, 255)   // orange for GIF
            : Theme.Accent;
        _accentHover = Color.FromArgb(
            255,
            (byte)Math.Min(255, _accent.R + 28),
            (byte)Math.Min(255, _accent.G + 28),
            (byte)Math.Min(255, _accent.B + 28));

        InitializeComponent();

        Width = BarWidth;
        Height = BarHeight;

        // ── Chrome setup ──
        Theme.Refresh();
        ConfigureShell();
        LoadIcons();
        HookHoverEffects();
        HookClickHandlers();

        // ── Rounded corners + no-activate + owner-window for z-order ──
        CyberSnapWindowChrome.ApplyRoundedCorners(this, 10);
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

        // ── Pulse timer for the recording dot ──
        _pulseTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(UiChrome.FrameIntervalMs)
        };
        _pulseTimer.Tick += (_, _) => UpdateRecordingDot();

        // ── Initial layout ──
        _lastCaptureRegion = captureRegion;
        UpdateLayoutVisibility();

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
        UpdateRecordingDot();
        UpdatePhaseLabel();
        UpdateStatusText();
    }

    public void TransitionToEncoding()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(TransitionToEncoding); return; }
        _isEncoding = true;
        _isPaused = false;
        StopShineAnimation();
        _pulseTimer.Stop();
        UpdateLayoutVisibility();
        UpdateRecordingDot();
        UpdatePhaseLabel();
        UpdateStatusText();
    }

    public void SetElapsed(TimeSpan elapsed)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetElapsed(elapsed)); return; }
        _elapsed = elapsed;
        if (_isRecording && !_isEncoding)
            UpdateStatusText();
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

        UpdateRecordingDot();
        UpdatePhaseLabel();
        UpdatePrimaryButtonVisual();
        UpdateStopButtonVisual();
    }

    public void Reposition(System.Drawing.Rectangle captureRegion)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Reposition(captureRegion)); return; }
        _lastCaptureRegion = captureRegion;
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

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // SWP_SHOWWINDOW + no NOZORDER flag → re-asserts HWND_TOPMOST each call,
            // so the bar stays above the fullscreen overlay even mid drag.
            User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, tx, ty, 0, 0,
                User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        }
        // If handle isn't ready, SourceInitialized re-invokes this and places it then.
    }

    // ══════════════════════════════════════════════════════════════
    //  Chrome / Visual Setup
    // ══════════════════════════════════════════════════════════════

    private void ConfigureShell()
    {
        // Bar background: semi-transparent dark (matches the GDI+ version's "mica" fill)
        Root.Background = Theme.Brush(Color.FromArgb(225, 12, 12, 16));

        // Edge ring: accent-tinted border
        EdgeRing.Background = Theme.Brush(Color.FromArgb(150, _accent.R, _accent.G, _accent.B));
        EdgeRing.Padding = new Thickness(1);

        // Shadow
        ShadowPlate.Background = Theme.Brush(Color.FromArgb(225, 12, 12, 16));
        ShadowPlate.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 4,
            Opacity = Theme.IsDark ? 0.42 : 0.20,
            Direction = 270,
            Color = Colors.Black,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
        };

        // Accent glow behind the bar
        OuterShell.Background = Theme.Brush(Color.FromArgb(25, _accent.R, _accent.G, _accent.B));
    }

    private void LoadIcons()
    {
        // Primary/Stop icons are set by UpdateLayoutVisibility() (UpdatePrimaryButtonVisual /
        // UpdateStopButtonVisual), which runs right after this in the constructor.
        UpdateTrimmerButtonIcon();
        UpdateCancelButtonIcon();
    }

    private static readonly System.Drawing.Color IconNormal =
        System.Drawing.Color.FromArgb(200, Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B);

    private void UpdateTrimmerButtonIcon()
    {
        var active = _sendToTrimmer;
        var c = active ? _accent : Theme.TextPrimary;
        var iconColor = System.Drawing.Color.FromArgb(active ? 255 : 200, c.R, c.G, c.B);
        TrimmerIcon.Source = FluentIcons.RenderWpf("filmstrip", iconColor, 32, active);
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
                PrimaryBtn.Background = Theme.Brush(_accentHover);
        };
        PrimaryBtn.MouseLeave += (_, _) =>
        {
            if (PrimaryBtn.IsEnabled)
                PrimaryBtn.Background = Theme.Brush(_accent);
        };

        // ── Stop button hover ──
        StopBtn.MouseEnter += (_, _) =>
        {
            if (StopBtn.IsEnabled)
            {
                StopBtn.Background = Theme.Brush(DoneAccentHover);
                var red = System.Drawing.Color.FromArgb(255, 255, 255, 255);
                StopIcon.Source = FluentIcons.RenderWpf("stopSquare", red, 32, active: true);
            }
        };
        StopBtn.MouseLeave += (_, _) =>
        {
            if (StopBtn.IsEnabled)
                SetStopEnabled(true); // restores red icon + transparent bg
        };

        // ── Trimmer button hover ──
        TrimmerBtn.MouseEnter += (_, _) =>
        {
            var c = _sendToTrimmer ? _accentHover : _accent;
            var iconColor = System.Drawing.Color.FromArgb(240, c.R, c.G, c.B);
            TrimmerIcon.Source = FluentIcons.RenderWpf("filmstrip", iconColor, 32, _sendToTrimmer);
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
            FpsCombo.Background = Theme.Brush(Color.FromArgb(90, _accent.R, _accent.G, _accent.B));
            FpsComboLabel.Foreground = Theme.Brush(Theme.TextPrimary);
        };
        FpsCombo.MouseLeave += (_, _) =>
        {
            FpsCombo.Background = Theme.Brush(Color.FromArgb(70, _accent.R, _accent.G, _accent.B));
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
        FpsCombo.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowFpsMenu(); };

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
            TrimmerBtn.IsEnabled = false;
            CancelBtn.IsEnabled = false;
            FpsCombo.Visibility = Visibility.Collapsed;
        }
        else if (!_isRecording)
        {
            // Ready phase: Record enabled + FPS + Trimmer + Cancel (Stop disabled)
            SetPrimaryEnabled(true);
            SetStopEnabled(false);
            TrimmerBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            FpsCombo.Visibility = Visibility.Visible;
        }
        else
        {
            // Recording phase: Pause/Resume enabled (MP4 only) + Stop + Trimmer + Cancel
            SetPrimaryEnabled(_supportsPause);
            SetStopEnabled(true);
            TrimmerBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            FpsCombo.Visibility = Visibility.Collapsed;
        }

        UpdatePrimaryButtonVisual();
        UpdateStopButtonVisual();
        UpdatePhaseLabel();
        UpdateStatusText();
        UpdateFpsComboVisual();
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

        PrimaryText.Text = label;
        PrimaryBtn.Background = Theme.Brush(_accent);
        PrimaryBtn.Cursor = _isEncoding ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;

        // Icon: white, rendered at 28 for crispness; only shown when primary is enabled
        var iconColor = System.Drawing.Color.FromArgb(255, 255, 255, 255);
        PrimaryIcon.Source = FluentIcons.RenderWpf(iconId, iconColor, 28);
        PrimaryIcon.Visibility = Visibility.Visible;
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
        StopBtn.Opacity = enabled ? 1.0 : 0.35;
        StopBtn.Background = System.Windows.Media.Brushes.Transparent;

        // Red square icon, bigger when enabled for accessibility
        int renderSize = enabled ? 32 : 24;
        int iconOpacity = enabled ? 255 : 140;
        var red = System.Drawing.Color.FromArgb(iconOpacity, 229, 72, 77);
        StopIcon.Source = FluentIcons.RenderWpf("stopSquare", red, renderSize, active: true);
    }

    private void UpdateFpsComboVisual()
    {
        FpsComboLabel.Text = $"{_fps} FPS";
        FpsCombo.Background = Theme.Brush(Color.FromArgb(70, _accent.R, _accent.G, _accent.B));
        FpsChevron.Fill = Theme.Brush(Color.FromArgb(180, _accent.R, _accent.G, _accent.B));
    }

    private void UpdateRecordingDot()
    {
        Color dotColor;
        Color glowColor;

        if (_isEncoding)
        {
            dotColor = Color.FromArgb(200, _accent.R, _accent.G, _accent.B);
            glowColor = Color.FromArgb(40, _accent.R, _accent.G, _accent.B);
        }
        else if (!_isRecording)
        {
            dotColor = Color.FromArgb(180, _accent.R, _accent.G, _accent.B);
            glowColor = Color.FromArgb(30, _accent.R, _accent.G, _accent.B);
        }
        else
        {
            var baseColor = _isPaused ? Theme.TextMuted : _accent;
            double pulse = _isPaused ? 0 : Math.Sin(Environment.TickCount / 250.0);
            float pa = (float)((pulse + 1.0) / 2.0);
            int dotAlpha = _isPaused ? 120 : (int)(200 + 55 * pa);
            int glowAlpha = _isPaused ? 20 : (int)(30 + 40 * pa);
            dotColor = Color.FromArgb((byte)dotAlpha, baseColor.R, baseColor.G, baseColor.B);
            glowColor = Color.FromArgb((byte)glowAlpha, baseColor.R, baseColor.G, baseColor.B);
        }

        RecDot.Background = Theme.Brush(dotColor);
        RecDotGlow.Background = Theme.Brush(glowColor);
    }

    private void UpdatePhaseLabel()
    {
        string label;
        Color labelColor;

        if (_isEncoding)
        {
            label = string.Empty; // no phase label during encoding
            labelColor = Theme.TextMuted;
        }
        else if (!_isRecording)
        {
            label = LocalizationService.Translate("Recording ready");
            labelColor = Color.FromArgb(220, _accent.R, _accent.G, _accent.B);
        }
        else if (_isPaused)
        {
            label = LocalizationService.Translate("Recording paused");
            labelColor = Color.FromArgb(220, Theme.TextMuted.R, Theme.TextMuted.G, Theme.TextMuted.B);
        }
        else
        {
            label = LocalizationService.Translate("Recording active");
            labelColor = Color.FromArgb(220, _accent.R, _accent.G, _accent.B);
        }

        PhaseLabel.Text = label;
        PhaseLabel.Foreground = Theme.Brush(labelColor);
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
            StatusText.FontSize = 11;
        }
        else if (_isRecording)
        {
            StatusText.Text = $"{(int)_elapsed.TotalMinutes:D2}:{_elapsed.Seconds:D2}";
            StatusText.Foreground = Theme.Brush(Theme.TextPrimary);
            StatusText.FontWeight = FontWeights.Bold;
            StatusText.FontSize = 13;
        }
        else
        {
            StatusText.Text = string.Format(
                LocalizationService.Translate("Recording ready hint"),
                FormatLabel(),
                _fps);
            StatusText.Foreground = Theme.Brush(Theme.TextMuted);
            StatusText.FontWeight = FontWeights.Normal;
            StatusText.FontSize = 11;
        }
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
        UpdateStatusText();
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

        // Trimmer
        UpdateTrimmerTooltip();

        // Cancel
        CancelBtn.ToolTip = LocalizationService.Translate(
            _isRecording ? "Recording discard tooltip" : "Recording cancel tooltip");
    }

    private void UpdatePrimaryTooltip()
    {
        // Primary button tooltip reflects its current mode
        PrimaryBtn.ToolTip = LocalizationService.Translate(
            !_isRecording ? "Recording start tooltip"
            : _isPaused ? "Recording resume tooltip"
            : "Recording pause tooltip");
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
        System.Windows.Forms.Form ownerForm)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            var w = new RecordingControlBarWindow(captureRegion, format, fps, sendToTrimmer);
            w.OwnerWinFormsForm = ownerForm;
            return w;
        }

        RecordingControlBarWindow? window = null;
        dispatcher.Invoke(() =>
        {
            window = new RecordingControlBarWindow(captureRegion, format, fps, sendToTrimmer);
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
        base.OnClosed(e);
    }
}
