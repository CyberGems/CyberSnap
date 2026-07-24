using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace CyberSnap;

public partial class App
{
    private void HandleCaptureResult(
        Bitmap result,
        RegionOverlayForm.ConfirmCommitAction commitAction = RegionOverlayForm.ConfirmCommitAction.Default)
    {
        var settings = _settingsService!.Settings;
        var ext = CaptureOutputService.GetExtension(settings.CaptureImageFormat);
        string? requestedPath = null;

        // Confirm-mode Save / Edit / History / Share / SystemViewer need a file on disk.
        bool forceSave = commitAction is RegionOverlayForm.ConfirmCommitAction.Save
            or RegionOverlayForm.ConfirmCommitAction.Edit
            or RegionOverlayForm.ConfirmCommitAction.History
            or RegionOverlayForm.ConfirmCommitAction.Share
            || settings.SaveToFile
            || settings.OpenInSystemViewerAfterCapture
            || settings.AfterCapture == AfterCaptureAction.OpenInSystemViewer;

        if (forceSave)
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

        _ = PersistCaptureAsync(result, requestedPath, saveHistory: settings.SaveHistory)
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
                    bool wantCopy = commitAction == RegionOverlayForm.ConfirmCommitAction.Copy
                        || Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Image);
                    bool copied = false;
                    if (wantCopy)
                        copied = TryCopyCaptureOutputToClipboard(persisted.Output, persisted.FilePath);
                    ResetCapturing();

                    CelebrateCaptureIfEarned(settings);

                    bool openEditor = commitAction == RegionOverlayForm.ConfirmCommitAction.Edit
                        || (commitAction == RegionOverlayForm.ConfirmCommitAction.Default
                            && settings.OpenEditorAfterCapture
                            && persisted.HistoryEntry?.Kind != Services.HistoryKind.Video
                            && persisted.HistoryEntry?.Kind != Services.HistoryKind.Gif);

