using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace CyberSnap;

public partial class App
{
    /// <summary>
    /// Routes a completed scrolling capture through the same optional preview used by
    /// regular image captures. The preview is opened on the WPF dispatcher, while the
    /// scrolling form remains responsible only for producing the bitmap.
    /// </summary>
    private void HandleScrollingCaptureResult(Bitmap capture)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => HandleScrollingCaptureResult(capture));
            return;
        }

        var latest = Services.SettingsService.LoadStatic();
        if (latest != null)
            _settingsService!.Settings = latest;

        var settings = _settingsService!.Settings;
        if (!settings.ShowCapturePreview)
        {
            HandleCaptureResult(capture, captureKind: CaptureKind.ScrollCapture);
            MarkFirstTime(
                settings.HasFirstScrollingCapture,
                () => settings.HasFirstScrollingCapture = true,
                "First scrolling capture",
                "scrollCapture",
                d => settings.FirstScrollCaptureAt = d);
            return;
        }

        bool previewOpened = false;
        try
        {
            bool copiedEarly = false;
            if (Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Image))
                copiedEarly = TryCopyCaptureOutputToClipboard(capture, null);

            string? earlySavePath = TrySaveCaptureFileEarly(capture, settings);
            var dialog = new UI.CapturePreviewDialog(
                capture,
                _settingsService,
                targetMonitorPoint: null,
                savedFilePath: earlySavePath,
                clipboardAlreadyCopied: copiedEarly);

            ShowCapturePreviewDialog(dialog, result =>
            {
                bool isScaled = dialog.ScaleFactor != 1;
                var effective = isScaled ? dialog.EffectiveBitmap : capture;
                string? effectiveSavePath =
                    ResolvePreviewCommitSavePath(dialog.SavedFilePath, earlySavePath, isScaled);
                bool effectiveCopied = isScaled ? false : dialog.ClipboardAlreadyCopied;

                if (result == true)
                {
                    if (isScaled && !string.IsNullOrEmpty(earlySavePath) && File.Exists(earlySavePath))
                    {
                        try { File.Delete(earlySavePath); } catch { }
                    }

                    HandleCaptureResult(
                        effective,
                        dialog.SelectedAction,
                        effectiveSavePath,
                        effectiveCopied,
                        isExplicitScaled: isScaled,
                        captureKind: CaptureKind.ScrollCapture);
                    MarkFirstTime(
                        settings.HasFirstScrollingCapture,
                        () => settings.HasFirstScrollingCapture = true,
                        "First scrolling capture",
                        "scrollCapture",
                        d => settings.FirstScrollCaptureAt = d);

                    if (isScaled)
                    {
                        try { capture.Dispose(); } catch { }
                    }
                }
                else
                {
                    try { effective.Dispose(); } catch { }
                    if (isScaled)
                    {
                        try { capture.Dispose(); } catch { }
                    }

                    // null means the preview was replaced by a newer capture; that new
                    // session owns the busy slot and will perform its own cleanup.
                    if (result == false)
                        ResetCapturing();
                }
            });
            previewOpened = true;
        }
        catch (Exception ex)
        {
            if (!previewOpened)
            {
                try { capture.Dispose(); } catch { }
            }

            AppDiagnostics.LogError("capture.scroll-preview", ex);
            ResetCapturing();
            ShowCaptureProcessingFailed(
                LocalizationService.Translate("Scroll capture error"),
                LocalizationService.Translate("CyberSnap could not finish the scrolling capture. Try a smaller scroll area or a visible scrollable window."),
                ex.Message);
        }
    }

    private void HandleCaptureResult(
        Bitmap result,
        RegionOverlayForm.ConfirmCommitAction commitAction = RegionOverlayForm.ConfirmCommitAction.Default,
        string? alreadySavedPath = null,
        bool clipboardAlreadyCopied = false,
        bool isExplicitScaled = false,
        CaptureKind captureKind = CaptureKind.Screenshot)
    {
        var settings = _settingsService!.Settings;
        var ext = CaptureOutputService.GetExtension(settings.CaptureImageFormat);
        string? requestedPath = null;

        // Confirm-mode Save / Edit / History / Share always need a file on disk.
        // SaveToFile is the user's explicit save choice. System viewer no longer
        // forces a save here: when SaveToFile is off it gets a temp file instead.
        bool forceSave = commitAction is RegionOverlayForm.ConfirmCommitAction.Save
            or RegionOverlayForm.ConfirmCommitAction.Edit
            or RegionOverlayForm.ConfirmCommitAction.History
            or RegionOverlayForm.ConfirmCommitAction.Share
            || settings.SaveToFile
            || settings.AutoShareAfterCapture;

        if (!string.IsNullOrEmpty(alreadySavedPath) && File.Exists(alreadySavedPath))
        {
            // Preview already wrote the file (immediate Save); reuse path for history / share.
            requestedPath = alreadySavedPath;
        }
        else if (forceSave)
        {
            var defaultPath = Helpers.CaptureSavePath.BuildAvailablePath(
                settings.SaveDirectory,
                $"{Helpers.FileNameTemplate.Format(settings.FileNameTemplate, result.Width, result.Height)}.{ext}",
                settings.SaveInMonthlyFolders);
            if (settings.AskForFileNameOnSave)
            {
                // SaveFileDialog must run on the WPF dispatcher thread
                string? resolved = null;
                Dispatcher.Invoke(() => resolved = ResolveSavePath(defaultPath, settings.CaptureImageFormat));
                requestedPath = resolved;
            }
            else
            {
                requestedPath = defaultPath;
            }
            if (requestedPath is null)
            {
                result.Dispose();
                ResetCapturing();
                return;
            }
        }

        _ = PersistCaptureAsync(result, requestedPath, saveHistory: settings.SaveHistory, isExplicitScaled: isExplicitScaled)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        ResetCapturing();
                        ShowCaptureProcessingFailed(
                            "Capture error",
                            "CyberSnap could not finish the capture result. Try again, or choose another save folder in Settings.",
                            task.Exception?.GetBaseException().Message ?? "Capture failed");
                        ScheduleIdleMemoryTrim();
                    });
                    return;
                }

                var persisted = task.Result;
                Dispatcher.BeginInvoke(() =>
                {
                    var action = NormalizeAfterCaptureAction(settings.AfterCapture);
                    // Skip the duplicate write when the preview already did the eager copy
                    // for this same capture (auto-copy ON + preview shown). Otherwise the
                    // clipboard gets stamped twice — visible in Win+V as a duplicated entry.
                    bool explicitCopy = commitAction == RegionOverlayForm.ConfirmCommitAction.Copy;
                    bool shouldTryCopy = explicitCopy
                        || (!clipboardAlreadyCopied
                            && Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Image));
                    bool copied;
                    if (shouldTryCopy)
                        copied = TryCopyCaptureOutputToClipboard(persisted.Output, persisted.FilePath);
                    else if (clipboardAlreadyCopied)
                        // Eager copy already ran for this capture — report it as done so the
                        // notification toast shows "Copied to clipboard" instead of stale
                        // "Clipboard copy failed".
                        copied = true;
                    else
                        copied = false;
                    // For the toast: clipboard was effectively done or attempted. This is what
                    // decides whether the "Copied to clipboard" row shows.
                    bool copyWanted = shouldTryCopy || clipboardAlreadyCopied;
                    ResetCapturing();

                    CelebrateCaptureIfEarned(settings, captureKind);

                    bool openEditor = commitAction == RegionOverlayForm.ConfirmCommitAction.Edit
                        || (commitAction == RegionOverlayForm.ConfirmCommitAction.Default
                            && settings.OpenEditorAfterCapture
                            && persisted.HistoryEntry?.Kind != Services.HistoryKind.Video
                            && persisted.HistoryEntry?.Kind != Services.HistoryKind.Gif);

                    // Respect the After Capture Notification pill setting
                    var outcomeState = Helpers.AfterCaptureOutcomeModel.FromSettings(settings);
                    bool wantNotification = outcomeState.Destination == Helpers.AfterCaptureDestination.Notification;
                    // Preview chips show in-dialog progress; the compact status toast still runs
                    // after confirm when Notification is on (including after Preview closes).
                    bool showCompactToast = wantNotification;
                    bool savedToDisk = !string.IsNullOrEmpty(persisted.FilePath) && File.Exists(persisted.FilePath);

                    if (openEditor)
                    {
                        bool openedInEditor = false;
                        try
                        {
                            openedInEditor = CyberSnap.UI.Editor.EditorForm.ShowEditor(
                                new Bitmap(persisted.Output),
                                persisted.FilePath,
                                CyberSnap.Helpers.ImageOpenSource.Capture);
                        }
                        catch (Exception ex)
                        {
                            AppDiagnostics.LogError("capture.auto-open-editor", ex);
                        }

                        if (!openedInEditor)
                        {
                            TryOpenSystemViewerAfterCapture(settings, action, persisted.FilePath);
                            persisted.Output.Dispose();
                            if (showCompactToast)
                            {
                                ShowDynamicAfterCaptureToast(
                                    saved: savedToDisk,
                                    copied: copied,
                                    copyWanted: copyWanted,
                                    share: default,
                                    openedEditor: false,
                                    openedViewer: true,
                                    filePath: persisted.FilePath);
                            }
                            ScheduleIdleMemoryTrim();
                            return;
                        }

                        persisted.Output.Dispose();
                        if (showCompactToast)
                        {
                            ShowDynamicAfterCaptureToast(
                                saved: savedToDisk,
                                copied: copied,
                                copyWanted: copyWanted,
                                share: default,
                                openedEditor: true,
                                openedViewer: false,
                                filePath: persisted.FilePath);
                        }
                    }
                    else if (commitAction == RegionOverlayForm.ConfirmCommitAction.History)
                    {
                        persisted.Output.Dispose();
                        ShowHistory(persisted.FilePath);
                    }
                    else if (commitAction == RegionOverlayForm.ConfirmCommitAction.Share)
                    {
                        var shareBmp = persisted.Output;
                        var sharePath = persisted.FilePath;
                        _ = ShareCaptureFromConfirmAsync(shareBmp, sharePath);
                    }
                    else
                    {
                        bool isExplicitViewer = commitAction == RegionOverlayForm.ConfirmCommitAction.Viewer;
                        bool wantViewer = isExplicitViewer
                            || settings.OpenInSystemViewerAfterCapture
                            || action == AfterCaptureAction.OpenInSystemViewer;
                        bool hadPersistentFile = savedToDisk;
                        string? viewerPath = hadPersistentFile ? persisted.FilePath : null;
                        bool openedViewer = false;

                        // System viewer needs a path to open; materialize a temp PNG only
                        // when nothing was persisted (SaveToFile off). Best-effort cleanup
                        // runs below so the temp file does not linger permanently.
                        if (wantViewer && !hadPersistentFile)
                            viewerPath = MaterializeTempViewerFile(persisted.Output);

                        if (viewerPath != null)
                            openedViewer = TryOpenSystemViewerAfterCapture(settings, action, viewerPath, force: isExplicitViewer);

                        bool createdTempForViewer = wantViewer && !hadPersistentFile && viewerPath != null;
                        if (createdTempForViewer)
                        {
                            var cleanupPath = viewerPath;
                            _ = Task.Delay(TimeSpan.FromSeconds(90))
                                .ContinueWith(_ => Helpers.CaptureSavePath.TryDeleteTempRecording(cleanupPath));
                            if (!openedViewer)
                                Helpers.CaptureSavePath.TryDeleteTempRecording(cleanupPath);
                        }

                        bool wantAutoShare = settings.AutoShareAfterCapture
                            && commitAction == RegionOverlayForm.ConfirmCommitAction.Default;

                        if (wantAutoShare)
                        {
                            var shareBmp = persisted.Output;
                            var sharePath = persisted.FilePath;
                            _ = ShareCaptureThenMaybeToastAsync(
                                shareBmp,
                                sharePath,
                                showCompactToast,
                                savedToDisk,
                                copied,
                                copyWanted,
                                openedViewer);
                        }
                        else
                        {
                            persisted.Output.Dispose();
                            if (showCompactToast)
                            {
                                ShowDynamicAfterCaptureToast(
                                    saved: savedToDisk,
                                    copied: copied,
                                    copyWanted: copyWanted,
                                    share: default,
                                    openedEditor: false,
                                    openedViewer: openedViewer,
                                    filePath: persisted.FilePath);
                            }
                        }
                    }

                    ScheduleIdleMemoryTrim();
                });
            }, TaskScheduler.Default);
    }

    private async Task ShareCaptureFromConfirmAsync(Bitmap bitmap, string? filePath)
    {
        try
        {
            await TryShareCaptureAsync(bitmap, filePath, presentResultToast: true).ConfigureAwait(true);
        }
        finally
        {
            try { bitmap.Dispose(); } catch { }
            ScheduleIdleMemoryTrim();
        }
    }

    private async Task ShareCaptureThenMaybeToastAsync(
        Bitmap bitmap,
        string? filePath,
        bool showCompactToast,
        bool saved,
        bool copied,
        bool copyWanted,
        bool openedViewer)
    {
        ShareAttempt shareAttempt = default;
        try
        {
            // When the enriched summary toast will list "Shared", skip PresentResult's own toast.
            shareAttempt = await TryShareCaptureAsync(bitmap, filePath, presentResultToast: !showCompactToast)
                .ConfigureAwait(true);
        }
        finally
        {
            try { bitmap.Dispose(); } catch { }
        }

        if (showCompactToast)
        {
            ShowDynamicAfterCaptureToast(
                saved: saved,
                copied: copied,
                copyWanted: copyWanted,
                share: shareAttempt,
                openedEditor: false,
                openedViewer: openedViewer,
                filePath: filePath);
        }

        ScheduleIdleMemoryTrim();
    }

    /// <summary>Short summary of a share attempt, for the enriched after-capture toast.</summary>
    private readonly record struct ShareAttempt(bool Success, string? Url, string? ErrorMessage);

    private async Task<ShareAttempt> TryShareCaptureAsync(Bitmap bitmap, string? filePath, bool presentResultToast)
    {
        try
        {
            var settings = _settingsService!.Settings;
            var provider = Services.Upload.ImageUploadService.GetDefaultProvider(settings);
            var owner = Current.MainWindow;
            IntPtr ownerHandle = IntPtr.Zero;
            try
            {
                if (owner is not null)
                    ownerHandle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            }
            catch { }

            if (!UI.Share.ImageShareFlow.ConfirmThirdPartyUploadIfNeeded(owner, ownerHandle, provider, settings))
                return default;

            var result = await UI.Share.ImageShareFlow.ShareBitmapAsync(bitmap).ConfigureAwait(true);
            if (presentResultToast)
                UI.Share.ImageShareFlow.PresentResult(result, settings);
            return new ShareAttempt(
                result.Success,
                result.PublicUrl ?? result.ClipboardText,
                result.Success ? null : result.ErrorMessage);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("capture.confirm-share", ex);
            ToastWindow.Show(
                LocalizationService.Translate("Upload failed"),
                LocalizationService.Translate("CyberSnap could not share the capture. Check your network or upload configuration in Settings."),
                filePath);
            return default;
        }
    }

    /// <summary>Truncate a long path to a compact "drive:\…\<folder>\<file>" form for the toast.</summary>
    private static string ShortenPath(string path, int maxLen = 42)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLen)
            return path;

        string file = System.IO.Path.GetFileName(path);
        string root = System.IO.Path.GetPathRoot(path) ?? "";        // e.g. "C:\"
        string dir  = System.IO.Path.GetDirectoryName(path) ?? "";
        string? parentName = null;
        if (!string.IsNullOrEmpty(dir))
            parentName = System.IO.Path.GetFileName(dir.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar));

        string candidate = parentName is { Length: > 0 }
            ? $"{root}…\\{parentName}\\{file}"
            : $"{root}…\\{file}";

        if (candidate.Length > maxLen)
            candidate = $"{root}…\\{file}";
        if (candidate.Length > maxLen)
            return path; // hopeless — hand back the original (TextTrimming will ellipsize if needed)

        return candidate;
    }

    /// <summary>
    /// Enriched status toast listing automatic steps completed when Notification is on
    /// (including after Preview closes). One row per SELECTED action. A row is ✓ when
    /// the action completed, ✗ when it was attempted but failed, and ⚠ (info) when the
    /// action is configured but didn't run this time (e.g. share-on-demand disabled).
    /// Preview row is omitted (preview already ran); the toast itself is the Notification.
    /// </summary>
    private void ShowDynamicAfterCaptureToast(
        bool saved,
        bool copied,
        bool copyWanted,
        ShareAttempt share,
        bool openedEditor,
        bool openedViewer,
        string? filePath)
    {
        var state = Helpers.AfterCaptureOutcomeModel.FromSettings(_settingsService!.Settings);
        var rows = new List<(int Order, UI.ToastStatusLine Line)>();

        void TryAdd(Helpers.AfterCapturePillKind pill, UI.ToastStatusLine line)
        {
            rows.Add((Helpers.AfterCaptureOutcomeModel.FlowDisplayOrder(pill), line));
        }

        // ── Save ──────────────────────────────────────────────────────────────────────
        // Show the row when the Save action is configured; icon reflects outcome.
        if (state.Save)
        {
            bool saveOk = saved && !string.IsNullOrEmpty(filePath);
            TryAdd(Helpers.AfterCapturePillKind.Save, new UI.ToastStatusLine
            {
                IconId = saveOk ? "check" : "warning",
                Label = saveOk
                    ? LocalizationService.Translate("Image saved")
                    : LocalizationService.Translate("Capture not saved"),
                Detail = saveOk ? ShortenPath(filePath!) : null,
                IsError = !saveOk,
                CopyableText = saveOk ? filePath : null,
                CopyableTooltip = saveOk ? LocalizationService.Translate("Copy path") : null
            });
        }

        // ── Clipboard ─────────────────────────────────────────────────────────────────
        // Configured via auto-copy pill, OR user pressed the preview "Copy" button (one-shot).
        bool autoCopyConfigured = Helpers.AutoCopyPreferences.ShouldCopy(
            _settingsService!.Settings, Helpers.AutoCopyKind.Image);
        bool clipboardConfigured = autoCopyConfigured || copyWanted;
        if (clipboardConfigured)
        {
            TryAdd(Helpers.AfterCapturePillKind.Clipboard, new UI.ToastStatusLine
            {
                IconId = copied ? "check" : "warning",
                Label = copied
                    ? LocalizationService.Translate("Copied to clipboard")
                    : LocalizationService.Translate("Clipboard copy failed"),
                IsError = !copied
            });
        }

        // ── Share ─────────────────────────────────────────────────────────────────────
        // Configured auto-share → row always appears; ✓ = success, ⚠ = didn't run, ✗ = failed.
        if (state.Share)
        {
            UI.ToastStatusLine shareLine;
            if (share.Success)
                shareLine = new UI.ToastStatusLine
                {
                    IconId = "check",
                    Label = LocalizationService.Translate("Shared"),
                    Detail = share.Url
                };
            else if (!string.IsNullOrEmpty(share.ErrorMessage))
                shareLine = new UI.ToastStatusLine
                {
                    IconId = "warning",
                    Label = LocalizationService.Translate("Shared"),
                    Detail = share.ErrorMessage,
                    IsError = true
                };
            else
                shareLine = new UI.ToastStatusLine
                {
                    IconId = "info",
                    Label = LocalizationService.Translate("Shared"),
                    Detail = LocalizationService.Translate("Not run")
                };
            TryAdd(Helpers.AfterCapturePillKind.Share, shareLine);
        }

        // ── Editor ────────────────────────────────────────────────────────────────────
        // Destination radio: Editor / Notification (we're inside the toast, so the user
        // picked Editor). Show the row only when Editor is the configured destination.
        if (state.Destination == Helpers.AfterCaptureDestination.Editor)
        {
            TryAdd(Helpers.AfterCapturePillKind.Editor, new UI.ToastStatusLine
            {
                IconId = openedEditor ? "check" : "info",
                Label = LocalizationService.Translate("Opened in editor"),
                Detail = openedEditor ? null : LocalizationService.Translate("Not run")
            });
        }

        // ── System Viewer ─────────────────────────────────────────────────────────────
        if (state.SystemViewer)
        {
            TryAdd(Helpers.AfterCapturePillKind.SystemViewer, new UI.ToastStatusLine
            {
                IconId = openedViewer ? "check" : "info",
                Label = LocalizationService.Translate("Opened in system viewer"),
                Detail = openedViewer ? null : LocalizationService.Translate("Not run")
            });
        }

        rows.Sort((a, b) => a.Order.CompareTo(b.Order));

        ToastWindow.Show(new ToastSpec
        {
            Title = LocalizationService.Translate("Capture processed"),
            StatusLines = rows.Select(r => r.Line).ToList(),
            FilePath = filePath,
            PlayCaptureSound = true,
            IsSystemMessage = false
        });
    }

    /// <summary>
    /// Writes the capture to disk before the preview dialog opens when Save is on and
    /// no Save-As prompt is required. Does not dispose <paramref name="source"/>.
    /// </summary>
    private string? TrySaveCaptureFileEarly(Bitmap source, AppSettings settings)
    {
        if (!settings.SaveToFile || settings.AskForFileNameOnSave)
            return null;

        try
        {
            var ext = CaptureOutputService.GetExtension(settings.CaptureImageFormat);
            var path = Helpers.CaptureSavePath.BuildAvailablePath(
                settings.SaveDirectory,
                $"{Helpers.FileNameTemplate.Format(settings.FileNameTemplate, source.Width, source.Height)}.{ext}",
                settings.SaveInMonthlyFolders);

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            Directory.CreateDirectory(directory);

            using var clone = new Bitmap(source);
            var prepared = CaptureOutputService.PrepareBitmap(clone, settings.CaptureMaxLongEdge);
            try
            {
                CaptureOutputService.SaveBitmap(prepared, path, settings.CaptureImageFormat, settings.JpegQuality);
            }
            finally
            {
                if (!ReferenceEquals(prepared, clone))
                    prepared.Dispose();
            }

            return path;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("capture.early-save", ex.Message, ex);
            return null;
        }
    }

    /// <summary>Picks the file path to hand to <see cref="HandleCaptureResult"/> after preview.
    /// Prefers a copy the user named with Save as...; a scaled preview cannot reuse the
    /// original-size early-save file.</summary>
    private static string? ResolvePreviewCommitSavePath(string? dialogPath, string? earlySavePath, bool isScaled)
    {
        if (isScaled)
        {
            bool namedCopy = !string.IsNullOrEmpty(dialogPath)
                && !string.Equals(dialogPath, earlySavePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(dialogPath);
            return namedCopy ? dialogPath : null;
        }

        if (!string.IsNullOrEmpty(dialogPath) && File.Exists(dialogPath))
            return dialogPath;
        return earlySavePath;
    }

    /// <summary>
    /// Materializes a temp PNG so a file-dependent after-capture step (system viewer)
    /// can run when SaveToFile is off. The caller schedules best-effort cleanup.
    /// </summary>
    private static string? MaterializeTempViewerFile(Bitmap output)
    {
        try
        {
            var tempPath = Helpers.CaptureSavePath.BuildTempCapturePath(".png");
            Services.CaptureOutputService.SavePng(output, tempPath);
            return tempPath;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("capture.viewer-temp-file", ex.Message, ex);
            return null;
        }
    }

    private Task<PersistedCaptureResult> PersistCaptureAsync(
        Bitmap source,
        string? requestedPath,
        bool saveHistory,
        bool isExplicitScaled = false)
    {
        var settings = _settingsService!.Settings;
        int maxLongEdge = isExplicitScaled ? 0 : settings.CaptureMaxLongEdge;
        var captureFormat = settings.CaptureImageFormat;
        int jpegQuality = settings.JpegQuality;

        return Task.Run(() =>
        {
            using (source)
            {
                var prepared = CaptureOutputService.PrepareBitmap(source, maxLongEdge);
                var output = prepared;
                string? filePath = requestedPath;
                Services.HistoryEntry? historyEntry = null;
                var historyService = saveHistory ? EnsureHistoryService() : null;

                if (requestedPath != null)
                {
                    var directory = Path.GetDirectoryName(requestedPath);
                    if (string.IsNullOrWhiteSpace(directory))
                        throw new InvalidOperationException("Save path must include a directory.");

                    Directory.CreateDirectory(directory);
                    CaptureOutputService.SaveBitmap(output, requestedPath, captureFormat, jpegQuality);

                    filePath = requestedPath;
                }

                // Gallery only indexes files the user actually saved (SaveToFile / save folder).
                // Never write capture images into a CyberSnap "History" (or gallery data) folder.
                if (historyService != null && filePath != null)
                {
                    historyEntry = historyService.TrackExistingCapture(
                        filePath,
                        output.Width,
                        output.Height,
                        HistoryKind.Image);
                }

                if (historyEntry is not null)
                    HistoryWindow.WarmRecentHistoryThumbs(new[] { historyEntry }, maxCount: 1);

                return new PersistedCaptureResult
                {
                    Output = output,
                    FilePath = filePath,
                    HistoryEntry = historyEntry
                };
            }
        });
    }

    // Counting core, shared by every capture path (image, OCR, video/GIF). Bumps the running
    // total, updates the consecutive-day streak on the first capture of each day, stamps the local
    // day, and persists (Save is debounced, so per-capture saving is cheap). Returns null when
    // Flips a first-time achievement flag on its very first occurrence and persists it (Save is
    // debounced). No-op once already unlocked. Independent of CelebrationsEnabled — the medal
    // grid records what happened even with celebration toasts turned off.
    private void MarkFirstTime(bool alreadyUnlocked, Action setUnlocked,
        string? achievementTitleKey = null, string? iconId = null, Action<string>? setUnlockedDate = null)
    {
        if (alreadyUnlocked) return;
        setUnlocked();
        setUnlockedDate?.Invoke(DateTime.Now.ToString("yyyy-MM-dd"));
        try { _settingsService!.Save(); }
        catch (Exception ex) { AppDiagnostics.LogWarning("capture.first-time-save", ex.Message, ex); }

        // Recording the medal is unconditional (above); the celebratory toast for the unlock
        // respects the Celebrations setting, matching the milestone/streak flourishes.
        if (achievementTitleKey is { Length: > 0 } && iconId is { Length: > 0 }
            && _settingsService?.Settings.CelebrationsEnabled == true)
        {
            ShowFirstTimeAchievementToast(achievementTitleKey, iconId);
        }
    }

    // Celebrates a first-time achievement unlock with a dedicated toast carrying the tool's own
    // icon. Shown after a short delay so it reads as a follow-up to the tool's functional toast
    // (scan result, "Color copied", etc.) rather than instantly replacing it in the single-toast
    // host. Fired at most once per achievement since MarkFirstTime no-ops after the first unlock.
    private void ShowFirstTimeAchievementToast(string achievementTitleKey, string iconId) =>
        ShowDelayedCelebrationToast(() =>
        {
            // Warm gold reads as a reward and stays legible on the dark toast shell.
            var accent = System.Drawing.Color.FromArgb(255, 0xFF, 0xC1, 0x07);
            var icon = Helpers.FluentIcons.RenderBitmap(iconId, accent, 40);
            var title = LocalizationService.Translate("Achievement unlocked!");
            var body = LocalizationService.Translate(achievementTitleKey);

            return (icon is not null
                ? ToastSpec.InlinePreview(icon, title, body)
                : ToastSpec.Standard(title, body)) with
            {
                Celebrate = true,
                SuppressSound = true,
                IsSystemMessage = false,
                CelebrationRank = ToastSpec.RankFirstTime,
                // A trophy after the name reads as an unlock; the default capture icon would be
                // out of place here since the tool's own icon already sits on the left badge.
                CelebrationBodyIconId = "trophy"
            };
        });

    // Shared follow-up for every celebration toast (first-time achievements, capture
    // milestones, streaks, first-of-day greeting). Posted at Background priority so any
    // functional toast already queued on this dispatcher turn (color copied, recording
    // done, OCR copied, …) occupies the single-toast slot first. ToastWindow then queues
    // the flourish until that slot is free, instead of morphing or replacing it.
    private void ShowDelayedCelebrationToast(Func<ToastSpec> buildSpec)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowDelayedCelebrationToast(buildSpec));
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            try { ToastWindow.Show(buildSpec()); }
            catch (Exception ex) { AppDiagnostics.LogWarning("celebration.toast", ex.Message, ex); }
        }, DispatcherPriority.Background);
    }

    // Core counting logic: always runs regardless of CelebrationsEnabled so that
    // CelebrationCaptureCount, CurrentStreak, LongestStreak and LastCelebrationDate
    // stay accurate even when the user has celebration toasts turned off. Callers
    // that want to show a toast check CelebrationsEnabled themselves afterwards.
    private (int Count, bool IsFirstToday, int Streak) RegisterCapture(AppSettings settings, CaptureKind kind = CaptureKind.Screenshot)
    {
        var count = ++settings.CelebrationCaptureCount;
        switch (kind)
        {
            case CaptureKind.Recording:     settings.RecordingCount++;     break;
            case CaptureKind.Ocr:           settings.OcrCount++;           break;
            case CaptureKind.ColorPick:     settings.ColorPickCount++;     break;
            case CaptureKind.Scan:          settings.ScanCount++;          break;
            case CaptureKind.ScrollCapture: settings.ScrollCaptureCount++; break;
            default:                        settings.ScreenshotCount++;    break;
        }

        var todayDate = DateTime.Now.Date;
        var today = todayDate.ToString("yyyy-MM-dd");
        var isFirstToday = settings.LastCelebrationDate != today;
        if (isFirstToday)
        {
            // Continue the streak when this day directly follows the previous capture day; otherwise
            // start over at 1. An unparseable/empty previous date is treated as a fresh start.
            settings.CurrentStreak =
                DateTime.TryParseExact(settings.LastCelebrationDate, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var prev)
                && (todayDate - prev.Date).Days == 1
                    ? settings.CurrentStreak + 1
                    : 1;
            if (settings.CurrentStreak > settings.LongestStreak)
                settings.LongestStreak = settings.CurrentStreak;
            settings.LastCelebrationDate = today;
        }

        try { _settingsService!.Save(); }
        catch (Exception ex) { AppDiagnostics.LogWarning("capture.celebration-save", ex.Message, ex); }

        return (count, isFirstToday, settings.CurrentStreak);
    }

    // Single celebration trigger shared by every capture path (image, OCR, video/GIF, standalone
    // tools). Counts the capture, then — when celebrations are enabled and this capture earns the
    // highest-priority flourish — schedules a dedicated delayed follow-up toast (see
    // ShowDelayedCelebrationToast). The follow-up is deliberately separate from the tool's own
    // functional toast so the celebration is always noticeable instead of merged into it. Priority:
    //   1. A milestone count (50, 100, 250, ...) — rarer, so it outranks the daily greeting.
    //   2. A streak milestone (3, 7, 14, ... consecutive days), on the first capture of the day.
    //   3. The plain first capture of the local day.
    private void CelebrateCaptureIfEarned(AppSettings settings, CaptureKind kind = CaptureKind.Screenshot)
    {
        var reg = RegisterCapture(settings, kind);

        if (!settings.CelebrationsEnabled)
            return;

        // Milestones win when both land on the same capture; the daily date is still stamped by
        // RegisterCapture so tomorrow's greeting fires normally. The number is formatted into a
        // translatable template; the toast translates the raw title key ("Milestone reached!").
        string? title = null;
        string? body = null;

        int rank = ToastSpec.RankDaily;
        if (CelebrationMilestones.IsMilestone(reg.Count))
        {
            title = "Milestone reached!";
            body = string.Format(LocalizationService.Translate("{0} captures and counting"), reg.Count);
            rank = ToastSpec.RankMilestone;
        }
        else if (reg.IsFirstToday && CelebrationMilestones.IsStreakMilestone(reg.Streak))
        {
            title = "On a roll!";
            body = string.Format(LocalizationService.Translate("{0}-day streak"), reg.Streak);
            rank = ToastSpec.RankStreak;
        }
        else if (reg.IsFirstToday)
        {
            // Time-neutral greeting (works for night owls). The trailing capture icon fits here.
            title = "Welcome back!";
            body = LocalizationService.Translate("Your first capture today");
            rank = ToastSpec.RankDaily;
        }

        if (title is null)
            return;

        var celebrationTitle = title;
        var celebrationBody = body!;
        var celebrationRank = rank;
        ShowDelayedCelebrationToast(() =>
            ToastSpec.Standard(celebrationTitle, celebrationBody) with
            {
                Celebrate = true,
                SuppressSound = true,
                IsSystemMessage = false,
                CelebrationRank = celebrationRank
            });
    }

    // Called by standalone tools (OCR, Scan, ColorPicker launched via hotkey) after a
    // successful capture so they participate in CelebrationCaptureCount, streak tracking
    // and first-time achievement flags, exactly like overlay captures do.
    // Safe to call from any thread; dispatches to the WPF thread internally.
    public static void NotifyStandaloneCapture(bool isOcr = false, bool isScan = false, bool isEditor = false, bool isColor = false)
    {
        if (System.Windows.Application.Current is not App app)
            return;

        app.Dispatcher.BeginInvoke(() =>
        {
            var settings = app._settingsService?.Settings;
            if (settings is null) return;

            // Count toward milestones and streak, and surface any earned milestone/streak/first-of-day
            // celebration as a delayed follow-up toast — same as overlay captures.
            var kind = isOcr ? CaptureKind.Ocr
                     : isScan ? CaptureKind.Scan
                     : isColor ? CaptureKind.ColorPick
                     : CaptureKind.Screenshot;
            app.CelebrateCaptureIfEarned(settings, kind);

            // First-time achievement flags.
            if (isOcr)
                app.MarkFirstTime(settings.HasFirstOcr, () => settings.HasFirstOcr = true, "First OCR", "ocr", d => settings.FirstOcrAt = d);
            if (isScan)
                app.MarkFirstTime(settings.HasFirstScan, () => settings.HasFirstScan = true, "First scan", "scan", d => settings.FirstScanAt = d);
            if (isEditor)
                app.MarkFirstTime(settings.HasFirstEditor, () => settings.HasFirstEditor = true, "First editor", "compose", d => settings.FirstEditorAt = d);
            if (isColor)
                app.MarkFirstTime(settings.HasFirstColorPicker, () => settings.HasFirstColorPicker = true, "First color pick", "picker", d => settings.FirstColorPickerAt = d);
        });
    }

    // Called from any thread to mark a first-time tool use without a capture count.
    // action identifies which flag to flip: "ruler", "editor".
    public static void NotifyFirstTimeTool(string action)
    {
        if (System.Windows.Application.Current is not App app)
            return;

        app.Dispatcher.BeginInvoke(() =>
        {
            var settings = app._settingsService?.Settings;
            if (settings is null) return;
            switch (action)
            {
                case "ruler":
                    app.MarkFirstTime(settings.HasFirstRuler, () => settings.HasFirstRuler = true, "First ruler", "ruler", d => settings.FirstRulerAt = d);
                    break;
                case "editor":
                    app.MarkFirstTime(settings.HasFirstEditor, () => settings.HasFirstEditor = true, "First editor", "compose", d => settings.FirstEditorAt = d);
                    break;
            }
        });
    }

    private static AfterCaptureAction NormalizeAfterCaptureAction(AfterCaptureAction action) =>
        Enum.IsDefined(typeof(AfterCaptureAction), action)
            ? action
            : AfterCaptureAction.PreviewAndCopy;

    private static bool ShouldPreviewAfterCapture(AfterCaptureAction action) =>
        action is AfterCaptureAction.PreviewAndCopy or AfterCaptureAction.PreviewOnly;

    /// <summary>
    /// Opens the saved file in the OS default app when the stackable viewer flag is on
    /// (or the legacy exclusive AfterCapture.OpenInSystemViewer value is still present).
    /// Safe to call before image-preview toasts: only uses the file path, not the bitmap.
    /// </summary>
    private static bool TryOpenSystemViewerAfterCapture(
        Models.AppSettings settings,
        AfterCaptureAction action,
        string? filePath,
        bool force = false)
    {
        bool wantViewer = force
            || settings.OpenInSystemViewerAfterCapture
            || action == AfterCaptureAction.OpenInSystemViewer;
        if (!wantViewer || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false);
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("capture.auto-open-async", ex);
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        });

        return true;
    }

    private static bool TryCopyCaptureOutputToClipboard(Bitmap output, string? filePath = null)
    {
        try
        {
            ClipboardService.CopyToClipboard(output, filePath);
            return true;
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Copy failed",
                $"CyberSnap could not copy the capture. The result flow will continue.\n{ex.Message}");
            return false;
        }
    }

    private static void ShowCaptureProcessingFailed(string title, string recoveryMessage, string details)
    {
        ToastWindow.ShowError(title, $"{recoveryMessage}\n{details}");
    }

    private void HandleOcrResult(Bitmap result)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var langTag = _settingsService?.Settings.OcrLanguageTag;
                string text = await OcrService.RecognizeAsync(result, langTag);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    SoundService.PlayTextSound();

                    if (_settingsService!.Settings.SaveHistory)
                        EnsureHistoryService().SaveOcrEntry(text);

                    // Count this OCR toward milestones (covers both the auto-copy toast and the
                    // workbench window). Any earned celebration is shown as a separate delayed
                    // follow-up toast; the "OCR copied" toast keeps its own functional text.
                    CelebrateCaptureIfEarned(_settingsService.Settings, CaptureKind.Ocr);
                    MarkFirstTime(_settingsService.Settings.HasFirstOcr,
                        () => _settingsService.Settings.HasFirstOcr = true, "First OCR", "ocr", d => _settingsService.Settings.FirstOcrAt = d);

                    if (Helpers.AutoCopyPreferences.ShouldCopy(_settingsService.Settings, Helpers.AutoCopyKind.Ocr))
                    {
                        var copied = TryCopyCaptureTextToClipboard(text);
                        ToastWindow.Show(copied
                            ? ToastSpec.Standard(LocalizationService.Translate("OCR copied"), FormatOcrAutoCopyToastPreview(text)) with { SuppressSound = true }
                            : ToastSpec.Standard(LocalizationService.Translate("OCR ready"), LocalizationService.Translate("Clipboard copy failed.")));
                        if (!copied)
                        {
                            var window = new OcrResultWindow(text, _settingsService, BitmapPerf.ToBitmapSource(result));
                            window.Show();
                        }
                    }
                    else
                    {
                        var window = new OcrResultWindow(text, _settingsService, BitmapPerf.ToBitmapSource(result));
                        window.Show();
                    }
                }
                else
                {
                    ToastWindow.Show(LocalizationService.Translate("OCR"), LocalizationService.Translate("No text found"));
                }
            }
            catch (Exception ex)
            {
                ShowCaptureProcessingFailed(
                    "OCR error",
                    "CyberSnap could not read text from this capture. Try a clearer region, or check Config -> OCR.",
                    ex.Message);
            }
            finally { result.Dispose(); }
            ScheduleIdleMemoryTrim();
        });
    }

    private static string FormatOcrAutoCopyToastPreview(string text)
    {
        var preview = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return preview.Length > 80 ? preview[..80] + "..." : preview;
    }

    private static string? ResolveSavePath(string defaultPath, CaptureImageFormat format)
    {
        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(defaultPath),
            InitialDirectory = Path.GetDirectoryName(defaultPath),
            Filter = format switch
            {
                CaptureImageFormat.Png => "PNG Image (*.png)|*.png",
                CaptureImageFormat.Jpeg => "JPEG Image (*.jpg)|*.jpg",
                CaptureImageFormat.Bmp => "Bitmap Image (*.bmp)|*.bmp",
                _ => "All Files (*.*)|*.*"
            }
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
