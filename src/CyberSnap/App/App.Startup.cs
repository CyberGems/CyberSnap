using System.Windows;
using System.Windows.Threading;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap;

public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) || a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            try { UninstallService.RemoveInstalledAppEntry(); } catch (Exception ex) { AppDiagnostics.LogError("startup.uninstall.remove-installed-entry", ex); }
            try { UninstallService.RemoveStartMenuShortcut(); } catch (Exception ex) { AppDiagnostics.LogError("startup.uninstall.remove-start-menu", ex); }
            try { UninstallService.RemoveStartupEntry(); } catch (Exception ex) { AppDiagnostics.LogError("startup.uninstall.remove-startup-entry", ex); }
            try { UninstallService.RemoveAppData(); } catch (Exception ex) { AppDiagnostics.LogError("startup.uninstall.remove-appdata", ex); }
            try { UninstallService.ScheduleInstallFolderRemoval(); } catch (Exception ex) { AppDiagnostics.LogError("startup.uninstall.schedule-folder-removal", ex); }
            Shutdown();
            return;
        }

        bool isPostInstall = e.Args.Any(a => a.Equals("--post-install", StringComparison.OrdinalIgnoreCase));
        bool openSettingsOnStartup = e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase) || a.Equals("/settings", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(false, "CyberSnapScreenshotTool_SingleInstance");
        bool acquired;
        try
        {
            acquired = _mutex.WaitOne(TimeSpan.FromSeconds(8), false);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            base.OnStartup(e);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        WireUnhandledExceptionLogging();

        try { UninstallService.RegisterInstalledAppEntry(); } catch (Exception ex) { AppDiagnostics.LogError("startup.register-installed-entry", ex); }
        try { UninstallService.EnsureStartMenuShortcut(); } catch (Exception ex) { AppDiagnostics.LogError("startup.ensure-start-menu-shortcut", ex); }

        _settingsService = new SettingsService();
        _settingsService.Load();
        _settingsService.SaveFailed += message =>
        {
            _ = Dispatcher.BeginInvoke(() =>
                ToastWindow.ShowError("Settings save failed", string.IsNullOrWhiteSpace(message) ? "CyberSnap could not write settings." : message));
        };
        LocalizationService.ApplyCurrentCulture(_settingsService.Settings.InterfaceLanguage);
        BackgroundRuntimeJobService.Initialize();
        StartBackgroundPreloads();

        if (isPostInstall)
            _settingsService.Settings.HasCompletedSetup = false;

        try { SyncStartupRegistry(_settingsService.Settings.StartWithWindows); } catch (Exception ex) { AppDiagnostics.LogError("startup.sync-startup-registry", ex); }
        System.Windows.Forms.Application.EnableVisualStyles();
        SoundService.Muted = _settingsService.Settings.MuteSounds;
        SoundService.SetPack(_settingsService.Settings.SoundPack);
        UI.Motion.Disabled = _settingsService.Settings.DisableAnimations;
        UiScale.Set(_settingsService.Settings.UiScale);
        Theme.Refresh();
        Theme.ApplyTo(Resources);
        Helpers.UiChrome.DetectRefreshRate();
        ToastWindow.SetPosition(_settingsService.Settings.ToastPosition);
        ToastWindow.SetMonitorIndex(_settingsService.Settings.ToastMonitorIndex);
        ToastWindow.SetDuration(_settingsService.Settings.ToastDurationSeconds);
        ToastWindow.SetSystemDuration(_settingsService.Settings.SystemToastDurationSeconds);
        ToastWindow.SetNotificationsEnabled(_settingsService.Settings.NotificationsEnabled);
        ToastWindow.SetSystemNotificationsEnabled(_settingsService.Settings.SystemNotificationsEnabled);
        ToastWindow.SetButtonLayout(_settingsService.Settings.ToastButtons);
        ToastWindow.SetFadeOutSeconds(_settingsService.Settings.ToastFadeOutSeconds);

        _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTrimTimer.Tick += (_, _) => TrimIdleMemory();
        ScheduleIdleMemoryTrim();

        bool openSettingsAfterWizard = false;
        if (!_settingsService.Settings.HasCompletedSetup)
        {
            var wizard = new SetupWizard(_settingsService);
            wizard.ShowDialog();
            openSettingsAfterWizard = wizard.Tag as string == "OpenSettings";
        }

        ConfigureTrayIcon();
        RegisterHotkeys();
        WarmDxgiCapture();
        Helpers.FluentIcons.Preload();

        ScheduleAutoUpdateCheck();
        EnsureWidgetWindowCreated();

        if (openSettingsAfterWizard || openSettingsOnStartup)
            ShowSettings();
    }

    private void WireUnhandledExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppDiagnostics.LogError("appdomain.unhandled", ex);
            else
                AppDiagnostics.LogWarning("appdomain.unhandled", args.ExceptionObject?.ToString() ?? "Unknown unhandled exception.");
        };
        DispatcherUnhandledException += (_, args) => AppDiagnostics.LogError("dispatcher.unhandled", args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppDiagnostics.LogError("tasks.unobserved", args.Exception);
            args.SetObserved();
        };
    }

    private void StartBackgroundPreloads()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var historyService = EnsureHistoryService();
                HistoryWindow.WarmHistoryThumbsInBackground(historyService.ImageEntries, maxCount: 96, immediateCount: 24, batchSize: 12);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("startup.preload-history-search", ex);
            }
        });
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon = new TrayIcon(_settingsService?.Settings);
        _trayIcon.OnCapture += OnHotkeyPressed;
        _trayIcon.OnOcr += OnOcrHotkeyPressed;
        _trayIcon.OnColorPicker += OnPickerHotkeyPressed;
        _trayIcon.OnRecordRequested += LaunchRecordingWithFormat;
        _trayIcon.OnScrollCapture += OnScrollCaptureHotkeyPressed;
        _trayIcon.OnSettings += () => ShowSettings();
        _trayIcon.OnHistory += () => ShowHistory();
        _trayIcon.OnQuit += () => Shutdown();
    }

    private static void WarmDxgiCapture()
    {
        _ = Task.Run(() =>
        {
            try { CyberSnap.Capture.DxgiScreenCapture.WarmUp(); } catch (Exception ex) { AppDiagnostics.LogError("startup.dxgi-warmup", ex); }
        });
    }

    private void ScheduleAutoUpdateCheck()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);
                if (_settingsService is null || !_settingsService.Settings.AutoCheckForUpdates)
                    return;

                var result = await UpdateService.CheckForUpdatesAsync();
                if (!result.IsUpdateAvailable)
                    return;

                if (Application.Current is null) return;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var spec = new ToastSpec
                    {
                        Title = "Update Available",
                        Body = $"CyberSnap {result.LatestVersionLabel} is out!\nYou're on {UpdateService.GetCurrentVersionLabel()}",
                        ClickActionUrl = result.ReleaseUrl,
                        ClickActionLabel = "Download",
                        DurationSeconds = 12,
                        SuppressSound = true
                    };
                    ToastWindow.Show(spec);
                });
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("startup.auto-update-check", ex);
            }
        });
    }
}
