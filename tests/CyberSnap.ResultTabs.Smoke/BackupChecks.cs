using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

internal static partial class Program
{
    private sealed class BackupClock(DateTimeOffset utc) : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private static void BackupAwait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new TimeoutException("Backup operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static T BackupAwait<T>(Task<T> task) { BackupAwait((Task)task); return task.GetAwaiter().GetResult(); }

    private static void ExpectBackupFailure(Action operation, string message)
    {
        try { operation(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        { Assert(true, message); return; }
        throw new InvalidOperationException("FAIL: " + message);
    }

    private static void CheckAutomaticBackups(string temp, string? renderDirectory)
    {
        string root = Path.Combine(temp, "automatic-backups");
        Directory.CreateDirectory(root);
        using var settings = new SettingsService(Path.Combine(root, "settings.json"), TimeSpan.FromHours(1));
        var clock = new BackupClock(new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero));
        using var service = new AutomaticBackupService(settings, clock);
        settings.Settings.ScreenshotCount = 42;
        settings.Settings.CurrentStreak = 5;
        settings.Settings.HasFirstOcr = true;
        settings.Settings.GoogleTranslateApiKey = "backup-test-secret";
        settings.Settings.UploadCustomPassword = "backup-test-password";
        settings.Save();
        var first = BackupAwait(service.CreateBackupAsync());
        using (var document = JsonDocument.Parse(File.ReadAllText(first.FilePath)))
        {
            var payload = document.RootElement.GetProperty("Settings");
            Assert(payload.GetProperty("ScreenshotCount").GetInt32() == 42
                && payload.GetProperty("CurrentStreak").GetInt32() == 5 && payload.GetProperty("HasFirstOcr").GetBoolean(),
                "backups include achievements and the latest debounced settings");
            Assert(payload.GetProperty("GoogleTranslateApiKey").GetString() == ""
                && payload.GetProperty("UploadCustomPassword").GetString() == ""
                && !File.ReadAllText(first.FilePath).Contains("backup-test-secret"), "backup files exclude credentials");
            Assert(document.RootElement.GetProperty("Sha256").GetString()!.Length == 64, "backups carry an integrity checksum");
        }
        Assert(!Directory.GetFiles(service.BackupDirectory, "*.tmp").Any(), "successful writes publish no temporary files");
        Assert(!SettingsService.TryDeserialize(File.ReadAllText(first.FilePath), out _),
            "automatic backup envelopes cannot accidentally import as default settings");
        Assert(SettingsService.TryDeserialize(SettingsService.ExportRedactedJson(settings.Settings), out var legacyExport)
            && legacyExport.ScreenshotCount == 42, "manual settings exports remain compatible");
        Assert(!BackupAwait(service.CheckScheduleAsync(true)), "a recent manual backup satisfies the daily schedule");
        clock.Utc = clock.Utc.AddDays(1);
        Assert(BackupAwait(service.CheckScheduleAsync(false)), "daily backups run when due");
        using (var restarted = new AutomaticBackupService(settings, clock))
            Assert(!BackupAwait(restarted.CheckScheduleAsync(true)), "restarting does not duplicate a completed scheduled backup");
        clock.Utc = clock.Utc.AddDays(20);
        Assert(BackupAwait(service.CheckScheduleAsync(true)) && !BackupAwait(service.CheckScheduleAsync(false)),
            "missed intervals create one catch-up copy rather than a burst");

        settings.Settings.AutomaticBackup.Frequency = BackupFrequency.OnStartup;
        settings.Save();
        clock.Utc = clock.Utc.AddMinutes(6);
        Assert(!BackupAwait(service.CheckScheduleAsync(false)), "startup-only backups never run on periodic ticks");
        Assert(BackupAwait(service.CheckScheduleAsync(true)) && !BackupAwait(service.CheckScheduleAsync(true)),
            "startup backups honor the five-minute restart grace period");
        settings.Settings.AutomaticBackup.Enabled = false;
        settings.Save();
        clock.Utc = clock.Utc.AddDays(10);
        Assert(!BackupAwait(service.CheckScheduleAsync(true)), "disabled backups never run automatically");
        Assert(AutomaticBackupService.NextBackupUtc(BackupFrequency.Weekly, clock.Utc) == clock.Utc.AddDays(7),
            "weekly scheduling uses seven-day intervals");
        var january = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        Assert(AutomaticBackupService.NextBackupUtc(BackupFrequency.Monthly, january) == january.AddMonths(1),
            "monthly scheduling handles short calendar months");

        settings.Settings.AutomaticBackup.MaxBackups = 1;
        settings.Save();
        string unrelated = Path.Combine(service.BackupDirectory, "personal-file.json");
        File.WriteAllText(unrelated, "unrelated");
        var keep = BackupAwait(service.CreateBackupAsync());
        var sameTime = BackupAwait(service.CreateBackupAsync());
        Assert(File.Exists(sameTime.FilePath) && !File.Exists(keep.FilePath),
            "retention always keeps the new verified copy even when timestamps match");
        Assert(File.ReadAllText(unrelated) == "unrelated", "retention leaves unrelated files untouched");
        using (var lockedBackup = new FileStream(sameTime.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var createdWithWarning = BackupAwait(service.CreateBackupAsync());
            Assert(File.Exists(createdWithWarning.FilePath) && File.Exists(sameTime.FilePath)
                && service.LastWarning is not null && service.LastError is null,
                "retention failures preserve the valid new copy and surface a warning");
        }
        sameTime = BackupAwait(service.CreateBackupAsync());
        Assert(service.LastWarning is null && BackupAwait(service.ListBackupsAsync()).Count(file => file.IsValid && !file.IsRecovery) == 1,
            "retention recovers after a locked old backup is released");

        string damaged = Path.Combine(service.BackupDirectory, $"CyberSnap-backup-20260131T120000000Z-{Guid.NewGuid():N}.json");
        File.WriteAllText(damaged, File.ReadAllText(sameTime.FilePath).Replace("\"ScreenshotCount\": 42", "\"ScreenshotCount\": 999"));
        var list = BackupAwait(service.ListBackupsAsync());
        Assert(list.Single(file => file.FilePath == damaged).IsValid == false, "tampered backups are shown as damaged");
        int beforeRestore = settings.Settings.ScreenshotCount;
        ExpectBackupFailure(() => BackupAwait(service.RestoreBackupAsync(damaged)), "checksum failures prevent restoration");
        Assert(settings.Settings.ScreenshotCount == beforeRestore, "a damaged backup cannot change live settings");
        string malformed = Path.Combine(service.BackupDirectory, $"CyberSnap-backup-20260131T120000000Z-{Guid.NewGuid():N}.json");
        File.WriteAllText(malformed, "{ truncated");
        ExpectBackupFailure(() => BackupAwait(service.RestoreBackupAsync(malformed)), "truncated JSON fails with a recoverable backup error");
        BackupAwait(service.CreateBackupAsync());
        Assert(File.Exists(damaged), "rotation preserves damaged copies for inspection");

        var restorePoint = BackupAwait(service.ListBackupsAsync()).Single(file => file.IsValid && !file.IsRecovery);
        settings.Settings.ScreenshotCount = 99;
        settings.Settings.GoogleTranslateApiKey = "current-test-key";
        settings.Settings.AutomaticBackup.MaxBackups = 5;
        settings.Save();
        BackupAwait(service.RestoreBackupAsync(restorePoint.FilePath));
        Assert(settings.Settings.ScreenshotCount == 42 && settings.Settings.CurrentStreak == 5,
            "restore applies settings and achievements");
        Assert(settings.Settings.GoogleTranslateApiKey == "current-test-key" && settings.Settings.AutomaticBackup.MaxBackups == 5,
            "restore preserves current credentials and backup preferences");
        var recovery = BackupAwait(service.ListBackupsAsync()).Single(file => file.IsRecovery && file.IsValid);
        using (var saved = JsonDocument.Parse(File.ReadAllText(recovery.FilePath)))
            Assert(saved.RootElement.GetProperty("Settings").GetProperty("ScreenshotCount").GetInt32() == 99,
                "a verified pre-restore recovery copy contains the previous achievements");
        using (var reloaded = new SettingsService(Path.Combine(root, "settings.json")))
        {
            reloaded.Load();
            Assert(reloaded.Settings.ScreenshotCount == 42 && reloaded.Settings.GoogleTranslateApiKey == "current-test-key",
                "restored settings are durable and credentials remain protected on disk");
        }
        using (var locked = new FileStream(Path.Combine(root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            ExpectBackupFailure(() => BackupAwait(service.RestoreBackupAsync(recovery.FilePath)),
                "restoration fails safely when settings cannot be replaced");
        Assert(settings.Settings.ScreenshotCount == 42, "failed durable replacement leaves current settings intact");
        ExpectBackupFailure(() => BackupAwait(service.DeleteBackupAsync(unrelated)), "deleting an unrelated file is rejected");
        string outside = Path.Combine(root, Path.GetFileName(restorePoint.FilePath));
        File.Copy(restorePoint.FilePath, outside);
        ExpectBackupFailure(() => BackupAwait(service.RestoreBackupAsync(outside)), "restoring from outside the managed folder is rejected");

        settings.Settings.AutomaticBackup.MaxBackups = 50;
        settings.Save();
        var concurrent = Enumerable.Range(0, 8).Select(_ => service.CreateBackupAsync()).ToArray();
        BackupAwait(Task.WhenAll(concurrent));
        Assert(concurrent.Select(task => task.Result.FilePath).Distinct().Count() == 8
            && concurrent.All(task => File.Exists(task.Result.FilePath)), "simultaneous requests produce distinct complete backups");
        BackupAwait(service.DeleteBackupAsync(concurrent[0].Result.FilePath));
        Assert(!File.Exists(concurrent[0].Result.FilePath), "deleting a selected managed backup removes only that file");
        settings.Settings.AutomaticBackup.Enabled = true;
        settings.Settings.AutomaticBackup.Frequency = BackupFrequency.Daily;
        settings.Save();
        clock.Utc = clock.Utc.AddDays(2);
        var scheduledTogether = Enumerable.Range(0, 8).Select(_ => service.CheckScheduleAsync(false)).ToArray();
        BackupAwait(Task.WhenAll(scheduledTogether));
        Assert(scheduledTogether.Count(task => task.Result) == 1, "overlapping schedule checks create only one due backup");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            ExpectBackupFailure(() => BackupAwait(service.CreateBackupAsync(cancellation.Token)), "canceled backups do not publish files");
        }

        string blocker = Path.Combine(root, "blocked-folder");
        File.WriteAllText(blocker, "block directory creation");
        string originalFolder = service.BackupDirectory;
        settings.Settings.AutomaticBackup.CustomDirectory = blocker;
        settings.Settings.AutomaticBackup.Enabled = true;
        settings.Settings.AutomaticBackup.Frequency = BackupFrequency.Daily;
        settings.Save();
        int defaultCount = Directory.GetFiles(originalFolder).Length;
        ExpectBackupFailure(() => BackupAwait(service.CheckScheduleAsync(true)), "unavailable backup destinations surface an error");
        Assert(service.LastError is not null && Directory.GetFiles(originalFolder).Length == defaultCount,
            "a failed custom destination never silently falls back to the default folder");
        Assert(!BackupAwait(service.CheckScheduleAsync(false)), "failed schedules back off before retrying");
        settings.Settings.AutomaticBackup.CustomDirectory = Path.Combine(root, "new", "nested-backups");
        settings.Save();
        clock.Utc = clock.Utc.AddMinutes(6);
        Assert(BackupAwait(service.CheckScheduleAsync(false)) && service.LastError is null,
            "the scheduler recovers after the destination is fixed, including new nested folders");

        using (var startupSettings = new SettingsService(Path.Combine(root, "startup", "settings.json")))
        using (var startupService = new AutomaticBackupService(startupSettings, clock))
        {
            startupSettings.Settings.AutomaticBackup.Frequency = BackupFrequency.OnStartup;
            startupSettings.Save();
            startupService.Start();
            AwaitCondition(() => Directory.Exists(startupService.BackupDirectory)
                && Directory.GetFiles(startupService.BackupDirectory, "*.json").Length > 0,
                "The startup timer did not create its backup", 15000);
            Assert(BackupAwait(startupService.ListBackupsAsync()).Count(file => file.IsValid) == 1,
                "starting the service creates a verified backup after the startup delay");
        }

        CheckBackupSettingsUi(settings, service, renderDirectory);
    }

    private static void CheckBackupSettingsUi(SettingsService settings, AutomaticBackupService service, string? renderDirectory)
    {
        string previousLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (string language in new[] { "en", "es" })
            {
                settings.Settings.InterfaceLanguage = language;
                settings.Save();
                LocalizationService.ApplyCurrentCulture(language);
                using var history = new HistoryService();
                using var search = new ImageSearchIndexService();
                var window = new SettingsWindow(settings, history, search);
                try
                {
                    window.Show();
                    Element<System.Windows.Controls.RadioButton>(window, "BackupDataTab").IsChecked = true;
                    Call(window, "TabChanged", null!, new RoutedEventArgs());
                    Pump();
                    BackupAwait((Task)Call(window, "RefreshAutomaticBackupsAsync")!);
                    var enabled = Element<CheckBox>(window, "AutomaticBackupEnabledCheck");
                    Assert(System.Windows.Automation.AutomationProperties.GetName(enabled) == LocalizationService.Translate("Enable automatic backups"),
                        "automatic backup controls are accessible and localized in " + language);
                    Assert(Element<ComboBox>(window, "AutomaticBackupRetentionCombo").Items.Count == 50,
                        "backup retention exposes the supported 1–50 range in " + language);
                    var liveSettings = settings.Settings;
                    enabled.IsChecked = false;
                    Pump();
                    Assert(!settings.Settings.AutomaticBackup.Enabled, "backup toggles persist immediately in " + language);
                    Assert(ReferenceEquals(liveSettings, settings.Settings), "backup preferences preserve live settings references in " + language);
                    enabled.IsChecked = true;
                    Pump();
                    string workingFolder = settings.Settings.AutomaticBackup.CustomDirectory;
                    string unavailable = Path.Combine(Path.GetDirectoryName(workingFolder)!, "unavailable-ui-folder");
                    File.WriteAllText(unavailable, "not a directory");
                    settings.Settings.AutomaticBackup.CustomDirectory = unavailable;
                    settings.Save();
                    Call(window, "LoadAutomaticBackupSettings");
                    BackupAwait((Task)Call(window, "RefreshAutomaticBackupsAsync")!);
                    AwaitCondition(() => Element<TextBlock>(window, "AutomaticBackupErrorText").Visibility == Visibility.Visible,
                        "The folder-listing error was not shown");
                    Assert(Element<ItemsControl>(window, "AutomaticBackupsList").Items.Count == 0
                        && Element<TextBlock>(window, "NoAutomaticBackupsText").Visibility == Visibility.Collapsed,
                        "unavailable folders show an error instead of stale backups or a false empty state in " + language);
                    settings.Settings.AutomaticBackup.CustomDirectory = workingFolder;
                    settings.Save();
                    var uiService = (AutomaticBackupService)Field(window, "_backupService");
                    BackupAwait((Task)Call(window, "RunAutomaticBackupOperationAsync",
                        new Func<Task>(() => uiService.CreateBackupAsync()))!);
                    if (renderDirectory is not null)
                    {
                        Render(window, renderDirectory, "backups-" + language + ".png");
                        Element<ItemsControl>(window, "AutomaticBackupsList").BringIntoView();
                        Pump();
                        var list = Element<ItemsControl>(window, "AutomaticBackupsList");
                        var container = list.ItemContainerGenerator.ContainerFromIndex(0);
                        Assert(container is not null && BackupVisualButtons(container).Any(button => Equals(button.Content, LocalizationService.Translate("Restore")))
                            && BackupVisualButtons(container).Any(button => Equals(button.Content, LocalizationService.Translate("Delete"))),
                            "dynamic restore/delete actions are localized in " + language);
                        Render(window, renderDirectory, "backup-list-" + language + ".png");
                    }
                }
                finally { window.Close(); Pump(); }
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(previousLanguage); }
    }

    private static IEnumerable<Button> BackupVisualButtons(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) yield return button;
            foreach (var nested in BackupVisualButtons(child)) yield return nested;
        }
    }
}
