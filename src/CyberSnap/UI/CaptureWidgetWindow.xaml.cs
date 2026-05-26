using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Forms;
using System.Linq;
using System.Windows.Shapes;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _hoverDelayTimer;
    private readonly DispatcherTimer _collapseTimer;
    private bool _isExpanded;
    private bool _isDragging;
    private System.Windows.Point _dragStartPoint;
    private double _dragStartOffset;
    private bool _mouseInWindow;

    // Fields to hold original state to restore when capture completes
    private AfterCaptureAction? _restorableAfterCapture;
    private RecordingFormat? _restorableRecordFormat;

    // Layout constants
    private const double HandleHeight = 16;
    private const double HandleWidth = 80;
    private const double PanelWidth = 196;
    private const double PanelHeight = 260;

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

        HandleBorder.MouseEnter += HandleBorder_MouseEnter;
        HandleBorder.MouseLeave += HandleBorder_MouseLeave;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;

        LoadIcons();
        RefreshLayout();
        UpdateDirectCopyState();
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

        // Apply scaling
        UiScale.ApplyToWindow(this, RootGrid, scaleWindowBounds: false);

        // Adjust components layout based on dock edge
        LayoutGrid.Children.Clear();
        LayoutGrid.RowDefinitions.Clear();
        LayoutGrid.ColumnDefinitions.Clear();

        var edge = _settings.WidgetDockEdge;

        if (edge == CaptureDockSide.Top || edge == CaptureDockSide.Bottom)
        {
            LayoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LayoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Setup GripPanel and HandleContainerPanel orientation and size using Grid definitions
            HandleCol0.Width = new GridLength(1, GridUnitType.Star);
            HandleCol1.Width = GridLength.Auto;
            HandleCol2.Width = new GridLength(1, GridUnitType.Star);

            HandleRow0.Height = new GridLength(1, GridUnitType.Star);
            HandleRow1.Height = new GridLength(0);
            HandleRow2.Height = new GridLength(0);

            Grid.SetColumn(HandleBorder, 1);
            Grid.SetRow(HandleBorder, 0);

            Grid.SetColumn(DragHandleBorder, 2);
            Grid.SetRow(DragHandleBorder, 0);

            GripPanel.Orientation = System.Windows.Controls.Orientation.Horizontal;
            GripRect1.Width = 2; GripRect1.Height = 6; GripRect1.Margin = new Thickness(1, 0, 1, 0);
            GripRect2.Width = 2; GripRect2.Height = 6; GripRect2.Margin = new Thickness(1, 0, 1, 0);
            GripRect3.Width = 2; GripRect3.Height = 6; GripRect3.Margin = new Thickness(1, 0, 1, 0);

            // Set size of hover activator handle
            HandleBorder.Height = HandleHeight;
            HandleBorder.Width = HandleWidth;
            HandleBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            HandleBorder.VerticalAlignment = System.Windows.VerticalAlignment.Center;

            // Set size of drag handle (aligned to left of column 2)
            DragHandleBorder.Height = HandleHeight;
            DragHandleBorder.Width = 24;
            DragHandleBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            DragHandleBorder.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            DragHandleBorder.Margin = new Thickness(6, 0, 0, 0);

            HandleContainerPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            HandleContainerPanel.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;

            if (edge == CaptureDockSide.Top)
            {
                // Top: Handle at top (Row 0), Main panel at bottom (Row 1)
                Grid.SetRow(HandleContainerPanel, 0);
                Grid.SetRow(MainPanelBorder, 1);
                IndicatorArrow.Text = _isExpanded ? "\uE0E4" : "\uE0E5"; // Up/Down chevrons
            }
            else
            {
                // Bottom: Main panel at top (Row 0), Handle at bottom (Row 1)
                Grid.SetRow(MainPanelBorder, 0);
                Grid.SetRow(HandleContainerPanel, 1);
                IndicatorArrow.Text = _isExpanded ? "\uE0E5" : "\uE0E4"; // Down/Up chevrons
            }
        }
        else // Left or Right
        {
            LayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            LayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Setup GripPanel and HandleContainerPanel orientation and size using Grid definitions
            HandleCol0.Width = new GridLength(1, GridUnitType.Star);
            HandleCol1.Width = new GridLength(0);
            HandleCol2.Width = new GridLength(0);

            HandleRow0.Height = new GridLength(1, GridUnitType.Star);
            HandleRow1.Height = GridLength.Auto;
            HandleRow2.Height = new GridLength(1, GridUnitType.Star);

            Grid.SetColumn(HandleBorder, 0);
            Grid.SetRow(HandleBorder, 1);

            Grid.SetColumn(DragHandleBorder, 0);
            Grid.SetRow(DragHandleBorder, 2);

            GripPanel.Orientation = System.Windows.Controls.Orientation.Vertical;
            GripRect1.Width = 6; GripRect1.Height = 2; GripRect1.Margin = new Thickness(0, 1, 0, 1);
            GripRect2.Width = 6; GripRect2.Height = 2; GripRect2.Margin = new Thickness(0, 1, 0, 1);
            GripRect3.Width = 6; GripRect3.Height = 2; GripRect3.Margin = new Thickness(0, 1, 0, 1);

            HandleBorder.Width = HandleHeight;
            HandleBorder.Height = HandleWidth;
            HandleBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            HandleBorder.VerticalAlignment = System.Windows.VerticalAlignment.Center;

            DragHandleBorder.Width = HandleHeight;
            DragHandleBorder.Height = 24;
            DragHandleBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            DragHandleBorder.VerticalAlignment = System.Windows.VerticalAlignment.Top;
            DragHandleBorder.Margin = new Thickness(0, 6, 0, 0);

            HandleContainerPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            HandleContainerPanel.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;

            if (edge == CaptureDockSide.Left)
            {
                // Left: Handle at left (Col 0), Main panel at right (Col 1)
                Grid.SetColumn(HandleContainerPanel, 0);
                Grid.SetColumn(MainPanelBorder, 1);
                IndicatorArrow.Text = _isExpanded ? "\uE0E2" : "\uE0E3"; // Left/Right chevrons
            }
            else
            {
                // Right: Main panel at left (Col 0), Handle at right (Col 1)
                Grid.SetColumn(MainPanelBorder, 0);
                Grid.SetColumn(HandleContainerPanel, 1);
                IndicatorArrow.Text = _isExpanded ? "\uE0E3" : "\uE0E2"; // Right/Left chevrons
            }
        }

        LayoutGrid.Children.Add(HandleContainerPanel);
        LayoutGrid.Children.Add(MainPanelBorder);

        PositionWindow();
    }

    private void UpdateDirectCopyState()
    {
        DirectCopyCheck.IsChecked = _settings.WidgetDirectCopy;
    }

    public void PositionWindow()
    {
        if (!IsLoaded) return;

        var screens = PopupWindowHelper.GetSortedScreens();
        int screenIdx = _settings.WidgetMonitorIndex;
        if (screenIdx < 0 || screenIdx >= screens.Length)
        {
            // default to screen containing the mouse/active area
            screenIdx = 0;
        }

        var targetScreen = screens[screenIdx];
        var workingArea = PopupWindowHelper.PhysicalPixelsToDips(targetScreen.WorkingArea, targetScreen.Bounds.Location);

        double w, h;
        if (_settings.WidgetDockEdge == CaptureDockSide.Top || _settings.WidgetDockEdge == CaptureDockSide.Bottom)
        {
            w = PanelWidth;
            h = _isExpanded ? (PanelHeight + HandleHeight) : HandleHeight;
        }
        else
        {
            w = _isExpanded ? (PanelWidth + HandleHeight) : HandleHeight;
            h = PanelWidth;
        }

        Width = w * UiScale.Current;
        Height = h * UiScale.Current;

        double offset = _settings.WidgetDockPositionOffset;

        switch (_settings.WidgetDockEdge)
        {
            case CaptureDockSide.Top:
                Left = workingArea.Left + (workingArea.Width - Width) * offset;
                Top = workingArea.Top - (_isExpanded ? HandleHeight * UiScale.Current : 0);
                break;
            case CaptureDockSide.Bottom:
                Left = workingArea.Left + (workingArea.Width - Width) * offset;
                Top = workingArea.Bottom - Height + (_isExpanded ? HandleHeight * UiScale.Current : 0);
                break;
            case CaptureDockSide.Left:
                Left = workingArea.Left - (_isExpanded ? HandleHeight * UiScale.Current : 0);
                Top = workingArea.Top + (workingArea.Height - Height) * offset;
                break;
            case CaptureDockSide.Right:
                Left = workingArea.Right - Width + (_isExpanded ? HandleHeight * UiScale.Current : 0);
                Top = workingArea.Top + (workingArea.Height - Height) * offset;
                break;
        }
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouseInWindow = true;
        _collapseTimer.Stop(); // Cancel scheduled collapse
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouseInWindow = false;
        _hoverDelayTimer.Stop();
        
        if (_isExpanded && !_isDragging)
        {
            _collapseTimer.Start();
        }
    }

    private void HandleBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isExpanded || _isDragging) return;

        _hoverDelayTimer.Interval = TimeSpan.FromMilliseconds(_settings.WidgetHoverDelayMs);
        _hoverDelayTimer.Start();
    }

    private void HandleBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverDelayTimer.Stop();
    }

    private void HoverDelayTimer_Tick(object? sender, EventArgs e)
    {
        _hoverDelayTimer.Stop();
        if (_mouseInWindow && HandleBorder.IsMouseOver)
        {
            ExpandWidget();
        }
    }

    private bool IsMouseOverWidgetWithPadding()
    {
        try
        {
            var mousePos = System.Windows.Forms.Control.MousePosition;
            var relativeMouse = PointFromScreen(new System.Windows.Point(mousePos.X, mousePos.Y));
            const double padding = 8; // 8 dips safety padding
            return relativeMouse.X >= -padding && relativeMouse.X <= ActualWidth + padding &&
                   relativeMouse.Y >= -padding && relativeMouse.Y <= ActualHeight + padding;
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
        // Safety check to ensure collapse if cursor left during complex window transitions
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

        MainPanelBorder.Visibility = Visibility.Visible;

        // Calculate start/end values first
        double startVal = 0;
        double handleStartVal = 0;
        double handleEndVal = 0;
        string path = "";

        switch (_settings.WidgetDockEdge)
        {
            case CaptureDockSide.Top:
                startVal = -PanelHeight * UiScale.Current;
                handleStartVal = HandleHeight * UiScale.Current;
                handleEndVal = 0;
                path = "Y";
                break;
            case CaptureDockSide.Bottom:
                startVal = PanelHeight * UiScale.Current;
                handleStartVal = -HandleHeight * UiScale.Current;
                handleEndVal = 0;
                path = "Y";
                break;
            case CaptureDockSide.Left:
                startVal = -PanelWidth * UiScale.Current;
                handleStartVal = HandleHeight * UiScale.Current;
                handleEndVal = 0;
                path = "X";
                break;
            case CaptureDockSide.Right:
                startVal = PanelWidth * UiScale.Current;
                handleStartVal = -HandleHeight * UiScale.Current;
                handleEndVal = 0;
                path = "X";
                break;
        }

        // Pre-apply starting translation offsets to prevent single-frame layout jumps
        if (path == "Y")
        {
            SlideTransform.Y = startVal;
            HandleSlideTransform.Y = handleStartVal;
        }
        else
        {
            SlideTransform.X = startVal;
            HandleSlideTransform.X = handleStartVal;
        }

        // Reposition window with pre-applied translation active
        PositionWindow();

        var anim = new DoubleAnimation(startVal, 0, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SlideTransform.BeginAnimation(
            path == "Y" ? TranslateTransform.YProperty : TranslateTransform.XProperty,
            anim);

        var handleAnim = new DoubleAnimation(handleStartVal, handleEndVal, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        HandleSlideTransform.BeginAnimation(
            path == "Y" ? TranslateTransform.YProperty : TranslateTransform.XProperty,
            handleAnim);

        var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        HandleContainerPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

        // Chevron update
        RefreshChevronText();
    }

    private void CollapseWidget()
    {
        if (!_isExpanded) return;

        double endVal = 0;
        double handleEndVal = 0;
        string path = "";

        switch (_settings.WidgetDockEdge)
        {
            case CaptureDockSide.Top:
                endVal = -PanelHeight * UiScale.Current;
                handleEndVal = HandleHeight * UiScale.Current;
                path = "Y";
                break;
            case CaptureDockSide.Bottom:
                endVal = PanelHeight * UiScale.Current;
                handleEndVal = -HandleHeight * UiScale.Current;
                path = "Y";
                break;
            case CaptureDockSide.Left:
                endVal = -PanelWidth * UiScale.Current;
                handleEndVal = HandleHeight * UiScale.Current;
                path = "X";
                break;
            case CaptureDockSide.Right:
                endVal = PanelWidth * UiScale.Current;
                handleEndVal = -HandleHeight * UiScale.Current;
                path = "X";
                break;
        }

        var anim = new DoubleAnimation(0, endVal, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var handleAnimIn = new DoubleAnimation(0, handleEndVal, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        anim.Completed += (s, e) =>
        {
            _isExpanded = false;
            MainPanelBorder.Visibility = Visibility.Collapsed;

            SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            SlideTransform.X = 0;
            SlideTransform.Y = 0;

            HandleSlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
            HandleSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            HandleSlideTransform.X = 0;
            HandleSlideTransform.Y = 0;
            HandleContainerPanel.BeginAnimation(UIElement.OpacityProperty, null);
            HandleContainerPanel.Opacity = 1;

            PositionWindow();
            RefreshChevronText();
        };

        SlideTransform.BeginAnimation(
            path == "Y" ? TranslateTransform.YProperty : TranslateTransform.XProperty,
            anim);

        HandleSlideTransform.BeginAnimation(
            path == "Y" ? TranslateTransform.YProperty : TranslateTransform.XProperty,
            handleAnimIn);

        var opacityAnimIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
        HandleContainerPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnimIn);
    }

    public void CollapseImmediately()
    {
        _isExpanded = false;
        MainPanelBorder.Visibility = Visibility.Collapsed;
        
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        SlideTransform.X = 0;
        SlideTransform.Y = 0;

        HandleSlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        HandleSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        HandleSlideTransform.X = 0;
        HandleSlideTransform.Y = 0;
        HandleContainerPanel.BeginAnimation(UIElement.OpacityProperty, null);
        HandleContainerPanel.Opacity = 1;

        PositionWindow();
        RefreshChevronText();
    }

    private void RefreshChevronText()
    {
        var edge = _settings.WidgetDockEdge;
        if (edge == CaptureDockSide.Top)
            IndicatorArrow.Text = _isExpanded ? "\uE0E4" : "\uE0E5";
        else if (edge == CaptureDockSide.Bottom)
            IndicatorArrow.Text = _isExpanded ? "\uE0E5" : "\uE0E4";
        else if (edge == CaptureDockSide.Left)
            IndicatorArrow.Text = _isExpanded ? "\uE0E2" : "\uE0E3";
        else
            IndicatorArrow.Text = _isExpanded ? "\uE0E3" : "\uE0E2";
    }

    private void DragHandleBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartPoint = e.GetPosition(this);
        _dragStartOffset = _settings.WidgetDockPositionOffset;
        DragHandleBorder.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging)
        {
            var screens = PopupWindowHelper.GetSortedScreens();
            int screenIdx = _settings.WidgetMonitorIndex;
            if (screenIdx < 0 || screenIdx >= screens.Length) screenIdx = 0;
            var targetScreen = screens[screenIdx];
            var workingArea = PopupWindowHelper.PhysicalPixelsToDips(targetScreen.WorkingArea, targetScreen.Bounds.Location);

            var curPos = e.GetPosition(this);
            double delta = 0;

            if (_settings.WidgetDockEdge == CaptureDockSide.Top || _settings.WidgetDockEdge == CaptureDockSide.Bottom)
            {
                delta = curPos.X - _dragStartPoint.X;
                double currentLeft = Left + delta;
                double maxLeft = workingArea.Right - Width;
                double newOffset = (currentLeft - workingArea.Left) / (workingArea.Width - Width);
                _settings.WidgetDockPositionOffset = Math.Clamp(newOffset, 0.0, 1.0);
            }
            else
            {
                delta = curPos.Y - _dragStartPoint.Y;
                double currentTop = Top + delta;
                double maxTop = workingArea.Bottom - Height;
                double newOffset = (currentTop - workingArea.Top) / (workingArea.Height - Height);
                _settings.WidgetDockPositionOffset = Math.Clamp(newOffset, 0.0, 1.0);
            }

            PositionWindow();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            DragHandleBorder.ReleaseMouseCapture();
            _settingsService.Save();
            CheckMouseLeaveWidget();
        }
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
        var edgeMenu = new MenuItem { Header = "Posicionar borde" };
        var edges = new[] { CaptureDockSide.Top, CaptureDockSide.Bottom, CaptureDockSide.Left, CaptureDockSide.Right };
        var edgeLabels = new[] { "Arriba", "Abajo", "Izquierda", "Derecha" };
        for (int i = 0; i < edges.Length; i++)
        {
            var ed = edges[i];
            var item = new MenuItem { Header = edgeLabels[i], IsChecked = _settings.WidgetDockEdge == ed };
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
            var monitorMenu = new MenuItem { Header = "Pantalla" };
            for (int i = 0; i < screens.Length; i++)
            {
                var idx = i;
                var s = screens[i];
                var label = $"Monitor {i + 1} ({(s.Primary ? "Principal" : "Secundario")})";
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
        var delayMenu = new MenuItem { Header = "Retardo de activación" };
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
        var disableItem = new MenuItem { Header = "Desactivar panel rápido" };
        disableItem.Click += (s, ev) =>
        {
            _settings.ShowCaptureWidget = false;
            _settingsService.Save();
            ToastWindow.Show("Panel rápido desactivado", "Puedes volver a activarlo en cualquier momento desde Configuración -> General.");
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

    private void DirectCopyCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.WidgetDirectCopy = DirectCopyCheck.IsChecked == true;
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

        // Perform temporary settings bypass
        _restorableAfterCapture = _settings.AfterCapture;
        _restorableRecordFormat = _settings.RecordingFormat;

        if (_settings.WidgetDirectCopy)
        {
            _settings.AfterCapture = AfterCaptureAction.CopyToClipboard;
        }

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
}
