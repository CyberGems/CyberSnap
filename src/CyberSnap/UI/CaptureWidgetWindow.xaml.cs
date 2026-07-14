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
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _hoverDelayTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _postDragGraceTimer;
    private bool _isExpanded;
    private bool _isDragging;
    private bool _isDragArmed;
    private bool _suppressHoverExpand; // brief grace after a drag so it doesn't auto-expand under the resting cursor
    private bool _suppressEditorToggle; // reflecting OpenEditorAfterCapture into the toggle without re-saving/syncing
    private bool _contextMenuOpen;     // keep the widget from collapsing while its context menu is open
    private System.Windows.Media.Effects.Effect? _panelShadow; // soft shadow, applied only when expanded
    private System.Windows.Point _dragStartPoint;
    private double _dragStartOffset;
    private bool _mouseInWindow;

    // Hide → launch → re-show lifecycle (capture overlay or standalone tool)
    private DispatcherTimer? _hideLaunchTimer;
    private bool _awaitingSessionRestore;

    // Layout constants
    private const double PanelWidth = 196;
    private const double PanelHeight = 250;
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
        _settings = settingsService.Settings;
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

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        Topmost = _settings.WidgetAlwaysOnTop;

        LoadIcons();
        RefreshLayout();
        UpdateEnableEditorState();
        LocalizationService.ApplyTo(this, _settings.InterfaceLanguage);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CancelHideLaunch();
        CancelSessionRestore();
        UnhookDisplayEvents();
        StopLayoutAnimations();
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
            if (!IsLoaded) return;
            ClampMonitorIndexToAvailableScreens();
            // Snap layout after resolution / monitor topology changes so the widget
            // never stays parked on a vanished display.
            PositionWindow();
        });
    }

    private void ClampMonitorIndexToAvailableScreens()
    {
        var screens = PopupWindowHelper.GetSortedScreens();
        if (_settings.WidgetMonitorIndex >= 0 && _settings.WidgetMonitorIndex >= screens.Length)
        {
            _settings.WidgetMonitorIndex = -1;
            try { _settingsService.Save(); }
            catch (Exception ex) { AppDiagnostics.LogWarning("widget.clamp-monitor", ex.Message, ex); }
        }
    }

    public void RefreshLocalization()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshLocalization);
            return;
        }
        LocalizationService.ApplyTo(this, _settings.InterfaceLanguage);
    }

    private void ApplyTheme()
    {
        Theme.Refresh();
        
        // Define colors/brushes for widget based on IsDark
        var bg = Theme.IsDark ? System.Windows.Media.Color.FromArgb(242, 13, 15, 26) : System.Windows.Media.Color.FromArgb(242, 223, 226, 234);
        var accent = Theme.IsGray ? System.Windows.Media.Color.FromRgb(184, 190, 198) : Theme.IsDark ? System.Windows.Media.Color.FromRgb(0, 255, 255) : System.Windows.Media.Color.FromRgb(0, 120, 215);
        var accentHover = Theme.IsGray ? System.Windows.Media.Color.FromRgb(214, 218, 224) : Theme.IsDark ? System.Windows.Media.Color.FromRgb(128, 255, 255) : System.Windows.Media.Color.FromRgb(50, 150, 240);
        var text = Theme.IsDark ? System.Windows.Media.Color.FromRgb(230, 240, 255) : System.Windows.Media.Color.FromRgb(26, 26, 26);
        var textMuted = Theme.IsDark ? System.Windows.Media.Color.FromRgb(160, 180, 210) : System.Windows.Media.Color.FromRgb(96, 96, 96);
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

    private void LoadIcons()
    {
        Theme.Refresh();
        var accentColor = Theme.IsGray
            ? System.Drawing.Color.FromArgb(184, 190, 198)
            : Theme.IsDark
            ? System.Drawing.Color.FromArgb(0, 255, 255)
            : System.Drawing.Color.FromArgb(0, 120, 215);
        var normalIconColor = Theme.IsDark 
            ? System.Drawing.Color.FromArgb(230, 240, 255) 
            : System.Drawing.Color.FromArgb(26, 26, 26);

        string captureIconId = (_settings.DefaultCaptureMode == Models.CaptureMode.Center) ? "center" : "captureDot";
        BigCaptureIcon.Source = Helpers.FluentIcons.RenderWpf(captureIconId, accentColor, 20);

        ScrollCaptureIcon.Source = Helpers.FluentIcons.RenderWpf("scrollCapture", normalIconColor, 22);
        GrabTextIcon.Source = Helpers.FluentIcons.RenderWpf("ocr", normalIconColor, 22);
        QrScanIcon.Source = Helpers.FluentIcons.RenderWpf("scan", normalIconColor, 22);
        ColorPickerIcon.Source = Helpers.FluentIcons.RenderWpf("picker", normalIconColor, 22);
        ScreenRecordIcon.Source = Helpers.FluentIcons.RenderWpf("record", normalIconColor, 22); // record dot for MP4
        GifRecordIcon.Source = Helpers.FluentIcons.RenderWpf("recordGif", normalIconColor, 22); // GIF format icon
        RulerIcon.Source = Helpers.FluentIcons.RenderWpf("ruler", normalIconColor, 22); // Ruler tool icon
        SettingsIcon.Source = Helpers.FluentIcons.RenderWpf("gear", normalIconColor, 16);
    }

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
    }

    public void RefreshLayout()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshLayout);
            return;
        }

        ApplyTheme();
        UpdateEnableEditorState();
        LoadIcons();

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

        PeekGrip.Width = horizontal ? PanelWidth : PeekSize;
        PeekGrip.Height = horizontal ? PeekSize : PanelHeight;
        PeekGrip.HorizontalAlignment = edge switch
        {
            CaptureDockSide.Left => System.Windows.HorizontalAlignment.Right,
            CaptureDockSide.Right => System.Windows.HorizontalAlignment.Left,
            _ => System.Windows.HorizontalAlignment.Stretch,
        };
        PeekGrip.VerticalAlignment = edge switch
        {
            CaptureDockSide.Top => System.Windows.VerticalAlignment.Bottom,
            CaptureDockSide.Bottom => System.Windows.VerticalAlignment.Top,
            _ => System.Windows.VerticalAlignment.Stretch,
        };
        // No border of its own: the peek's outline is defined by the panel's clean 1px
        // CornerRadius=8 border (same as the expanded widget). A second, partial accent
        // border here only doubled the line and left the rounded corner unclosed.
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

        // Nudge the grip bars toward the docked (screen) edge with a tiny overflow, so they
        // appear to emerge from the edge instead of hugging the free rounded edge.
        const double overflow = 2;
        GripPanel.HorizontalAlignment = edge switch
        {
            CaptureDockSide.Left => System.Windows.HorizontalAlignment.Left,
            CaptureDockSide.Right => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Center,
        };
        GripPanel.VerticalAlignment = edge switch
        {
            CaptureDockSide.Top => System.Windows.VerticalAlignment.Top,
            CaptureDockSide.Bottom => System.Windows.VerticalAlignment.Bottom,
            _ => System.Windows.VerticalAlignment.Center,
        };
        GripPanel.Margin = edge switch
        {
            CaptureDockSide.Top => new Thickness(0, -overflow, 0, 0),
            CaptureDockSide.Bottom => new Thickness(0, 0, 0, -overflow),
            CaptureDockSide.Left => new Thickness(-overflow, 0, 0, 0),
            CaptureDockSide.Right => new Thickness(0, 0, -overflow, 0),
            _ => new Thickness(0),
        };

        GripPill.Width = horizontal ? 24 : 3;
        GripPill.Height = horizontal ? 3 : 24;
        PeekGrip.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateEnableEditorState()
    {
        _suppressEditorToggle = true;
        try { EnableEditorToggle.IsChecked = _settings.OpenEditorAfterCapture || _settings.OpenVideoTrimmerAfterCapture; }
        finally { _suppressEditorToggle = false; }
    }

    // Settings → widget: re-read the persisted value so the toggle matches the Settings checkbox.
    public void RefreshEnableEditorToggle() => UpdateEnableEditorState();

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
    {
        if (!IsLoaded) return;
        if (!TryGetWindowPlacement(_isExpanded, out var left, out var top, out var width, out var height, out var halo))
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
        out Thickness halo)
    {
        left = top = width = height = 0;
        halo = default;
        if (!IsLoaded) return false;

        var screens = PopupWindowHelper.GetSortedScreens();
        var targetScreen = ResolveTargetScreen(screens, _settings.WidgetMonitorIndex);

        // Move the window onto the target monitor first so WPF adopts that monitor's
        // per-monitor DPI context. Once it has, PointFromScreen maps physical pixels to
        // this window's DIPs self-consistently — no dividing by a foreign monitor's scale.
        // Only jump when the window is actually on a different monitor: PositionWindow runs
        // every frame while dragging, and an unconditional corner-jump leaves ghost trails
        // on this transparent topmost window.
        var phys = targetScreen.WorkingArea;
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

        var topLeft = PointFromScreen(new System.Windows.Point(phys.Left, phys.Top));
        var bottomRight = PointFromScreen(new System.Windows.Point(phys.Right, phys.Bottom));
        var workingArea = new Rect(
            Left + topLeft.X,
            Top + topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);

        var bounds = CalculateWidgetBounds(
            workingArea,
            _settings.WidgetDockEdge,
            _settings.WidgetDockPositionOffset,
            expanded,
            UiScale.Current);

        // Grow the window by the shadow halo; the content is inset by the same amounts
        // (RootGrid.Margin), so the visible widget lands exactly on 'bounds'. The docked side has
        // no halo (ShadowHalo), so that edge sits flush against the screen and the peek stays
        // interactive right at the screen-edge pixel.
        halo = ShadowHalo(_settings.WidgetDockEdge);
        width = bounds.Width + halo.Left + halo.Right;
        height = bounds.Height + halo.Top + halo.Bottom;
        left = bounds.Left - halo.Left;
        top = bounds.Top - halo.Top;
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
        if (_isExpanded)
        {
            MainPanelBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            MainPanelBorder.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        }
        else
        {
            switch (edge)
            {
                case CaptureDockSide.Left:
                    MainPanelBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                    MainPanelBorder.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
                    break;
                case CaptureDockSide.Right:
                    MainPanelBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                    MainPanelBorder.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
                    break;
                case CaptureDockSide.Top:
                    MainPanelBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                    MainPanelBorder.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                    break;
                case CaptureDockSide.Bottom:
                    MainPanelBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                    MainPanelBorder.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                    break;
            }
        }
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


    private static Screen ResolveTargetScreen(Screen[] screens, int requestedIndex)
    {
        if (requestedIndex >= 0 && requestedIndex < screens.Length)
        {
            return screens[requestedIndex];
        }

        var cursorScreen = Screen.FromPoint(System.Windows.Forms.Control.MousePosition);
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
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;

            if (!Native.User32.GetWindowRect(hwnd, out var rect)) return false;
            if (!Native.User32.GetCursorPos(out var pt)) return false;

            // The window includes a transparent shadow halo; test against the visible content by
            // deflating the rect by the per-side halo (converted to physical pixels for this
            // monitor). The docked side has no halo, so it deflates by 0 there.
            var dpi = VisualTreeHelper.GetDpi(this);
            var h = ShadowHalo(_settings.WidgetDockEdge);
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
            var current = GetCursorPositionInDips();
            if (Math.Abs(current.X - _dragStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(current.Y - _dragStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;
            }
        }

        if (_isDragging)
        {
            var screens = PopupWindowHelper.GetSortedScreens();
            var targetScreen = ResolveTargetScreen(screens, _settings.WidgetMonitorIndex);
            var workingArea = PopupWindowHelper.ScreenWorkingAreaToDips(targetScreen);

            var curPos = GetCursorPositionInDips();

            // Snap to a different screen edge when the cursor is clearly nearer to it.
            // Hysteresis in ResolvePreferredDockEdge prevents flicker near corners.
            var preferredEdge = ResolvePreferredDockEdge(workingArea, curPos, _settings.WidgetDockEdge);
            if (preferredEdge != _settings.WidgetDockEdge)
            {
                _settings.WidgetDockEdge = preferredEdge;
                _settings.WidgetDockPositionOffset = OffsetAlongEdgeFromCursor(
                    workingArea, preferredEdge, curPos);
                _dragStartPoint = curPos;
                _dragStartOffset = _settings.WidgetDockPositionOffset;
                UpdatePeekGrip(preferredEdge);
                UpdateGripVisibility();
            }
            else
            {
                // Slide along the current dock edge.
                var halo = ShadowHalo(_settings.WidgetDockEdge);
                var contentWidth = Width - halo.Left - halo.Right;
                var contentHeight = Height - halo.Top - halo.Bottom;
                if (_settings.WidgetDockEdge == CaptureDockSide.Top || _settings.WidgetDockEdge == CaptureDockSide.Bottom)
                {
                    var travel = workingArea.Width - contentWidth;
                    if (travel > 0)
                    {
                        var delta = curPos.X - _dragStartPoint.X;
                        _settings.WidgetDockPositionOffset = Math.Clamp(_dragStartOffset + delta / travel, 0.0, 1.0);
                    }
                }
                else
                {
                    var travel = workingArea.Height - contentHeight;
                    if (travel > 0)
                    {
                        var delta = curPos.Y - _dragStartPoint.Y;
                        _settings.WidgetDockPositionOffset = Math.Clamp(_dragStartOffset + delta / travel, 0.0, 1.0);
                    }
                }
            }

            PositionWindow();
        }
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
                BeginPostDragGrace();
            }
            CheckMouseLeaveWidget();
        }
    }

    private void ArmWidgetDrag(MouseButtonEventArgs e)
    {
        _hoverDelayTimer.Stop();
        _isDragArmed = true;
        _dragStartPoint = GetCursorPositionInDips();
        _dragStartOffset = _settings.WidgetDockPositionOffset;
        CaptureMouse();
        Cursor = System.Windows.Input.Cursors.Hand;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Hand;
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

    private System.Windows.Point GetCursorPositionInDips()
    {
        var point = System.Windows.Forms.Control.MousePosition;
        var localPoint = PointFromScreen(new System.Windows.Point(point.X, point.Y));
        return new System.Windows.Point(Left + localPoint.X, Top + localPoint.Y);
    }

    // Context Menu options (right click handle)
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ShowWidgetContextMenu();
    }

    private void ShowWidgetContextMenu()
    {
        // Built with WindowsMenuRenderer so it is pixel-identical to the tray icon menu
        // (dark rounded surface, accent hover bar) instead of the default WPF look.
        var menu = Helpers.WindowsMenuRenderer.Create(showImages: true, minWidth: 220);

        menu.Items.Add(BuildPositionSubmenu());

        var screenMenu = BuildScreenSubmenu();
        if (screenMenu != null)
            menu.Items.Add(screenMenu);

        menu.Items.Add(BuildActivationDelaySubmenu());

        menu.Items.Add(BuildCaptureToggle("Always on top", _settings.WidgetAlwaysOnTop,
            () => {
                _settings.WidgetAlwaysOnTop = !_settings.WidgetAlwaysOnTop;
                Topmost = _settings.WidgetAlwaysOnTop;
                ((App)System.Windows.Application.Current).SyncSettingsAlwaysOnTopCheck();
            }));

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        menu.Items.Add(BuildDisableItem());

        Helpers.WindowsMenuRenderer.NormalizeItemWidths(menu, minWidth: 220);

        ShowThemedMenu(menu);
    }

    // Config button menu: capture toggles at the top, a "Widget" submenu mirroring the right-click
    // options, and a shortcut into the full widget settings in the Config window.
    private void ShowConfigMenu()
    {
        var menu = Helpers.WindowsMenuRenderer.Create(showImages: true, minWidth: 240);

        // Capture toggles (top level, checkmark style)
        menu.Items.Add(BuildCaptureToggle("Capture cursor", _settings.ShowCursor,
            () => _settings.ShowCursor = !_settings.ShowCursor));
        menu.Items.Add(BuildCaptureToggle("Show magnifier", _settings.ShowCaptureMagnifier,
            () => _settings.ShowCaptureMagnifier = !_settings.ShowCaptureMagnifier));
        menu.Items.Add(BuildCaptureToggle("Show selection size", _settings.ShowSelectionSize,
            () => _settings.ShowSelectionSize = !_settings.ShowSelectionSize));
        menu.Items.Add(BuildCaptureToggle("Show crosshair guides", _settings.ShowCrosshairGuides,
            () => _settings.ShowCrosshairGuides = !_settings.ShowCrosshairGuides));
        // Window auto-detection is driven by the WindowDetection enum; the DetectWindows bool is
        // vestigial (assigned but never read by the overlay). Flip the enum or this has no effect.
        menu.Items.Add(BuildCaptureToggle("Detect windows",
            _settings.WindowDetection != Models.WindowDetectionMode.Off,
            () =>
            {
                bool turningOn = _settings.WindowDetection == Models.WindowDetectionMode.Off;
                _settings.WindowDetection = turningOn ? Models.WindowDetectionMode.WindowOnly : Models.WindowDetectionMode.Off;
                _settings.DetectWindows = turningOn;
            }));

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // "Widget" submenu mirrors the right-click context menu options.
        var widgetMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Widget"), showImages: true);
        widgetMenu.DropDownItems.Add(BuildPositionSubmenu());

        var screenMenu = BuildScreenSubmenu();
        if (screenMenu != null)
            widgetMenu.DropDownItems.Add(screenMenu);
        widgetMenu.DropDownItems.Add(BuildActivationDelaySubmenu());
        widgetMenu.DropDownItems.Add(BuildCaptureToggle("Always on top", _settings.WidgetAlwaysOnTop,
            () => {
                _settings.WidgetAlwaysOnTop = !_settings.WidgetAlwaysOnTop;
                Topmost = _settings.WidgetAlwaysOnTop;
                ((App)System.Windows.Application.Current).SyncSettingsAlwaysOnTopCheck();
            }));
        widgetMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        widgetMenu.DropDownItems.Add(BuildDisableItem());
        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(widgetMenu, minWidth: 200);
        menu.Items.Add(widgetMenu);

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

        Helpers.WindowsMenuRenderer.NormalizeItemWidths(menu, minWidth: 240);

        ShowThemedMenu(menu);
    }

    // A capture-setting toggle row: shows a checkmark when on (matching the editor's burger menu),
    // flips the bound setting on click and persists it.
    private System.Windows.Forms.ToolStripMenuItem BuildCaptureToggle(string label, bool isChecked, Action toggle)
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
        };
        return item;
    }

    private System.Windows.Forms.ToolStripMenuItem? BuildScreenSubmenu()
    {
        var screens = PopupWindowHelper.GetSortedScreens();
        if (screens.Length <= 1)
            return null;

        var monitorMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Screen"));
        for (int i = 0; i < screens.Length; i++)
        {
            var idx = i;
            var s = screens[i];
            var label = FormatMonitorLabel(i, s.Primary);
            var item = Helpers.WindowsMenuRenderer.Item(label, active: _settings.WidgetMonitorIndex == idx);
            item.Click += (s, ev) =>
            {
                _settings.WidgetMonitorIndex = idx;
                _settingsService.Save();
                PositionWindow();
            };
            monitorMenu.DropDownItems.Add(item);
        }
        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(monitorMenu, minWidth: 200);
        return monitorMenu;
    }

    internal static string FormatMonitorLabel(int zeroBasedIndex, bool isPrimary)
    {
        var role = LocalizationService.Translate(isPrimary ? "Primary" : "Secondary");
        return string.Format(
            LocalizationService.Translate("Monitor {0} ({1})"),
            zeroBasedIndex + 1,
            role);
    }

    private System.Windows.Forms.ToolStripMenuItem BuildActivationDelaySubmenu()
    {
        var delayMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Activation delay"));
        var delays = new[] { 0, 100, 250, 500, 1000 };
        foreach (var d in delays)
        {
            var delayMs = d;
            var item = Helpers.WindowsMenuRenderer.Item($"{d} ms", active: _settings.WidgetHoverDelayMs == delayMs);
            item.Click += (s, ev) =>
            {
                _settings.WidgetHoverDelayMs = delayMs;
                _settingsService.Save();
            };
            delayMenu.DropDownItems.Add(item);
        }
        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(delayMenu, minWidth: 150);
        return delayMenu;
    }

    private System.Windows.Forms.ToolStripMenuItem BuildDisableItem()
    {
        var disableItem = Helpers.WindowsMenuRenderer.Item(
            LocalizationService.Translate("Disable Widget"), iconId: "close", danger: true);
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
        var positionMenu = Helpers.WindowsMenuRenderer.Submenu(LocalizationService.Translate("Position"));

        // Dock edge items: Top, Bottom, Left, Right
        var edges = new[] { CaptureDockSide.Top, CaptureDockSide.Bottom, CaptureDockSide.Left, CaptureDockSide.Right };
        var edgeLabels = new[] { "Top", "Bottom", "Left", "Right" };
        for (int i = 0; i < edges.Length; i++)
        {
            var ed = edges[i];
            var item = Helpers.WindowsMenuRenderer.Item(edgeLabels[i], active: _settings.WidgetDockEdge == ed);
            item.Click += (s, ev) =>
            {
                _settings.WidgetDockEdge = ed;
                _settingsService.Save();
                RefreshLayout();
            };
            positionMenu.DropDownItems.Add(item);
        }

        positionMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());

        // Reset position item
        var resetItem = Helpers.WindowsMenuRenderer.Item(LocalizationService.Translate("Reset position"));
        resetItem.Click += (s, ev) =>
        {
            _settings.WidgetDockPositionOffset = 0.5;
            _settingsService.Save();
            PositionWindow();
        };
        positionMenu.DropDownItems.Add(resetItem);

        Helpers.WindowsMenuRenderer.NormalizeDropDownWidths(positionMenu, minWidth: 180);
        return positionMenu;
    }

    // Shared menu presentation: keep the widget expanded while open, clamp to the working area,
    // flip submenus inward near a screen edge, and force foreground so click-away dismissal works.
    private void ShowThemedMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        // Keep the widget expanded/visible while the menu is open: showing the menu moves the
        // cursor off the widget, which would otherwise start the collapse timer and leave the
        // menu floating over an empty spot.
        _contextMenuOpen = true;
        _collapseTimer.Stop();
        menu.Closed += (_, _) =>
        {
            _contextMenuOpen = false;
            CheckMouseLeaveWidget(); // collapse now if the cursor ended up outside
        };

        // Clamp the menu to the working area of the monitor under the cursor, so opening it near
        // a screen edge flips it inward instead of spilling onto the adjacent monitor.
        var cursor = System.Windows.Forms.Cursor.Position;
        var wa = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var sz = menu.PreferredSize;
        int x = Math.Max(wa.Left, Math.Min(cursor.X, wa.Right - sz.Width));
        int y = Math.Max(wa.Top, Math.Min(cursor.Y, wa.Bottom - sz.Height));

        // If the menu sits near the right edge of this monitor, open submenus to the LEFT so they
        // don't spill onto the adjacent monitor (WinForms would otherwise flow right into it).
        const int submenuWidth = 180;
        var subDirection = (x + sz.Width + submenuWidth > wa.Right)
            ? System.Windows.Forms.ToolStripDropDownDirection.Left
            : System.Windows.Forms.ToolStripDropDownDirection.Right;
        foreach (System.Windows.Forms.ToolStripItem it in menu.Items)
        {
            if (it is System.Windows.Forms.ToolStripMenuItem mi && mi.HasDropDownItems)
                mi.DropDownDirection = subDirection;
        }

        menu.Show(new System.Drawing.Point(x, y));

        // The widget is a NOACTIVATE window, so the menu opens without focus and wouldn't close
        // on an outside click. Force it foreground so click-away dismissal works.
        var menuHandle = menu.Handle;
        if (menuHandle != IntPtr.Zero)
            Native.User32.SetForegroundWindow(menuHandle);
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

    private void EnableEditorToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorToggle) return; // value pushed in from Settings — don't echo back

        var enabled = EnableEditorToggle.IsChecked == true;
        _settings.OpenEditorAfterCapture = enabled;
        _settings.OpenVideoTrimmerAfterCapture = enabled;
        _settingsService.Save();
        // Keep the Settings window's "Enable editor" checkbox in lockstep when it's open.
        ((App)System.Windows.Application.Current).SyncSettingsEnableEditorCheck();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // The Config button now opens a quick menu (capture toggles + Widget options + a shortcut
        // into the full settings) instead of jumping straight to the Config window.
        ShowConfigMenu();
    }

    private void TriggerAppCapture(Models.CaptureMode mode, RecordingFormat? recordingFormat = null)
    {
        // Hide so the widget is not included in the capture, then launch after a brief delay.
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
    private void TriggerStandaloneTool(Action<App> launch) => HideAndLaunch(launch);

    /// <summary>
    /// Hides the widget, waits for the hide to settle, launches the action, then re-shows
    /// when the app session (capture and/or standalone tool) becomes idle again.
    /// </summary>
    private void HideAndLaunch(Action<App> launch)
    {
        CancelHideLaunch();
        CancelSessionRestore();

        _hoverDelayTimer.Stop();
        _collapseTimer.Stop();

        // Drop opacity first so the expand→peek snap is invisible, then store peek geometry
        // before Hide(). Otherwise WPF/DWM can re-show a leftover expanded frame (the brief
        // ~30% panel flash users saw when a standalone tool closed).
        Opacity = 0;
        CollapseImmediately();
        Hide();

        _hideLaunchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hideLaunchTimer.Tick += (_, _) =>
        {
            CancelHideLaunch();

            var app = (App)System.Windows.Application.Current;
            BeginSessionRestore(app);
            launch(app);

            // If the launch did not open a session (e.g. busy gate rejected it), re-show now.
            if (!app.IsSessionBusy())
                FinishSessionRestore();
        };
        _hideLaunchTimer.Start();
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
    }

    private void CancelSessionRestore()
    {
        _awaitingSessionRestore = false;
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
        if (!_awaitingSessionRestore) return;
        CancelSessionRestore();

        _hoverDelayTimer.Stop();
        _collapseTimer.Stop();

        // Still Hidden + Opacity 0: lock peek geometry before the first composed frame.
        CollapseImmediately();
        Show();
        // After Show, DPI/work-area math is definitive — snap again, then reveal on the
        // next render pass so layout has flushed (avoids a one-frame expanded ghost).
        CollapseImmediately();
        BeginPostDragGrace(); // cursor may rest on the peek; don't auto-expand for a beat

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            PositionWindow();
            Opacity = 1;
        }), DispatcherPriority.Render);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private const int IDC_HAND = 32649;
    private const int WM_SETCURSOR = 0x0020;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETCURSOR && (_isDragging || _isDragArmed))
        {
            var hCursor = LoadCursor(IntPtr.Zero, IDC_HAND);
            SetCursor(hCursor);
            handled = true;
            return (IntPtr)1;
        }
        return IntPtr.Zero;
    }
}
