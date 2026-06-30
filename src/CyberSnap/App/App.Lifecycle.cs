using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using CyberSnap.Models;
using CyberSnap.Native;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap;

public partial class App
{
    public Services.UpdateCheckResult? LatestUpdateResult { get; set; }

    private const long IdleTrimPrivateBytesThreshold = 384L * 1024 * 1024;
    private static readonly TimeSpan MinimumIdleTrimInterval = TimeSpan.FromMinutes(2);

    private static void SyncStartupRegistry(bool enabled)
    {
        UninstallService.SetStartupEntry(enabled);
    }

    public void ShowSettings(string? navigateTo = null)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            NavigateSettingsTo(_settingsWindow, navigateTo);
            return;
        }

        if (Interlocked.CompareExchange(ref _settingsWindowOpening, 1, 0) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var historyService = EnsureHistoryService();
                var imageSearchIndexService = EnsureImageSearchIndexService();
                _ = Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (_settingsWindow is { IsVisible: true })
                        {
                            _settingsWindow.Activate();
                            NavigateSettingsTo(_settingsWindow, navigateTo);
                            return;
                        }

                        ShowSettingsWindow(historyService, imageSearchIndexService, navigateTo);
                    }
                    catch (Exception ex)
                    {
                        _settingsWindow = null;
                        ShowSettingsOpenFailed(ex, "lifecycle.show-settings", "lifecycle.show-settings.toast");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _settingsWindowOpening, 0);
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _settingsWindow = null;
                    ShowSettingsOpenFailed(ex, "lifecycle.show-settings.init", "lifecycle.show-settings.init.toast");
                    Interlocked.Exchange(ref _settingsWindowOpening, 0);
                }, DispatcherPriority.Background);
            }
        });
    }

    public void ShowSettingsAndDownloadUpdate(UpdateCheckResult result)
    {
        ShowSettings();
        _ = Dispatcher.BeginInvoke(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                if (_settingsWindow is { IsVisible: true })
                    break;
                await Task.Delay(100);
            }
            if (_settingsWindow != null)
            {
                await _settingsWindow.StartUpdateDownloadAsync(result);
            }
        }, DispatcherPriority.Background);
    }

    private static void NavigateSettingsTo(SettingsWindow win, string? navigateTo)
    {
        if (string.IsNullOrEmpty(navigateTo))
            return;
        if (navigateTo == "widget")
            win.NavigateToWidgetSettings();
        else if (navigateTo == "editor")
            win.NavigateToEditorSettings();
        else if (navigateTo == "gallery")
            win.NavigateToGallerySettings();
        else if (navigateTo == "about")
            win.NavigateToAboutSettings();
    }

    private static void ShowSettingsOpenFailed(Exception ex, string diagnosticKey, string toastDiagnosticKey)
    {
        AppDiagnostics.LogError(diagnosticKey, ex);
        try
        {
            ToastWindow.ShowError(
                LocalizationService.Translate("Error"),
                $"{LocalizationService.Translate("CyberSnap was unable to launch Configuration")}\n{ex.Message}");
        }
        catch (Exception toastEx)
        {
            AppDiagnostics.LogError(toastDiagnosticKey, toastEx);
        }
    }

    private void ShowSettingsWindow(HistoryService historyService, ImageSearchIndexService imageSearchIndexService, string? navigateTo = null)
    {
        var win = new SettingsWindow(_settingsService!, historyService, imageSearchIndexService);

        var activeEditor = UI.Editor.EditorForm.ActiveInstance;
        if (activeEditor != null)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(win)
            {
                Owner = activeEditor.Handle
            };
        }

        Action hotkeyHandler = () => RegisterHotkeys(showReadyNotification: false);
        Action localizationHandler = () =>
        {
            _trayIcon?.RefreshLocalization();
            _widgetWindow?.RefreshLocalization();
            UI.Editor.EditorForm.ActiveInstance?.RefreshLocalization();
        };
        win.HotkeyChanged += hotkeyHandler;
        win.LocalizationChanged += localizationHandler;
        win.Closed += (_, _) =>
        {
            win.HotkeyChanged -= hotkeyHandler;
            win.LocalizationChanged -= localizationHandler;
            _settingsWindow = null;
            // Defer GC compact to avoid layout jump when History window regains focus
            Dispatcher.BeginInvoke(() => ScheduleIdleMemoryTrim(), System.Windows.Threading.DispatcherPriority.Background);
        };
        _settingsWindow = win;
        win.Show();
        // The widget collapses as Settings opens, which releases foreground; under the Windows
        // foreground lock a plain Show() can leave the window behind other apps. Force it to the
        // front with a brief Topmost flip (canonical workaround) without keeping it always-on-top.
        win.Activate();
        win.Topmost = true;
        win.Topmost = false;
        win.Focus();

        NavigateSettingsTo(win, navigateTo);
    }

    public void ShowHistory(string? navigateToFilePath = null)
    {
        if (_historyWindow is { IsVisible: true })
        {
            _historyWindow.Activate();
            if (navigateToFilePath != null)
                _historyWindow.NavigateToItem(navigateToFilePath);
            return;
        }

        if (Interlocked.CompareExchange(ref _historyWindowOpening, 1, 0) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var historyService = EnsureHistoryService();
                var imageSearchIndexService = EnsureImageSearchIndexService();
                _ = Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (_historyWindow is { IsVisible: true })
                        {
                            _historyWindow.Activate();
                            if (navigateToFilePath != null)
                                _historyWindow.NavigateToItem(navigateToFilePath);
                            return;
                        }

                        ShowHistoryWindow(historyService, imageSearchIndexService, navigateToFilePath);
                    }
                    catch (Exception ex)
                    {
                        _historyWindow = null;
                        ShowHistoryOpenFailed(ex);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _historyWindowOpening, 0);
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _historyWindow = null;
                    ShowHistoryOpenFailed(ex);
                    Interlocked.Exchange(ref _historyWindowOpening, 0);
                }, DispatcherPriority.Background);
            }
        });
    }

    private void ShowHistoryWindow(HistoryService historyService, ImageSearchIndexService imageSearchIndexService, string? navigateToFilePath = null)
    {
        var win = new HistoryWindow(_settingsService!, historyService, imageSearchIndexService);

        var activeEditor = UI.Editor.EditorForm.ActiveInstance;
        if (activeEditor != null)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(win)
            {
                Owner = activeEditor.Handle
            };
        }

        win.Closed += (_, _) =>
        {
            _historyWindow = null;
            ScheduleIdleMemoryTrim();
        };
        _historyWindow = win;
        win.Show();
        if (navigateToFilePath != null)
            win.NavigateToItem(navigateToFilePath);
    }

    private static void ShowHistoryOpenFailed(Exception ex)
    {
        AppDiagnostics.LogError("lifecycle.show-history", ex);
        try
        {
            ToastWindow.ShowError(
                LocalizationService.Translate("Error"),
                LocalizationService.Translate("Gallery was unable to open.") + $"\n{ex.Message}");
        }
        catch (Exception toastEx)
        {
            AppDiagnostics.LogError("lifecycle.show-history.toast", toastEx);
        }
    }

    /// <summary>Toggles a boolean history setting by name and persists it.</summary>
    public void ToggleHistorySetting(string propertyName, bool value)
    {
        if (_settingsService is null) return;
        var prop = typeof(AppSettings).GetProperty(propertyName);
        if (prop is not null && prop.CanWrite && prop.PropertyType == typeof(bool))
        {
            prop.SetValue(_settingsService.Settings, value);
            _settingsService.Save();
            // Light refresh: only update visibility, no full reload
            _historyWindow?.RefreshVisibility();
        }
    }

    /// <summary>Returns the current app settings (never null after startup).</summary>
    public AppSettings GetSettings() => _settingsService?.Settings ?? new AppSettings();

    private HistoryService EnsureHistoryService()
    {
        lock (_historyGate)
        {
            if (_historyService is null)
            {
                _historyService = new HistoryService();
                _historyService.Load();
                HistoryService.PrimaryInstance = _historyService;
                if (!_historyChangedHooked)
                {
                    _historyService.Changed += HistoryService_Changed;
                    _historyChangedHooked = true;
                }
            }

            _historyService.CompressHistory = _settingsService!.Settings.CompressHistory;
            _historyService.JpegQuality = _settingsService.Settings.JpegQuality;
            _historyService.CaptureImageFormat = _settingsService.Settings.CaptureImageFormat;
            _historyService.HistoryCountLimit = _settingsService.Settings.HistoryCountLimit;
            _historyService.HistoryDeleteOriginalOnPrune = _settingsService.Settings.HistoryDeleteOriginalOnPrune;
            QueueHistoryMaintenance();
            return _historyService;
        }
    }

    private ImageSearchIndexService EnsureImageSearchIndexService()
    {
        lock (_historyGate)
        {
            if (_imageSearchIndexService is null)
            {
                _imageSearchIndexService = new ImageSearchIndexService();
                _imageSearchIndexService.Load();
                if (_historyService is not null && _settingsService!.Settings.AutoIndexImages)
                    _imageSearchIndexService.RequestSync(_historyService.ImageEntries, _settingsService!.Settings.OcrLanguageTag);
            }

            QueueHistoryMaintenance();
            return _imageSearchIndexService;
        }
    }

    private void QueueHistoryMaintenance()
    {
        lock (_historyGate)
        {
            if (_historyMaintenanceScheduled || _historyService is null || _settingsService is null)
                return;

            _historyMaintenanceScheduled = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                lock (_historyGate)
                {
                    if (_historyService is null || _settingsService is null)
                        return;

                    if (!_historyRecovered)
                    {
                        _historyService.RecoverFromDirectories(_settingsService.Settings.SaveDirectory);
                        _historyRecovered = true;
                    }

                    _historyService.PruneByRetention(_settingsService.Settings.HistoryRetention);
                    _historyService.PruneByCount(_settingsService.Settings.HistoryCountLimit, _settingsService.Settings.HistoryDeleteOriginalOnPrune);

                    if (_settingsService.Settings.AutoIndexImages && _imageSearchIndexService is not null)
                    {
                        _imageSearchIndexService.RequestSync(
                            _historyService.ImageEntries,
                            _settingsService.Settings.OcrLanguageTag);
                    }
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("lifecycle.history-maintenance", ex);
            }
            finally
            {
                lock (_historyGate)
                    _historyMaintenanceScheduled = false;
            }
        });
    }

    private void HistoryService_Changed()
    {
        QueueImageSearchIndexRefresh();
    }

    public void NotifyEditedCaptureSaved(string filePath, int width, int height)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => NotifyEditedCaptureSaved(filePath, width, height));
            return;
        }

        try
        {
            HistoryWindow.InvalidateThumbCache(filePath);
            var historyService = EnsureHistoryService();
            historyService.RefreshExistingCapture(filePath, width, height, HistoryKind.Image);
            RefreshHistoryWindowIfOpen();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "lifecycle.editor-save-history",
                $"Failed to refresh Capture History after saving {Path.GetFileName(filePath)}: {ex.Message}",
                ex);
        }
    }

    private void QueueImageSearchIndexRefresh()
    {
        if (Interlocked.Exchange(ref _historyIndexRefreshScheduled, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);

                HistoryService? historyService;
                ImageSearchIndexService? imageSearchIndexService;
                SettingsService? settingsService;
                lock (_historyGate)
                {
                    historyService = _historyService;
                    imageSearchIndexService = _imageSearchIndexService;
                    settingsService = _settingsService;
                }

                if (historyService is null ||
                    imageSearchIndexService is null ||
                    settingsService is null ||
                    !settingsService.Settings.AutoIndexImages)
                {
                    return;
                }

                imageSearchIndexService.RequestSync(historyService.ImageEntries, settingsService.Settings.OcrLanguageTag);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("lifecycle.history-index-refresh", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _historyIndexRefreshScheduled, 0);
            }
        });
    }

    private void ScheduleIdleMemoryTrim()
    {
        if (_idleTrimTimer is null)
            return;

        _idleTrimTimer.Stop();
        _idleTrimTimer.Start();
    }

    private void TrimIdleMemory()
    {
        _idleTrimTimer?.Stop();

        if (_isCapturing != 0)
        {
            ScheduleIdleMemoryTrim();
            return;
        }

        if (Interlocked.CompareExchange(ref _idleTrimInProgress, 1, 0) != 0)
        {
            ScheduleIdleMemoryTrim();
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now - _lastIdleTrimUtc < MinimumIdleTrimInterval)
                    return;

                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var privateBytes = process.PrivateMemorySize64;
                HistoryWindow.TrimThumbCache(privateBytes >= IdleTrimPrivateBytesThreshold ? 64 : 96);

                if (privateBytes < IdleTrimPrivateBytesThreshold)
                {
                    _lastIdleTrimUtc = now;
                    return;
                }

                try { _imageSearchIndexService?.TrimMemory(); } catch (Exception ex) { AppDiagnostics.LogError("lifecycle.trim-idle-memory.image-search", ex); }

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
                ProcessMemory.TrimCurrentProcessWorkingSet();
                _lastIdleTrimUtc = now;
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("lifecycle.trim-idle-memory", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _idleTrimInProgress, 0);
            }
        });
    }

    public void HandleCommandLineArgs(string[] args)
    {
        if (args == null || args.Length == 0) return;

        bool openEditor = args.Any(a => a.Equals("--editor", StringComparison.OrdinalIgnoreCase) || a.Equals("/editor", StringComparison.OrdinalIgnoreCase));
        string? editorFilePath = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("-") || arg.StartsWith("/")) continue;
            if (File.Exists(arg))
            {
                editorFilePath = arg;
                break;
            }
        }

        if (openEditor || editorFilePath != null)
        {
            if (editorFilePath != null)
            {
                CyberSnap.UI.Editor.EditorForm.ShowEditorFromFile(editorFilePath);
            }
            else
            {
                CyberSnap.UI.Editor.EditorForm.ShowEditorEmptyOrPrompt();
            }
        }
    }
}
