using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace CyberSnap.UI;

public partial class TrayContextMenuWindow : Window
{
    private readonly TrayIcon _trayIcon;
    private readonly System.Drawing.Point _clickPoint;
    private bool _isClosing = false;
    private WpfToolTip? _activeTooltip;
    private FrameworkElement? _activeTooltipOwner;

    public TrayContextMenuWindow(TrayIcon trayIcon, System.Drawing.Point clickPoint)
    {
        _trayIcon = trayIcon;
        _clickPoint = clickPoint;
        InitializeComponent();
        AddHandler(UIElement.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(Window_PreviewMouseMove), true);

        // Refresh local theme resources to guarantee proper look on creation
        Theme.Refresh();
        Theme.ApplyTo(Resources);
        Theme.ApplyTo(Application.Current.Resources);

        LoadLocalizedLabels();
        LoadIcons();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Physical cursor captured at tray click (Win32 virtual-screen pixels).
            var physicalCursor = _clickPoint;
            var screen = System.Windows.Forms.Screen.FromPoint(physicalCursor);
            var physWork = screen.WorkingArea;
            var physBounds = screen.Bounds;

            // Move onto the click monitor first so WPF adopts that monitor's per-monitor DPI.
            // Without this, Left/Top and PointFromScreen use the wrong scale on mixed-DPI setups
            // (e.g. 150% primary + 125% secondary) and the menu lands far away or off-screen.
            try
            {
                var hwnd = new WindowInteropHelper(this).EnsureHandle();
                if (hwnd != IntPtr.Zero)
                {
                    Native.User32.SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        physWork.X,
                        physWork.Y,
                        0,
                        0,
                        Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE | Native.User32.SWP_NOZORDER);
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogWarning("traymenu.setwindowpos", ex.Message, ex);
            }

            // Map physical rects/points into this window's DIP space (same approach as Toast/Widget).
            Rect PhysicalToWindowDips(System.Drawing.Rectangle r)
            {
                var tl = PointFromScreen(new System.Windows.Point(r.Left, r.Top));
                var br = PointFromScreen(new System.Windows.Point(r.Right, r.Bottom));
                return new Rect(
                    Left + tl.X,
                    Top + tl.Y,
                    Math.Max(0, br.X - tl.X),
                    Math.Max(0, br.Y - tl.Y));
            }

            var workArea = PhysicalToWindowDips(physWork);
            var screenArea = PhysicalToWindowDips(physBounds);

            var cursorLocal = PointFromScreen(new System.Windows.Point(physicalCursor.X, physicalCursor.Y));
            double cursorX = Left + cursorLocal.X;
            double cursorY = Top + cursorLocal.Y;

            UpdateLayout();
            double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            double windowHeight = ActualHeight > 0 ? ActualHeight : 360;

            const double gap = 8;
            const double eps = 2; // ignore sub-pixel work-area vs bounds differences
            double left;
            double top;

            // Detect taskbar dock side from work area vs full bounds on the click monitor.
            bool taskbarLeft = workArea.Left > screenArea.Left + eps;
            bool taskbarRight = workArea.Right < screenArea.Right - eps;
            bool taskbarTop = workArea.Top > screenArea.Top + eps;

            if (taskbarLeft)
            {
                left = workArea.Left + gap;
                top = cursorY - (windowHeight / 2);
            }
            else if (taskbarRight)
            {
                left = workArea.Right - windowWidth - gap;
                top = cursorY - (windowHeight / 2);
            }
            else if (taskbarTop)
            {
                left = cursorX - (windowWidth / 2);
                top = workArea.Top + gap;
            }
            else
            {
                // Bottom taskbar (default) or auto-hide
                left = cursorX - (windowWidth / 2);
                top = workArea.Bottom - windowHeight - gap;
            }

            // Clamp so the ENTIRE window stays inside the work area.
            // Previous bug set left/top to (edge - gap) without subtracting size, which shoved
            // most of the menu off-screen (and made it "disappear" on the secondary monitor).
            double minLeft = workArea.Left + gap;
            double maxLeft = workArea.Right - windowWidth - gap;
            double minTop = workArea.Top + gap;
            double maxTop = workArea.Bottom - windowHeight - gap;

            if (maxLeft < minLeft) left = workArea.Left + (workArea.Width - windowWidth) / 2;
            else left = Math.Clamp(left, minLeft, maxLeft);

            if (maxTop < minTop) top = workArea.Top + (workArea.Height - windowHeight) / 2;
            else top = Math.Clamp(top, minTop, maxTop);

            Left = left;
            Top = top;

            Activate();
            Focus();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("traymenu.loaded", ex);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Dismiss when clicking outside using the reentrancy-safe CloseMenu helper
        CloseMenu();
    }

