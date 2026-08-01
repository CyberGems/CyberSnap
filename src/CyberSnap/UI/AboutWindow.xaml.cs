using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private readonly SettingsService _settingsService;
    private bool _suppressAutoCheckUpdateChange;

    public AboutWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
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
            PopupWindowHelper.CenterOnCurrentScreen(this);
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
        Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
        OuterBorder.Background = Theme.Brush(Theme.BgPrimary);
        OuterBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
        Icon = WindowIcons.Wpf(WindowIconKind.Main);
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

    public async Task StartUpdateDownloadAsync(UpdateCheckResult result)
    {
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateBtn.IsEnabled = false;
        SetFooterIconsEnabled(false);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var updatesFolder = Path.Combine(appData, "CyberSnap", "Updates");
        var filename = result.AssetName ?? $"cybersnap_setup_{UpdateService.GetRuntimeChannel()}.exe";
        var installerPath = Path.Combine(updatesFolder, filename);

        var progress = new Progress<double>(val =>
        {
            UpdateProgressBar.Value = val;
            UpdateProgressText.Text = string.Format(LocalizationService.Translate("Downloading update ({0:F1}%)..."), val);
        });

        try
        {
            UpdateProgressBar.Value = 0;
            UpdateProgressText.Text = LocalizationService.Translate("Downloading update (0.0%)...");

            if (string.IsNullOrEmpty(result.DownloadUrl))
                throw new Exception("Direct download link is not available for this release.");

            await UpdateService.DownloadUpdateAsync(result.DownloadUrl, installerPath, progress);

            UpdateProgressText.Text = LocalizationService.Translate("Download completed. Launching installer...");

            ThemedConfirmDialog.Alert(this,
                LocalizationService.Translate("Download Complete"),
                LocalizationService.Translate("The update has been successfully downloaded. CyberSnap will now close to continue the installation."),
                error: false);

            UpdateService.LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            UpdateBtn.IsEnabled = true;
            SetFooterIconsEnabled(true);

            var errorChoice = ThemedConfirmDialog.Confirm(this,
                LocalizationService.Translate("Download Failed"),
                string.Format(LocalizationService.Translate("Failed to download update automatically:\n{0}\n\nWould you like to open the GitHub release page instead?"), ex.Message),
                LocalizationService.Translate("Open Browser"),
                LocalizationService.Translate("Cancel"),
                danger: false);
            if (errorChoice)
                OpenUrl(result.ReleaseUrl);
        }
    }

    private void LoadContent()
    {
        _suppressAutoCheckUpdateChange = true;
        try
        {
            AutoCheckUpdateCheck.IsChecked = _settingsService.Settings.AutoCheckForUpdates;
            LoadVersionLabels();
            RefreshLocalization();
        }
        finally
        {
            _suppressAutoCheckUpdateChange = false;
        }
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
            AboutDescriptionText.Text = LocalizationService.Translate("CyberSnap is a professional screen capture suite for fast workflows — local OCR, instant translation, and a full gallery.");
            AboutUpdatesSectionLabel.Text = LocalizationService.Translate("Updates & Maintenance");
            AboutAutoUpdateTitle.Text = LocalizationService.Translate("Check for updates on startup");
            AboutAutoUpdateDesc.Text = LocalizationService.Translate("Automatically check for new versions when CyberSnap starts.");
            AutoCheckUpdateCheck.ToolTip = LocalizationService.Translate("Automatically check for new versions when CyberSnap starts.");
            AboutUpdateTitle.Text = LocalizationService.Translate("Check for updates");
            AboutUpdateDesc.Text = LocalizationService.Translate("Check for the latest version and download updates directly.");
            UpdateBtn.Content = LocalizationService.Translate("Check Now");
            UpdateBtn.ToolTip = LocalizationService.Translate("Check for the latest version");
            UpdateProgressText.Text = LocalizationService.Translate("Downloading update...");
            AboutTitleBar.Title = LocalizationService.Translate("About");
            AboutFooterCopyright.ToolTip = LocalizationService.Translate("Visit CyberGems website");
            AboutFooterWebsiteBtn.ToolTip = LocalizationService.Translate("Visit CyberGems website");
            AboutFooterGithubBtn.ToolTip = LocalizationService.Translate("View project on GitHub");
            AboutFooterIssuesBtn.ToolTip = LocalizationService.Translate("Report a bug or open an issue");
            AboutFooterReleasesBtn.ToolTip = LocalizationService.Translate("View releases and changelogs");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("about.localization", ex);
        }
    }

    private void UpdateWindowTitle()
    {
        var aboutLabel = LocalizationService.Translate("About");
        AboutTitleBar.Title = aboutLabel;
        WindowTitles.ApplyTaskbar(this, WindowTitles.About, _settingsService.Settings.InterfaceLanguage);
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

    private void AutoCheckUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressAutoCheckUpdateChange) return;

        var previous = _settingsService.Settings.AutoCheckForUpdates;
        var selected = AutoCheckUpdateCheck.IsChecked == true;
        try
        {
            _settingsService.Settings.AutoCheckForUpdates = selected;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("about.auto-check-updates", ex);
            _settingsService.Settings.AutoCheckForUpdates = previous;
            _suppressAutoCheckUpdateChange = true;
            try { AutoCheckUpdateCheck.IsChecked = previous; }
            finally { _suppressAutoCheckUpdateChange = false; }
            try { _settingsService.Save(); } catch { }
        }
    }

    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await UpdateService.CheckForUpdatesAsync();
        if (result.IsUpdateAvailable)
        {
            var msg = $"{result.StatusMessage}\n\nCurrent: {result.CurrentVersion}\nLatest: {result.LatestVersionLabel}\n\nDownload and install now?";
            var choice = ThemedConfirmDialog.Confirm(this,
                LocalizationService.Translate("Update available"),
                msg,
                LocalizationService.Translate("Download"),
                LocalizationService.Translate("Later"),
                danger: false);
            if (choice)
                await StartUpdateDownloadAsync(result);
        }
        else
        {
            ThemedConfirmDialog.Alert(this,
                LocalizationService.Translate("Check for Updates"),
                result.StatusMessage,
                error: false);
        }
    }

    private void AboutFooterWebsite_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenUrl(WebsiteUrl);
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

    private void AboutFooterReleases_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenUrl($"{RepoUrl}/releases");
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
        border.Background = Theme.Brush(Theme.TabHoverBg);
        SetFooterIconAccent(border, primary: true);
    }

    private void AboutFooterIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = System.Windows.Media.Brushes.Transparent;
        SetFooterIconAccent(border, primary: false);
    }

    private void SetFooterIconAccent(Border border, bool primary)
    {
        var brushKey = primary ? "ThemeTextPrimaryBrush" : "ThemeMutedBrush";
        if (border == AboutFooterWebsiteBtn)
        {
            AboutFooterWebsiteBox.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterWebsiteArrow.SetResourceReference(Shape.StrokeProperty, brushKey);
        }
        else if (border == AboutFooterGithubBtn)
        {
            AboutFooterGithubIcon.SetResourceReference(Shape.FillProperty, brushKey);
        }
        else if (border == AboutFooterIssuesBtn)
        {
            AboutFooterIssuesRing.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterIssuesDot.SetResourceReference(Shape.FillProperty, brushKey);
        }
        else if (border == AboutFooterReleasesBtn)
        {
            AboutFooterTagBody.SetResourceReference(Shape.StrokeProperty, brushKey);
            AboutFooterTagDot.SetResourceReference(Shape.FillProperty, brushKey);
        }
    }

    private void ResetFooterVisuals()
    {
        AboutFooterCopyright.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeMutedBrush");
        AboutFooterWebsiteBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterGithubBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterIssuesBtn.Background = System.Windows.Media.Brushes.Transparent;
        AboutFooterReleasesBtn.Background = System.Windows.Media.Brushes.Transparent;
        SetFooterIconAccent(AboutFooterWebsiteBtn, primary: false);
        SetFooterIconAccent(AboutFooterGithubBtn, primary: false);
        SetFooterIconAccent(AboutFooterIssuesBtn, primary: false);
        SetFooterIconAccent(AboutFooterReleasesBtn, primary: false);
    }

    private void SetFooterIconsEnabled(bool enabled)
    {
        AboutFooterWebsiteBtn.IsEnabled = enabled;
        AboutFooterGithubBtn.IsEnabled = enabled;
        AboutFooterIssuesBtn.IsEnabled = enabled;
        AboutFooterReleasesBtn.IsEnabled = enabled;
        AboutFooterCopyright.IsEnabled = enabled;
        AboutFooterWebsiteBtn.Opacity = enabled ? 1 : 0.45;
        AboutFooterGithubBtn.Opacity = enabled ? 1 : 0.45;
        AboutFooterIssuesBtn.Opacity = enabled ? 1 : 0.45;
        AboutFooterReleasesBtn.Opacity = enabled ? 1 : 0.45;
        AboutFooterCopyright.Opacity = enabled ? 1 : 0.45;
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
