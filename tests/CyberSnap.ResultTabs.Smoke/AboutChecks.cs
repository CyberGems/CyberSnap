using System.Windows;
using System.Windows.Controls;
using CyberSnap.Services;
using CyberSnap.UI;

internal static partial class Program
{
    private static void CheckAboutUpdates(SettingsService settings, string? renderDirectory)
    {
        const string notes = """
            - 💾 **Scheduled settings and achievement backups**
            - 🗂️ **Clearer backup controls and readable filenames**
            - 🖥️ **Tray and widget positioning across monitors**
            - ✅ **.NET runtime and portable packages**
            <details>
            <summary>Ver notas en Español</summary>
            - 💾 **Copias programadas de configuración y logros**
            - 🗂️ **Panel de respaldos y nombres de archivo más claros**
            - 🖥️ **Correcciones de posición del tray y del widget**
            - ✅ **.NET y paquetes portables**
            </details>
            """;
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        string originalLanguageOption = settings.Settings.InterfaceLanguage;
        string originalSkippedVersion = settings.Settings.SkippedUpdateVersion;
        double originalScale = UiScale.Current;
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (double scale in new[] { 1.0, 1.4 })
            foreach (double width in new[] { 600.0, 740.0 })
            {
                settings.Settings.InterfaceLanguage = language;
                LocalizationService.ApplyCurrentCulture(language);
                UiScale.Set(scale);
                var window = new AboutWindow(settings) { Width = width, Height = 680 };
                string context = $"{language}, width={width}, scale={scale}";
                bool render = width == 740 && scale == 1 || width == 600 && scale == 1.4;
                string suffix = language + (width == 600 ? "-narrow-large" : "");
                try
                {
                    window.Show();
                    Pump();
                    // Centering can move the HWND between 150% and 125% displays.
                    // Set the requested DIP width after that native DPI transition.
                    window.Width = width;
                    Pump();
                    Assert(Math.Abs(window.ActualWidth - width) < 1,
                        "the About fixture uses the requested window width after monitor centering: " + context);
                    var available = new UpdateCheckResult(new Version(1, 20, 1), new Version(1, 21, 0), "v1.21.0",
                        "https://example.invalid/release", null, null, null, null, true,
                        string.Format(LocalizationService.Translate("Update {0} available"), "v1.21.0"), notes);
                    Call(window, "ApplyUpdateCheckResult", available);
                    Pump();
                    var highlights = Element<ItemsControl>(window, "UpdateHighlightsList");
                    Assert(highlights.Items.Count == 4 && highlights.Items.Cast<string>().All(text => !text.StartsWith("•"))
                        && (string)highlights.Items[0] == (language == "es"
                            ? "Copias programadas de configuración y logros" : "Scheduled settings and achievement backups")
                        && ((string)highlights.Items[3]).StartsWith(".NET"),
                        "About highlights use the selected language, clean bullets and preserve meaningful punctuation: " + context);
                    CheckAboutUpdateBounds(window, context);
                    if (render) RenderAboutState(window, renderDirectory, "about-available-" + suffix + ".png");
                    if (width == 600 && scale == 1.4)
                        Assert(AboutBounds(Element<Button>(window, "SkipVersionBtn"), window).Top
                            > AboutBounds(Element<Button>(window, "UpdateBtn"), window).Top + 1,
                            "About update actions wrap at minimum width and large UI scale: " + context
                            + $", actualWidth={window.ActualWidth}, transform={Element<Border>(window, "OuterBorder").LayoutTransform.Value}, actions={AboutBounds(Element<WrapPanel>(window, "UpdateActions"), window)}");

                    Call(window, "ShowUpdateChecking");
                    window.RefreshUpdateState();
                    Pump();
                    Assert(!Element<Button>(window, "UpdateBtn").IsEnabled
                        && Element<TextBlock>(window, "AboutUpdateTitle").Text == LocalizationService.Translate("Checking for updates...")
                        && Element<Border>(window, "UpdateBadge").Visibility == Visibility.Collapsed
                        && highlights.Items.Count == 0,
                        "checking clears old release details and background refresh cannot replace it: " + context);
                    if (render) RenderAboutState(window, renderDirectory, "about-checking-" + suffix + ".png");

                    var current = available with { IsUpdateAvailable = false, LatestVersion = available.CurrentVersion,
                        StatusMessage = string.Format(LocalizationService.Translate("You're up to date on {0}"), "v1.20.1") };
                    Call(window, "ApplyUpdateCheckResult", current);
                    Call(window, "SetUpdateActionsEnabled", true);
                    Pump();
                    Assert(Element<TextBlock>(window, "AboutUpdateTitle").Text == LocalizationService.Translate("CyberSnap is up to date")
                        && Element<TextBlock>(window, "AboutUpdateDesc").Text == current.StatusMessage
                        && Element<Button>(window, "ViewReleaseBtn").Visibility == Visibility.Collapsed
                        && Element<Button>(window, "SkipVersionBtn").Visibility == Visibility.Collapsed,
                        "a current-version result has readable status and no stale release actions: " + context);
                    if (render) RenderAboutState(window, renderDirectory, "about-current-" + suffix + ".png");

                    var failed = current with { LatestVersion = null,
                        StatusMessage = LocalizationService.Translate("Could not check for updates. Check your internet connection.") };
                    Call(window, "ApplyUpdateCheckResult", failed);
                    Pump();
                    Assert(Element<TextBlock>(window, "AboutUpdateTitle").Text == LocalizationService.Translate("Update check failed")
                        && Element<TextBlock>(window, "AboutUpdateDesc").Text == failed.StatusMessage
                        && Element<Button>(window, "UpdateBtn").IsEnabled,
                        "an update-check error stays visible with a retry action: " + context);
                    CheckAboutUpdateBounds(window, context);
                    if (render) RenderAboutState(window, renderDirectory, "about-error-" + suffix + ".png");

                    settings.Settings.SkippedUpdateVersion = "v1.21.0";
                    Call(window, "ApplyUpdateCheckResult", new object[] { null! });
                    Assert(Element<TextBlock>(window, "AboutUpdateDesc").Text == string.Format(
                        LocalizationService.Translate("Update {0} skipped"), "v1.21.0"),
                        "skipped releases remain visible in the localized status: " + context);
                    settings.Settings.SkippedUpdateVersion = "";

                    Call(window, "ApplyUpdateCheckResult", available);
                    Element<StackPanel>(window, "UpdateProgressPanel").Visibility = Visibility.Visible;
                    Element<ProgressBar>(window, "UpdateProgressBar").Value = 42;
                    Element<TextBlock>(window, "UpdateProgressText").Text = string.Format(
                        LocalizationService.Translate("Downloading update ({0:F1}%)..."), 42.0);
                    Call(window, "SetUpdateActionsEnabled", false);
                    window.RefreshUpdateState();
                    Pump();
                    Assert(new[] { "UpdateBtn", "ViewReleaseBtn", "SkipVersionBtn" }.All(name => !Element<Button>(window, name).IsEnabled)
                        && Element<TextBlock>(window, "UpdateBadgeText").Text == "v1.21.0"
                        && Element<ProgressBar>(window, "UpdateProgressBar").Value == 42,
                        "download progress survives refresh while update actions are disabled: " + context);
                    CheckAboutUpdateBounds(window, context);
                    if (render) RenderAboutState(window, renderDirectory, "about-downloading-" + suffix + ".png");
                }
                finally { window.Close(); Pump(); }
            }
        }
        finally
        {
            settings.Settings.InterfaceLanguage = originalLanguageOption;
            settings.Settings.SkippedUpdateVersion = originalSkippedVersion;
            UiScale.Set(originalScale);
            LocalizationService.ApplyCurrentCulture(originalLanguage);
        }
    }

    private static Rect AboutBounds(FrameworkElement element, Window window)
        => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));

    private static void CheckAboutUpdateBounds(AboutWindow window, string context)
    {
        window.UpdateLayout();
        var card = AboutBounds(Element<Border>(window, "AboutUpdatesCard"), window);
        var title = AboutBounds(Element<TextBlock>(window, "AboutUpdateTitle"), window);
        var actions = AboutBounds(Element<WrapPanel>(window, "UpdateActions"), window);
        var peek = Element<StackPanel>(window, "UpdatePeekPanel");
        var visible = new[] { "UpdateBtn", "ViewReleaseBtn", "SkipVersionBtn" }.Select(name => Element<Button>(window, name))
            .Where(button => button.Visibility == Visibility.Visible).ToArray();
        Assert(card.Left >= 0 && card.Right <= window.ActualWidth,
            "the update card stays within the actual window width: " + context);
        Assert(visible.All(button => {
            var bounds = AboutBounds(button, window);
            return bounds.Left >= card.Left && bounds.Right <= card.Right && bounds.Top >= title.Bottom
                && bounds.Bottom <= card.Bottom && Math.Abs(button.ActualHeight - 32) < 0.5;
        }) && visible.SelectMany((button, index) => visible.Skip(index + 1)
            .Select(other => !AboutBounds(button, window).IntersectsWith(AboutBounds(other, window)))).All(clear => clear),
            "update buttons keep equal heights inside the card without clipping or overlap: " + context);
        Assert(Math.Abs(actions.Left - title.Left) < 1,
            "update status and actions share a left alignment: " + context);
        if (peek.Visibility == Visibility.Visible)
        {
            var notes = AboutBounds(peek, window);
            var badge = AboutBounds(Element<Border>(window, "UpdateBadge"), window);
            Assert(notes.Bottom <= actions.Top && Math.Abs(notes.Left - actions.Left) < 1
                && badge.Right <= card.Right && !badge.IntersectsWith(title),
                "release highlights use the full width above actions and the version badge is not clipped: " + context);
        }
        var progress = Element<StackPanel>(window, "UpdateProgressPanel");
        if (progress.Visibility == Visibility.Visible)
        {
            var bounds = AboutBounds(progress, window);
            Assert(bounds.Top >= actions.Bottom && Math.Abs(bounds.Left - actions.Left) < 1 && bounds.Right <= card.Right,
                "download progress has its own aligned row below actions: " + context);
        }
    }

    private static void RenderAboutState(AboutWindow window, string? directory, string name)
    {
        Element<Border>(window, "AboutUpdatesCard").BringIntoView();
        Pump();
        Render(window, directory, name);
    }
}
