using System.Reflection;
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
        var update = new UpdateCheckResult(new Version(1, 20, 1), new Version(1, 21, 0), "v1.21.0",
            "https://example.invalid/release", new UpdateAsset("fixture.exe", new Uri("https://example.invalid/fixture.exe"),
                2_000_000, null, false), null, notes);
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        string originalLanguageOption = settings.Settings.InterfaceLanguage;
        double originalScale = UiScale.Current;
        try
        {
            foreach (string language in new[] { "en", "es" })
            {
                settings.Settings.InterfaceLanguage = language;
                LocalizationService.ApplyCurrentCulture(language);
                foreach (double scale in new[] { 1.0, 1.4 })
                foreach (double width in new[] { 600.0, 740.0 })
                {
                    UiScale.Set(scale);
                    var window = new AboutWindow(settings) { Width = width, Height = 680 };
                    string context = $"{language}, width={width}, scale={scale}";
                    bool render = width == 740 && scale == 1 || width == 600 && scale == 1.4;
                    string suffix = language + (width == 600 ? "-narrow-large" : "");
                    try
                    {
                        window.Show();
                        Pump();
                        // Positioning can move the HWND to another monitor with different DPI before layout settles.
                        window.Width = width;
                        Pump();
                        Assert(Math.Abs(window.ActualWidth - width) < 1,
                            "the About fixture uses the requested width after monitor centering: " + context);
                        var intro = AboutBounds(Element<Border>(window, "AboutIntroCard"), window);
                        var logo = AboutBounds(Element<Image>(window, "AboutLogoImage"), window);
                        var description = Element<TextBlock>(window, "AboutDescriptionText");
                        var copy = AboutBounds(description, window);
                        Assert(Math.Abs(copy.Left - logo.Left) < 1 && copy.Top > logo.Bottom && intro.Contains(copy),
                            "the localized description uses the card width below the heading: " + context, false);
                        Assert(description.Text == LocalizationService.Translate(LocalizationService.GetSourceText(description)),
                            "the description retains its full localized text: " + context, false);
                        if (render) Render(window, renderDirectory, "about-description-" + suffix + ".png");

                        var status = Element<TextBlock>(window, "AboutUpdateDesc");
                        var checkNow = Element<Button>(window, "UpdateBtn");
                        Call(window, "ApplyUpdateCheckResult", update);
                        Pump();
                        Assert(status.Text == LocalizationService.Translate("Update available") &&
                            checkNow.IsVisible && checkNow.IsEnabled &&
                            new[] { "UpdateNowBtn", "ViewReleaseBtn", "SkipVersionBtn", "UpdateHighlightsList", "UpdateProgressPanel" }
                                .All(name => window.FindName(name) is null),
                            "About reports availability and keeps the update actions and progress in the notice: " + context);
                        CheckAboutUpdateBounds(window, context);
                        if (render) RenderAboutState(window, renderDirectory, "about-available-" + suffix + ".png");

                        var checking = typeof(AboutWindow).GetField("_checkingForUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        checking.SetValue(window, true);
                        var current = update with { LatestVersion = update.CurrentVersion };
                        Call(window, "ApplyUpdateCheckResult", current);
                        Pump();
                        Assert(!checkNow.IsEnabled && status.Text == LocalizationService.Translate("Checking for updates..."),
                            "a background result does not overwrite the in-progress check: " + context);
                        if (render) RenderAboutState(window, renderDirectory, "about-checking-" + suffix + ".png");

                        checking.SetValue(window, false);
                        Call(window, "RefreshUpdateSection");
                        Assert(checkNow.IsEnabled && status.Text == LocalizationService.Translate("CyberSnap is up to date"),
                            "the current-version result restores the check action: " + context);
                        if (render) RenderAboutState(window, renderDirectory, "about-current-" + suffix + ".png");

                        var failed = current with
                        {
                            LatestVersion = null,
                            ErrorKey = "Could not check for updates. Check your internet connection."
                        };
                        Call(window, "ApplyUpdateCheckResult", failed);
                        Assert(checkNow.IsEnabled && status.Text == failed.GetStatusMessage(),
                            "a check error remains visible with a retry action: " + context);
                        CheckAboutUpdateBounds(window, context);
                        if (render) RenderAboutState(window, renderDirectory, "about-error-" + suffix + ".png");
                    }
                    finally { window.Close(); Pump(); }
                }
                CheckUpdateNoticeLayout(update, language, renderDirectory);
            }
        }
        finally
        {
            settings.Settings.InterfaceLanguage = originalLanguageOption;
            UiScale.Set(originalScale);
            LocalizationService.ApplyCurrentCulture(originalLanguage);
        }
    }

    private static void CheckUpdateNoticeLayout(UpdateCheckResult update, string language, string? renderDirectory)
    {
        var phase = typeof(UpdateNoticeWindow).Assembly.GetType("CyberSnap.UpdateNoticePhase", throwOnError: true)!;
        var notice = new UpdateNoticeWindow { Width = 320 };
        var renderState = typeof(UpdateNoticeWindow).GetMethod("Render",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        void ApplyState(string name, UpdateDownloadProgress progress) =>
            renderState.Invoke(notice, [update, Enum.Parse(phase, name), progress, false, null]);
        try
        {
            ApplyState("Available", new UpdateDownloadProgress(0, null));
            notice.Show();
            Pump();
            var notes = Element<ItemsControl>(notice, "NotesList");
            Assert(notes.Items.Count == 4 &&
                (string)notes.Items[0] == (language == "es"
                    ? "Copias programadas de configuración y logros" : "Scheduled settings and achievement backups") &&
                ((string)notes.Items[3]).StartsWith(".NET", StringComparison.Ordinal),
                "the notice keeps localized highlights, cleans leading decorations and preserves punctuation: " + language);
            var buttons = new[] { "GithubButton", "SkipButton", "LaterButton", "PrimaryButton" }
                .Select(name => Element<Button>(notice, name)).ToArray();
            Assert(buttons.All(button => button.IsVisible) && buttons.Select(button => (string)button.Content).SequenceEqual(
                new[] { "View on GitHub", "Skip version", "Later", "Update now" }.Select(LocalizationService.Translate)),
                "the notice offers the four initial actions in order: " + language);
            var actionBounds = buttons.Select(button => AboutBounds(button, notice)).ToArray();
            Assert(actionBounds.All(bounds => bounds.Left >= 0 && bounds.Right <= notice.ActualWidth) &&
                actionBounds.SelectMany((left, index) => actionBounds.Skip(index + 1)
                    .Select(right => !left.IntersectsWith(right))).All(clear => clear),
                "the initial actions fit at narrow widths without overlapping: " + language);
            Render(notice, renderDirectory, "update-notice-available-" + language + ".png");

            var progress = new UpdateDownloadProgress(840_000L, 2_000_000L);
            ApplyState("Downloading", progress);
            Call(notice, "RefreshLocalization");
            Pump();
            Assert(Element<ProgressBar>(notice, "ProgressBar").Value == 42 &&
                notes.Items.Count == 4 && Element<Button>(notice, "CancelButton").IsVisible,
                "progress and localized notes survive refresh: " + language);
            Render(notice, renderDirectory, "update-notice-downloading-" + language + ".png");

            ApplyState("Ready", progress);
            Pump();
            Assert((string)Element<Button>(notice, "PrimaryButton").Content ==
                LocalizationService.Translate("Restart to update") && notes.Items.Count == 4,
                "the installed edition is ready to restart and still shows the notes: " + language);
        }
        finally { notice.Close(); Pump(); }
    }

    private static Rect AboutBounds(FrameworkElement element, Window window) =>
        element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));

    private static void CheckAboutUpdateBounds(AboutWindow window, string context)
    {
        window.UpdateLayout();
        var card = AboutBounds(Element<Border>(window, "AboutUpdatesCard"), window);
        var check = Element<Button>(window, "UpdateBtn");
        var bounds = AboutBounds(check, window);
        var status = AboutBounds(Element<TextBlock>(window, "AboutUpdateDesc"), window);
        Assert(card.Left >= 0 && card.Right <= window.ActualWidth && card.Contains(bounds) &&
            check.IsVisible && Math.Abs(check.ActualHeight - 32) < 0.5 && !bounds.IntersectsWith(status),
            "the check button and status fit in the About card without overlap: " + context);
    }

    private static void RenderAboutState(AboutWindow window, string? directory, string name)
    {
        Element<Border>(window, "AboutUpdatesCard").BringIntoView();
        Pump();
        Render(window, directory, name);
    }
}
