using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class VideoTrimmerWindow
{
    private readonly List<TrimmerSession> _sessions = new();
    private TrimmerSession _active = null!;
    private TrimmerSession? _lastClosed;
    private bool _sessionsFinalized;
    private bool _closeAfterExport;
    private bool _closeApproved;
    private bool _restoringSession;
    private bool _previewReady;
    private bool _documentPromptOpen;
    private readonly List<Task> _previewLoads = new();

    private void TrackPreviewLoad(Task task)
    {
        lock (_previewLoads) _previewLoads.Add(task);
        _ = task.ContinueWith(completed =>
        {
            if (completed.Exception is not null) AppDiagnostics.LogError("trimmer.preview-task", completed.Exception);
            lock (_previewLoads) _previewLoads.Remove(completed);
        }, TaskScheduler.Default);
    }

    private async Task ReleaseSessionsAfterPreviewLoadsAsync(TrimmerSession[] sessions)
    {
        Task[] pending;
        lock (_previewLoads) pending = _previewLoads.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception ex) { AppDiagnostics.LogError("trimmer.preview-cleanup", ex); }
        await Dispatcher.InvokeAsync(() =>
        {
            foreach (var session in sessions) ReleaseSession(session);
        });
    }

    public static VideoTrimmerWindow ShowFile(string path, SettingsService settings,
        System.Drawing.Bitmap? posterFrame = null, bool ephemeral = false)
    {
        var window = Application.Current.Windows.OfType<VideoTrimmerWindow>()
            .FirstOrDefault(w => !w._sessionsFinalized && !w._closeAfterExport);
        if (window is null)
        {
            window = new VideoTrimmerWindow(path, settings, posterFrame, ephemeral);
            window.Show();
        }
        else
        {
            window.AddFile(path, posterFrame, ephemeral);
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        }
        CyberSnapWindowChrome.EnsureForeground(window);
        return window;
    }

    private TrimmerSession CreateSession(string path, System.Drawing.Bitmap? poster = null, bool ephemeral = false)
    {
        path = Path.GetFullPath(path);
        var settings = _settingsService.Settings;
        var session = new TrimmerSession(path)
        {
            Volume = settings.VideoTrimmerVolume,
            ExportMuted = settings.VideoTrimmerExportMuted,
            Loop = settings.VideoTrimmerLoopEnabled,
            Precise = settings.VideoTrimmerPreciseCut,
            DetailedTime = settings.VideoTrimmerDetailedTimeDisplay,
            Fps = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? settings.GifFps : settings.RecordingFps
        };
        if (session.Fps <= 0) session.Fps = 30;
        try { if (poster is not null) session.Poster = BitmapPerf.ToBitmapSource(poster); }
        finally { poster?.Dispose(); }
        if (ephemeral) session.OwnedRecordings.Add(path);
        return session;
    }

    private void InitializeTabs(string path, System.Drawing.Bitmap? poster, bool ephemeral)
    {
        _active = CreateSession(path, poster, ephemeral);
        _sessions.Add(_active);
        TrimmerTabStrip.TabSelected += (_, index) => ActivateSessionAt(index);
        TrimmerTabStrip.TabCloseRequested += (_, index) => CloseSessionAt(index);
        TrimmerTabStrip.TabContextMenuRequested += (_, index) => ShowTabMenu(index);
        TrimmerTabStrip.BarContextMenuRequested += (_, _) => ShowTabMenu(_sessions.IndexOf(_active));
        RefreshTabs();
        Loaded += (_, _) => LoadActiveSession();
    }

    private void AddFile(string path, System.Drawing.Bitmap? poster = null, bool ephemeral = false)
    {
        path = Path.GetFullPath(path);
        var existing = _sessions.Find(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            poster?.Dispose();
            if (ephemeral) existing.OwnedRecordings.Add(path);
            ActivateSessionAt(_sessions.IndexOf(existing));
            return;
        }
        if (_isExporting || _documentPromptOpen) { poster?.Dispose(); poster = null; }
        TrimmerSession session;
        if (_lastClosed is not null && string.Equals(_lastClosed.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            session = _lastClosed;
            _lastClosed = null;
            poster?.Dispose();
            if (ephemeral) session.OwnedRecordings.Add(path);
        }
        else session = CreateSession(path, poster, ephemeral);
        _sessions.Add(session);
        if (!_isExporting && !_documentPromptOpen) ActivateSessionAt(_sessions.Count - 1);
        RefreshTabs();
    }

    private void CaptureSession()
    {
        if (_active is null || !_previewReady) return;
        _active.Duration = _videoDurationSeconds;
        _active.Start = _startTimeSeconds;
        _active.End = _endTimeSeconds;
        _active.Position = GetCurrentTimelineSeconds();
        _active.Playing = IsPlaying();
        _active.Volume = VolumeControl.Volume;
        _active.ExportMuted = VolumeControl.IsExportMuted;
        _active.Loop = _loopEnabled;
        _active.Precise = _preciseCut;
        _active.DetailedTime = _detailedTimeDisplay;
        _active.Zoom = _currentZoom;
        _active.Fit = _zoomToFit;
        _active.DidInitialContain = _didInitialContain;
        _active.PanX = ZoomViewport.HorizontalOffset;
        _active.PanY = ZoomViewport.VerticalOffset;
        _active.StartMarker = SetStartBtn.IsChecked == true;
        _active.EndMarker = SetEndBtn.IsChecked == true;
    }

    private void ActivateSessionAt(int index)
    {
        if (_sessionsFinalized || _isExporting || _documentPromptOpen || index < 0 || index >= _sessions.Count) return;
        if (ReferenceEquals(_sessions[index], _active)) return;
        // Finish text edits and default persistence against the outgoing document.
        CommitPendingTimeInputs();
        FlushAudioPreferences();
        CaptureSession();
        PausePlayback();
        _active = _sessions[index];
        LoadActiveSession();
        RefreshTabs();
    }

    private void LoadActiveSession()
    {
        _previewReady = false;
        _restoringSession = true;
        try
        {
            TeardownPreviewForReload();
            _bannerTimer?.Stop();
            BannerBorder.Visibility = Visibility.Collapsed;
            _filmstripResizeTimer?.Stop();
            _isPanning = _isSliderDragging = _isHandleDragging = false;
            ZoomViewport.ReleaseMouseCapture();
            TimeSlider.ReleaseMouseCapture();
            _userTargetSeconds = _lastTargetSeekSeconds = -1;
            _lastHandleSeekTime = _lastUserDragTime = DateTime.MinValue;
            _mediaFilePath = _active.FilePath;
            _isGif = _mediaFilePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
            _fps = _active.Fps;
            _videoDurationSeconds = 0;
            _startTimeSeconds = _endTimeSeconds = 0;
            TimeSlider.Value = TimeSlider.Maximum = 0;
            GifPreviewImage.Source = _active.Poster;
            GifPreviewImage.Visibility = _active.Poster is null ? Visibility.Collapsed : Visibility.Visible;
            _hasAudioTrack = !_isGif && MediaHasAudioTrack(_mediaFilePath);
            VolumeControl.Volume = _active.Volume;
            VolumeControl.IsExportMuted = _active.ExportMuted;
            if (_isGif) VolumeControl.SetGifMode();
            else if (!_hasAudioTrack) VolumeControl.SetNoAudioMode();
            else { VolumeControl.HasAudioTrack = true; SyncMediaAudio(); }
            _loopEnabled = _active.Loop;
            _preciseCut = _active.Precise;
            _detailedTimeDisplay = _active.DetailedTime;
            LoopToggleBtn.IsChecked = _loopEnabled;
            PreciseCutToggleBtn.IsChecked = _preciseCut;
            UpdateLoopTooltip();
            UpdatePreciseCutTooltip();
            SetStartBtn.IsChecked = SetEndBtn.IsChecked = false;
            ResetPreviewZoom();
            SetPlayPauseIcon(false);
            UpdatePlayPauseToolTip();
            RefreshTimeDisplay();
            EvaluateCropState();
            SetDocumentControlsEnabled(false);
            ReattachPreviewRendering();
            if (_isGif) LoadGifPreview(); else LoadMp4Preview();
        }
        finally { _restoringSession = false; }
    }

    private void RestoreLoadedSession()
    {
        var session = _active;
        _previewReady = true;
        _startTimeSeconds = session.Initialized ? Math.Clamp(session.Start, 0, _videoDurationSeconds) : 0;
        _endTimeSeconds = session.Initialized ? Math.Clamp(session.End, _startTimeSeconds, _videoDurationSeconds) : _videoDurationSeconds;
        SetStartBtn.IsChecked = session.Initialized && session.StartMarker;
        SetEndBtn.IsChecked = session.Initialized && session.EndMarker;
        double position = Math.Clamp(session.Position, 0, _videoDurationSeconds);
        _currentZoom = session.Zoom;
        _zoomToFit = session.Fit;
        _didInitialContain = session.DidInitialContain;
        _gifPausedTimelineSeconds = position;
        ResetGifPlayAnchor(position);
        _gifTimelinePaused = !session.Playing;
        UpdatePreviewFrameDisplay(position, force: true);
        TimeSlider.Value = position;
        SetPlayPauseIcon(session.Playing);
        UpdatePlayPauseToolTip();
        if (!_isGif) SyncMp4Audio(position, session.Playing);
        RefreshTimeDisplay();
        UpdateMarkerLabels();
        EvaluateCropState();
        ApplyZoom();
        TryApplyInitialContain();
        session.Initialized = true;
        session.Duration = _videoDurationSeconds;
        session.Poster = null;
        SetDocumentControlsEnabled(!_isExporting);
        int version = _isGif ? _gifLoadVersion : _mp4LoadVersion;
        Dispatcher.BeginInvoke(() =>
        {
            if (_sessionsFinalized || !ReferenceEquals(_active, session)
                || version != (_isGif ? _gifLoadVersion : _mp4LoadVersion)) return;
            ApplyZoom();
            ZoomViewport.UpdateLayout();
            ZoomViewport.ScrollToHorizontalOffset(session.PanX);
            ZoomViewport.ScrollToVerticalOffset(session.PanY);
        }, DispatcherPriority.Loaded);
    }

    private void RefreshTabs()
    {
        TrimmerTabStrip.Visibility = _sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        TrimmerTabStrip.SetTabs(_sessions.Select(s => new PreviewTabInfo(
            Path.GetFileName(s.FilePath) + ((ReferenceEquals(s, _active) && _previewReady ? IsCropModified() : s.Modified) ? " *" : ""),
            ReferenceEquals(s, _active))).ToArray());
        TrimmerTabStrip.RevealActiveTab();
    }

    private bool ConfirmDiscardChanges(TrimmerSession session)
    {
        if (!session.Modified) return true;
        string lang = _settingsService.Settings.InterfaceLanguage;
        bool previousPrompt = _documentPromptOpen;
        _documentPromptOpen = true;
        try
        {
            string message = session.OwnedRecordings.Contains(session.FilePath)
                ? LocalizationService.Translate(lang, "The trim changes and this temporary recording will be discarded.")
                : string.Format(LocalizationService.Translate(lang, "The changes made will be lost.\nThe original file '{0}' will be kept."), Path.GetFileName(session.FilePath));
            return ThemedConfirmDialog.Confirm(this, LocalizationService.Translate(lang, "Discard changes?"),
                message,
                LocalizationService.Translate(lang, "Discard"), LocalizationService.Translate(lang, "Keep editing"), danger: false);
        }
        finally { _documentPromptOpen = previousPrompt; }
    }

    private void CloseSessionAt(int index)
    {
        if (_isExporting || _sessionsFinalized || _documentPromptOpen || index < 0 || index >= _sessions.Count) return;
        if (_sessions.Count == 1) { Close(); return; }
        CommitPendingTimeInputs();
        CaptureSession();
        var session = _sessions[index];
        if (!ConfirmDiscardChanges(session)) return;
        bool active = ReferenceEquals(session, _active);
        if (active) { FlushAudioPreferences(); TeardownPreviewForReload(); }
        _sessions.RemoveAt(index);
        if (_lastClosed is not null) _ = ReleaseSessionsAfterPreviewLoadsAsync([_lastClosed]);
        _lastClosed = session;
        if (active) { _active = _sessions[Math.Min(index, _sessions.Count - 1)]; LoadActiveSession(); }
        RefreshTabs();
    }

    private void ReopenLastSession()
    {
        if (_lastClosed is null || _isExporting || _sessionsFinalized || _documentPromptOpen) return;
        var session = _lastClosed;
        if (!File.Exists(session.FilePath))
        {
            ShowBanner(LocalizationService.Translate("The source file no longer exists."));
            return;
        }
        _lastClosed = null;
        int duplicate = _sessions.FindIndex(s => string.Equals(s.FilePath, session.FilePath, StringComparison.OrdinalIgnoreCase));
        if (duplicate >= 0)
        {
            _sessions[duplicate].OwnedRecordings.UnionWith(session.OwnedRecordings);
            ActivateSessionAt(duplicate);
            return;
        }
        _sessions.Add(session);
        ActivateSessionAt(_sessions.Count - 1);
    }

    private bool HandleTabHotkey(System.Windows.Input.KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.W) CloseSessionAt(_sessions.IndexOf(_active));
        else if (e.Key == Key.Tab && (mods == ModifierKeys.Control || mods == (ModifierKeys.Control | ModifierKeys.Shift)))
            ActivateSessionAt((_sessions.IndexOf(_active) + (mods.HasFlag(ModifierKeys.Shift) ? _sessions.Count - 1 : 1)) % _sessions.Count);
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.T) ReopenLastSession();
        else return false;
        e.Handled = true;
        return true;
    }

    private void ShowTabMenu(int index)
    {
        if (_isExporting || index < 0 || index >= _sessions.Count) return;
        var session = _sessions[index];
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, Background = Theme.Brush(Theme.BgElevated),
            Foreground = Theme.Brush(Theme.TextPrimary), BorderBrush = Theme.Brush(Theme.BorderSubtle), Padding = new Thickness(4) };
        Add("Close tab", "Ctrl+W", () => CloseSessionAt(_sessions.IndexOf(session)));
        Add("Close all", "", Close);
        if (_lastClosed is not null) Add("Reopen last closed tab", "Ctrl+Shift+T", ReopenLastSession);
        menu.IsOpen = true;
        void Add(string key, string shortcut, Action action)
        {
            var item = new MenuItem { Header = LocalizationService.Translate(key), InputGestureText = shortcut };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_sessionsFinalized) return;
        if (_documentPromptOpen) { e.Cancel = true; return; }
        CommitPendingTimeInputs();
        CaptureSession();
        if (!_closeApproved)
        {
            var candidates = _sessions.ToArray();
            _documentPromptOpen = true;
            try
            {
                string lang = _settingsService.Settings.InterfaceLanguage;
                if (_sessions.Count > 1 && !ThemedConfirmDialog.Confirm(this, LocalizationService.Translate(lang, "Close all tabs?"),
                    string.Format(LocalizationService.Translate(lang, "This window contains {0} files. Close all tabs?"), _sessions.Count),
                    LocalizationService.Translate(lang, "Close all"), LocalizationService.Translate(lang, "Cancel"), danger: false))
                { e.Cancel = true; return; }
                foreach (var session in candidates)
                    if (!ConfirmDiscardChanges(session)) { e.Cancel = true; return; }
                if (_isExporting && !ThemedConfirmDialog.Confirm(this, LocalizationService.Translate(lang, "Cancel export?"),
                    LocalizationService.Translate(lang, "An export is in progress. Cancel it and close the Trimmer?"),
                    LocalizationService.Translate(lang, "Cancel export and close"), LocalizationService.Translate(lang, "Keep editing"), danger: false))
                { e.Cancel = true; return; }
                if (_sessions.Count != candidates.Length) { e.Cancel = true; return; }
            }
            finally { _documentPromptOpen = false; }
        }
        if (_isExporting)
        {
            _closeAfterExport = _closeApproved = true;
            _exportCts?.Cancel();
            e.Cancel = true;
            return;
        }
        _sessionsFinalized = true;
        FlushAudioPreferences();
        TeardownPreviewForReload();
        CancelZoomHideTimer();
        _bannerTimer?.Stop();
        _filmstripResizeTimer?.Stop();
        // A cancelled FFmpeg decoder may still be releasing the source handle.
        // Delete owned recordings only after every outstanding decoder has settled.
        var retired = _sessions.Concat(_lastClosed is null ? [] : new[] { _lastClosed }).ToArray();
        _ = ReleaseSessionsAfterPreviewLoadsAsync(retired);
        _sessions.Clear();
        _lastClosed = null;
    }

    private void FlushAudioPreferences()
    {
        _audioPersistTimer.Stop();
        if (_audioPersistPending && Application.Current is App app)
            app.PersistVideoTrimmerAudio(VolumeControl.Volume, VolumeControl.IsExportMuted);
        _audioPersistPending = false;
    }

    private void CommitPendingTimeInputs()
    {
        if (StartTimeInput.Visibility == Visibility.Visible) CommitTimeInput(StartTimeInput);
        if (EndTimeInput.Visibility == Visibility.Visible) CommitTimeInput(EndTimeInput);
    }

    private void ReleaseSession(TrimmerSession? session)
    {
        if (session is null) return;
        session.Poster = null;
        foreach (var path in session.OwnedRecordings)
        {
            var openSessions = _sessions.Concat(Application.Current.Windows.OfType<VideoTrimmerWindow>()
                .Where(w => !ReferenceEquals(w, this)).SelectMany(w => w._sessions));
            var receiver = openSessions.FirstOrDefault(s => !ReferenceEquals(s, session)
                && (string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase) || s.OwnedRecordings.Contains(path)));
            if (receiver is not null) receiver.OwnedRecordings.Add(path);
            else CaptureSavePath.TryDeleteTempRecording(path);
        }
        session.OwnedRecordings.Clear();
    }

    private void SetDocumentControlsEnabled(bool enabled)
    {
        TimelinePanel.IsEnabled = PlaybackPanel.IsEnabled = enabled;
    }
}

internal sealed class TrimmerSession(string path)
{
    public string FilePath { get; set; } = path;
    public HashSet<string> OwnedRecordings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ImageSource? Poster { get; set; }
    public bool Initialized { get; set; }
    public double Duration { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public double Position { get; set; }
    public bool Playing { get; set; } = true;
    public double Volume { get; set; }
    public bool ExportMuted { get; set; }
    public bool Loop { get; set; }
    public bool Precise { get; set; }
    public bool DetailedTime { get; set; }
    public double Fps { get; set; }
    public double Zoom { get; set; } = 1;
    public bool Fit { get; set; }
    public bool DidInitialContain { get; set; }
    public double PanX { get; set; }
    public double PanY { get; set; }
    public bool StartMarker { get; set; }
    public bool EndMarker { get; set; }
    public bool Modified => Initialized && (Start > 0.05 || End < Duration - 0.05);
}
