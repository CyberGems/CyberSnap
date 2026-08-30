using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using ZXing;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CyberSnap.UI;

public partial class QrResultWindow : Window
{
    private readonly SettingsService _settingsService;

    public QrResultWindow(
        string codeText,
        BarcodeFormat format,
        SettingsService settingsService,
        ImageSource? previewSource = null)
    {
        _settingsService = settingsService;
        InitializeComponent();
        CyberSnapWindowChrome.Apply(this);
        UiScale.Set(settingsService.Settings.UiScale);
        UiScale.ApplyToWindow(this, RootBorder, scaleWindowBounds: true);

        ContentTextBox.Text = codeText;
        PreviewImage.Source = previewSource;
        PreviewImage.Visibility = previewSource is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewUnavailableText.Visibility = previewSource is null ? Visibility.Visible : Visibility.Collapsed;

        Theme.Refresh();
        ApplyTheme();
        LocalizationService.ApplyCurrentCulture(settingsService.Settings.InterfaceLanguage);
        FormatText.Text = GetFormatLabel(format);

        var language = settingsService.Settings.InterfaceLanguage;
        WindowTitles.ApplyTaskbar(this, WindowTitles.Qr, language);
        QrTitleBar.Title = LocalizationService.Translate(language, WindowTitles.Qr);
        DetectedTypeLabel.Text = LocalizationService.Translate("Detected type");
        ContentLabel.Text = LocalizationService.Translate("Code content");
        PreviewUnavailableText.Text = LocalizationService.Translate("Source preview unavailable");
        CopyButtonText.Text = LocalizationService.Translate("Copy and close");
        CopyButton.ToolTip = LocalizationService.Translate("Copy this QR & Barcode text");
        QrTitleBar.CloseToolTip = LocalizationService.Translate("Close");

        Loaded += (_, _) =>
        {
            Topmost = true;
            Activate();
            Dispatcher.BeginInvoke(new Action(() => Topmost = false));
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<Native.User32.MINMAXINFO>(lParam);
        var monitor = Native.User32.MonitorFromWindow(hwnd, Native.User32.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new Native.User32.MONITORINFO
            {
                cbSize = Marshal.SizeOf<Native.User32.MONITORINFO>()
            };
            if (Native.User32.GetMonitorInfo(monitor, ref monitorInfo))
            {
                var work = monitorInfo.rcWork;
                var monitorBounds = monitorInfo.rcMonitor;
                mmi.ptMaxPosition.X = work.Left - monitorBounds.Left;
                mmi.ptMaxPosition.Y = work.Top - monitorBounds.Top;
                mmi.ptMaxSize.X = work.Width;
                mmi.ptMaxSize.Y = work.Height;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void ApplyTheme()
    {
        RootBorder.Background = Theme.Brush(Theme.BgPrimary);
        RootBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
        RootBorder.BorderThickness = new Thickness(1);
        Resources["ThemeTextPrimaryBrush"] = Theme.Brush(Theme.TextPrimary);
        Resources["ThemeTextSecondaryBrush"] = Theme.Brush(Theme.TextSecondary);
        Resources["ThemeMutedBrush"] = Theme.Brush(Theme.TextMuted);
        Resources["ThemeCardBrush"] = Theme.Brush(Theme.BgCard);
        Resources["ThemeInputBackgroundBrush"] = Theme.Brush(Theme.BgSecondary);
        Resources["ThemeInputBorderBrush"] = Theme.Brush(Theme.BorderSubtle);
        Resources["ThemeWindowBorderBrush"] = Theme.Brush(Theme.WindowBorder);
        Resources["ThemeAccentBrush"] = Theme.Brush(Theme.Accent);
        Resources["ThemeAccentHoverBrush"] = Theme.Brush(Theme.AccentHover);
        Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
        CheckerboardHost.Background = Theme.CreateCheckerboardBrush();
        Icon = WindowIcons.Wpf(WindowIconKind.Qr);
    }

    private string GetFormatLabel(BarcodeFormat format)
    {
        var key = format switch
        {
            BarcodeFormat.QR_CODE => "QR Code",
            BarcodeFormat.AZTEC => "Aztec",
            BarcodeFormat.DATA_MATRIX => "Data Matrix",
            BarcodeFormat.PDF_417 => "PDF417",
            BarcodeFormat.CODE_128 => "Code 128",
            BarcodeFormat.CODE_39 => "Code 39",
            BarcodeFormat.CODE_93 => "Code 93",
            BarcodeFormat.CODABAR => "Codabar",
            BarcodeFormat.ITF => "ITF",
            BarcodeFormat.EAN_13 => "EAN-13",
            BarcodeFormat.EAN_8 => "EAN-8",
            BarcodeFormat.UPC_A => "UPC-A",
            BarcodeFormat.UPC_E => "UPC-E",
            _ => "Barcode"
        };
        return LocalizationService.Translate(key);
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClipboardService.CopyTextToClipboard(ContentTextBox.Text);
            CopyButton.IsHitTestVisible = false;
            await Task.Delay(120);
            Close();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("qr-result.copy", ex.Message, ex);
            CopyStatusText.Text = LocalizationService.Translate("Copy failed");
            CopyButton.IsHitTestVisible = true;
        }
    }

    private void TitleBar_CloseRequested(object? sender, EventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
