using System.Globalization;
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
        var oldDate = DateTime.Today.AddDays(-19);
        var galleryItem = new HistoryEntry { Kind = HistoryKind.Image, CapturedAt = oldDate };
        SetField(history, "_entries", new List<HistoryEntry> { galleryItem });
        var progress = settings.Settings;
        progress.HasFirstEditor = true;
        progress.FirstEditorAt = oldDate.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        progress.CurrentStreak = progress.LongestStreak = 3;
        progress.LastCelebrationDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var window = new AchievementsWindow(settings, history);
        try
        {
            window.Show();
            AwaitCondition(() => window.Opacity == 1 && progress.AchievementHistoryInitialized,
                "Achievements did not finish opening", 5000);
            var label = Element<TextBlock>(window, "AchievementsStartDate");
            var reset = Element<Button>(window, "ResetAchievementsButton");
            Assert(progress.AchievementsStartedOn == DateOnly.FromDateTime(oldDate)
                && progress.CelebrationCaptureCount == 1 && progress.ScreenshotCount == 1,
                "existing achievement progress uses the earliest recorded date");
            Assert(label.Text.StartsWith("Fecha de inicio:") && reset.Content is "Reiniciar progreso",
                "the start date and reset action are localized");
            Render(window, renders, "achievements-start-date.png");

            RespondToConfirmation(false);
            Assert(progress.CelebrationCaptureCount == 1 && progress.HasFirstEditor && progress.LongestStreak == 3,
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

            using var reloaded = new SettingsService(settingsPath);
            reloaded.Load();
            Assert(reloaded.Settings.AchievementHistoryInitialized && reloaded.Settings.AchievementsStartedOn == today
                && reloaded.Settings.CelebrationCaptureCount == 0 && !reloaded.Settings.HasFirstEditor,
                "the new period and zero progress survive a settings reload");

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
