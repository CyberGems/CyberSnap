using System.Windows;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Services;
using MediaBrush = System.Windows.Media.Brush;

namespace CyberSnap.UI;

public partial class AchievementsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly System.Drawing.Point _openMonitorPoint;
    private bool _didCenterOnOpen;

    public AchievementsWindow(SettingsService settingsService, HistoryService historyService)
    {
        _settingsService = settingsService;
        _historyService = historyService;
        _openMonitorPoint = System.Windows.Forms.Cursor.Position;
        InitializeComponent();
        Opacity = 0;
        CyberSnapWindowChrome.Apply(this);
        Theme.Refresh();
        Theme.ApplyTo(Application.Current.Resources);
        ApplyThemeColors();
        UpdateWindowTitle();
        LocalizationService.ApplyCurrentCulture(_settingsService.Settings.InterfaceLanguage);
        LocalizationService.ApplyTo(this, _settingsService.Settings.InterfaceLanguage);
        AchievementsTitleBar.RefreshTooltips();

        ContentRendered += AchievementsWindow_ContentRendered;
        Activated += (_, _) =>
        {
            ApplyThemeColors();
            // Defer so layout/DPI (and Opacity→1) have settled — building Star-column
            // grids during the first Loaded pass can leave stats/medals with 0 width.
            Dispatcher.BeginInvoke(
                new Action(() => RefreshAchievementContent(revealRail: true)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
        Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(
                new Action(() => RefreshAchievementContent(revealRail: true)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    /// <summary>Rebuilds rail + stats + medals from live settings.</summary>
    public void RefreshAchievementContent(bool revealRail)
    {
        try
        {
            RefreshAchievements();
            RefreshMilestoneRail(reveal: revealRail);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("achievements.refresh-content", ex);
        }
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
        Icon = WindowIcons.Wpf(WindowIconKind.Achievements);
        Foreground = Theme.Brush(Theme.TextPrimary);
        UiScale.ApplyToWindow(this, OuterBorder, scaleWindowBounds: false);
        AchievementsTitleBar.RefreshIcons();
    }

    public void RefreshLocalization()
    {
        LocalizationService.ApplyCurrentCulture(_settingsService.Settings.InterfaceLanguage);
        LocalizationService.ApplyTo(this, _settingsService.Settings.InterfaceLanguage);
        AchievementsTitleBar.RefreshTooltips();
        UpdateWindowTitle();
        RefreshAchievementContent(revealRail: false);
    }

    /// <summary>Called when Celebrations are toggled in Settings while this window is open.</summary>
    public void RefreshFromSettings()
    {
        RefreshAchievementContent(revealRail: true);
    }

    private void UpdateWindowTitle()
    {
        var label = LocalizationService.Translate("Achievements ᐧ CyberSnap");
        AchievementsTitleBar.Title = label;
        WindowTitles.ApplyTaskbar(this, WindowTitles.Achievements, _settingsService.Settings.InterfaceLanguage);
    }

    private void TitleBar_CloseRequested(object? sender, EventArgs e) => Close();

    private void AchievementsWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= AchievementsWindow_ContentRendered;
        Dispatcher.BeginInvoke(
            new Action(CenterOnOpenMonitor),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void CenterOnOpenMonitor()
    {
        if (_didCenterOnOpen)
            return;
        _didCenterOnOpen = true;

        try
        {
            UpdateLayout();
            PopupWindowHelper.CenterWindowOnPhysicalMonitor(this, _openMonitorPoint);
        }
        catch
        {
            // Retry below.
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                UpdateLayout();
                PopupWindowHelper.CenterWindowOnPhysicalMonitor(this, _openMonitorPoint);
            }
            catch
            {
                // Keep current position.
            }

            Opacity = 1;
            // Populate after the window is visible and sized on the target monitor.
            RefreshAchievementContent(revealRail: true);
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
}
