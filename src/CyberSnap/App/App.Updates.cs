using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;
using Point = System.Drawing.Point;
using Forms = System.Windows.Forms;

namespace CyberSnap;

internal enum UpdateNoticePhase { Available, Downloading, Ready, Installing }

public partial class App
{
    internal UpdateCheckResult? LastUpdateCheck { get; private set; }
    internal bool HasPendingUpdate => _pendingUpdate is not null;

    private Task<UpdateCheckResult>? _updateCheck;
    private UpdateCheckResult? _pendingUpdate;
    private UpdateNoticePhase _updatePhase;
    private UpdateDownloadProgress _updateProgress;
    private CancellationTokenSource? _updateDownload;
    private string? _downloadedUpdatePath;
    private string? _updateNoticeError;
    private UpdateNoticeWindow? _updateNotice;
    private Point? _updateNoticeAnchor;
    private (Point Anchor, bool Activate)? _updateNoticeRequest;

    private async void ScheduleAutoUpdateCheck()
    {
        SessionBecameIdle += () => Dispatcher.BeginInvoke(TryShowUpdateNotice, DispatcherPriority.Background);
        try
        {
            await Task.Delay(3000);
            if (_settingsService?.Settings.AutoCheckForUpdates == true)
                await CheckForUpdatesAsync(manual: false);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("updates.startup", ex);
        }
    }

    internal async Task CheckForUpdatesAsync(bool manual, Point? anchor = null)
    {
        var check = _pendingUpdate is not null && _updatePhase != UpdateNoticePhase.Available
            ? Task.FromResult(_pendingUpdate)
            : _updateCheck ??= UpdateService.CheckForUpdatesAsync();
        UpdateCheckResult result;
        try
        {
            result = await check;
        }
        finally
        {
            if (ReferenceEquals(_updateCheck, check)) _updateCheck = null;
        }

        if (!manual && _settingsService?.Settings.AutoCheckForUpdates != true) return;

        // A response from a concurrent check must not replace an ongoing download or a ready package.
        if (_pendingUpdate is not null && _updatePhase != UpdateNoticePhase.Available)
            result = _pendingUpdate;

        LastUpdateCheck = result;
        _aboutWindow?.ApplyUpdateCheckResult(result);
        if (result.ErrorKey is not null) return;

        if (!result.IsUpdateAvailable)
        {
            ClearPendingUpdate();
            return;
        }

        if (!manual &&
            string.Equals(_settingsService?.Settings.SkippedUpdateVersion, result.LatestVersionLabel, StringComparison.Ordinal))
            return;

        bool newRelease = _pendingUpdate?.LatestVersion != result.LatestVersion;
        if (newRelease)
        {
            _pendingUpdate = result;
            _updatePhase = UpdateNoticePhase.Available;
            _updateProgress = default;
            _updateNoticeError = null;
            RefreshUpdateShortcuts();
        }

        if (manual || newRelease) RequestUpdateNotice(anchor, activate: manual);
    }

    internal void OpenUpdateNotice(Point? anchor = null) => RequestUpdateNotice(anchor, activate: true);

    private void RequestUpdateNotice(Point? anchor, bool activate)
    {
        if (_pendingUpdate is null || Dispatcher.HasShutdownStarted) return;
        if (_updateNoticeRequest is { Activate: true } && !activate) return;
        _updateNoticeRequest = (anchor ?? _updateNoticeAnchor ?? Forms.Cursor.Position, activate);
        TryShowUpdateNotice();
    }

