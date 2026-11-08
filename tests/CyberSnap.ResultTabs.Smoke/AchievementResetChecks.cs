using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

internal static partial class Program
{
    private static void CheckAchievementReset(SettingsService settings, string settingsPath, string? renders)
    {
        settings.Settings.InterfaceLanguage = "es";
        LocalizationService.ApplyCurrentCulture("es");
        using var history = new HistoryService();
        var oldDate = new DateTime(2026, 5, 10);
        var galleryItem = new HistoryEntry { Kind = HistoryKind.Image, CapturedAt = oldDate.AddDays(-5) };
        SetField(history, "_entries", new List<HistoryEntry> { galleryItem });
        settings.Settings.AchievementsStartedOn = DateOnly.FromDateTime(oldDate);
        settings.Settings.CelebrationCaptureCount = settings.Settings.ScreenshotCount = 240;
        settings.Settings.HasFirstEditor = true;
        settings.Settings.FirstEditorAt = oldDate.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        settings.Settings.CurrentStreak = settings.Settings.LongestStreak = 3;
        settings.Settings.LastCelebrationDate = oldDate.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        settings.Save();
        settings.Load();
        var progress = settings.Settings;

        var window = new AchievementsWindow(settings, history);
        try
        {
            window.Show();
            AwaitCondition(() => window.Opacity == 1 && progress.AchievementHistoryInitialized,
                "Achievements did not finish opening", 5000);
            var label = Element<TextBlock>(window, "AchievementsStartDate");
            var reset = Element<Button>(window, "ResetAchievementsButton");
            Assert(progress.AchievementsStartedOn == DateOnly.FromDateTime(oldDate)
                && progress.CelebrationCaptureCount == 240 && progress.ScreenshotCount == 240,
                "migration preserves the previously recorded start date and capture counts");
            Assert(label.Text == "Fecha de inicio: Mayo 10, 2026" && reset.Content is "Reiniciar progreso",
                "the Spanish start date shows a capitalized full month first and the reset action is localized");
            Render(window, renders, "achievements-start-date.png");

            progress.InterfaceLanguage = "en";
            window.RefreshLocalization();
            Assert(label.Text == "Start date: May 10, 2026",
                "the English start date shows the full month first");
            progress.InterfaceLanguage = "es";
            window.RefreshLocalization();

            SetField(history, "_entries", new List<HistoryEntry>());
            window.RefreshAchievementContent(revealRail: false);
            Assert(label.Text == "Fecha de inicio: Mayo 10, 2026", "removing older captures leaves the start date intact");
            SetField(history, "_entries", new List<HistoryEntry> { galleryItem });

            settings.FlushPendingWrites();
            RewriteSettingsAsLegacy(0);
            using (var downgraded = new SettingsService(settingsPath))
            {
                downgraded.Load();
                Assert(downgraded.Settings.AchievementsStartedOn == DateOnly.FromDateTime(oldDate)
                    && downgraded.Settings.ScreenshotCount == 240 && downgraded.Settings.HasFirstEditor,
                    "loading after an older settings write preserves the migrated date, counters and medals");
            }

            RespondToConfirmation(false);
            Assert(progress.CelebrationCaptureCount == 240 && progress.HasFirstEditor && progress.LongestStreak == 3,
                "canceling the confirmation preserves progress");

            RespondToConfirmation(true);
            window.RefreshAchievementContent(revealRail: false);
            var today = DateOnly.FromDateTime(DateTime.Today);
            Assert(progress.AchievementsStartedOn == today && progress.CelebrationCaptureCount == 0
                && progress.ScreenshotCount == 0 && progress.CurrentStreak == 0 && progress.LongestStreak == 0
                && progress.LastCelebrationDate is null && progress.FirstEditorAt is null
                && progress.SeenAchievementIds.Count == 0 && AchievementCatalog.Build(progress, key => key).All(a => !a.Unlocked),
                "confirming reset starts a new period with empty counters, streaks and medals");
            Assert(history.Entries.Count == 1 && ReferenceEquals(galleryItem, history.Entries[0]),
                "reset preserves the Gallery and never reimports its older activity");

            settings.FlushPendingWrites();
            RewriteSettingsAsLegacy(240);
            using var reloaded = new SettingsService(settingsPath);
            reloaded.Load();
            Assert(reloaded.Settings.AchievementHistoryInitialized && reloaded.Settings.AchievementsStartedOn == today
                && reloaded.Settings.CelebrationCaptureCount == 0 && reloaded.Settings.ScreenshotCount == 0
                && !reloaded.Settings.HasFirstEditor,
                "the new period and zero progress survive an older build repopulating settings from the Gallery");

            void RewriteSettingsAsLegacy(int galleryCount)
            {
                var legacy = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
                legacy.Remove(nameof(AppSettings.AchievementsStartedOn));
                legacy.Remove(nameof(AppSettings.AchievementHistoryInitialized));
                legacy[nameof(AppSettings.CelebrationCaptureCount)] = galleryCount;
                legacy[nameof(AppSettings.ScreenshotCount)] = galleryCount;
                File.WriteAllText(settingsPath, legacy.ToJsonString());
            }

            void RespondToConfirmation(bool accepted)
            {
                bool sawConfirmation = false;
                Exception? failure = null;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.Current.Windows.Cast<Window>()
                        .FirstOrDefault(w => w.Owner == window && w.GetType().Name == "ThemedConfirmDialog");
                    if (dialog is null) return;
                    sawConfirmation = true;
                    timer.Stop();
                    try
                    {
                        var action = VisualDescendants(dialog).OfType<Button>()
                            .Single(button => accepted ? button.IsDefault : button.IsCancel);
                        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        dialog.Close();
                    }
                };
                timer.Start();
                try { reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                Assert(sawConfirmation, "reset asks for confirmation before changing progress", false);
            }
        }
        finally { window.Close(); }
    }
}
