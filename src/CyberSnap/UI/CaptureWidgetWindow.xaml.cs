using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Linq;
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
    private System.Windows.Point _dragStartPoint;
    private double _dragStartOffset;
    private bool _mouseInWindow;

    // Fields to hold original state to restore when capture completes
    private AfterCaptureAction? _restorableAfterCapture;
    private RecordingFormat? _restorableRecordFormat;

    // Layout constants
    private const double PanelWidth = 196;
    private const double PanelHeight = 250;
    private const double PeekSize = 16;

    public CaptureWidgetWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        InitializeComponent();
        SizeToContent = SizeToContent.Manual;

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

        LoadIcons();
        RefreshLayout();
        UpdateEnableEditorState();
        LocalizationService.ApplyTo(this, _settings.InterfaceLanguage);
    }

    private void LoadIcons()
    {
        var accentColor = System.Drawing.Color.FromArgb(0, 255, 255); // Neon Cyan for CyberGems aesthetics
        var normalIconColor = System.Drawing.Color.FromArgb(230, 240, 255); // TextPrimary white-blue

        BigCaptureIcon.Source = Helpers.FluentIcons.RenderWpf("center", accentColor, 20); // target crosshair

        RegionCaptureIcon.Source = Helpers.FluentIcons.RenderWpf("rect", normalIconColor, 22);
        ScrollCaptureIcon.Source = Helpers.FluentIcons.RenderWpf("scrollCapture", normalIconColor, 22);
        GrabTextIcon.Source = Helpers.FluentIcons.RenderWpf("ocr", normalIconColor, 22);
        QrScanIcon.Source = Helpers.FluentIcons.RenderWpf("scan", normalIconColor, 22);
        ColorPickerIcon.Source = Helpers.FluentIcons.RenderWpf("picker", normalIconColor, 22);
        ScreenRecordIcon.Source = Helpers.FluentIcons.RenderWpf("record", normalIconColor, 22); // record dot for MP4
        GifRecordIcon.Source = Helpers.FluentIcons.RenderWpf("play", normalIconColor, 22);      // play dot for GIF
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
        PositionWindow();
    }

    public void RefreshLayout()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshLayout);
            return;
        }

        UpdateEnableEditorState();

        // Apply scaling
        UiScale.ApplyToWindow(this, RootGrid, scaleWindowBounds: false);

        LayoutGrid.RowDefinitions.Clear();
        LayoutGrid.ColumnDefinitions.Clear();
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
        PeekGrip.BorderThickness = edge switch
        {
            CaptureDockSide.Top => new Thickness(1, 1, 1, 0),
            CaptureDockSide.Bottom => new Thickness(1, 0, 1, 1),
            CaptureDockSide.Left => new Thickness(1, 1, 0, 1),
            CaptureDockSide.Right => new Thickness(0, 1, 1, 1),
            _ => new Thickness(1),
        };

        GripPanel.Orientation = horizontal
            ? System.Windows.Controls.Orientation.Horizontal
            : System.Windows.Controls.Orientation.Vertical;

        SetGripRect(GripRect1, horizontal);
        SetGripRect(GripRect2, horizontal);
        SetGripRect(GripRect3, horizontal);
        PeekGrip.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetGripRect(System.Windows.Shapes.Rectangle rect, bool horizontal)
    {
        rect.Width = horizontal ? 2 : 6;
        rect.Height = horizontal ? 6 : 2;
        rect.Margin = horizontal ? new Thickness(1, 0, 1, 0) : new Thickness(0, 1, 0, 1);
    }

    private void UpdateEnableEditorState()
    {
        EnableEditorToggle.IsChecked = _settings.OpenEditorAfterCapture;
    }

    public void PositionWindow()
    {
        if (!IsLoaded) return;

        ControlsGrid.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;

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
            _isExpanded,
            UiScale.Current);

        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;

        UpdateMainPanelBorderAlignment();
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
        
        if (_isExpanded && !_isDragging)
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

            const int padding = 12; // 12 physical pixels safety padding
            return pt.X >= rect.Left - padding && pt.X <= rect.Right + padding &&
                   pt.Y >= rect.Top - padding && pt.Y <= rect.Bottom + padding;
        }
        catch
        {
            return false;
        }
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (!_mouseInWindow && _isExpanded && !_isDragging)
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
            if (!_mouseInWindow && _isExpanded && !_isDragging)
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
        PositionWindow();
        UpdateGripVisibility();
    }

    private void CollapseWidget()
    {
        if (!_isExpanded) return;

        _isExpanded = false;
        MainPanelBorder.Visibility = Visibility.Visible;
        MainPanelBorder.Cursor = System.Windows.Input.Cursors.Hand;

        StopWidgetAnimations();
        PositionWindow();
        UpdateGripVisibility();
    }

    public void CollapseImmediately()
    {
        _isExpanded = false;
        MainPanelBorder.Visibility = Visibility.Visible;
        MainPanelBorder.Cursor = System.Windows.Input.Cursors.Hand;

        StopWidgetAnimations();
        PositionWindow();
        UpdateGripVisibility();
    }

    private void StopWidgetAnimations()
    {
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        SlideTransform.X = 0;
        SlideTransform.Y = 0;
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

            if (_settings.WidgetDockEdge == CaptureDockSide.Top || _settings.WidgetDockEdge == CaptureDockSide.Bottom)
            {
                var travel = workingArea.Width - Width;
                if (travel > 0)
                {
                    var delta = curPos.X - _dragStartPoint.X;
                    _settings.WidgetDockPositionOffset = Math.Clamp(_dragStartOffset + delta / travel, 0.0, 1.0);
                }
            }
            else
            {
                var travel = workingArea.Height - Height;
                if (travel > 0)
                {
                    var delta = curPos.Y - _dragStartPoint.Y;
                    _settings.WidgetDockPositionOffset = Math.Clamp(_dragStartOffset + delta / travel, 0.0, 1.0);
                }
            }

            PositionWindow();
        }
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
        var menu = new System.Windows.Controls.ContextMenu();

        // Dock Edge Selection
        var edgeMenu = new MenuItem { Header = LocalizationService.Translate("Dock edge") };
        var edges = new[] { CaptureDockSide.Top, CaptureDockSide.Bottom, CaptureDockSide.Left, CaptureDockSide.Right };
        var edgeLabels = new[] { "Top", "Bottom", "Left", "Right" };
        for (int i = 0; i < edges.Length; i++)
        {
            var ed = edges[i];
            var item = new MenuItem { Header = LocalizationService.Translate(edgeLabels[i]), IsChecked = _settings.WidgetDockEdge == ed };
            item.Click += (s, ev) =>
            {
                _settings.WidgetDockEdge = ed;
                _settingsService.Save();
                RefreshLayout();
            };
            edgeMenu.Items.Add(item);
        }
        menu.Items.Add(edgeMenu);

        // Screen selection if multiple monitors
        var screens = PopupWindowHelper.GetSortedScreens();
        if (screens.Length > 1)
        {
            var monitorMenu = new MenuItem { Header = LocalizationService.Translate("Screen") };
            for (int i = 0; i < screens.Length; i++)
            {
                var idx = i;
                var s = screens[i];
                var primarySecondary = s.Primary ? LocalizationService.Translate("Primary") : LocalizationService.Translate("Secondary");
                var label = $"Monitor {i + 1} ({primarySecondary})";
                var item = new MenuItem { Header = label, IsChecked = _settings.WidgetMonitorIndex == idx };
                item.Click += (s, ev) =>
                {
                    _settings.WidgetMonitorIndex = idx;
                    _settingsService.Save();
                    PositionWindow();
                };
                monitorMenu.Items.Add(item);
            }
            menu.Items.Add(monitorMenu);
        }

        // Hover Delay trigger submenu
        var delayMenu = new MenuItem { Header = LocalizationService.Translate("Activation delay") };
        var delays = new[] { 0, 100, 250, 500, 1000 };
        foreach (var d in delays)
        {
            var delayMs = d;
            var item = new MenuItem { Header = $"{d} ms", IsChecked = _settings.WidgetHoverDelayMs == delayMs };
            item.Click += (s, ev) =>
            {
                _settings.WidgetHoverDelayMs = delayMs;
                _settingsService.Save();
            };
            delayMenu.Items.Add(item);
        }
        menu.Items.Add(delayMenu);

        menu.Items.Add(new Separator());

        // Disable Floating Panel completely
        var disableItem = new MenuItem { Header = LocalizationService.Translate("Disable quick panel") };
        disableItem.Click += (s, ev) =>
        {
            _settings.ShowCaptureWidget = false;
            _settingsService.Save();
            ToastWindow.Show(
                LocalizationService.Translate("Quick panel disabled"),
                LocalizationService.Translate("You can re-enable it anytime from Settings -> General."));
            Close();
        };
        menu.Items.Add(disableItem);

        menu.IsOpen = true;
    }

    // Capture Actions
    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.Rectangle);
    }

    private void RegionCapture_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.Rectangle);
    }

    private void ScrollCapture_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.ScrollCapture);
    }

    private void GrabText_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.Ocr);
    }

    private void QrScan_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.Scan);
    }

    private void ColorPicker_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.ColorPicker);
    }

    private void ScreenRecord_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.Record, forceMp4: true);
    }

    private void GifRecord_Click(object sender, RoutedEventArgs e)
    {
        TriggerAppCapture(Models.CaptureMode.Record, forceGif: true);
    }

    private void EnableEditorToggle_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = EnableEditorToggle.IsChecked == true;
        _settings.OpenEditorAfterCapture = enabled;
        _settingsService.Save();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ((App)System.Windows.Application.Current).ShowSettings();
    }

    private void TriggerAppCapture(CyberSnap.Models.CaptureMode mode, bool forceMp4 = false, bool forceGif = false)
    {
        // Hide panel so it doesn't get captured
        Hide();

        // Perform temporary settings bypass (recording format only)
        _restorableAfterCapture = _settings.AfterCapture;
        _restorableRecordFormat = _settings.RecordingFormat;

        if (forceMp4)
        {
            _settings.RecordingFormat = RecordingFormat.MP4;
        }
        else if (forceGif)
        {
            _settings.RecordingFormat = RecordingFormat.GIF;
        }

        // Delay to allow window fade/hide animation to finish
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (s, ev) =>
        {
            timer.Stop();

            var app = (App)System.Windows.Application.Current;
            // Launch capture
            switch (mode)
            {
                case Models.CaptureMode.Rectangle:
                    app.OnHotkeyPressedProxy();
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
                    app.OnGifHotkeyPressedProxy();
                    break;
            }

            // Wait until capturing ends to show again.
            CheckCaptureFinishedAndRestore();
        };
        timer.Start();
    }

    private void CheckCaptureFinishedAndRestore()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (s, ev) =>
        {
            var app = (App)System.Windows.Application.Current;
            if (!app.IsCapturingActive())
            {
                timer.Stop();

                // Restore settings after capture completes
                if (_restorableAfterCapture.HasValue)
                {
                    _settings.AfterCapture = _restorableAfterCapture.Value;
                    _restorableAfterCapture = null;
                }
                if (_restorableRecordFormat.HasValue)
                {
                    _settings.RecordingFormat = _restorableRecordFormat.Value;
                    _restorableRecordFormat = null;
                }

                // Safe restore position and show
                CollapseImmediately();
                Show();
            }
        };
        timer.Start();
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
