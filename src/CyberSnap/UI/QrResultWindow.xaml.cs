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
    private readonly BarcodeFormat _format;
    private string? _copiedText;
    private bool _copyFailed;

    public QrResultWindow(
        string codeText,
        BarcodeFormat format,
        SettingsService settingsService,
        ImageSource? previewSource = null,
        string? alreadyCopiedText = null)
    {
        _settingsService = settingsService;
        _format = format;
        _copiedText = alreadyCopiedText;
        InitializeComponent();
        CyberSnapWindowChrome.Apply(this);
        UiScale.Set(settingsService.Settings.UiScale);
        UiScale.ApplyToWindow(this, RootBorder, scaleWindowBounds: true);

        ContentTextBox.Text = codeText;
        ApplyCopyActionState();
        PreviewImage.Source = previewSource;
        PreviewImage.Visibility = previewSource is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewUnavailableText.Visibility = previewSource is null ? Visibility.Visible : Visibility.Collapsed;

        Theme.Refresh();
        ApplyTheme();
        RefreshLocalization();

        Activated += (_, _) => ApplyTheme();

        Loaded += (_, _) => CyberSnapWindowChrome.EnsureForeground(this, CopyButton);
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

    public void ApplyTheme()
    {
        Theme.Refresh();
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
        Resources["ThemeAccentSubtleBrush"] = Theme.Brush(Theme.AccentSubtle);
        Resources["ThemeAccentForegroundBrush"] = Theme.Brush(Theme.AccentForeground);
        Resources["ThemeAccentHoverBrush"] = Theme.Brush(Theme.AccentHover);
        Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
        CheckerboardHost.Background = Theme.CreateCheckerboardBrush();
        Icon = WindowIcons.Wpf(WindowIconKind.Qr);
        QrTitleBar.RefreshIcons();
    }

    public void RefreshLocalization()
    {
        var language = _settingsService.Settings.InterfaceLanguage;
        LocalizationService.ApplyCurrentCulture(language);
        LocalizationService.ApplyTo(this, language);
        WindowTitles.ApplyTaskbar(this, WindowTitles.Qr, language);
        QrTitleBar.Title = LocalizationService.Translate(language, WindowTitles.Qr);
        FormatText.Text = GetFormatLabel(_format);
        DetectedTypeLabel.Text = LocalizationService.Translate(language, "Detected type");
        ContentLabel.Text = LocalizationService.Translate(language, "Code content");
        PreviewUnavailableText.Text = LocalizationService.Translate(language, "Source preview unavailable");
        CopyOnlyButtonText.Text = LocalizationService.Translate(language, "Copy");
        CopyOnlyButton.ToolTip = LocalizationService.Translate(language, "Copy the text and keep this window open.");
        ApplyCopyActionState();
        QrTitleBar.CloseToolTip = LocalizationService.Translate(language, "Close");
        QrTitleBar.RefreshTooltips();
    }

    private string GetFormatLabel(BarcodeFormat format)
    {
        var language = _settingsService.Settings.InterfaceLanguage;
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
        return LocalizationService.Translate(language, key);
    }

    private void CopyOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        CopyCode(closeAfter: false);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsCodeCopied())
        {
            Close();
            return;
        }

        CopyCode(closeAfter: true);
    }

    private void CopyCode(bool closeAfter)
    {
        var text = ContentTextBox.Text;
        if (!ResultDelivery.TryCopyText(text))
        {
            _copiedText = null;
            _copyFailed = true;
            ApplyCopyActionState();
            return;
        }

        _copyFailed = false;

        _copiedText = text;
        ApplyCopyActionState();
        if (closeAfter)
            Close();
    }

    private bool IsCodeCopied() =>
        ResultDelivery.MatchesCopied(ContentTextBox.Text, _copiedText);

    private void ApplyCopyActionState()
    {
        if (CopyButtonText is null)
            return;

        bool copied = IsCodeCopied();
        if (copied)
            _copyFailed = false;
        CopyStatusLabel.Text = LocalizationService.Translate(copied ? "Copied" : "Copy failed");
        CopyStatusText.Visibility = copied || _copyFailed ? Visibility.Visible : Visibility.Collapsed;
        CopyButtonText.Text = LocalizationService.Translate(copied ? "Close" : "Copy and close");
        CopyButtonIcon.Visibility = copied ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TitleBar_CloseRequested(object? sender, EventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            CopyButton_Click(CopyButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
