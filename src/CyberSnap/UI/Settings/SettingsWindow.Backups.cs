using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    private readonly AutomaticBackupService _backupService;
    private readonly bool _ownsBackupService;
    private bool _loadingBackupOptions;
    private bool _backupOperationRunning;
    private bool _refreshingBackups;
    private bool _backupRefreshRequested;
    private string? _backupUiError;

    private sealed record BackupRow(BackupFileInfo File)
    {
        public string DisplayName => File.FileName;
        public string Description => string.Format(BackupText(File.IsRecovery ? "Recovery copy from {0}" : "Backup from {0}"),
            File.CreatedUtc.ToLocalTime().ToString("G")) + $" · {File.SizeBytes / 1024.0:N1} KB"
            + (!File.IsValid ? " · " + BackupText("Damaged or unsupported backup") : "");
        public bool CanRestore => File.IsValid;
        public string RestoreLabel => BackupText("Restore");
        public string DeleteLabel => BackupText("Delete");
    }

    private static string BackupText(string text) => LocalizationService.Translate(text);

    private void InitializeAutomaticBackups()
    {
        _backupService.Changed += AutomaticBackups_Changed;
        Loaded += async (_, _) => await RefreshAutomaticBackupsAsync();
        Activated += async (_, _) => await RefreshAutomaticBackupsAsync();
        LocalizationChanged += () =>
        {
            LoadAutomaticBackupSettings();
            AutomaticBackups_Changed();
        };
    }

    private void LoadAutomaticBackupSettings()
    {
        _loadingBackupOptions = true;
        try
        {
            var options = _settingsService.Settings.AutomaticBackup.Normalize();
            AutomaticBackupEnabledCheck.IsChecked = options.Enabled;
            AutomaticBackupFrequencyCombo.SelectedIndex = (int)options.Frequency;
            AutomaticBackupRetentionCombo.ItemsSource = Enumerable.Range(1, 50).ToArray();
            AutomaticBackupRetentionCombo.SelectedItem = options.MaxBackups;
            AutomaticBackupScheduleControls.IsEnabled = options.Enabled && !_backupOperationRunning;
            AutomaticBackupDirectoryText.Text = string.IsNullOrWhiteSpace(options.CustomDirectory)
                ? _backupService.DefaultDirectory : options.CustomDirectory;
            ResetBackupFolderBtn.IsEnabled = options.CustomDirectory.Length > 0 && !_backupOperationRunning;
            System.Windows.Automation.AutomationProperties.SetName(AutomaticBackupEnabledCheck, BackupText("Enable automatic backups"));
            System.Windows.Automation.AutomationProperties.SetName(AutomaticBackupFrequencyCombo, BackupText("Backup frequency"));
            System.Windows.Automation.AutomationProperties.SetName(AutomaticBackupRetentionCombo, BackupText("Backups to keep"));
            System.Windows.Automation.AutomationProperties.SetName(AutomaticBackupDirectoryText, BackupText("Backup folder"));
        }
        finally { _loadingBackupOptions = false; }
    }

    private void AutomaticBackupOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingBackupOptions || !IsLoaded || _backupOperationRunning) return;
        var options = _settingsService.Settings.AutomaticBackup.Normalize();
        options.Enabled = AutomaticBackupEnabledCheck.IsChecked == true;
        options.Frequency = (BackupFrequency)Math.Max(0, AutomaticBackupFrequencyCombo.SelectedIndex);
        options.MaxBackups = AutomaticBackupRetentionCombo.SelectedItem is int count ? count : 3;
        SaveAutomaticBackupOptions(options);
    }

    private void SaveAutomaticBackupOptions(AutomaticBackupSettings options)
    {
        var previous = _settingsService.Settings.AutomaticBackup;
        try
        {
            _settingsService.SaveAutomaticBackupOptionsDurably(options);
        }
        catch (Exception ex)
        {
            _settingsService.Settings.AutomaticBackup = previous;
            ShowAutomaticBackupError(ex);
        }
        LoadAutomaticBackupSettings();
        AutomaticBackups_Changed();
    }

    private void AutomaticBackups_Changed()
    {
        if (Dispatcher.HasShutdownStarted || !IsLoaded) return;
        _ = Dispatcher.BeginInvoke(async () => await RefreshAutomaticBackupsAsync(), DispatcherPriority.Background);
    }

    private async Task RefreshAutomaticBackupsAsync()
    {
        if (_backupOperationRunning || !IsLoaded) return;
        if (_refreshingBackups) { _backupRefreshRequested = true; return; }
        _refreshingBackups = true;
        try
        {
            string requestedDirectory = _backupService.BackupDirectory;
            var files = await _backupService.ListBackupsAsync();
            if (!string.Equals(requestedDirectory, _backupService.BackupDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _backupRefreshRequested = true;
                return;
            }
            AutomaticBackupsList.ItemsSource = files.Select(file => new BackupRow(file)).ToArray();
            NoAutomaticBackupsText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var last = files.Where(file => file.IsValid && !file.IsRecovery).Select(file => (DateTimeOffset?)file.CreatedUtc).Max();
            var options = _settingsService.Settings.AutomaticBackup.Normalize();
            string lastText = last is null ? BackupText("No successful backup yet.")
                : string.Format(BackupText("Last backup: {0}"), last.Value.ToLocalTime().ToString("g"));
            string nextText = !options.Enabled ? BackupText("Automatic backups are disabled.")
                : options.Frequency == BackupFrequency.OnStartup ? BackupText("Next backup: on the next app start.")
                : last is null ? BackupText("The first automatic backup is pending.")
                : string.Format(BackupText("Next backup: {0}"), AutomaticBackupService.NextBackupUtc(options.Frequency, last)!.Value.ToLocalTime().ToString("g"));
            AutomaticBackupStatusText.Text = lastText + "\n" + nextText;
            if ((_backupUiError ?? _backupService.LastError) is { } error)
            {
                AutomaticBackupErrorText.Text = string.Format(BackupText("Backup operation failed: {0}"), BackupText(error));
                AutomaticBackupErrorText.Visibility = Visibility.Visible;
            }
            else if (_backupService.LastWarning is { } warning)
            {
                AutomaticBackupErrorText.Text = string.Format(BackupText("Backup notice: {0}"), BackupText(warning));
                AutomaticBackupErrorText.Visibility = Visibility.Visible;
            }
            else AutomaticBackupErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AutomaticBackupsList.ItemsSource = Array.Empty<BackupRow>();
            NoAutomaticBackupsText.Visibility = Visibility.Collapsed;
            AutomaticBackupStatusText.Text = BackupText("Backups could not be listed.");
            ShowAutomaticBackupError(ex);
        }
        finally
        {
            _refreshingBackups = false;
            if (_backupRefreshRequested)
            {
                _backupRefreshRequested = false;
                AutomaticBackups_Changed();
            }
        }
    }

    private async Task RunAutomaticBackupOperationAsync(Func<Task> operation)
    {
        if (_backupOperationRunning) return;
        _backupOperationRunning = true;
        _backupUiError = null;
        AutomaticBackupStatusText.Text = BackupText("Working on backups…");
        SetBackupControlsEnabled(false);
        try { await operation(); }
        catch (Exception ex) { ShowAutomaticBackupError(ex); }
        finally
        {
            _backupOperationRunning = false;
            SetBackupControlsEnabled(true);
            LoadAutomaticBackupSettings();
            await RefreshAutomaticBackupsAsync();
        }
    }

    private void SetBackupControlsEnabled(bool enabled)
    {
        AutomaticBackupEnabledCheck.IsEnabled = enabled;
        AutomaticBackupScheduleControls.IsEnabled = enabled && AutomaticBackupEnabledCheck.IsChecked == true;
        AutomaticBackupFolderControls.IsEnabled = enabled;
        AutomaticBackupsList.IsEnabled = enabled;
        CreateBackupNowBtn.IsEnabled = enabled;
        RefreshBackupsBtn.IsEnabled = enabled;
    }

    private void ShowAutomaticBackupError(Exception exception)
    {
        AppDiagnostics.LogError("settings.backup", exception);
        _backupUiError = exception.Message;
        AutomaticBackupErrorText.Text = string.Format(BackupText("Backup operation failed: {0}"), BackupText(exception.Message));
        AutomaticBackupErrorText.Visibility = Visibility.Visible;
    }

    private async void CreateBackupNow_Click(object sender, RoutedEventArgs e)
        => await RunAutomaticBackupOperationAsync(async () => { await _backupService.CreateBackupAsync(); });

    private async void RefreshBackups_Click(object sender, RoutedEventArgs e) => await RefreshAutomaticBackupsAsync();

    private void BackupDirectory_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // The read-only TextBox has an internal ScrollViewer. Keep wheel scrolling
        // on the settings page while preserving text selection/copy for the path.
        e.Handled = true;
        BackupDataPanel.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent
        });
    }

    private void ChooseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = BackupText("Choose backup folder") };
        if (dialog.ShowDialog(this) != true) return;
        var options = _settingsService.Settings.AutomaticBackup.Normalize();
        options.CustomDirectory = dialog.FolderName;
        SaveAutomaticBackupOptions(options);
    }

    private void ResetBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var options = _settingsService.Settings.AutomaticBackup.Normalize();
        options.CustomDirectory = "";
        SaveAutomaticBackupOptions(options);
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = _backupService.BackupDirectory;
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowAutomaticBackupError(ex); }
    }

    private async void RestoreAutomaticBackup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackupRow row || !row.CanRestore) return;
        if (!ThemedConfirmDialog.Confirm(this, "Restore backup",
            "Replace your settings and achievements with this backup? A recovery copy will be created first. Current API keys and backup preferences will be kept.", "Restore", "Cancel")) return;
        await RunAutomaticBackupOperationAsync(async () =>
        {
            await _backupService.RestoreBackupAsync(row.File.FilePath);
            LoadSettings();
            HotkeyChanged?.Invoke();
            LocalizationChanged?.Invoke();
            SettingsService.PublishWidgetPreferences(_settingsService.Settings);
            SettingsService.PublishAutoCopyPreferences(_settingsService.Settings);
            ToastWindow.Show(BackupText("Backup restored"),
                BackupText("Settings and achievements were restored. Restart CyberSnap to apply all changes."));
        });
    }

    private async void DeleteAutomaticBackup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackupRow row) return;
        if (!ThemedConfirmDialog.Confirm(this, "Delete backup", "Permanently delete the selected backup file?", "Delete", "Cancel")) return;
        await RunAutomaticBackupOperationAsync(() => _backupService.DeleteBackupAsync(row.File.FilePath));
    }
}
