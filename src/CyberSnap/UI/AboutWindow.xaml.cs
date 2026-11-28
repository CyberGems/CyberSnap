using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Services;
using MediaBrush = System.Windows.Media.Brush;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Shape = System.Windows.Shapes.Shape;

namespace CyberSnap.UI;

public partial class AboutWindow : Window
{
    private const string RepoUrl = "https://github.com/CyberGems/CyberSnap";
    private const string WebsiteUrl = "https://cybergems.org";

    private const double SuiteIdleOpacity = 0.38;

    private readonly SettingsService _settingsService;
    private bool _suiteBuilt;
    private DropShadowEffect? _aboutLogoGlow;
    private UpdateCheckResult? _lastUpdateResult;
    private bool _checkingForUpdates;
    private DispatcherTimer? _diagTipTimer;

    public AboutWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _lastUpdateResult = (Application.Current as App)?.LastUpdateCheck;
        InitializeComponent();
        CyberSnapWindowChrome.Apply(this);
        Theme.Refresh();
        Theme.ApplyTo(Application.Current.Resources);
        ApplyThemeColors();
        LoadContent();
        UpdateWindowTitle();
        Loaded += (_, _) =>
        {
            ApplyMicaBackdrop();
            // Physical-pixel centering (not DIP math): DIP placement drifts on mixed-DPI
            // setups when the window starts on another monitor (e.g. 150% left + 125% right).
            PopupWindowHelper.CenterWindowOnPhysicalMonitor(this);
            StartAboutLogoGlow();
        };
    }

    public void ApplyThemeColors()
    {
        Theme.Refresh();
        Resources["ThemeTextPrimaryBrush"] = Theme.Brush(Theme.TextPrimary);
        Resources["ThemeTextSecondaryBrush"] = Theme.Brush(Theme.TextSecondary);
        Resources["ThemeMutedBrush"] = Theme.Brush(Theme.TextMuted);
        Resources["ThemeCardBrush"] = Theme.Brush(Theme.CardBg);
        Resources["ThemeTabActiveBrush"] = Theme.Brush(Theme.TabActiveBg);
        Resources["ThemeTabHoverBrush"] = Theme.Brush(Theme.TabHoverBg);
        Resources["ThemeInputBackgroundBrush"] = Theme.Brush(Theme.BgSecondary);
        Resources["ThemeInputBorderBrush"] = Theme.Brush(Theme.BorderSubtle);
        Resources["ThemeWindowBorderBrush"] = Theme.Brush(Theme.WindowBorder);
        Resources["ThemePanelBackgroundBrush"] = Theme.Brush(Theme.BgPrimary);
        Resources["ThemeAccentBrush"] = Theme.Brush(Theme.Accent);
        Resources["ThemeAccentSubtleBrush"] = Theme.Brush(Theme.AccentSubtle);
        Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
        OuterBorder.Background = Theme.Brush(Theme.BgPrimary);
        OuterBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
        Icon = WindowIcons.Wpf(WindowIconKind.About);
        if (_aboutLogoGlow != null)
            _aboutLogoGlow.Color = Theme.Accent;
        Foreground = Theme.Brush(Theme.TextPrimary);
        UiScale.ApplyToWindow(this, OuterBorder, scaleWindowBounds: false);
        ApplyThemeToVisualTree(OuterBorder);
        ResetFooterVisuals();
    }

    public void RefreshLocalization()
    {
        LocalizationService.ApplyCurrentCulture(_settingsService.Settings.InterfaceLanguage);
        LocalizationService.ApplyTo(this, _settingsService.Settings.InterfaceLanguage);
        RefreshAboutLocalization();
        UpdateWindowTitle();
        LoadVersionLabels();
    }

    private void LoadContent()
    {
        LoadVersionLabels();
        RefreshLocalization();
        EnsureSuiteApps();
        ApplySuiteVisibility();
    }

    private void LoadVersionLabels()
    {
        var label = UpdateService.GetCurrentVersionLabel();
        AboutVersionText.Text = $"Version {label}";
    }

    private void RefreshAboutLocalization()
    {
        try
        {
            AboutDescriptionText.Text = LocalizationService.Translate(LocalizationService.GetSourceText(AboutDescriptionText));
            AboutUpdatesSectionLabel.Text = LocalizationService.Translate("Updates & Maintenance");
            RefreshUpdateSection();
            AboutTitleBar.Title = LocalizationService.Translate("About CyberSnap");
            AboutFooterCopyright.ToolTip = LocalizationService.Translate("Visit CyberGems website");
            AboutFooterWebsiteBtn.ToolTip = LocalizationService.Translate("Visit CyberGems website");
            AboutFooterDocsBtn.ToolTip = LocalizationService.Translate("Open the online documentation");
            AboutFooterGithubBtn.ToolTip = LocalizationService.Translate("View project on GitHub");
            System.Windows.Automation.AutomationProperties.SetName(AboutFooterStarBtn,
                LocalizationService.Translate(LocalizationService.GetSourceToolTip(AboutFooterStarBtn)));
            AboutFooterIssuesBtn.ToolTip = LocalizationService.Translate("Report a bug or open an issue");
            AboutFooterDiagBtn.ToolTip = LocalizationService.Translate("Copy diagnostic information");
            AboutFooterReleasesBtn.ToolTip = LocalizationService.Translate("View releases and changelogs");
            AboutFooterDonateBtn.ToolTip = LocalizationService.Translate("Donate to project");
            SuiteSectionLabel.Text = LocalizationService.Translate("More apps from CyberGems");
            SuiteMoreBtn.Content = LocalizationService.Translate("See more...");
            SuiteMoreBtn.ToolTip = LocalizationService.Translate("Visit CyberGems website");
            System.Windows.Automation.AutomationProperties.SetName(SuiteMoreBtn, LocalizationService.Translate("See more..."));
            RefreshSuiteTooltips();
            ApplySuiteVisibility();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("about.localization", ex);
        }
    }

    private void UpdateWindowTitle()
    {
        var aboutLabel = LocalizationService.Translate("About CyberSnap");
        AboutTitleBar.Title = aboutLabel;
        WindowTitles.ApplyTaskbar(this, WindowTitles.About, _settingsService.Settings.InterfaceLanguage);
    }

    private void StartAboutLogoGlow()
    {
        var glow = new DropShadowEffect
        {
            Color = Theme.Accent,
            Direction = 0,
            ShadowDepth = 0,
            BlurRadius = 18,
            Opacity = 0.36,
            RenderingBias = RenderingBias.Performance
        };
        AboutLogoImage.Effect = glow;
        _aboutLogoGlow = glow;

        glow.BeginAnimation(
            DropShadowEffect.OpacityProperty,
            new DoubleAnimation
            {
                From = 0.28,
                To = 0.54,
                Duration = TimeSpan.FromSeconds(2.4),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    private void ApplyThemeToVisualTree(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case System.Windows.Controls.Button button when button.Style == null:
                    button.Background = Theme.Brush(Theme.AccentSubtle);
                    button.Foreground = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    button.BorderBrush = (MediaBrush)Resources["ThemeInputBorderBrush"];
                    break;
                case System.Windows.Controls.CheckBox checkBox:
                    checkBox.Foreground = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    break;
            }
            ApplyThemeToVisualTree(child);
        }
    }

    private void ApplyMicaBackdrop()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.Dwm.DisableBackdrop(hwnd);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("about.backdrop", ex.Message, ex);
        }
    }

    private void TitleBar_CloseRequested(object? sender, EventArgs e) => Close();

    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e) =>
        await RunUpdateCheckAsync();

    internal async Task RunUpdateCheckAsync()
    {
        if (_checkingForUpdates || Application.Current is not App app) return;
        _checkingForUpdates = true;
        RefreshUpdateSection();
        try
        {
            await app.CheckForUpdatesAsync(manual: true, PopupWindowHelper.GetWindowMonitorAnchor(this));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("updates.about", ex);
        }
        finally
        {
            _checkingForUpdates = false;
            if (IsLoaded) RefreshUpdateSection();
        }
    }

    internal void ApplyUpdateCheckResult(UpdateCheckResult? result)
    {
        _lastUpdateResult = result;
        RefreshUpdateSection();
    }

    private void RefreshUpdateSection()
    {
        AboutUpdateTitle.Text = LocalizationService.Translate("Check for updates");
        AboutUpdateDesc.Text = _checkingForUpdates
            ? LocalizationService.Translate("Checking for updates...")
            : _lastUpdateResult?.GetStatusMessage() ?? LocalizationService.Translate("Check for the latest version");
        UpdateBtn.Content = LocalizationService.Translate(_checkingForUpdates ? "Checking..." : "Check Now");
        UpdateBtn.IsEnabled = !_checkingForUpdates;
        System.Windows.Automation.AutomationProperties.SetName(UpdateBtn, UpdateBtn.Content.ToString());
    }

    private void AboutFooterWebsite_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenUrl(WebsiteUrl);
    }

    private void AboutFooterStar_Click(object sender, RoutedEventArgs e) => GiveGitHubStar();

    private async void GiveGitHubStar()
    {
        if (!AboutFooterStarBtn.IsEnabled) return;
        AboutFooterStarBtn.IsEnabled = false;
        LocalizationService.SetSourceToolTip(AboutFooterStarBtn, "Starring…");
        AboutFooterStarBtn.ToolTip = LocalizationService.Translate("Starring…");
        var result = await GitHubStarService.StarAsync();
        var source = result switch
        {
            GitHubStarResult.Starred => "Starred, thank you",
            GitHubStarResult.Opened => "Complete your star on GitHub",
            _ => "Could not open GitHub. Retry"
        };
        LocalizationService.SetSourceToolTip(AboutFooterStarBtn, source);
        AboutFooterStarBtn.ToolTip = LocalizationService.Translate(source);
        System.Windows.Automation.AutomationProperties.SetName(AboutFooterStarBtn, LocalizationService.Translate(source));
        if (result == GitHubStarResult.Starred)
            AboutFooterStarIcon.SetResourceReference(Shape.FillProperty, "ThemeAccentBrush");
        AboutFooterStarBtn.IsEnabled = result != GitHubStarResult.Starred;
    }

    private void AboutFooterGithub_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenUrl(RepoUrl);
    }

    private void AboutFooterIssues_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenUrl($"{RepoUrl}/issues");
    }

    private void AboutFooterDiag_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            ClipboardService.CopyTextToClipboard(BuildDiagnosticReport());
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("about.copy-diagnostics", ex);
            return;
        }

        var copied = LocalizationService.Translate("Diagnostic info copied to clipboard!");
        AboutFooterDiagBtn.ToolTip = copied;
        _diagTipTimer?.Stop();
        _diagTipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _diagTipTimer.Tick += (_, _) =>
        {
            _diagTipTimer?.Stop();
            AboutFooterDiagBtn.ToolTip = LocalizationService.Translate("Copy diagnostic information");
        };
        _diagTipTimer.Start();
    }

    private void SuiteMoreBtn_Click(object sender, RoutedEventArgs e) => OpenUrl(WebsiteUrl);

    /// <summary>Shows or hides the sister-app row from the current setting.</summary>
    public void ApplySuiteVisibility()
    {
        SuiteSection.Visibility = _settingsService.Settings.ShowSuiteRecommendations
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void EnsureSuiteApps()
    {
        if (_suiteBuilt)
        {
            RefreshSuiteTooltips();
            return;
        }

        _suiteBuilt = true;
        SuiteAppsHost.Children.Clear();
        if (FindResource("SuiteAppButton") is not Style style)
            return;

        foreach (var app in SuiteCatalog.Pick(5))
        {
            BitmapImage? bitmap;
            try
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri($"pack://application:,,,/Assets/Suite/{app.Slug}.png", UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogWarning("about.suite-icon", app.Slug + ": " + ex.Message, ex);
                continue;
            }

            var image = new System.Windows.Controls.Image
            {
                Width = 30,
                Height = 30,
                Stretch = Stretch.Uniform,
                Opacity = SuiteIdleOpacity,
                IsHitTestVisible = false,
                Source = bitmap
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var button = new System.Windows.Controls.Button
            {
                Style = style,
                Content = image,
                Tag = app
            };
            System.Windows.Automation.AutomationProperties.SetName(button, app.Name);
            button.ToolTip = SuiteTooltip(app);
            button.MouseEnter += (_, _) => FadeSuiteIcon(image, 1);
            button.MouseLeave += (_, _) => FadeSuiteIcon(image, SuiteIdleOpacity);
            button.Click += (_, _) => OpenUrl(app.Site);
            SuiteAppsHost.Children.Add(button);
        }
    }

    private void RefreshSuiteTooltips()
    {
        foreach (var child in SuiteAppsHost.Children)
        {
            if (child is System.Windows.Controls.Button button && button.Tag is SuiteApp app)
                button.ToolTip = SuiteTooltip(app);
        }
    }

    private static string SuiteTooltip(SuiteApp app)
    {
        var pitch = LocalizationService.CurrentLanguageCode.StartsWith("es", StringComparison.OrdinalIgnoreCase)
            ? app.PitchEs
            : app.PitchEn;
        return $"{app.Name}: {pitch}";
    }

    private static void FadeSuiteIcon(System.Windows.Controls.Image image, double to)
    {
        image.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private string BuildDiagnosticReport()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var dpi = VisualTreeHelper.GetDpi(this);
        var ffmpeg = VideoRecorder.FindFfmpeg();
        var themeSetting = _settingsService.Settings.ThemeMode.ToString();
        var themeEffective = Theme.IsGray ? "grayscale" : Theme.IsDark ? "dark" : "light";
        var install = InstallService.IsInstalled() ? "installed" : "portable or development";

        return string.Join(Environment.NewLine,
            "CyberSnap diagnostic report",
            "",
            $"- Version: {UpdateService.GetCurrentVersionLabel()}",
            $"- Install: {install}",
            $"- OS: {RuntimeInformation.OSDescription}",
            $"- Display: {screen.Bounds.Width}x{screen.Bounds.Height} at ({screen.Bounds.X}, {screen.Bounds.Y}), DPI {dpi.PixelsPerInchX:0} ({dpi.DpiScaleX * 100:0}%)",
            $"- Language: {_settingsService.Settings.InterfaceLanguage} (resolved {LocalizationService.CurrentLanguageCode})",
            $"- Theme: {themeSetting} (effective {themeEffective})",
            $"- Check for updates on startup: {OnOff(_settingsService.Settings.AutoCheckForUpdates)}",
            $"- Suite suggestions: {OnOff(_settingsService.Settings.ShowSuiteRecommendations)}",
            $"- FFmpeg: {(string.IsNullOrWhiteSpace(ffmpeg) ? "not found" : ffmpeg)}",
            $"- Runtime: {RuntimeInformation.FrameworkDescription}",
            $"- Local time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ({TimeZoneInfo.Local.DisplayName})");
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private void AboutFooterReleases_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenUrl($"{RepoUrl}/releases");
    }

    private void AboutFooterDocs_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CyberSnap.Helpers.WikiLinks.Open(CyberSnap.Helpers.WikiLinks.HomePage);
    }

    private void AboutFooterDonate_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CyberSnap.Helpers.DonationLinks.Open();
    }

    private void AboutFooterCopyright_MouseEnter(object sender, MouseEventArgs e)
    {
        AboutFooterCopyright.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextPrimaryBrush");
    }

    private void AboutFooterCopyright_MouseLeave(object sender, MouseEventArgs e)
    {
        AboutFooterCopyright.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeMutedBrush");
    }

    private void AboutFooterIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = Theme.Brush(Theme.ChromeHoverTile);
        SetFooterIconAccent(border, hovered: true);
    }

    private void AboutFooterIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = System.Windows.Media.Brushes.Transparent;
        SetFooterIconAccent(border, hovered: false);
    }

    private void SetFooterIconAccent(Border border, bool hovered)
    {
        // Hover follows the chrome ink: whitish in the default dark theme,
        // the accent family in grayscale and light (see Theme.ChromeHoverInk).
        var brushKey = hovered ? "ThemeChromeHoverInkBrush" : "ThemeMutedBrush";
        if (border == AboutFooterWebsiteBtn)
        {
            AboutFooterWebsiteIcon.SetResourceReference(Shape.StrokeProperty, brushKey);
        }
        else if (border == AboutFooterGithubBtn)
        {
            AboutFooterGithubIcon.SetResourceReference(Shape.FillProperty, brushKey);
        }
        else if (border == AboutFooterIssuesBtn)
        {
            AboutFooterIssuesIcon1.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterIssuesIcon2.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterIssuesIcon3.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterIssuesIcon4.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterIssuesIcon5.SetResourceReference(Shape.StrokeProperty, brushKey);
        }
        else if (border == AboutFooterDiagBtn)
        {
            AboutFooterDiagBody.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterDiagClip.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterDiagLines.SetResourceReference(Shape.StrokeProperty, brushKey);
        }
        else if (border == AboutFooterReleasesBtn)
        {
            AboutFooterTagBody.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterTagDot.SetResourceReference(Shape.FillProperty, brushKey);
        }
        else if (border == AboutFooterDocsBtn)
        {
            AboutFooterDocsBody.SetResourceReference(Shape.StrokeProperty, brushKey);
        }
        // Donate heart keeps its #F43F5E fill in both states (same as CyberWall).
    }

    private void ResetFooterVisuals()
    {
        AboutFooterCopyright.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeMutedBrush");
        AboutFooterWebsiteBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterGithubBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterIssuesBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterDiagBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterReleasesBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterDocsBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterDonateBtn.Background = System.Windows.Media.Brushes.Transparent;
        SetFooterIconAccent(AboutFooterWebsiteBtn, hovered: false);
        SetFooterIconAccent(AboutFooterGithubBtn, hovered: false);
        SetFooterIconAccent(AboutFooterIssuesBtn, hovered: false);
        SetFooterIconAccent(AboutFooterDiagBtn, hovered: false);
        SetFooterIconAccent(AboutFooterReleasesBtn, hovered: false);
        SetFooterIconAccent(AboutFooterDocsBtn, hovered: false);
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