                    // Respect the After Capture Notification pill setting
                    var outcomeState = Helpers.AfterCaptureOutcomeModel.FromSettings(settings);
                    bool wantNotification = outcomeState.Destination == Helpers.AfterCaptureDestination.Notification;

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
                            if (wantNotification)
                            {
                                ToastWindow.Show(
                                    LocalizationService.Translate("Screenshot ready"),
                                    "",
                                    persisted.FilePath);
                            }
                            persisted.Output.Dispose();
                            ScheduleIdleMemoryTrim();
                            return;
                        }

                        persisted.Output.Dispose();
                        if (wantNotification)
                        {
                            ToastWindow.Show(ToastSpec.Standard(
                                LocalizationService.Translate("Sent to the editor"),
                                LocalizationService.Translate("Your capture is open in the editor."),
                                persisted.FilePath) with { PlayCaptureSound = true });
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
                        TryOpenSystemViewerAfterCapture(settings, action, persisted.FilePath);
                        persisted.Output.Dispose();

                        if (wantNotification)
                        {
                            ShowConfirmDestinationFeedback(commitAction, wantCopy, copied, persisted.FilePath);
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
                return;

            var result = await UI.Share.ImageShareFlow.ShareBitmapAsync(bitmap).ConfigureAwait(true);
            UI.Share.ImageShareFlow.PresentResult(result, settings);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("capture.confirm-share", ex);
            ToastWindow.Show(
                LocalizationService.Translate("Upload failed"),
                LocalizationService.Translate("CyberSnap could not share the capture. Check your network or upload configuration in Settings."),
                filePath);
        }
        finally
        {
            try { bitmap.Dispose(); } catch { }
            ScheduleIdleMemoryTrim();
        }
    }

    /// <summary>
    /// Minimal post-confirm status toast (no image-preview overlay). Wording follows the
    /// destination pill the user chose; capture sound confirms the action completed.
    /// </summary>
    private static void ShowConfirmDestinationFeedback(
        RegionOverlayForm.ConfirmCommitAction commitAction,
        bool wantCopy,
        bool copied,
        string? filePath)
    {
        string title;
        string body = "";

        switch (commitAction)
        {
            case RegionOverlayForm.ConfirmCommitAction.Copy:
                title = copied
                    ? LocalizationService.Translate("Copied to clipboard")
                    : LocalizationService.Translate("Clipboard copy failed");
                if (!string.IsNullOrEmpty(filePath))
                    body = LocalizationService.Translate("Saved");
                break;
            case RegionOverlayForm.ConfirmCommitAction.Save:
                title = LocalizationService.Translate("Saved");
                if (wantCopy)
                {
                    body = copied
                        ? LocalizationService.Translate("Copied to clipboard")
                        : LocalizationService.Translate("Clipboard copy failed");
                }
                break;
            case RegionOverlayForm.ConfirmCommitAction.History:
                // Gallery window opens immediately; skip a redundant status toast.
                return;
            default:
                title = LocalizationService.Translate("Screenshot ready");
                if (wantCopy)
                {
                    body = copied
                        ? LocalizationService.Translate("Copied to clipboard")
                        : LocalizationService.Translate("Clipboard copy failed");
                }
                break;
        }

        ToastWindow.Show(ToastSpec.Standard(title, body, filePath) with { PlayCaptureSound = true });
    }

    private Task<PersistedCaptureResult> PersistCaptureAsync(
        Bitmap source,
        string? requestedPath,
        bool saveHistory)
    {
        var settings = _settingsService!.Settings;
        int maxLongEdge = settings.CaptureMaxLongEdge;
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
        string? achievementTitleKey = null, string? iconId = null)
    {
        if (alreadyUnlocked) return;
        setUnlocked();
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
                // A trophy after the name reads as an unlock; the default capture icon would be
                // out of place here since the tool's own icon already sits on the left badge.
                CelebrationBodyIconId = "trophy"
            };
        });

    // Shared delayed-follow-up mechanism for every celebration toast (first-time achievements,
    // capture milestones, streaks, first-of-day greeting). The short delay lets the tool's own
    // functional toast (scan result, "Color copied", file size, ...) appear first, so the
    // celebration reads as a distinct follow-up instead of being merged into — and easily missed
    // alongside — it. Plays the dedicated, user-customizable Achievement sound on show; the toast
    // itself is built with SuppressSound so the sound isn't doubled.
    private void ShowDelayedCelebrationToast(Func<ToastSpec> buildSpec)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowDelayedCelebrationToast(buildSpec));
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2.2)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                ToastWindow.Show(buildSpec());
                SoundService.PlayAchievementSound();
            }
            catch (Exception ex) { AppDiagnostics.LogWarning("celebration.toast", ex.Message, ex); }
        };
        timer.Start();
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

        if (CelebrationMilestones.IsMilestone(reg.Count))
        {
            title = "Milestone reached!";
            body = string.Format(LocalizationService.Translate("{0} captures and counting"), reg.Count);
        }
        else if (reg.IsFirstToday && CelebrationMilestones.IsStreakMilestone(reg.Streak))
        {
            title = "On a roll!";
            body = string.Format(LocalizationService.Translate("{0}-day streak"), reg.Streak);
        }
        else if (reg.IsFirstToday)
        {
            // Time-neutral greeting (works for night owls). The trailing capture icon fits here.
            title = "Welcome back!";
            body = LocalizationService.Translate("Your first capture today");
        }

        if (title is null)
            return;

        var celebrationTitle = title;
        var celebrationBody = body!;
        ShowDelayedCelebrationToast(() =>
            ToastSpec.Standard(celebrationTitle, celebrationBody) with
            {
                Celebrate = true,
                SuppressSound = true,
                IsSystemMessage = false
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
                app.MarkFirstTime(settings.HasFirstOcr, () => settings.HasFirstOcr = true, "First OCR", "ocr");
            if (isScan)
                app.MarkFirstTime(settings.HasFirstScan, () => settings.HasFirstScan = true, "First scan", "scan");
            if (isEditor)
                app.MarkFirstTime(settings.HasFirstEditor, () => settings.HasFirstEditor = true, "First editor", "compose");
            if (isColor)
                app.MarkFirstTime(settings.HasFirstColorPicker, () => settings.HasFirstColorPicker = true, "First color pick", "picker");
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
                    app.MarkFirstTime(settings.HasFirstRuler, () => settings.HasFirstRuler = true, "First ruler", "ruler");
                    break;
                case "editor":
                    app.MarkFirstTime(settings.HasFirstEditor, () => settings.HasFirstEditor = true, "First editor", "compose");
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
        string? filePath)
    {
        bool wantViewer = settings.OpenInSystemViewerAfterCapture
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
                        () => _settingsService.Settings.HasFirstOcr = true, "First OCR", "ocr");

                    if (Helpers.AutoCopyPreferences.ShouldCopy(_settingsService.Settings, Helpers.AutoCopyKind.Ocr))
                    {
                        var copied = TryCopyCaptureTextToClipboard(text);
                        ToastWindow.Show(copied
                            ? ToastSpec.Standard(LocalizationService.Translate("OCR copied"), FormatOcrAutoCopyToastPreview(text)) with { SuppressSound = true }
                            : ToastSpec.Standard(LocalizationService.Translate("OCR ready"), LocalizationService.Translate("Clipboard copy failed.")));
                        if (!copied)
                        {
                            var window = new OcrResultWindow(text, _settingsService);
                            window.Show();
                        }
                    }
                    else
                    {
                        var window = new OcrResultWindow(text, _settingsService);
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
