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
    private BarcodeFormat _format;
    private string? _copiedText;
    private bool _suppressDeliveryOptions;

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
        _tabs = new ResultTabHost<QrResultSession>(this, ResultTabStrip,
            new QrResultSession(codeText, format, previewSource, alreadyCopiedText, _nextOrdinal++),
            session => $"{GetFormatLabel(session.Format)} {session.Ordinal}",
            CaptureSession, ApplySession, _ => { });
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

        RefreshDeliveryOptions();
        SettingsService.AutoCopyToClipboardChanged += OnScanAutoCopySettingChanged;

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
        Resources["ThemeBgHoverBrush"] = Theme.Brush(Theme.BgHover);
        CheckerboardHost.Background = Theme.CreateCheckerboardBrush();
        Icon = WindowIcons.Wpf(WindowIconKind.Qr);
        QrTitleBar.RefreshIcons();
        _tabs?.Refresh();
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
        _tabs?.Refresh();
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
            _tabs.CloseActive();
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
            ApplyCopyActionState();
            ToastWindow.ShowError(
                "Copy failed",
                "CyberSnap could not copy the code. Keep this window open and try again.");
            return;
        }

        _copiedText = text;
        ApplyCopyActionState();
        if (closeAfter)
            _tabs.CloseActive();
    }

    private bool IsCodeCopied() =>
        ResultDelivery.MatchesCopied(ContentTextBox.Text, _copiedText);

    private void ApplyCopyActionState()
    {
        if (CopyButtonText is null || CopyStatusText is null || CopyOnlyButton is null)
            return;

        // The CTA morphs to a compact "Close" when copied and the badge sits
        // by the toggle that caused it, like the OCR window.
        bool copied = IsCodeCopied();
        CopyStatusText.Visibility = copied ? Visibility.Visible : Visibility.Collapsed;
        CopyStatusLabel.Text = LocalizationService.Translate("Copied");
        CopyButtonText.Text = LocalizationService.Translate(copied ? "Close" : "Copy and close");
        CopyButtonIcon.Visibility = copied ? Visibility.Collapsed : Visibility.Visible;
        // The ghost copy is pointless while the box holds exactly what is already
        // on the clipboard (e.g. just auto-copied): it wakes up on the first edit.
        CopyOnlyButton.IsEnabled = !copied && !string.IsNullOrWhiteSpace(ContentTextBox.Text);
    }

    private void RefreshDeliveryOptions()
    {
        _settingsService.RefreshResultDeliveryPreferences();
        var plan = ResultDelivery.ForScan(_settingsService.Settings);

        _suppressDeliveryOptions = true;
        try
        {
            AutoCopyCheck.IsChecked = plan.Copy;
            ShowWindowCheck.IsChecked = plan.ShowWindow;
        }
        finally { _suppressDeliveryOptions = false; }
    }

    private void AutoCopyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDeliveryOptions) return;

        // Independent kind toggle: enabling also lifts the master (SetKindEnabled),
        // so this switch never lies with a snap-back. Disabling only excludes scans.
        _settingsService.RefreshResultDeliveryPreferences();
        var settings = _settingsService.Settings;
        Helpers.AutoCopyPreferences.SetKindEnabled(settings, Helpers.AutoCopyKind.Scan, AutoCopyCheck.IsChecked == true);
        var plan = ResultDelivery.Normalize(
            Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Scan),
            settings.ScanShowResultWindow);
        settings.ScanShowResultWindow = plan.ShowWindow;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("qr-result.auto-copy", ex); }
        SettingsService.PublishAutoCopyState(settings);
        RefreshDeliveryOptions();
    }

    private void OnScanAutoCopySettingChanged(bool _)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => RefreshDeliveryOptions()));
            return;
        }
        RefreshDeliveryOptions();
    }

    internal bool IsShowResultWindow => _settingsService.Settings.ScanShowResultWindow;

    /// <summary>
    /// Title-bar More menu counterpart of the auto-copy toggle. Interlocked
    /// pair like the OCR and color picker windows: hiding the window while
    /// auto-copy is off enables auto-copy instead of bricking both
    /// deliveries. Returns the effective value so the menu checkmark and both
    /// footer toggles stay in sync with published preferences.
    /// </summary>
    internal bool SetShowResultWindow(bool show)
    {
        _settingsService.RefreshResultDeliveryPreferences();
        var settings = _settingsService.Settings;
        if (!show && !Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Scan))
        {
            Helpers.AutoCopyPreferences.SetKindEnabled(settings, Helpers.AutoCopyKind.Scan, true);
            settings.ScanShowResultWindow = false;
        }
        else
        {
            settings.ScanShowResultWindow = show;
        }
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("qr-result.show-window", ex); }
        SettingsService.PublishAutoCopyState(settings);
        RefreshDeliveryOptions();
        return settings.ScanShowResultWindow;
    }

    private void ShowWindowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppressDeliveryOptions)
            SetShowResultWindow(ShowWindowCheck.IsChecked == true);
    }

    private void DeliveryLabel_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Label { Target: System.Windows.Controls.CheckBox toggle })
        {
            toggle.IsChecked = toggle.IsChecked != true;
            toggle.Focus();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SettingsService.AutoCopyToClipboardChanged -= OnScanAutoCopySettingChanged;
        base.OnClosed(e);
    }

    private void TitleBar_CloseRequested(object? sender, EventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_tabs.HandleHotkey(e))
            return;
        if (e.Key == Key.Escape)
        {
            _tabs.CloseActive();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            CopyButton_Click(CopyButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