    private void CloseMenu()
    {
        if (_isClosing) return;
        _isClosing = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("traymenu.close", ex.Message, ex);
        }
    }

    private void LoadLocalizedLabels()
    {
        TitleTextBlock.Text = $"CyberSnap  {UpdateService.GetCurrentVersionLabel()}";
        
        var app = Application.Current as App;
        bool updateAvailable = app?.LatestUpdateResult?.IsUpdateAvailable ?? false;
        if (updateAvailable)
        {
            UpdateLed.Visibility = Visibility.Visible;
            SetTooltip(UpdateLed, T("Update available"));
            SetTooltip(AboutHeaderBtn, $"{T("Open About CyberSnap")} ({T("Update available")})");
        }
        else
        {
            UpdateLed.Visibility = Visibility.Collapsed;
            UpdateLed.ToolTip = null;
            SetTooltip(AboutHeaderBtn, T("Open About CyberSnap"));
        }

        // Shorter labels for buttons
        AreaCaptureText.Text = T("Area");
        FullscreenText.Text = T("Fullscreen short");
        ActiveWindowText.Text = T("Active window short");
        RepeatLastAreaText.Text = T("Repeat short");
        ScrollQuickText.Text = T("Scrolling");
        OcrText.Text = T("OCR");
        QrText.Text = T("QR");
        ColorPickerText.Text = T("Color");
        RulerText.Text = T("Ruler");
        AnnotationsText.Text = T("Editor");
        GalleryText.Text = T("Gallery");
        
        // Tooltips — short action descriptions (labels stay compact on the tiles).
        SetTooltip(AreaCaptureBtn, T("Capture a rectangular region of the screen"));
        SetTooltip(ScrollQuickBtn, T("Capture a long scrolling page or window"));
        SetTooltip(FullscreenBtn, T("Fullscreen capture"));
        SetTooltip(ActiveWindowBtn, T("Active window"));
        SetTooltip(RepeatLastAreaBtn, T("Repeat last area"));
        SetTooltip(OcrBtn, T("Extract text from a region of the screen"));
        SetTooltip(QrBtn, T("Scan QR codes and barcodes on screen"));
        SetTooltip(ColorPickerBtn, T("Capture a color sample from the screen"));
        SetTooltip(RulerBtn, T("Measure distances on the screen"));
        SetTooltip(AnnotationsBtn, T("Open the annotations editor"));
        SetTooltip(GalleryBtn, T("Browse and manage your captures"));
        SetTooltip(GifRecordBtn, T("Record the screen as an animated GIF"));
        SetTooltip(VideoRecordBtn, T("Record the screen as an MP4 video"));

        // Compact labels for the half-width row; full names live in tooltips.

        SetTooltip(SettingsBtn, T("Open CyberSnap settings"));
        AchievementsText.Text = T("Achievements");
        SetTooltip(AchievementsBtn, T("Open Achievements"));
        ExitText.Text = T("Exit");
        SetTooltip(ExitBtn, T("Quit CyberSnap"));

        // Determine recording state and localize the compact record button.
        bool isRecording = Capture.RecordingForm.Current != null;
        if (isRecording)
        {
            VideoRecordText.Text = T("Stop recording");
            SetTooltip(VideoRecordBtn, T("Stop the current screen recording"));
            VideoRecordBtn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            GifRecordBtn.IsEnabled = false;
        }
        else
        {
            VideoRecordText.Text = T("Record");
            SetTooltip(VideoRecordBtn, T("Record the screen as an MP4 video"));
            VideoRecordBtn.ClearValue(ForegroundProperty);
            GifRecordBtn.IsEnabled = true;
            GifRecordText.Text = T("Record") + " GIF";
        }
    }

    private void LoadIcons()
    {
        var fgColor = Theme.TextPrimary;
        // Primary capture matches the widget: accent cyan so it reads as the default action.
        var accentColor = Theme.Accent;

        AppLogoImage.Source = ThemedLogo.SquareGrayscale(20);

        AreaCaptureRing.Fill = new SolidColorBrush(accentColor);
        AreaCaptureCenter.Fill = new SolidColorBrush(accentColor);
        AreaCaptureCenterGlow.Color = accentColor;
        ScrollQuickIcon.Source = GetIcon("scrollCapture", fgColor, 22);
        FullscreenIcon.Source = GetIcon("fullscreen", fgColor, 22);
        ActiveWindowIcon.Source = GetIcon("activeWindow", fgColor, 22);
        RepeatLastAreaIcon.Source = GetIcon("captureBack", fgColor, 22);
        OcrIcon.Source = GetIcon("ocr", fgColor, 20);
        QrIcon.Source = GetIcon("scan", fgColor, 20);
        ColorPickerIcon.Source = GetIcon("picker", fgColor, 20);
        RulerIcon.Source = GetIcon("ruler", fgColor, 20);
        // Bottom row: larger assets for accessibility while keeping compact tile height.
        AnnotationsIcon.Source = GetIcon("compose", fgColor, 20);
        GalleryIcon.Source = GetIcon("history", fgColor, 20);

        SettingsIcon.Source = GetIcon("gear", fgColor, 17);
        AchievementsIcon.Source = GetIcon("trophy", fgColor, 20);
        ExitIcon.Source = GetDangerIcon("signOut", 20);

        bool isRecording = Capture.RecordingForm.Current != null;
        if (isRecording)
        {
            VideoRecordIcon.Source = GetDangerIcon("play", 20);
        }
        else
        {
            VideoRecordIcon.Source = GetIcon("record", fgColor, 20);
            GifRecordIcon.Source = GetIcon("recordGif", fgColor, 20);
        }
    }

    private ImageSource? GetIcon(string id, System.Windows.Media.Color mediaColor, int size)
    {
        var drawingColor = System.Drawing.Color.FromArgb(mediaColor.A, mediaColor.R, mediaColor.G, mediaColor.B);
        return FluentIcons.RenderWpf(id, drawingColor, size);
    }

    private ImageSource? GetDangerIcon(string id, int size)
    {
        return FluentIcons.RenderWpf(id, System.Drawing.Color.FromArgb(239, 68, 68), size);
    }

    private static string T(string text) => LocalizationService.Translate(text);

    private void SetTooltip(FrameworkElement element, string text)
    {
        var tooltip = new WpfToolTip
        {
            Content = text,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse
        };

        tooltip.Opened += (_, _) =>
        {
            if (!ReferenceEquals(_activeTooltip, tooltip))
                DismissActiveTooltip();

            _activeTooltip = tooltip;
            _activeTooltipOwner = element;
        };
        tooltip.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeTooltip, tooltip))
            {
                _activeTooltip = null;
                _activeTooltipOwner = null;
            }
        };

        element.ToolTip = tooltip;
        ToolTipService.SetInitialShowDelay(element, 400);
        ToolTipService.SetBetweenShowDelay(element, 0);
        ToolTipService.SetShowDuration(element, 5000);
    }

    private void Window_PreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_activeTooltip is null || _activeTooltipOwner is null)
            return;

        if (e.OriginalSource is DependencyObject source &&
            !IsWithinElement(source, _activeTooltipOwner))
        {
            DismissActiveTooltip();
        }
    }

    private void DismissActiveTooltip()
    {
        if (_activeTooltip is not null)
            _activeTooltip.IsOpen = false;
    }

    private static bool IsWithinElement(DependencyObject source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual || element is Visual3D)
            return VisualTreeHelper.GetParent(element);

        return LogicalTreeHelper.GetParent(element);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.close-btn", ex); }
    }

    private void AreaCapture_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerCapture(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.area-capture", ex); }
    }

    private void ScrollCapture_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerScrollCapture(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.scroll-capture", ex); }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerFullscreen(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.fullscreen", ex); }
    }

    private void ActiveWindow_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerActiveWindow(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.active-window", ex); }
    }

    private void RepeatLastArea_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerRepeatLastArea(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.repeat-last-area", ex); }
    }

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerOcr(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.ocr", ex); }
    }

    private void QrScan_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerScan(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.qr-scan", ex); }
    }

    private void ColorPicker_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerColorPicker(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.color-picker", ex); }
    }

    private void Ruler_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerRuler(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.ruler", ex); }
    }

    private void Annotations_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerAnnotationEditor(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.annotations", ex); }
    }

    private void Gallery_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerHistory(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.gallery", ex); }
    }

    private void VideoRecord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CloseMenu();
            bool isRecording = Capture.RecordingForm.Current != null;
            if (isRecording)
            {
                if (Capture.RecordingForm.Current != null)
                    Capture.RecordingForm.Current.RequestStop();
            }
            else
            {
                _trayIcon.TriggerRecord(RecordingFormat.MP4);
            }
        }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.video-record", ex); }
    }

    private void GifRecord_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerRecord(RecordingFormat.GIF); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.gif-record", ex); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerSettings(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.settings", ex); }
    }

    private void Achievements_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerAchievements(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.achievements", ex); }
    }

    private void AboutHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            e.Handled = true;
            CloseMenu();
            _trayIcon.TriggerAbout();
        }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.about", ex); }
    }

    private void AboutHeader_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        try
        {
            System.Windows.Controls.ToolTipService.SetIsEnabled(AboutHeaderBtn, true);
            AboutHeaderBtn.Background = System.Windows.Media.Brushes.Transparent;
            TitleTextBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextPrimaryBrush");
            AppLogoImage.Source = ThemedLogo.Square(20);
            AppLogoImage.Opacity = 1;
        }
        catch { }
    }

    private void AboutHeader_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        try
        {
            AboutHeaderBtn.Background = System.Windows.Media.Brushes.Transparent;
            TitleTextBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextSecondaryBrush");
            AppLogoImage.Source = ThemedLogo.SquareGrayscale(20);
            AppLogoImage.Opacity = 0.75;
            DismissAboutHeaderToolTip();
        }
        catch { }
    }

    private void TrayCloseBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        DismissAboutHeaderToolTip();
    }

    private void DismissAboutHeaderToolTip()
    {
        try
        {
            // Force-hide the currently visible tooltip when moving to another control.
            DismissActiveTooltip();
        }
        catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerQuit(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.exit", ex); }
    }
}
