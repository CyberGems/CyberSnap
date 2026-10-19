using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Linq;
using Microsoft.Win32;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow : Window
{
    private readonly SettingsService _settingsService;
    // Always read the live settings instance — App.Capture may replace
    // SettingsService.Settings with a fresh clone after each capture.
    private AppSettings _settings => _settingsService.Settings;
    private readonly DispatcherTimer _hoverDelayTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _postDragGraceTimer;
    private bool _isExpanded;
    private bool _isDragging;
    private bool _isDragArmed;
    private bool _suppressHoverExpand; // brief grace after a drag so it doesn't auto-expand under the resting cursor
    private bool _suppressCaptureCursorToggle; // reflecting ShowCursor into the toggle without re-saving/syncing
    private bool _contextMenuOpen;     // keep the widget from collapsing while its context menu is open
    private System.Windows.Forms.ContextMenuStrip? _contextMenu;
    private bool _moreMenuOpen;
    private bool _suppressMoreMouseDown;
    private System.Windows.Media.Effects.Effect? _panelShadow; // soft shadow, applied only when expanded
    private System.Windows.Point _dragStartPoint;
    private double _dragStartOffset;
    private bool _mouseInWindow;

    // Hide → launch → re-show lifecycle (capture overlay or standalone tool)
    private DispatcherTimer? _hideLaunchTimer;
    private readonly DispatcherTimer _sessionRestorePollTimer;
    private bool _awaitingSessionRestore;
    private bool _sessionRestoreInProgress;

    // Topmost watchdog: WPF sets WS_EX_TOPMOST once, but Windows can demote the HWND
    // within the topmost Z-band over time (other topmost windows, UAC/fullscreen,
    // Show desktop, sleep/resume, Hide→Show cycles). The managed Topmost property
    // stays true, so the user sees "activado pero no arriba". Re-assert natively.
    private readonly DispatcherTimer _topmostWatchdogTimer;

    // Layout constants
    private const double PanelWidth = 196;
    private const double PanelHeight = 248; // keep both panel dimensions aligned at common fractional display DPIs
    private const double PeekSize = 9; // slim peek (SnagIt-like), less intrusive than the old 16px

    // Transparent halo (DIPs) added around the content on every side so the panel's drop shadow
    // has room to render instead of being clipped by a content-sized window. The window grows by
    // 2x this; the content is inset by the same amount via RootGrid.Margin, so the visible widget
    // stays put. Drag-travel and hover hit-testing compensate for this margin.
    private const double ShadowMargin = 22;

    // Expand/collapse: short and snappy so the panel feels responsive, not ornamental.
    private const int ExpandAnimMs = 110;
    private const int CollapseAnimMs = 90;

    private int _layoutAnimGeneration;
    private bool _displayEventsHooked;

    public CaptureWidgetWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();
        SizeToContent = SizeToContent.Manual;

        // Inset the content so the transparent halo around it (window grown by ShadowMargin)
        // is free for the drop shadow to render into. The docked side gets no halo (see ShadowHalo).
        RootGrid.Margin = ShadowHalo(_settings.WidgetDockEdge);

        // The soft shadow looks great on the expanded panel but creates corner artifacts on the
        // thin peek, so we only apply it while expanded (toggled in PositionWindow).
        _panelShadow = MainPanelBorder.Effect;

        _hoverDelayTimer = new DispatcherTimer();
        _hoverDelayTimer.Tick += HoverDelayTimer_Tick;

        _collapseTimer = new DispatcherTimer();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(400);
        _collapseTimer.Tick += CollapseTimer_Tick;

        _postDragGraceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _postDragGraceTimer.Tick += (_, _) =>
        {
            _postDragGraceTimer.Stop();
            _suppressHoverExpand = false;
        };

        // The idle event is the fast path. Polling is a safety net for a narrow race where
        // the session can become idle while the widget's restore callback is being rewired.
        _sessionRestorePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _sessionRestorePollTimer.Tick += (_, _) =>
        {
            if (!_awaitingSessionRestore) return;
            if (System.Windows.Application.Current is App app && !app.IsSessionBusy())
                FinishSessionRestore();
        };

        _topmostWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _topmostWatchdogTimer.Tick += (_, _) =>
        {
            if (_settings.WidgetAlwaysOnTop && IsVisible)
                AssertWidgetTopmost();
        };
        _activeMonitorTimer.Tick += FollowActiveMonitorTick;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        SettingsService.AutoCopyPreferencesChanged += OnDeliveryPreferencesChanged;
        SettingsService.WidgetPreferencesChanged += OnWidgetPreferencesChanged;

        Topmost = _settings.WidgetAlwaysOnTop;

        LoadIcons();
        RefreshLayout();
        UpdateCaptureCursorState();
        LocalizationService.ApplyTo(this, _settings.InterfaceLanguage);
        RefreshUpdateBadge();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SettingsService.AutoCopyPreferencesChanged -= OnDeliveryPreferencesChanged;
        SettingsService.WidgetPreferencesChanged -= OnWidgetPreferencesChanged;
        var menu = _contextMenu;
        menu?.Close();
        menu?.Dispose();
        try { _topmostWatchdogTimer.Stop(); } catch { }
        _activeMonitorTimer.Stop();
        StopMonitorPointerObserver();
        CancelHideLaunch();
        CancelSessionRestore();
        UnhookDisplayEvents();
        StopLayoutAnimations();
    }

    /// <summary>
    /// Re-inserts the widget HWND at the top of the topmost Z-band without
    /// activating it. Setting <see cref="Window.Topmost"/> only takes effect when
    /// the value changes, so a widget that is already Topmost=true but was demoted
    /// in Z-order (other topmost window, Hide/Show, Show desktop, sleep/resume)
    /// needs this native re-assert. Mirrors RecordingControlBarWindow.AssertBarTopmost.
    /// </summary>
    public void AssertWidgetTopmost()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(AssertWidgetTopmost);
            return;
        }

        if (!_settings.WidgetAlwaysOnTop) return;
        if (!IsLoaded) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            Native.User32.SetWindowPos(hwnd, Native.User32.HWND_TOPMOST, 0, 0, 0, 0,
                Native.User32.SWP_NOMOVE | Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE);
        }
        catch { /* best-effort: watchdog retries */ }
    }

    /// <summary>
    /// Applies the settings value to both the WPF property and the native Z-order.
    /// Needed because assigning Topmost=true when it is already true is a no-op and
    /// would not restore a demoted HWND.
    /// </summary>
    public void SyncTopmostFromSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(SyncTopmostFromSettings);
            return;
        }

        Topmost = _settings.WidgetAlwaysOnTop;
        if (_settings.WidgetAlwaysOnTop)
            AssertWidgetTopmost();
    }

    private void HookDisplayEvents()
    {
        if (_displayEventsHooked) return;
        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _displayEventsHooked = true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("widget.display-events", ex.Message, ex);
        }
    }

    private void UnhookDisplayEvents()
    {
        if (!_displayEventsHooked) return;
        try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; }
        catch { /* best-effort on teardown */ }
        _displayEventsHooked = false;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // SystemEvents may fire off the UI thread.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || !_displayEventsHooked) return;
            _contextMenu?.Close();
            int previousIndex = _settings.WidgetMonitorIndex;
            ClampMonitorIndexToAvailableScreens();
            // Snap layout after resolution / monitor topology changes so the widget
            // never stays parked on a vanished display.
            PositionWindow();
            AssertWidgetTopmost();
            if (previousIndex == _settings.WidgetMonitorIndex)
                SettingsService.PublishWidgetPreferences(_settings);
        });
    }

    private void ClampMonitorIndexToAvailableScreens()
    {
        var screens = PopupWindowHelper.GetSortedScreens();
        var previousDevice = _configuredMonitorIndex == _settings.WidgetMonitorIndex ? _configuredMonitorDevice : null;
        int index = ResolveMonitorIndexAfterDisplayChange(_settings.WidgetMonitorIndex, previousDevice,
            screens.Select(screen => screen.DeviceName).ToArray());
        if (_settings.WidgetMonitorIndex != index)
        {
            _settings.WidgetMonitorIndex = index;
            try { _settingsService.Save(); }
            catch (Exception ex) { AppDiagnostics.LogWarning("widget.clamp-monitor", ex.Message, ex); }
            SettingsService.PublishWidgetPreferences(_settings);
        }
        RememberConfiguredMonitor(screens);
    }

    public void RefreshLocalization()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshLocalization);
            return;
        }
        LocalizationService.ApplyTo(this, _settings.InterfaceLanguage);
        RefreshUpdateBadge();
    }

    private void ApplyTheme()
    {
        Theme.Refresh();
        
        // Match the contextual menu surface in grayscale while preserving the established
        // CyberSnap dark/light backgrounds.
        var bg = Theme.IsGray
            ? Theme.BgSecondary
            : Theme.IsDark
                ? System.Windows.Media.Color.FromRgb(13, 15, 26)
                : System.Windows.Media.Color.FromRgb(223, 226, 234);
        var accent = Theme.Accent;
        var accentHover = Theme.IsGray ? System.Windows.Media.Color.FromRgb(214, 218, 224) : Theme.IsDark ? System.Windows.Media.Color.FromRgb(128, 255, 255) : System.Windows.Media.Color.FromRgb(50, 150, 240);
        var text = Theme.TextPrimary;
        var textMuted = Theme.TextSecondary;
        var border = Theme.IsGray ? System.Windows.Media.Color.FromArgb(32, 184, 190, 198) : Theme.IsDark ? System.Windows.Media.Color.FromArgb(32, 0, 255, 255) : System.Windows.Media.Color.FromArgb(22, 0, 0, 0);
        var borderActive = Theme.IsGray ? System.Windows.Media.Color.FromArgb(128, 184, 190, 198) : Theme.IsDark ? System.Windows.Media.Color.FromArgb(128, 0, 255, 255) : System.Windows.Media.Color.FromArgb(80, 0, 120, 215);
        var peekGripBg = Theme.IsGray ? System.Windows.Media.Color.FromArgb(38, 184, 190, 198) : Theme.IsDark ? System.Windows.Media.Color.FromArgb(38, 0, 255, 255) : System.Windows.Media.Color.FromArgb(38, 0, 120, 215);
        var hoverBg = Theme.IsGray ? System.Windows.Media.Color.FromArgb(21, 184, 190, 198) : Theme.IsDark ? System.Windows.Media.Color.FromArgb(21, 0, 255, 255) : System.Windows.Media.Color.FromArgb(21, 0, 120, 215);
        var pressedBg = Theme.IsGray ? System.Windows.Media.Color.FromArgb(48, 184, 190, 198) : Theme.IsDark ? System.Windows.Media.Color.FromArgb(48, 0, 255, 255) : System.Windows.Media.Color.FromArgb(48, 0, 120, 215);

        // Toggle Switch colors
        var toggleTrackBg = Theme.IsDark ? System.Windows.Media.Color.FromRgb(42, 42, 42) : System.Windows.Media.Color.FromRgb(224, 224, 224);
        var toggleTrackBorder = Theme.IsDark ? System.Windows.Media.Color.FromRgb(68, 68, 68) : System.Windows.Media.Color.FromRgb(204, 204, 204);
        var toggleThumbBg = Theme.IsDark ? System.Windows.Media.Color.FromRgb(136, 136, 136) : System.Windows.Media.Color.FromRgb(136, 136, 136);
        var toggleThumbHoverBg = Theme.IsDark ? System.Windows.Media.Color.FromRgb(170, 170, 170) : System.Windows.Media.Color.FromRgb(85, 85, 85);

        Resources["WidgetBg"] = new SolidColorBrush(bg);
        Resources["WidgetAccent"] = new SolidColorBrush(accent);
        Resources["WidgetAccentHover"] = new SolidColorBrush(accentHover);
        Resources["WidgetText"] = new SolidColorBrush(text);
        Resources["WidgetTextMuted"] = new SolidColorBrush(textMuted);
        Resources["WidgetBorder"] = new SolidColorBrush(border);
        Resources["WidgetBorderActive"] = new SolidColorBrush(borderActive);
        Resources["WidgetPeekGripBg"] = new SolidColorBrush(peekGripBg);
        Resources["WidgetButtonHoverBg"] = new SolidColorBrush(hoverBg);
        Resources["WidgetButtonPressedBg"] = new SolidColorBrush(pressedBg);
        Resources["WidgetToggleTrackBg"] = new SolidColorBrush(toggleTrackBg);
        Resources["WidgetToggleTrackBorder"] = new SolidColorBrush(toggleTrackBorder);
        Resources["WidgetToggleThumbBg"] = new SolidColorBrush(toggleThumbBg);
        Resources["WidgetToggleThumbHoverBg"] = new SolidColorBrush(toggleThumbHoverBg);
        Resources["WidgetAccentColor"] = accent;
        Resources["WidgetGlowOpacity"] = Theme.IsDark ? 0.6 : 0.35;
    }

    private ImageSource? _widgetLogoColor;
    private ImageSource? _widgetLogoMuted;

    private void LoadIcons()
    {
        Theme.Refresh();
        var accentColor = ToDrawingColor(Theme.Accent);
        var normalIconColor = ToDrawingColor(Theme.TextPrimary);

        string captureIconId = (_settings.DefaultCaptureMode == Models.CaptureMode.Center) ? "center" : "captureDot";
        BigCaptureIcon.Source = Helpers.FluentIcons.RenderWpf(captureIconId, accentColor, 20);

        ScrollCaptureIcon.Source = Helpers.FluentIcons.RenderWpf("scrollCapture", normalIconColor, 22);
        GrabTextIcon.Source = Helpers.FluentIcons.RenderWpf("ocr", normalIconColor, 22);
        QrScanIcon.Source = Helpers.FluentIcons.RenderWpf("scan", normalIconColor, 22);
        ColorPickerIcon.Source = Helpers.FluentIcons.RenderWpf("picker", normalIconColor, 22);
        ScreenRecordIcon.Source = Helpers.FluentIcons.RenderWpf("record", normalIconColor, 22); // record dot for MP4
        GifRecordIcon.Source = Helpers.FluentIcons.RenderWpf("recordGif", normalIconColor, 22); // GIF format icon
        RulerIcon.Source = Helpers.FluentIcons.RenderWpf("ruler", normalIconColor, 22); // Ruler tool icon
        MoreIcon.Source = Helpers.FluentIcons.RenderWpf("more", normalIconColor, 18);
        System.Windows.Automation.AutomationProperties.SetName(WidgetMoreButton,
            LocalizationService.Translate(_settings.InterfaceLanguage, "More"));

        _widgetLogoColor = ThemedLogo.Square(12);
        _widgetLogoMuted = ThemedLogo.SquareGrayscale(12);
        WidgetLogoImage.Source = _widgetLogoMuted;
        WidgetLogoImage.Opacity = 0.7;
        WidgetBrandText.Opacity = 0.65;
        WidgetBrandText.SetResourceReference(TextBlock.ForegroundProperty, "WidgetTextMuted");
    }

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color color) =>
        System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        PopupWindowHelper.ApplyNoActivateChrome(this);
        var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookDisplayEvents();
        ClampMonitorIndexToAvailableScreens();
        PositionWindow();
        AssertWidgetTopmost();
        try { _topmostWatchdogTimer.Start(); } catch { }
        _activeMonitorTimer.Start();
        // Template parts exist now: park both thumbs on their rest position
        // without playing the slide on startup.
        SnapToggleThumbs();
    }

    public void RefreshLayout()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshLayout);
            return;
        }

        ApplyTheme();
        UpdateCaptureCursorState();
        LoadIcons();
        RefreshUpdateBadge();

        // Tooltip reflects the configured default capture mode (area vs from-center).
        var lang = _settings.InterfaceLanguage;
        CaptureButton.ToolTip = _settings.DefaultCaptureMode == Models.CaptureMode.Center
            ? LocalizationService.Translate(lang, "Quick screenshot (From center)")
            : LocalizationService.Translate(lang, "Quick screenshot (Area Capture)");

        // Apply scaling
        UiScale.ApplyToWindow(this, RootGrid, scaleWindowBounds: false);

        var edge = _settings.WidgetDockEdge;

        if (!LayoutGrid.Children.Contains(MainPanelBorder))
        {
            LayoutGrid.Children.Add(MainPanelBorder);
        }

        MainPanelBorder.Width = PanelWidth;
        MainPanelBorder.Height = PanelHeight;
        MainPanelBorder.Margin = new Thickness(0);
        MainPanelBorder.Cursor = _isExpanded ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;
        MainPanelBorder.Visibility = Visibility.Visible;
        UpdatePeekGrip(edge);
        UpdateGripVisibility();

        PositionWindow();
    }

    private void UpdatePeekGrip(CaptureDockSide edge)
    {
        var horizontal = edge == CaptureDockSide.Top || edge == CaptureDockSide.Bottom;

        // The collapsed panel now owns its full outline instead of clipping a
        // full-height panel. Let the fill fit INSIDE that one-pixel border.
        PeekGrip.Width = double.NaN;
        PeekGrip.Height = double.NaN;
        PeekGrip.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        PeekGrip.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        PeekGrip.BorderThickness = new Thickness(0);
        // Round the interior-facing edge to match the panel's inner radius (8 minus the
        // 1px border = 7), so the teal fill seats cleanly inside the corner.
        // CornerRadius order is TopLeft, TopRight, BottomRight, BottomLeft.
        const double r = 7;
        PeekGrip.CornerRadius = edge switch
        {
            CaptureDockSide.Top => new CornerRadius(0, 0, r, r),    // free edge faces down
            CaptureDockSide.Bottom => new CornerRadius(r, r, 0, 0), // free edge faces up
            CaptureDockSide.Left => new CornerRadius(0, r, r, 0),   // free edge faces right
            CaptureDockSide.Right => new CornerRadius(r, 0, 0, r),  // free edge faces left
            _ => new CornerRadius(r),
        };

        // Center the pill within the activator bar so it renders as a fully rounded, visible pill.
        GripPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        GripPanel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        GripPanel.Margin = new Thickness(0);

        GripPill.Width = horizontal ? 24 : 3;
        GripPill.Height = horizontal ? 3 : 24;
        PeekGrip.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCaptureCursorState()
    {
        _suppressCaptureCursorToggle = true;
        try { CaptureCursorToggle.IsChecked = _settings.ShowCursor; }
        finally { _suppressCaptureCursorToggle = false; }
    }

    // Settings → widget: re-read ShowCursor so the toggle matches the Capture tab checkbox.
    public void RefreshCaptureCursorToggle() => UpdateCaptureCursorState();

    private const double WidgetToggleThumbTravel = 16.0;
    private const int WidgetToggleSlideMs = 140;
    private const int WidgetCapturePopMs = 120;
    private const int WidgetCaptureSettleMs = 180;

    /// <summary>
    /// Slides a WidgetToggleSwitch thumb with Motion so "Disable animations"
    /// snaps instantly instead of playing the old hardcoded XAML Storyboard.
    /// </summary>
    private void AnimateToggleThumb(System.Windows.Controls.Primitives.ToggleButton toggle, bool animate)
    {
        try
        {
            toggle.ApplyTemplate();
            var offset = toggle.Template.FindName("ThumbOffset", toggle) as TranslateTransform;
            if (offset is null)
                return;
            double target = toggle.IsChecked == true ? WidgetToggleThumbTravel : 0.0;
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            if (!animate || Motion.Disabled)
            {
                offset.X = target;
                return;
            }
            offset.BeginAnimation(TranslateTransform.XProperty,
                Motion.FromTo(offset.X, target, WidgetToggleSlideMs, Motion.SmoothOut));
        }
        catch { /* best-effort microanimation; checked colors still apply via template */ }
    }

    private void SnapToggleThumbs()
    {
        AnimateToggleThumb(CaptureCursorToggle, animate: false);
    }

    private void CaptureButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        AnimateCaptureButton(hovered: true);

    private void CaptureButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        AnimateCaptureButton(hovered: false);

    /// <summary>
    /// Hover pop for the big capture mark (ring + center + glow), Motion-aware.
    /// </summary>
    private void AnimateCaptureButton(bool hovered)
    {
        try
        {
            CaptureButton.ApplyTemplate();
            var ring = CaptureButton.Template.FindName("RingScale", CaptureButton) as ScaleTransform;
            var center = CaptureButton.Template.FindName("CenterScale", CaptureButton) as ScaleTransform;
            var glow = CaptureButton.Template.FindName("CenterGlow", CaptureButton) as System.Windows.Media.Effects.DropShadowEffect;
            if (ring is null || center is null)
                return;

            double ringTarget = hovered ? 1.06 : 1.0;
            double centerTarget = hovered ? 1.30 : 1.0;
            double blurTarget = hovered ? 22.0 : 10.0;
            double glowTarget = hovered ? 1.0 : 0.8;
            int ms = hovered ? WidgetCapturePopMs : WidgetCaptureSettleMs;

            if (Motion.Disabled)
            {
                ring.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ring.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                center.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                center.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ring.ScaleX = ring.ScaleY = ringTarget;
                center.ScaleX = center.ScaleY = centerTarget;
                if (glow is not null)
                {
                    glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
                    glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
                    glow.BlurRadius = blurTarget;
                    glow.Opacity = glowTarget;
                }
                return;
            }

            ring.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(ringTarget, ms, Motion.SmoothOut));
            ring.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(ringTarget, ms, Motion.SmoothOut));
            center.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(centerTarget, ms, Motion.SmoothOut));
            center.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(centerTarget, ms, Motion.SmoothOut));
            if (glow is not null)
            {
                glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, Motion.To(blurTarget, ms, Motion.SmoothOut));
                glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, Motion.To(glowTarget, ms, Motion.SmoothOut));
            }
        }
        catch { /* hover pop is decorative; ring highlight still applies via template */ }
    }

    /// <summary>
    /// Mirrors the tray menu's green update LED: visible next to the widget branding
    /// when a newer release was detected (App.LatestUpdateResult). Clicking the
    /// branding already opens About, same as the tray menu header.
    /// </summary>
    public void RefreshUpdateBadge()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshUpdateBadge);
            return;
        }

        try
        {
            var lang = _settings.InterfaceLanguage;
            bool updateAvailable = (System.Windows.Application.Current as App)?.LatestUpdateResult?.IsUpdateAvailable ?? false;
            UpdateLed.Visibility = updateAvailable ? Visibility.Visible : Visibility.Collapsed;
            if (updateAvailable)
            {
                UpdateLed.ToolTip = LocalizationService.Translate(lang, "Update available");
                WidgetBrandingBtn.ToolTip = $"{LocalizationService.Translate(lang, "Open About CyberSnap")} ({LocalizationService.Translate(lang, "Update available")})";
            }
            else
            {
                UpdateLed.ToolTip = null;
                WidgetBrandingBtn.ToolTip = LocalizationService.Translate(lang, "Open About CyberSnap");
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("widget.update-badge", ex.Message, ex);
        }
    }

    /// <summary>
    /// Transparent shadow halo per side. The docked side gets ZERO halo so the window sits flush
    /// against the screen edge: the peek's interactive edge then lands exactly on the screen-edge
    /// pixel (where Windows clamps a flung cursor) instead of on the content/halo boundary, which
    /// hit-tests unreliably and was eating the hover/click/hand-cursor right at the edge. The
    /// shadow on the docked side isn't visible anyway — it falls against the screen bezel.
    /// </summary>
    private static Thickness ShadowHalo(CaptureDockSide edge) => edge switch
    {
        // Thickness order: left, top, right, bottom.
        CaptureDockSide.Left => new Thickness(0, ShadowMargin, ShadowMargin, ShadowMargin),
        CaptureDockSide.Right => new Thickness(ShadowMargin, ShadowMargin, 0, ShadowMargin),
        CaptureDockSide.Top => new Thickness(ShadowMargin, 0, ShadowMargin, ShadowMargin),
        CaptureDockSide.Bottom => new Thickness(ShadowMargin, ShadowMargin, ShadowMargin, 0),
        _ => new Thickness(ShadowMargin),
    };

    public void PositionWindow()
        => PositionWindowOnScreen(ResolvePlacementScreen(PopupWindowHelper.GetSortedScreens()));

    private void PositionWindowAtCursor(System.Drawing.Point cursor)
        => PositionWindowOnScreen(ResolveTargetScreen(PopupWindowHelper.GetSortedScreens(), _settings.WidgetMonitorIndex, cursor));

    private void PositionWindowOnScreen(Screen targetScreen)
    {
        if (!IsLoaded) return;
        if (!TryGetWindowPlacement(_isExpanded, out var left, out var top, out var width, out var height, out var halo, targetScreen))
            return;

        ApplyWindowPlacement(left, top, width, height, halo, _isExpanded, animate: false);
    }

    /// <summary>
    /// Ensures the HWND is on the target monitor (for correct per-monitor DPI) and computes
    /// the window placement for the current dock edge/offset, including the shadow halo.
    /// </summary>
    private bool TryGetWindowPlacement(
        bool expanded,
        out double left,
        out double top,
        out double width,
        out double height,
        out Thickness halo,
        Screen? targetScreen = null)
    {
        left = top = width = height = 0;
        halo = default;
        if (!IsLoaded) return false;

        targetScreen ??= ResolvePlacementScreen(PopupWindowHelper.GetSortedScreens());
        _placementMonitorDevice = targetScreen.DeviceName;

        // Move the HWND first, then map physical pixels through its rendering context.
        // Only jump when the window is actually on a different monitor: PositionWindow runs
        // every frame while dragging, and an unconditional corner-jump leaves ghost trails
        // on this transparent topmost window.
        var phys = GetPhysicalWorkingArea(targetScreen);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var windowMonitor = Native.User32.MonitorFromWindow(hwnd, Native.User32.MONITOR_DEFAULTTONEAREST);
            var targetMonitor = Native.User32.MonitorFromPoint(
                new Native.User32.POINT(phys.X, phys.Y), Native.User32.MONITOR_DEFAULTTONEAREST);
            if (windowMonitor != targetMonitor)
            {
                Native.User32.SetWindowPos(hwnd, IntPtr.Zero, phys.X, phys.Y, 0, 0,
                    Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE | Native.User32.SWP_NOZORDER);
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        // Window.Left/Top use desktop coordinates in the window's render DIPs.
        // A relative PointFromScreen conversion can observe the native move before
        // WPF updates Left/Top on .NET 10, shifting placement on mixed-DPI monitors.
        var workingArea = new Rect(
            phys.Left / dpi.DpiScaleX,
            phys.Top / dpi.DpiScaleY,
            phys.Width / dpi.DpiScaleX,
            phys.Height / dpi.DpiScaleY);

        var targetDpi = PopupWindowHelper.GetScaleForPoint(new System.Drawing.Point(phys.X + phys.Width / 2, phys.Y + phys.Height / 2));
        double correctionX = targetDpi.X / dpi.DpiScaleX;
        double correctionY = targetDpi.Y / dpi.DpiScaleY;
        // A programmatic monitor move can update the HWND's DPI before WPF updates
        // its visual/render DPI. Compensate only for that difference. Once WPF adopts
        // the new DPI, OnDpiChanged repositions us and these factors become 1 again.
        RootGrid.LayoutTransform = new ScaleTransform(UiScale.Current * correctionX, UiScale.Current * correctionY);
        var localBounds = CalculateWidgetBounds(
            new Rect(0, 0, phys.Width / targetDpi.X, phys.Height / targetDpi.Y),
            _settings.WidgetDockEdge,
            _settings.WidgetDockPositionOffset,
            expanded,
            UiScale.Current);
        var bounds = new Rect(workingArea.Left + localBounds.Left * correctionX,
            workingArea.Top + localBounds.Top * correctionY,
            localBounds.Width * correctionX, localBounds.Height * correctionY);

        // Grow the window by the shadow halo; the content is inset by the same amounts
        // (RootGrid.Margin), so the visible widget lands exactly on 'bounds'. The docked side has
        // no halo (ShadowHalo), so that edge sits flush against the screen and the peek stays
        // interactive right at the screen-edge pixel.
        halo = ShadowHalo(_settings.WidgetDockEdge);
        halo = new Thickness(halo.Left * correctionX, halo.Top * correctionY, halo.Right * correctionX, halo.Bottom * correctionY);
        double requestedWidth = bounds.Width + halo.Left + halo.Right;
        double requestedHeight = bounds.Height + halo.Top + halo.Bottom;
        // Native window sizes use whole pixels. Reserve the full outer pixel so
        // fractional UI scales cannot round the client area inside the panel border.
        width = Math.Ceiling(requestedWidth * dpi.DpiScaleX) / dpi.DpiScaleX;
        height = Math.Ceiling(requestedHeight * dpi.DpiScaleY) / dpi.DpiScaleY;
        left = bounds.Left - halo.Left - (_settings.WidgetDockEdge == CaptureDockSide.Right ? width - requestedWidth : 0);
        top = bounds.Top - halo.Top - (_settings.WidgetDockEdge == CaptureDockSide.Bottom ? height - requestedHeight : 0);
        return true;
    }

    private void ApplyWindowPlacement(
        double left,
        double top,
        double width,
        double height,
        Thickness halo,
        bool expanded,
        bool animate)
    {
        StopLayoutAnimations();
        RootGrid.Margin = halo;

        if (!animate || Motion.Disabled)
        {
            ControlsGrid.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            MainPanelBorder.Effect = expanded ? _panelShadow : null;
            UpdateMainPanelBorderAlignment();
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            return;
        }

        // Expand: show chrome immediately. Collapse: keep it painted until the shrink ends
        // so the panel never flashes empty mid-animation.
        if (expanded)
        {
            ControlsGrid.Visibility = Visibility.Visible;
            MainPanelBorder.Effect = _panelShadow;
            UpdateMainPanelBorderAlignment();
        }

        var fromLeft = Left;
        var fromTop = Top;
        var fromWidth = Width;
        var fromHeight = Height;
        var ms = expanded ? ExpandAnimMs : CollapseAnimMs;
        var gen = ++_layoutAnimGeneration;
        var pending = 0;

        void Finish()
        {
            if (gen != _layoutAnimGeneration) return;
            // Freeze final values so drag/hover can write Left/Top freely again.
            StopLayoutAnimations();
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            if (!expanded)
            {
                ControlsGrid.Visibility = Visibility.Collapsed;
                MainPanelBorder.Effect = null;
                UpdateMainPanelBorderAlignment();
            }
        }

        // Keep starting geometry; animate toward the target. Animating Left/Width together
        // on the free edge produces a natural "slide out of the dock" motion.
        void Start(DependencyProperty property, double from, double to)
        {
            if (Math.Abs(from - to) < 0.25)
            {
                SetValue(property, to);
                return;
            }

            pending++;
            var anim = Motion.FromTo(from, to, ms, Motion.SoftOut);
            anim.FillBehavior = FillBehavior.Stop;
            anim.Completed += (_, _) =>
            {
                if (gen != _layoutAnimGeneration) return;
                pending--;
                if (pending <= 0) Finish();
            };
            BeginAnimation(property, anim);
        }

        Start(LeftProperty, fromLeft, left);
        Start(TopProperty, fromTop, top);
        Start(WidthProperty, fromWidth, width);
        Start(HeightProperty, fromHeight, height);

        if (pending == 0)
            Finish();
    }

    private void StopLayoutAnimations()
    {
        _layoutAnimGeneration++;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        SlideTransform.X = 0;
        SlideTransform.Y = 0;
    }

    private void UpdateMainPanelBorderAlignment()
    {
        var edge = _settings.WidgetDockEdge;
        bool horizontal = edge is CaptureDockSide.Top or CaptureDockSide.Bottom;
        MainPanelBorder.Width = _isExpanded || horizontal ? PanelWidth : PeekSize;
        MainPanelBorder.Height = _isExpanded || !horizontal ? PanelHeight : PeekSize;
        MainPanelBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        MainPanelBorder.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        MainPanelBorder.BorderThickness = _isExpanded ? new Thickness(1) : edge switch
        {
            CaptureDockSide.Top => new Thickness(1, 0, 1, 1),
            CaptureDockSide.Bottom => new Thickness(1, 1, 1, 0),
            CaptureDockSide.Left => new Thickness(0, 1, 1, 1),
            CaptureDockSide.Right => new Thickness(1, 1, 0, 1),
            _ => new Thickness(1)
        };
        TopDragStrip.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        BottomDragStrip.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateGripVisibility();
        MainPanelBorder.CornerRadius = _isExpanded ? new CornerRadius(8) : edge switch
        {
            CaptureDockSide.Top => new CornerRadius(0, 0, 8, 8),
            CaptureDockSide.Bottom => new CornerRadius(8, 8, 0, 0),
            CaptureDockSide.Left => new CornerRadius(0, 8, 8, 0),
            CaptureDockSide.Right => new CornerRadius(8, 0, 0, 8),
            _ => new CornerRadius(8)
        };
    }

    internal static Rect CalculateWidgetBounds(
        Rect workingArea,
        CaptureDockSide dockEdge,
        double dockOffset,
        bool isExpanded,
        double uiScale)
    {
        var scale = UiScale.Normalize(uiScale);
        var offset = Math.Clamp(double.IsFinite(dockOffset) ? dockOffset : 0.5, 0.0, 1.0);
        var peekSize = PeekSize * scale;
        var panelWidth = PanelWidth * scale;
        var panelHeight = PanelHeight * scale;

        double width, height;
        if (isExpanded)
        {
            width = panelWidth;
            height = panelHeight;
        }
        else
        {
            if (dockEdge == CaptureDockSide.Left || dockEdge == CaptureDockSide.Right)
            {
                width = peekSize;
                height = panelHeight;
            }
            else
            {
                width = panelWidth;
                height = peekSize;
            }
        }

        var horizontalTravel = Math.Max(0, workingArea.Width - panelWidth);
        var verticalTravel = Math.Max(0, workingArea.Height - panelHeight);

        return dockEdge switch
        {
            CaptureDockSide.Top => new Rect(
                workingArea.Left + horizontalTravel * offset,
                workingArea.Top,
                width,
                height),
            CaptureDockSide.Bottom => new Rect(
                workingArea.Left + horizontalTravel * offset,
                workingArea.Bottom - height,
                width,
                height),
            CaptureDockSide.Left => new Rect(
                workingArea.Left,
                workingArea.Top + verticalTravel * offset,
                width,
                height),
            CaptureDockSide.Right => new Rect(
                workingArea.Right - width,
                workingArea.Top + verticalTravel * offset,
                width,
                height),
            _ => new Rect(
                workingArea.Left + horizontalTravel * offset,
                workingArea.Top,
                width,
                height),
        };
    }


    private static Screen ResolveTargetScreen(Screen[] screens, int requestedIndex, System.Drawing.Point cursor)
    {
        if (requestedIndex >= 0 && requestedIndex < screens.Length)
        {
            return screens[requestedIndex];
        }

        var cursorScreen = Screen.FromPoint(cursor);
        return screens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, cursorScreen.DeviceName, StringComparison.OrdinalIgnoreCase))
            ?? screens.FirstOrDefault()
            ?? cursorScreen;
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouseInWindow = true;
        _collapseTimer.Stop(); // Cancel scheduled collapse
        ActivatorSurface_MouseEnter(sender, e);
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouseInWindow = false;
        ActivatorSurface_MouseLeave(sender, e);
        
        if (_isExpanded && !_isDragging && !_contextMenuOpen)
        {
            _collapseTimer.Start();
        }
    }

    private void ActivatorSurface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isExpanded || _isDragging || _isDragArmed || _suppressHoverExpand) return;

        _hoverDelayTimer.Interval = TimeSpan.FromMilliseconds(_settings.WidgetHoverDelayMs);
        _hoverDelayTimer.Start();
    }

    private void ActivatorSurface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverDelayTimer.Stop();
    }

    private void HoverDelayTimer_Tick(object? sender, EventArgs e)
    {
        _hoverDelayTimer.Stop();
        if (_isDragArmed || _isDragging || _suppressHoverExpand) return;
        if (_mouseInWindow && !_isExpanded && IsMouseOverWidgetWithPadding())
        {
            ExpandWidget();
        }
    }

    private bool IsMouseOverWidgetWithPadding()
        => Native.User32.GetCursorPos(out var point)
            && IsPointOverWidgetWithPadding(new System.Drawing.Point(point.X, point.Y));

    private bool IsPointOverWidgetWithPadding(System.Drawing.Point pt)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;

            if (!Native.User32.GetWindowRect(hwnd, out var rect)) return false;
            // The window includes a transparent shadow halo; test against the visible content by
            // deflating the rect by the per-side halo (converted to physical pixels for this
            // monitor). The docked side has no halo, so it deflates by 0 there.
            var dpi = VisualTreeHelper.GetDpi(this);
            var h = RootGrid.Margin;
            int haloL = (int)Math.Round(h.Left * dpi.DpiScaleX);
            int haloR = (int)Math.Round(h.Right * dpi.DpiScaleX);
            int haloT = (int)Math.Round(h.Top * dpi.DpiScaleY);
            int haloB = (int)Math.Round(h.Bottom * dpi.DpiScaleY);

            const int padding = 12; // 12 physical pixels safety padding
            return pt.X >= rect.Left + haloL - padding && pt.X <= rect.Right - haloR + padding &&
                   pt.Y >= rect.Top + haloT - padding && pt.Y <= rect.Bottom - haloB + padding;
        }
        catch
        {
            return false;
        }
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (!_mouseInWindow && _isExpanded && !_isDragging && !_contextMenuOpen)
        {
            if (!IsMouseOverWidgetWithPadding())
            {
                CollapseWidget();
            }
        }
    }

    private void CheckMouseLeaveWidget()
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_mouseInWindow && _isExpanded && !_isDragging && !_contextMenuOpen)
            {
                if (!IsMouseOverWidgetWithPadding())
                {
                    CollapseWidget();
                }
            }
        }), DispatcherPriority.Background);
    }

    private void ExpandWidget()
    {
        if (_isExpanded) return;
        _isExpanded = true;

        // Re-sync toggles from live settings (SettingsService.Settings may have been
        // replaced while the peek was collapsed).
        UpdateCaptureCursorState();
        RefreshUpdateBadge();

        MainPanelBorder.Cursor = System.Windows.Input.Cursors.Arrow;
        MainPanelBorder.Visibility = Visibility.Visible;
        UpdateGripVisibility();

        if (!TryGetWindowPlacement(expanded: true, out var left, out var top, out var width, out var height, out var halo))
        {
            PositionWindow();
            return;
        }

        ApplyWindowPlacement(left, top, width, height, halo, expanded: true, animate: true);
    }

    private void CollapseWidget()
    {
        if (!_isExpanded) return;

        _isExpanded = false;
        MainPanelBorder.Visibility = Visibility.Visible;
        MainPanelBorder.Cursor = System.Windows.Input.Cursors.Hand;
        UpdateGripVisibility();

        if (!TryGetWindowPlacement(expanded: false, out var left, out var top, out var width, out var height, out var halo))
        {
            PositionWindow();
            return;
        }

        // Keep controls painted until the shrink finishes so the panel doesn't flash empty.
        ApplyWindowPlacement(left, top, width, height, halo, expanded: false, animate: true);
    }

    public void CollapseImmediately()
    {
        _isExpanded = false;
        MainPanelBorder.Visibility = Visibility.Visible;
        MainPanelBorder.Cursor = System.Windows.Input.Cursors.Hand;
        StopLayoutAnimations();
        PositionWindow();
        UpdateGripVisibility();
    }

    private void UpdateGripVisibility()
    {
        PeekGrip.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragArmed && !_isDragging)
        {
            var current = System.Windows.Forms.Control.MousePosition;
            var dpi = _dragDpi;
            if (Math.Abs(current.X - _dragStartPoint.X) / dpi.DpiScaleX >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(current.Y - _dragStartPoint.Y) / dpi.DpiScaleY >= SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;
            }
        }

        if (_isDragging)
            UpdateDragPosition(System.Windows.Forms.Control.MousePosition);
    }

    /// <summary>
    /// Pick the nearest dock edge under the cursor, with a proximity zone and hysteresis so
    /// the widget does not thrash between edges at corners.
    /// </summary>
    private static CaptureDockSide ResolvePreferredDockEdge(
        Rect workingArea,
        System.Windows.Point cursor,
        CaptureDockSide current)
    {
        double dTop = cursor.Y - workingArea.Top;
        double dBottom = workingArea.Bottom - cursor.Y;
        double dLeft = cursor.X - workingArea.Left;
        double dRight = workingArea.Right - cursor.X;

        double Dist(CaptureDockSide edge) => edge switch
        {
            CaptureDockSide.Top => dTop,
            CaptureDockSide.Bottom => dBottom,
            CaptureDockSide.Left => dLeft,
            CaptureDockSide.Right => dRight,
            _ => dTop,
        };

        var best = CaptureDockSide.Top;
        var bestDist = dTop;
        void Consider(CaptureDockSide edge, double dist)
        {
            if (dist < bestDist)
            {
                best = edge;
                bestDist = dist;
            }
        }
        Consider(CaptureDockSide.Bottom, dBottom);
        Consider(CaptureDockSide.Left, dLeft);
        Consider(CaptureDockSide.Right, dRight);

        if (best == current) return current;

        // Zone scales with the smaller working-area side (clamped for tiny/huge screens).
        double zone = Math.Clamp(Math.Min(workingArea.Width, workingArea.Height) * 0.08, 40, 80);
        if (bestDist > zone) return current;

        // Stay put unless the new edge is clearly closer (corner hysteresis).
        const double hysteresis = 24;
        if (bestDist + hysteresis >= Dist(current)) return current;

        return best;
    }

    private static double OffsetAlongEdgeFromCursor(
        Rect workingArea,
        CaptureDockSide edge,
        System.Windows.Point cursor)
    {
        var scale = UiScale.Normalize(UiScale.Current);
        var panelWidth = PanelWidth * scale;
        var panelHeight = PanelHeight * scale;
        // Use the expanded panel footprint for offset so the grip lands under the cursor
        // even when the widget is still collapsed (peek-sized).
        if (edge == CaptureDockSide.Top || edge == CaptureDockSide.Bottom)
        {
            var travel = Math.Max(0, workingArea.Width - panelWidth);
            if (travel <= 0) return 0.5;
            return Math.Clamp((cursor.X - workingArea.Left - panelWidth / 2) / travel, 0.0, 1.0);
        }

        var vTravel = Math.Max(0, workingArea.Height - panelHeight);
        if (vTravel <= 0) return 0.5;
        return Math.Clamp((cursor.Y - workingArea.Top - panelHeight / 2) / vTravel, 0.0, 1.0);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // Arm a drag whether collapsed or expanded. When expanded, the toolbar buttons mark
        // their own clicks Handled, so this window-level handler only fires for presses on the
        // panel background — letting the user drag an (even accidentally) expanded widget without
        // swallowing button clicks. Drag vs click is still decided by the movement threshold.
        if (!_isDragging)
        {
            ArmWidgetDrag(e);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging || _isDragArmed)
        {
            var wasDragging = _isDragging;
            _isDragging = false;
            _isDragArmed = false;
            ReleaseMouseCapture();
            Cursor = null;
            Mouse.OverrideCursor = null;
            ForceCursor = false;
            if (wasDragging)
            {
                _settingsService.Save();
                SettingsService.PublishWidgetPreferences(_settings);
                ResetActiveMonitorAfterDrag();
                BeginPostDragGrace();
            }
            else if (!_isExpanded)
            {
                ExpandWidget();
            }

            CheckMouseLeaveWidget();
        }
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_isDragging || _isDragArmed)
        {
            var wasDragging = _isDragging;
            _isDragging = false;
            _isDragArmed = false;
            Cursor = null;
            Mouse.OverrideCursor = null;
            ForceCursor = false;
            if (wasDragging)
            {
                _settingsService.Save();
                SettingsService.PublishWidgetPreferences(_settings);
                ResetActiveMonitorAfterDrag();
                BeginPostDragGrace();
            }
            CheckMouseLeaveWidget();
        }
    }

    private void ArmWidgetDrag(MouseButtonEventArgs e)
    {
        _hoverDelayTimer.Stop();
        _isDragArmed = true;
        var cursor = System.Windows.Forms.Control.MousePosition;
        _dragStartPoint = new System.Windows.Point(cursor.X, cursor.Y);
        _dragStartOffset = _settings.WidgetDockPositionOffset;
        RememberDragMonitor(ResolveTargetScreen(PopupWindowHelper.GetSortedScreens(), _settings.WidgetMonitorIndex, cursor));
        CaptureMouse();
        Cursor = System.Windows.Input.Cursors.SizeAll;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeAll;
        ForceCursor = true;
        e.Handled = true;
    }

    /// <summary>Suppress hover auto-expand briefly after a drag so the widget doesn't pop open
    /// under the cursor while the user repositions their hand.</summary>
    private void BeginPostDragGrace()
    {
        _suppressHoverExpand = true;
        _hoverDelayTimer.Stop();
        _postDragGraceTimer.Stop();
        _postDragGraceTimer.Start();
    }

    // Context Menu options (right click handle)
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ShowWidgetContextMenu();
    }

    private void ShowWidgetContextMenu()
    {
        _settingsService.RefreshWidgetPreferences();
        // Built with WindowsMenuRenderer so it is pixel-identical to the tray icon menu
        // (dark rounded surface, accent hover bar) instead of the default WPF look.
        var menu = Helpers.WindowsMenuRenderer.Create(showImages: true, minWidth: 220);

        menu.Items.Add(BuildPositionSubmenu());

        var screenMenu = BuildScreenSubmenu();
        if (screenMenu != null)
            menu.Items.Add(screenMenu);

        menu.Items.Add(BuildActivationDelaySubmenu());

        menu.Items.Add(BuildWidgetAlwaysOnTopItem());

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        menu.Items.Add(BuildDisableItem());

        Helpers.WindowsMenuRenderer.NormalizeItemWidths(menu, minWidth: 220);

        ShowThemedMenu(menu);
    }

    // More menu: widget options, delivery by mode, editor, configuration and help.
    private System.Windows.Forms.ContextMenuStrip BuildMoreMenu()
    {
        _settingsService.RefreshResultDeliveryPreferences();
        _settingsService.RefreshWidgetPreferences();
        var menu = Helpers.WindowsMenuRenderer.Create(showImages: true, minWidth: 240);

        // "Widget" submenu mirrors the right-click context menu options.
        var widgetMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Widget"), showImages: true, iconId: "activeWindow");
        widgetMenu.DropDownItems.Add(BuildPositionSubmenu());

        var screenMenu = BuildScreenSubmenu();
        if (screenMenu != null)
            widgetMenu.DropDownItems.Add(screenMenu);
        widgetMenu.DropDownItems.Add(BuildActivationDelaySubmenu());
        widgetMenu.DropDownItems.Add(BuildWidgetAlwaysOnTopItem());
        widgetMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        widgetMenu.DropDownItems.Add(BuildDisableItem());
        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(widgetMenu, minWidth: 200);
        menu.Items.Add(widgetMenu);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(BuildImageCaptureSubmenu());
        menu.Items.Add(BuildDeliverySubmenu("Scrolling capture", AutoCopyKind.Scroll));
        menu.Items.Add(BuildDeliverySubmenu("Video (MP4)", AutoCopyKind.Video));
        menu.Items.Add(BuildDeliverySubmenu("GIF", AutoCopyKind.Gif));
        menu.Items.Add(BuildDeliverySubmenu("OCR", AutoCopyKind.Ocr));
        menu.Items.Add(BuildDeliverySubmenu("Color picker", AutoCopyKind.Color));
        menu.Items.Add(BuildDeliverySubmenu("QR and Barcodes", AutoCopyKind.Scan));

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // Open the annotation editor directly from the widget
        var editorItem = Helpers.WindowsMenuRenderer.Item(
            LocalizationService.Translate("Annotations Editor..."), iconId: "compose");
        editorItem.Click += (s, ev) =>
        {
            CollapseWidget();
            UI.Editor.EditorForm.ShowEditorEmptyOrPrompt();
        };
        menu.Items.Add(editorItem);

        // Jump straight to the widget's section in the Config window.
        var settingsItem = Helpers.WindowsMenuRenderer.Item(LocalizationService.Translate("Configuration..."), iconId: "gear");
        settingsItem.Click += (s, ev) =>
        {
            ((App)System.Windows.Application.Current).ShowSettings("widget");
            CollapseWidget();
        };
        menu.Items.Add(settingsItem);

        // ── Help submenu: wiki / homepage / updates / about ──
        var helpSubmenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Help"), showImages: true, iconId: "question");

        var wikiItem = Helpers.WindowsMenuRenderer.Item(
            LocalizationService.Translate("Wiki: Capture..."), iconId: "question");
        wikiItem.ToolTipText = LocalizationService.Translate("Open the Capture page in the CyberSnap wiki.");
        wikiItem.Click += (s, ev) =>
        {
            CollapseWidget();
            WikiLinks.Open(WikiLinks.CapturePage);
        };

        var homepageItem = Helpers.WindowsMenuRenderer.Item(LocalizationService.Translate("CyberGems Website..."), iconId: "home");
        homepageItem.ToolTipText = LocalizationService.Translate("Visit CyberGems website");
        homepageItem.Click += (s, ev) => WikiLinks.OpenUrl(WikiLinks.HomepageUrl);

        var updatesItem = Helpers.WindowsMenuRenderer.Item(LocalizationService.Translate("Check for Updates..."), iconId: "redo");
        updatesItem.ToolTipText = LocalizationService.Translate("Check for the latest version");
        updatesItem.Click += (s, ev) =>
        {
            CollapseWidget();
            ((App)System.Windows.Application.Current).ShowAboutAndCheckForUpdates();
        };

        var aboutItem = Helpers.WindowsMenuRenderer.Item(LocalizationService.Translate("About CyberSnap"), iconId: "info");
        aboutItem.Click += (s, ev) =>
        {
            CollapseWidget();
            ((App)System.Windows.Application.Current).ShowAbout();
        };

        helpSubmenu.DropDownItems.Add(wikiItem);
        helpSubmenu.DropDownItems.Add(homepageItem);
        helpSubmenu.DropDownItems.Add(updatesItem);
        helpSubmenu.DropDownItems.Add(aboutItem);
        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(helpSubmenu);
        menu.Items.Add(helpSubmenu);

        Helpers.WindowsMenuRenderer.NormalizeItemWidths(menu, minWidth: 240);

        return menu;
    }

    private System.Windows.Forms.ToolStripMenuItem BuildImageCaptureSubmenu()
    {
        var captureMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Image capture"), showImages: true, iconId: "captureRect");
        AddDeliveryOptions(captureMenu, AutoCopyKind.Image);
        captureMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        // Cursor capture remains directly available on the panel for every mode.
        captureMenu.DropDownItems.Add(BuildCaptureToggle("Show magnifier", _settings.ShowCaptureMagnifier,
            () => _settings.ShowCaptureMagnifier = !_settings.ShowCaptureMagnifier));
        captureMenu.DropDownItems.Add(BuildCaptureToggle("Show selection size", _settings.ShowSelectionSize,
            () => _settings.ShowSelectionSize = !_settings.ShowSelectionSize));
        captureMenu.DropDownItems.Add(BuildCaptureToggle("Show crosshair guides", _settings.ShowCrosshairGuides,
            () => _settings.ShowCrosshairGuides = !_settings.ShowCrosshairGuides));
        // The overlay reads WindowDetection, rather than the legacy DetectWindows bool.
        captureMenu.DropDownItems.Add(BuildCaptureToggle("Detect windows",
            _settings.WindowDetection != Models.WindowDetectionMode.Off,
            () =>
            {
                bool turningOn = _settings.WindowDetection == Models.WindowDetectionMode.Off;
                _settings.WindowDetection = turningOn ? Models.WindowDetectionMode.WindowOnly : Models.WindowDetectionMode.Off;
                _settings.DetectWindows = turningOn;
            }));
        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(captureMenu, minWidth: 240);
        return captureMenu;
    }

    // A capture-setting toggle row: shows a checkmark when on (matching the editor's burger menu),
    // flips the bound setting on click and persists it.
    private System.Windows.Forms.ToolStripMenuItem BuildCaptureToggle(string label, bool isChecked, Action toggle, bool publishDelivery = false)
    {
        var item = Helpers.WindowsMenuRenderer.Item(label);
        if (isChecked)
        {
            var checkColor = System.Drawing.Color.FromArgb(255,
                Helpers.UiChrome.SurfaceTextPrimary.R,
                Helpers.UiChrome.SurfaceTextPrimary.G,
                Helpers.UiChrome.SurfaceTextPrimary.B);
            item.Image = Helpers.FluentIcons.RenderBitmap("check", checkColor, 20, true);
            item.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
        }
        item.Click += (s, ev) =>
        {
            toggle();
            _settingsService.Save();
            if (publishDelivery)
            {
                SettingsService.PublishAutoCopyPreferences(_settings);
            }
        };
        return item;
    }

    private System.Windows.Forms.ToolStripMenuItem? BuildScreenSubmenu()
    {
        var screens = PopupWindowHelper.GetSortedScreens();
        if (screens.Length <= 1)
            return null;

        var monitorMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Screen"), showImages: true, iconId: "fullscreen");
        monitorMenu.DropDownItems.Add(BuildWidgetChoice("Follow active monitor", settings => settings.WidgetMonitorIndex == -1,
            settings => settings.WidgetMonitorIndex = -1));
        monitorMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        for (int i = 0; i < screens.Length; i++)
        {
            var idx = i;
            var s = screens[i];
            var label = FormatMonitorLabel(i, s.Primary, _settings.InterfaceLanguage);
            var item = BuildWidgetChoice(label, settings => settings.WidgetMonitorIndex == idx,
                settings => settings.WidgetMonitorIndex = idx);
            monitorMenu.DropDownItems.Add(item);
        }
        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(monitorMenu, minWidth: 200);
        return monitorMenu;
    }

    internal static string FormatMonitorLabel(int zeroBasedIndex, bool isPrimary, string? language = null)
    {
        string Tr(string key) => language is null ? LocalizationService.Translate(key) : LocalizationService.Translate(language, key);
        var role = Tr(isPrimary ? "Primary" : "Secondary");
        return string.Format(
            Tr("Monitor {0} ({1})"),
            zeroBasedIndex + 1,
            role);
    }

    private System.Windows.Forms.ToolStripMenuItem BuildActivationDelaySubmenu()
    {
        var delayMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Activation delay"), showImages: true, iconId: "clock");
        var delays = new[] { 0, 100, 250, 500, 1000 };
        foreach (var d in delays)
        {
            var delayMs = d;
            var item = BuildWidgetChoice($"{d} ms", settings => settings.WidgetHoverDelayMs == delayMs,
                settings => settings.WidgetHoverDelayMs = delayMs);
            delayMenu.DropDownItems.Add(item);
        }
        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(delayMenu, minWidth: 150);
        return delayMenu;
    }

    private System.Windows.Forms.ToolStripMenuItem BuildDisableItem()
    {
        var disableItem = Helpers.WindowsMenuRenderer.Item(
            LocalizationService.Translate("Disable Widget"), iconId: "signOut", danger: true, dangerIconOnly: true);
        disableItem.Click += (s, ev) =>
        {
            _settings.ShowCaptureWidget = false;
            _settingsService.Save();
            ToastWindow.Show(
                LocalizationService.Translate("Widget disabled"),
                LocalizationService.Translate("You can re-enable it from Configuration -> Widget."));
            Close();
        };
        return disableItem;
    }

    private System.Windows.Forms.ToolStripMenuItem BuildPositionSubmenu()
    {
        var positionMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Position"), showImages: true, iconId: "position");

        // Dock edge items: Top, Bottom, Left, Right
        var edges = new[] { CaptureDockSide.Top, CaptureDockSide.Bottom, CaptureDockSide.Left, CaptureDockSide.Right };
        var edgeLabels = new[] { "Top", "Bottom", "Left", "Right" };
        for (int i = 0; i < edges.Length; i++)
        {
            var ed = edges[i];
            var item = BuildWidgetChoice(edgeLabels[i], settings => settings.WidgetDockEdge == ed,
                settings => settings.WidgetDockEdge = ed);
            positionMenu.DropDownItems.Add(item);
        }

        positionMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());

        // Reset position item
        var resetItem = Helpers.WindowsMenuRenderer.Item(LocalizationService.Translate("Reset position"), iconId: "redo");
        resetItem.Click += (s, ev) =>
        {
            SaveWidgetPreferences(settings => settings.WidgetDockPositionOffset = 0.5);
        };
        positionMenu.DropDownItems.Add(resetItem);

        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(positionMenu, minWidth: 180);
        return positionMenu;
    }

    // Shared menu presentation: keep the widget expanded while open, clamp to the working area,
    // flip submenus inward near a screen edge, and force foreground so click-away dismissal works.
    private void ShowThemedMenu(System.Windows.Forms.ContextMenuStrip menu, bool fromMoreButton = false)
    {
        var previousMenu = _contextMenu;
        previousMenu?.Close();
        // Close has finished processing here. Release the previous native popup
        // before opening its replacement, so its deferred disposal cannot disrupt
        // the new menu's focus or WinForms' active-menu tracking.
        previousMenu?.Dispose();
        _contextMenu = menu;
        // Keep the widget expanded/visible while the menu is open: showing the menu moves the
        // cursor off the widget, which would otherwise start the collapse timer and leave the
        // menu floating over an empty spot.
        _contextMenuOpen = true;
        SetMoreMenuOpen(fromMoreButton);
        _collapseTimer.Stop();
        menu.Closed += (_, e) =>
        {
            if (fromMoreButton && e.CloseReason == System.Windows.Forms.ToolStripDropDownCloseReason.AppClicked
                && IsPointerOverMoreButton())
            {
                // WinForms dismisses on mouse-down before WPF sees the same input.
                // Consume that press instead of reopening the menu on mouse-up.
                _suppressMoreMouseDown = true;
                Dispatcher.BeginInvoke(() => _suppressMoreMouseDown = false, DispatcherPriority.Background);
            }
            if (ReferenceEquals(_contextMenu, menu))
            {
                _contextMenu = null;
                _contextMenuOpen = false;
                SetMoreMenuOpen(false);
                if (IsVisible) CheckMouseLeaveWidget();
            }
            // Let ToolStrip finish processing the close before releasing its handles.
            Dispatcher.BeginInvoke(menu.Dispose, DispatcherPriority.Background);
        };

        ShowMenuOnWidgetMonitor(menu, fromMoreButton);

        // The widget is a NOACTIVATE window, so the menu opens without focus and wouldn't close
        // on an outside click. Force it foreground so click-away dismissal works.
        var menuHandle = menu.Handle;
        if (menuHandle != IntPtr.Zero)
            Native.User32.SetForegroundWindow(menuHandle);
    }

    private void SetMoreMenuOpen(bool open)
    {
        _moreMenuOpen = open;
        if (open)
            WidgetMoreButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "WidgetButtonHoverBg");
        else
            WidgetMoreButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
    }

    private bool IsPointerOverMoreButton()
    {
        if (!WidgetMoreButton.IsVisible) return false;
        var cursor = System.Windows.Forms.Cursor.Position;
        var local = WidgetMoreButton.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        return new Rect(WidgetMoreButton.RenderSize).Contains(local);
    }

    // Capture Actions
    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(_settings.DefaultCaptureMode);
    }

    private void ScrollCapture_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.ScrollCapture);
    }

    private void GrabText_Click(object sender, RoutedEventArgs e)
    {
        TriggerStandaloneTool(app => app.OnStandaloneOcrProxy());
    }

    private void QrScan_Click(object sender, RoutedEventArgs e)
    {
        TriggerStandaloneTool(app => app.OnStandaloneScanProxy());
    }

    private void ColorPicker_Click(object sender, RoutedEventArgs e)
    {
        TriggerStandaloneTool(app => app.OnStandaloneColorPickerProxy());
    }

    private void ScreenRecord_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.Record, RecordingFormat.MP4);
    }

    private void GifRecord_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.Record, RecordingFormat.GIF);
    }

    private void Ruler_Click(object sender, RoutedEventArgs e)
    {
        TriggerStandaloneTool(app => app.OnStandaloneRulerProxy());
    }

    private void CaptureCursorToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool programmatic = _suppressCaptureCursorToggle;
        AnimateToggleThumb(CaptureCursorToggle, animate: !programmatic && IsLoaded);
        if (programmatic) return;

        // The widget's cursor toggle is screenshot-only: video and GIF have their
        // own per-format toggles in Settings (and now also inline on the recording
        // bar). VideoShowCursor and GifShowCursor are intentionally NOT touched
        // here so the user can keep the three states independent.
        _settings.ShowCursor = CaptureCursorToggle.IsChecked == true;
        _settingsService.Save();
        _settingsService.FlushPendingWrites();
        ((App)System.Windows.Application.Current).SyncSettingsShowCursorCheck();
    }

    private void WidgetBranding_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CollapseWidget();
        ((App)System.Windows.Application.Current).ShowAbout();
    }

    private void WidgetBranding_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_widgetLogoColor != null)
            WidgetLogoImage.Source = _widgetLogoColor;
        WidgetLogoImage.Opacity = 1.0;

        WidgetBrandText.Opacity = 1.0;
        WidgetBrandText.SetResourceReference(TextBlock.ForegroundProperty, "WidgetText");
    }

    private void WidgetBranding_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_widgetLogoMuted != null)
            WidgetLogoImage.Source = _widgetLogoMuted;
        WidgetLogoImage.Opacity = 0.7;

        WidgetBrandText.Opacity = 0.65;
        WidgetBrandText.SetResourceReference(TextBlock.ForegroundProperty, "WidgetTextMuted");
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_moreMenuOpen)
        {
            _contextMenu?.Close();
            return;
        }
        ShowThemedMenu(BuildMoreMenu(), fromMoreButton: true);
    }

    private void More_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_suppressMoreMouseDown) return;
        _suppressMoreMouseDown = false;
        e.Handled = true;
    }

    private void TriggerAppCapture(Models.CaptureMode mode, RecordingFormat? recordingFormat = null)
    {
        // Hide so the widget is not included in the capture, then launch after a brief delay.
        // The proxy methods call TryClaimCaptureSlotAllowingPreviewReplace() which hides
        // an open Preview so the next capture can stack as a tab (same as hotkeys).
        HideAndLaunch(app =>
        {
            switch (mode)
            {
                case Models.CaptureMode.Rectangle:
                    app.OnHotkeyPressedProxy();
                    break;
                case Models.CaptureMode.Center:
                    app.OnCenterHotkeyPressedProxy();
                    break;
                case Models.CaptureMode.ScrollCapture:
                    app.OnScrollCaptureHotkeyPressedProxy();
                    break;
                case Models.CaptureMode.Ocr:
                    app.OnOcrHotkeyPressedProxy();
                    break;
                case Models.CaptureMode.Scan:
                    app.OnScanHotkeyPressedProxy();
                    break;
                case Models.CaptureMode.ColorPicker:
                    app.OnPickerHotkeyPressedProxy();
                    break;
                case Models.CaptureMode.Record:
                    if (recordingFormat.HasValue)
                        app.OnRecordWithFormatProxy(recordingFormat.Value);
                    else
                        app.OnGifHotkeyPressedProxy();
                    break;
                case Models.CaptureMode.Ruler:
                    app.OnRulerHotkeyPressedProxy();
                    break;
            }
        });
    }

    /// <summary>
    /// Launches a standalone tool (Color Picker, OCR, Ruler, Scan) that runs as its own form
    /// and bypasses the capture overlay entirely.
    /// </summary>
    private void TriggerStandaloneTool(Action<App> launch)
    {
        HideAndLaunch(launch);
    }

    /// <summary>
    /// Hides the widget, waits for the hide to settle, launches the action, then re-shows
    /// when the app session (capture and/or standalone tool) becomes idle again.
    /// </summary>
    private void HideAndLaunch(Action<App> launch)
    {
        CancelHideLaunch();
        CancelSessionRestore();

        PrepareForSession();

        _hideLaunchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hideLaunchTimer.Tick += (_, _) =>
        {
            CancelHideLaunch();

            var app = (App)System.Windows.Application.Current;
            BeginSessionRestore(app);
            try
            {
                launch(app);
            }
            catch (Exception ex)
            {
                // The widget is already hidden at this point. Always restore it even when a
                // capture entry point fails before its normal cleanup path is established.
                AppDiagnostics.LogError("widget.capture-launch", ex);
                FinishSessionRestore();
                return;
            }

            // If the launch did not open a session (e.g. busy gate rejected it), re-show now.
            if (!app.IsSessionBusy())
                FinishSessionRestore();
        };
        _hideLaunchTimer.Start();
    }

    private void PrepareForSession()
    {
        _hoverDelayTimer.Stop();
        _collapseTimer.Stop();

        // Drop opacity first so the expand→peek snap is invisible, then store peek geometry
        // before Hide(). Otherwise WPF/DWM can re-show a leftover expanded frame (the brief
        // ~30% panel flash users saw when a standalone tool closed).
        Opacity = 0;
        CollapseImmediately();
        Hide();
    }

    private void CancelHideLaunch()
    {
        if (_hideLaunchTimer == null) return;
        _hideLaunchTimer.Stop();
        _hideLaunchTimer = null;
    }

    private void BeginSessionRestore(App app)
    {
        CancelSessionRestore();
        _awaitingSessionRestore = true;
        app.SessionBecameIdle += OnAppSessionBecameIdleAction;
        _sessionRestorePollTimer.Start();
    }

    private void CancelSessionRestore()
    {
        _awaitingSessionRestore = false;
        _sessionRestoreInProgress = false;
        _sessionRestorePollTimer.Stop();
        if (System.Windows.Application.Current is App app)
            app.SessionBecameIdle -= OnAppSessionBecameIdleAction;
    }

    // Named method (not a lambda) so -= reliably unsubscribes the same delegate instance.
    private void OnAppSessionBecameIdleAction() => OnAppSessionBecameIdle();

    private void OnAppSessionBecameIdle()
    {
        if (!_awaitingSessionRestore) return;
        if (System.Windows.Application.Current is App app && app.IsSessionBusy()) return;
        FinishSessionRestore();
    }

    private void FinishSessionRestore()
    {
        if (!_awaitingSessionRestore || _sessionRestoreInProgress) return;
        _sessionRestoreInProgress = true;

        _hoverDelayTimer.Stop();
        _collapseTimer.Stop();

        try
        {
            // Still Hidden + Opacity 0: lock peek geometry before the first composed frame.
            CollapseImmediately();
            if (!IsVisible)
                Show();
            AssertWidgetTopmost();
            // After Show, DPI/work-area math is definitive — snap again, then reveal on the
            // next render pass so layout has flushed (avoids a one-frame expanded ghost).
            CollapseImmediately();
            BeginPostDragGrace(); // cursor may rest on the peek; don't auto-expand for a beat
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("widget.restore-prepare", ex.Message, ex);
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (!IsVisible)
                    Show();
                if (IsLoaded)
                    PositionWindow();
                AssertWidgetTopmost();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogWarning("widget.restore-position", ex.Message, ex);
            }
            finally
            {
                // Visibility is more important than perfect placement. Never let a transient
                // DPI/work-area failure leave a live widget permanently transparent.
                Opacity = 1;
                if (IsVisible)
                    CancelSessionRestore();
                else
                    _sessionRestoreInProgress = false; // polling will retry
            }
        }), DispatcherPriority.Render);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private const int IDC_SIZEALL = 32646;
    private const int WM_SETCURSOR = 0x0020;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETCURSOR && (_isDragging || _isDragArmed))
        {
            var hCursor = LoadCursor(IntPtr.Zero, IDC_SIZEALL);
            SetCursor(hCursor);
            handled = true;
            return (IntPtr)1;
        }
        return IntPtr.Zero;
    }
}