    private void TryShowUpdateNotice()
    {
        if (_pendingUpdate is null || _updateNoticeRequest is not { } request || Dispatcher.HasShutdownStarted ||
            IsSessionBusy() || IsSessionHandoffPending) return;

        _updateNoticeRequest = null;
        var settings = _settingsService?.Settings;
        if (!request.Activate && (settings?.NotificationsEnabled == false || QuietHours.IsActive(settings) ||
            (_updatePhase == UpdateNoticePhase.Available && settings?.AutoCheckForUpdates == false))) return;
        _updateNoticeAnchor = request.Anchor;
        if (_updateNotice is null)
        {
            var window = new UpdateNoticeWindow();
            if (ReferenceEquals(MainWindow, window)) MainWindow = null;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_updateNotice, window)) _updateNotice = null;
            };
            _updateNotice = window;
        }
        RefreshUpdateNotice();
        _updateNotice.Present(request.Anchor, request.Activate);
    }

    private void RefreshUpdateNotice()
    {
        if (_pendingUpdate is { } release)
            _updateNotice?.Render(release, _updatePhase, _updateProgress,
                _updateDownload?.IsCancellationRequested == true, _updateNoticeError);
    }

    internal void HideUpdateNotice()
    {
        _updateNoticeRequest = null;
        _updateNotice?.Close();
    }

    private void ClearPendingUpdate()
    {
        HideUpdateNotice();
        _pendingUpdate = null;
        _updateNoticeAnchor = null;
        RefreshUpdateShortcuts();
    }

    internal void SkipPendingUpdate()
    {
        if (_pendingUpdate is null || _updatePhase != UpdateNoticePhase.Available || _settingsService is null) return;
        var settings = _settingsService.Settings;
        var previous = settings.SkippedUpdateVersion;
        settings.SkippedUpdateVersion = _pendingUpdate.LatestVersionLabel;
        try
        {
            _settingsService.Save();
            _settingsService.FlushPendingWrites();
        }
        catch (Exception ex)
        {
            settings.SkippedUpdateVersion = previous;
            SetUpdateError("Could not skip this version. Try again.", ex);
            return;
        }
        ClearPendingUpdate();
    }

    internal async Task DownloadPendingUpdateAsync()
    {
        if (_pendingUpdate?.Asset is not { } asset || _updatePhase != UpdateNoticePhase.Available) return;

        using var download = new CancellationTokenSource();
        _updateDownload = download;
        _updatePhase = UpdateNoticePhase.Downloading;
        _updateProgress = new UpdateDownloadProgress(0, asset.Size);
        _updateNoticeError = null;
        RefreshUpdateNotice();

        var progress = new Progress<UpdateDownloadProgress>(current =>
        {
            if (_updateDownload != download || download.IsCancellationRequested) return;
            _updateProgress = current;
            _updateNotice?.RenderProgress(current);
        });

        try
        {
            var folder = Path.Combine(SettingsService.StorageDirectory, "Updates");
            _downloadedUpdatePath = await UpdateService.DownloadUpdateAsync(asset, folder, progress, download.Token);
            _updatePhase = UpdateNoticePhase.Ready;
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested)
        {
            _updatePhase = UpdateNoticePhase.Available;
        }
        catch (Exception ex)
        {
            _updatePhase = UpdateNoticePhase.Available;
            SetUpdateError("Could not download the update. Try again.", ex);
        }
        finally
        {
            _updateDownload = null;
            RefreshUpdateNotice();
        }

        if (_updatePhase == UpdateNoticePhase.Ready && _updateNotice?.IsVisible != true)
            RequestUpdateNotice(_updateNoticeAnchor, activate: false);
    }

    internal void CancelUpdateDownload()
    {
        _updateDownload?.Cancel();
        RefreshUpdateNotice();
    }

    internal void FinishPendingUpdate()
    {
        if (_pendingUpdate?.Asset is not { } asset || _updatePhase != UpdateNoticePhase.Ready) return;
        if (_downloadedUpdatePath is not { } path || !File.Exists(path))
        {
            _updatePhase = UpdateNoticePhase.Available;
            _updateNoticeError = "The downloaded update is missing. Download it again.";
            RefreshUpdateNotice();
            return;
        }

        if (asset.IsPortable)
        {
            OpenUpdateLocation(Path.GetDirectoryName(path)!, "Could not open the download folder.");
            return;
        }

        if (IsSessionBusy() || IsSessionHandoffPending)
        {
            _updateNoticeError = "Finish the capture or recording before restarting.";
            RefreshUpdateNotice();
            return;
        }

        _updatePhase = UpdateNoticePhase.Installing;
        _updateNoticeError = null;
        RefreshUpdateNotice();
        try
        {
            if (UI.Editor.EditorForm.ActiveInstance is { } editor)
            {
                if (_updateNotice is { } notice) notice.Topmost = false;
                editor.Activate();
                editor.Close();
                if (!editor.IsDisposed) return;
            }
            if (IsSessionBusy() || IsSessionHandoffPending)
            {
                _updateNoticeError = "Finish the capture or recording before restarting.";
                return;
            }
            UpdateService.LaunchInstaller(path);
            Shutdown();
        }
        catch (Exception ex)
        {
            SetUpdateError("Could not start the installer. Try again.", ex);
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                _updatePhase = UpdateNoticePhase.Ready;
                if (_updateNotice is { } notice) notice.Topmost = true;
                RefreshUpdateNotice();
            }
        }
    }

    internal void OpenPendingRelease()
    {
        if (_pendingUpdate is { } update)
            OpenUpdateLocation(update.ReleaseUrl, "Could not open the release on GitHub.");
    }

    private void OpenUpdateLocation(string path, string error)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { SetUpdateError(error, ex); }
    }

    private void SetUpdateError(string error, Exception ex)
    {
        AppDiagnostics.LogError("updates.notice", ex);
        _updateNoticeError = error;
        RefreshUpdateNotice();
    }
}
