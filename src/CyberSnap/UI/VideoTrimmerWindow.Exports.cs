using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class VideoTrimmerWindow
{
    private TrimmerExportJob? _currentExport;

    private TrimmerExportJob CaptureExportJob(string output, double start, double end) =>
        new(_active, _mediaFilePath, output, start, end, _hasAudioTrack,
            VolumeControl.Volume, VolumeControl.IsExportMuted, _preciseCut, _settingsService.Settings.InterfaceLanguage);

    private void BeginExport()
    {
        _isExporting = true;
        _exportCts = new CancellationTokenSource();
        PausePlayback();
        TrimmerTabStrip.IsEnabled = false;
        SetDocumentControlsEnabled(false);
        ShowExportProgress();
    }

    private void EndExport()
    {
        _currentExport = null;
        _exportCts?.Dispose();
        _exportCts = null;
        _isExporting = false;
        TrimmerTabStrip.IsEnabled = true;
        SetDocumentControlsEnabled(_previewReady);
        if (_previewReady) HideProgressOverlay();
        RefreshTabs();
        if (_closeAfterExport) Close();
    }

    private async void TrimBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting || !_previewReady) return;
        CommitPendingTimeInputs();
        if (!IsSegmentValid()) return;
        string lang = _settingsService.Settings.InterfaceLanguage;
        _documentPromptOpen = true;
        bool confirmed;
        try { confirmed = ThemedConfirmDialog.Confirm(this, LocalizationService.Translate(lang, "Confirm Overwrite"),
            string.Format(LocalizationService.Translate(lang, "Are you sure you want to overwrite the original file '{0}'?\nThis action cannot be undone."), Path.GetFileName(_mediaFilePath)),
            LocalizationService.Translate(lang, "Yes"), LocalizationService.Translate(lang, "No"), danger: false); }
        finally { _documentPromptOpen = false; }
        if (!confirmed) return;
        await ExportToFileAsync(_mediaFilePath);
    }

    private async void SaveAsNewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting || !_previewReady) return;
        CommitPendingTimeInputs();
        if (!IsSegmentValid()) return;
        string ext = Path.GetExtension(_mediaFilePath);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = Path.GetDirectoryName(_mediaFilePath),
            FileName = Path.GetFileNameWithoutExtension(_mediaFilePath) + "_edited" + ext,
            DefaultExt = ext,
            Filter = $"{(_isGif ? "GIF" : "MP4")} (*{ext})|*{ext}"
        };
        _documentPromptOpen = true;
        bool? accepted;
        try { accepted = dialog.ShowDialog(this); }
        finally { _documentPromptOpen = false; }
        if (accepted != true) return;
        if (string.Equals(Path.GetFullPath(dialog.FileName), _mediaFilePath, StringComparison.OrdinalIgnoreCase))
        { TrimBtn_Click(sender, e); return; }
        await ExportToFileAsync(dialog.FileName);
    }

    // The lock covers encoding, cancellation, file replacement and reload, including
    // every await after FFmpeg exits. A newly arriving file can only enqueue a tab.
    private async Task<bool> ExportToFileAsync(string destination)
    {
        if (_isExporting || !_previewReady) return false;
        // A context-menu command can leave a time editor focused. Validate the
        // committed range so the job always matches the user's latest marks.
        CommitPendingTimeInputs();
        if (!IsSegmentValid()) return false;
        destination = Path.GetFullPath(destination);
        var source = _active;
        bool Conflicts() => _sessions.Any(s => !ReferenceEquals(s, source)
            && string.Equals(s.FilePath, destination, StringComparison.OrdinalIgnoreCase));
        if (Conflicts())
        {
            ShowBanner(LocalizationService.Translate("This file is open in another tab. Choose a different destination."));
            return false;
        }
        CaptureSession();
        string staging = Path.Combine(Path.GetDirectoryName(destination)!, $"cybersnap-trim-{Guid.NewGuid():N}{Path.GetExtension(destination)}");
        var job = CaptureExportJob(staging, _startTimeSeconds, _endTimeSeconds);
        bool overwrite = string.Equals(destination, job.SourcePath, StringComparison.OrdinalIgnoreCase);
        BeginExport();
        var ct = _exportCts!.Token;
        try
        {
            if (!await RunFfmpegTrimAsync(job, ct)) return false;
            await WaitForDocumentPromptAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (Conflicts())
            {
                ShowBanner(LocalizationService.Translate("This file is open in another tab. Choose a different destination."));
                return false;
            }
            TeardownPreviewForReload();
            await Task.Delay(200, ct);
            await WaitForDocumentPromptAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (Conflicts())
            {
                ShowBanner(LocalizationService.Translate("This file is open in another tab. Choose a different destination."));
                return false;
            }
            if (overwrite) File.Replace(job.OutputPath, job.SourcePath, null);
            else File.Move(job.OutputPath, destination, overwrite: true);
            LoadMediaFile(destination);
            if (Application.Current is App app) app.RefreshHistoryWindowIfOpen();
            ToastWindow.Show(LocalizationService.Translate(job.Language, overwrite ? "Video trimmed" : "Video saved"),
                LocalizationService.Translate(job.Language, overwrite ? "Original file overwritten successfully." : "Trimmed copy saved successfully."), destination);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("trimmer.export", ex);
            ThemedConfirmDialog.Alert(this, LocalizationService.Translate(job.Language, "Error"), ex.Message, error: true);
            return false;
        }
        finally
        {
            TryDeleteFile(staging);
            try
            {
                if (!_closeAfterExport && _gifSequence is null && _mp4Sequence is null) LoadActiveSession();
            }
            finally { EndExport(); }
        }
    }

    private async Task WaitForDocumentPromptAsync(CancellationToken ct)
    {
        // Modal WPF dialogs run a nested Dispatcher loop. Encoding may finish
        // there, but a pending close decision must settle before committing files.
        while (_documentPromptOpen) await Task.Delay(25, ct);
    }
}

internal sealed record TrimmerExportJob(TrimmerSession Session, string SourcePath, string OutputPath,
    double Start, double End, bool HasAudio, double Volume, bool Muted, bool Precise, string Language);
