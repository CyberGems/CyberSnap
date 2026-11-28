using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.UI.Controls;

internal static partial class Program
{
    private static void CheckGitHubStarSurfaces(SettingsService settings, string? renders)
    {
        string languageBefore = LocalizationService.CurrentLanguageSetting;
        string settingBefore = settings.Settings.InterfaceLanguage;
        var themeBefore = settings.Settings.ThemeMode;
        try
        {
            Assert(FluentIcons.HasIcon("star"), "the shared icon catalog supplies the star");
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Grayscale, AppThemeMode.Light })
            foreach (string language in new[] { "en", "es" })
            {
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.ThemeMode = theme;
                Theme.SetMode(theme);
                LocalizationService.ApplyCurrentCulture(language);
                string expectedStar = language == "es" ? "Dar una estrella en GitHub" : "Star on GitHub";
                string expectedHelp = language == "es" ? "Ayuda" : "Help";
                string context = $"{theme}-{language}";
                var about = new AboutWindow(settings) { Width = 600, Height = 680 };
                try
                {
                    about.Show();
                    Pump();
                    Assert(LocalizationService.CurrentLanguageCode == language, "the fixture retains the requested UI language: " + context);
                    var star = Element<Button>(about, "AboutFooterStarBtn");
                    Assert(AutomationProperties.GetName(star) == expectedStar, "About star has an explicit accessible name: " + context);
                    Assert(star.IsVisible && star.ActualWidth > 0, "About exposes a star button: " + context);
                    Assert((string)star.ToolTip == expectedStar, "About star is localized: " + context);
                    var bounds = AboutBounds(star, about);
                    Assert(!bounds.IntersectsWith(AboutBounds(Element<Border>(about, "AboutFooterGithubBtn"), about)), "About footer links do not overlap: " + context);
                    Render(about, renders, "github-about-" + context + ".png");

                    var title = new CyberSnapTitleBar();
                    var buildHelp = typeof(CyberSnapTitleBar).GetMethod("CreateHelpSubmenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var help = (MenuItem)buildHelp.Invoke(title, new object?[] { new ContextMenu(), WikiLinks.HomePage, "Documentation & Wiki", null })!;
                    Assert(help.Items[0] is MenuItem item && (string)item.Header == expectedStar, "titlebar Help starts with the star: " + context);
                    Assert(help.Items[1] is Separator, "titlebar star has its own separator: " + context);
                }
                finally { about.Close(); Pump(); }

                var tray = new TrayContextMenuWindow(null!, new System.Drawing.Point(400, 400));
                try
                {
                    tray.Show();
                    Pump();
                    Call(tray, "PanelMenu_Click", tray, new RoutedEventArgs());
                    Pump();
                    var popup = (Popup)typeof(TrayContextMenuWindow).GetField("_panelPopup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
                    Assert(popup.IsOpen, "tray More opens: " + context);
                    var shell = (Border)popup.Child;
                    var rows = (StackPanel)shell.Child;
                    var helpRow = (Button)rows.Children[^2];
                    var helpItems = (StackPanel)rows.Children[^1];
                    Assert(AutomationProperties.GetName(helpRow) == expectedHelp, "tray More contains Help: " + context);
                    helpRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump();
                    Assert(helpItems.IsVisible, "tray Help expands inside More: " + context);
                    Assert(AutomationProperties.GetName((Button)helpItems.Children[0]) == expectedStar, "tray Help starts with the star: " + context);
                    Assert(helpItems.Children[1] is Border divider && divider.Height == 1, "tray star has its own separator: " + context);
                    Render(shell, renders, "github-tray-help-" + context + ".png");
                }
                finally { tray.Close(); Pump(); }
            }
        }
        finally
        {
            settings.Settings.InterfaceLanguage = settingBefore;
            settings.Settings.ThemeMode = themeBefore;
            LocalizationService.ApplyCurrentCulture(languageBefore);
            Theme.SetMode(themeBefore);
        }
    }
}
