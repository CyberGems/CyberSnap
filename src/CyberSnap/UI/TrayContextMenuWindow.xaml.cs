using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class TrayContextMenuWindow : Window
{
    private readonly TrayIcon _trayIcon;
    private readonly System.Drawing.Point _clickPoint;
    private bool _isClosing = false;

    public TrayContextMenuWindow(TrayIcon trayIcon, System.Drawing.Point clickPoint)
    {
        _trayIcon = trayIcon;
        _clickPoint = clickPoint;
        InitializeComponent();
        
        // Refresh local theme resources to guarantee proper look on creation
        Theme.Refresh();
        Theme.ApplyTo(Resources);
        Theme.ApplyTo(Application.Current.Resources);

        LoadLocalizedLabels();
        LoadIcons();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Physical cursor captured at tray click (Win32 virtual-screen pixels).
        var physicalCursor = _clickPoint;
        var screen = System.Windows.Forms.Screen.FromPoint(physicalCursor);
        var physWork = screen.WorkingArea;
        var physBounds = screen.Bounds;

        // Move onto the click monitor first so WPF adopts that monitor's per-monitor DPI.
        // Without this, Left/Top and PointFromScreen use the wrong scale on mixed-DPI setups
        // (e.g. 150% primary + 125% secondary) and the menu lands far away or off-screen.
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

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Dismiss when clicking outside using the reentrancy-safe CloseMenu helper
        CloseMenu();
    }

    private void CloseMenu()
    {
        if (_isClosing) return;
        _isClosing = true;
        Close();
    }

    private void LoadLocalizedLabels()
    {
        TitleTextBlock.Text = $"CyberSnap  {UpdateService.GetCurrentVersionLabel()}";
        
        var app = Application.Current as App;
        bool updateAvailable = app?.LatestUpdateResult?.IsUpdateAvailable ?? false;
        if (updateAvailable)
        {
            UpdateLed.Visibility = Visibility.Visible;
            UpdateLed.ToolTip = T("Update available");
            AboutHeaderBtn.ToolTip = $"{T("Open About CyberSnap")} ({T("Update available")})";
        }
        else
        {
            UpdateLed.Visibility = Visibility.Collapsed;
            UpdateLed.ToolTip = null;
            AboutHeaderBtn.ToolTip = T("Open About CyberSnap");
        }

        // Shorter labels for buttons
        AreaCaptureText.Text = T("Area");
        ScrollCaptureText.Text = "Scrolling";
        OcrText.Text = T("OCR");
        QrText.Text = T("QR");
        ColorPickerText.Text = T("Color");
        RulerText.Text = T("Ruler");
        AnnotationsText.Text = T("Editor");
        GalleryText.Text = T("Gallery");
        
        // Tooltips — short action descriptions (labels stay compact on the tiles).
        AreaCaptureBtn.ToolTip = T("Capture a rectangular region of the screen");
        ScrollCaptureBtn.ToolTip = T("Capture a long scrolling page or window");
        OcrBtn.ToolTip = T("Extract text from a region of the screen");
        QrBtn.ToolTip = T("Scan QR codes and barcodes on screen");
        ColorPickerBtn.ToolTip = T("Capture a color sample from the screen");
        RulerBtn.ToolTip = T("Measure distances on the screen");
        AnnotationsBtn.ToolTip = T("Open the annotations editor");
        GalleryBtn.ToolTip = T("Browse and manage your captures");
        GifRecordBtn.ToolTip = T("Record the screen as an animated GIF");
        
        // Compact labels for the half-width row; full names live in tooltips.
        SettingsText.Text = T("Settings");
        SettingsBtn.ToolTip = T("Open CyberSnap settings");
        ExitText.Text = T("Exit");
        ExitBtn.ToolTip = T("Quit CyberSnap");

        // Determine recording state and localize record button
        bool isRecording = Capture.RecordingForm.Current != null;
        if (isRecording)
        {
            VideoRecordText.Text = T("Stop recording");
            VideoRecordBtn.ToolTip = T("Stop the current screen recording");
            VideoRecordBtn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            GifRecordBtn.IsEnabled = false;
        }
        else
        {
            VideoRecordText.Text = T("Record");
            VideoRecordBtn.ToolTip = T("Record the screen as an MP4 video");
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

        AppLogoImage.Source = ThemedLogo.Square(16);

        AreaCaptureIcon.Source = GetIcon("captureRect", accentColor, 32);
        ScrollCaptureIcon.Source = GetIcon("scrollCapture", fgColor, 20);
        OcrIcon.Source = GetIcon("ocr", fgColor, 20);
        QrIcon.Source = GetIcon("scan", fgColor, 20);
        ColorPickerIcon.Source = GetIcon("picker", fgColor, 20);
        RulerIcon.Source = GetIcon("ruler", fgColor, 20);
        // Bottom row: larger assets for accessibility while keeping compact tile height.
        AnnotationsIcon.Source = GetIcon("compose", fgColor, 20);
        GalleryIcon.Source = GetIcon("history", fgColor, 20);

        SettingsIcon.Source = GetIcon("gear", fgColor, 20);
        ExitIcon.Source = GetDangerIcon("signOut", 20);

        bool isRecording = Capture.RecordingForm.Current != null;
        if (isRecording)
        {
            VideoRecordIcon.Source = GetDangerIcon("play", 32);
        }
        else
        {
            VideoRecordIcon.Source = GetIcon("record", fgColor, 32);
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
    }

    private void AreaCapture_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerCapture();
    }

    private void ScrollCapture_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerScrollCapture();
    }

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerOcr();
    }

    private void QrScan_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerScan();
    }

    private void ColorPicker_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerColorPicker();
    }

    private void Ruler_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerRuler();
    }

    private void Annotations_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerAnnotationEditor();
    }

    private void Gallery_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerHistory();
    }

    private void VideoRecord_Click(object sender, RoutedEventArgs e)
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

    private void GifRecord_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerRecord(RecordingFormat.GIF);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerSettings();
    }

    private void AboutHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseMenu();
        _trayIcon.TriggerAbout();
    }

    private void AboutHeader_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        System.Windows.Controls.ToolTipService.SetIsEnabled(AboutHeaderBtn, true);
        AboutHeaderBtn.Background = Theme.Brush(Theme.TabHoverBg);
        TitleTextBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextPrimaryBrush");
        AppLogoImage.Opacity = 1;
    }

    private void AboutHeader_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AboutHeaderBtn.Background = System.Windows.Media.Brushes.Transparent;
        TitleTextBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextSecondaryBrush");
        AppLogoImage.Opacity = 0.75;
        DismissAboutHeaderToolTip();
    }

    private void TrayCloseBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        DismissAboutHeaderToolTip();
    }

    private void DismissAboutHeaderToolTip()
    {
        // Force-hide sticky WPF tooltips when moving onto the adjacent close button.
        if (AboutHeaderBtn.ToolTip is System.Windows.Controls.ToolTip tip)
            tip.IsOpen = false;

        System.Windows.Controls.ToolTipService.SetIsEnabled(AboutHeaderBtn, false);
        System.Windows.Controls.ToolTipService.SetIsEnabled(AboutHeaderBtn, true);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _trayIcon.TriggerQuit();
    }
}
