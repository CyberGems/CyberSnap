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
    private void HandleCaptureResult(Bitmap result)
    {
        var settings = _settingsService!.Settings;
        var ext = CaptureOutputService.GetExtension(settings.CaptureImageFormat);
        string? requestedPath = null;
        if (settings.SaveToFile)
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
                    if (ShouldCopyAfterCapture(action))
                        TryCopyCaptureOutputToClipboard(persisted.Output);
                    ResetCapturing();

                    // Evaluated once per capture, before the action branches, so every capture counts
                    // toward milestones — including straight-to-editor and copy-only, which never show
                    // a preview. Each branch below surfaces the flourish in its own toast shape.
                    var celebration = TryGetCaptureCelebration(settings);

                    if (settings.OpenEditorAfterCapture &&
                        persisted.HistoryEntry?.Kind != Services.HistoryKind.Video &&
                        persisted.HistoryEntry?.Kind != Services.HistoryKind.Gif)
                    {
                        try
                        {
                            // Editor takes ownership of its own clone; dispose the original
                            // so the persisted output doesn't leak when we skip the toast path.
                            CyberSnap.UI.Editor.EditorForm.ShowEditor(new Bitmap(persisted.Output), persisted.FilePath);
                        }
                        catch (Exception ex)
                        {
                            AppDiagnostics.LogError("capture.auto-open-editor", ex);
                            // Fall back to the standard preview so the capture isn't lost.
                            ToastWindow.ShowImagePreview(persisted.Output, persisted.FilePath, settings.AutoPinPreviews);
                            return;
                        }
                        persisted.Output.Dispose();

                        // No preview toast is shown when the editor opens directly, so surface a
                        // lightweight system message confirming where the capture went — unless this
                        // capture earned a celebration, which takes the toast instead. A text-only
                        // toast (no preview bitmap) is treated as a system message by ToastWindow.
                        if (celebration is { } editorCopy)
                            ShowCelebrationToast(editorCopy.Title, editorCopy.Body, persisted.FilePath);
                        else
                            ToastWindow.Show(
                                LocalizationService.Translate("Sent to the editor"),
                                LocalizationService.Translate("Your capture is open in the editor."),
                                persisted.FilePath);
                    }
                    else if (ShouldPreviewAfterCapture(action))
                    {
                        if (celebration is { } copy)
                            ToastWindow.ShowImagePreview(persisted.Output, copy.Title, copy.Body, persisted.FilePath, settings.AutoPinPreviews, celebrate: true);
                        else
                            ToastWindow.ShowImagePreview(persisted.Output, persisted.FilePath, settings.AutoPinPreviews);
                    }
                    else
                    {
                        persisted.Output.Dispose();
                        if (celebration is { } copy)
                            ShowCelebrationToast(copy.Title, copy.Body, persisted.FilePath);
                        else
                            ToastWindow.Show("Screenshot ready", "", persisted.FilePath);
                    }

                    if (settings.AutoOpenCapturedImages && !string.IsNullOrEmpty(persisted.FilePath) && File.Exists(persisted.FilePath))
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = persisted.FilePath,
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            AppDiagnostics.LogError("capture.auto-open", ex);
                        }
                    }

                    ScheduleIdleMemoryTrim();
                });
            }, TaskScheduler.Default);
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

                if (historyService != null)
                {
                    if (filePath != null)
                    {
                        historyEntry = historyService.TrackExistingCapture(
                            filePath,
                            output.Width,
                            output.Height,
                            HistoryKind.Image);
                    }
                    else
                    {
                        historyEntry = historyService.SaveCapture(output);
                        filePath = historyEntry.FilePath;
                    }
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

    // A text-only celebration toast for the branches that show no preview (straight-to-editor,
    // copy-only). The rainbow/glow flourish rides the toast's progress bar, so it reads as
    // celebratory without a preview image. Title is translated by the toast; body arrives ready.
    private static void ShowCelebrationToast(string title, string body, string? filePath) =>
        ToastWindow.Show(ToastSpec.Standard(title, body, filePath) with { Celebrate = true });

    // Counting core, shared by every capture path (image, OCR, video/GIF). Bumps the running
    // total, updates the consecutive-day streak on the first capture of each day, stamps the local
    // day, and persists (Save is debounced, so per-capture saving is cheap). Returns null when
    // Flips a first-time achievement flag on its very first occurrence and persists it (Save is
    // debounced). No-op once already unlocked. Independent of CelebrationsEnabled — the medal
    // grid records what happened even with celebration toasts turned off.
    private void MarkFirstTime(bool alreadyUnlocked, Action setUnlocked)
    {
        if (alreadyUnlocked) return;
        setUnlocked();
        try { _settingsService!.Save(); }
        catch (Exception ex) { AppDiagnostics.LogWarning("capture.first-time-save", ex.Message, ex); }
    }

    // Core counting logic: always runs regardless of CelebrationsEnabled so that
    // CelebrationCaptureCount, CurrentStreak, LongestStreak and LastCelebrationDate
    // stay accurate even when the user has celebration toasts turned off. Callers
    // that want to show a toast check CelebrationsEnabled themselves afterwards.
    private (int Count, bool IsFirstToday, int Streak) RegisterCapture(AppSettings settings)
    {
        var count = ++settings.CelebrationCaptureCount;

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

    // Image-capture trigger: counts the capture, then picks the highest-priority flourish and
    // returns its copy (the image toast replaces its text with this celebration copy), or null
    // when nothing fires or celebrations are disabled:
    //   1. A milestone count (50, 100, 250, ...) — rarer, so it outranks the daily greeting.
    //   2. A streak milestone (3, 7, 14, ... consecutive days), on the first capture of the day.
    //   3. The plain first capture of the local day.
    private (string Title, string Body)? TryGetCaptureCelebration(AppSettings settings)
    {
        var reg = RegisterCapture(settings);

        if (!settings.CelebrationsEnabled)
            return null;

        // Milestones win when both land on the same capture; the daily date is still stamped above
        // so tomorrow's greeting fires normally.
        // The number is formatted into a translatable template; the toast translates the
        // raw title key ("Milestone reached!") on its own.
        if (CelebrationMilestones.IsMilestone(reg.Count))
            return ("Milestone reached!", string.Format(
                LocalizationService.Translate("{0} captures and counting"), reg.Count));

        // Streak milestone — only on the first capture of the day, since the streak just advanced.
        if (reg.IsFirstToday && CelebrationMilestones.IsStreakMilestone(reg.Streak))
            return ("On a roll!", string.Format(
                LocalizationService.Translate("{0}-day streak"), reg.Streak));

        // First capture of the day. Time-neutral greeting (works for night owls); the capture icon
        // is added by the toast.
        if (reg.IsFirstToday)
            return ("Welcome back!", "Your first capture today");

        return null;
    }

    // OCR / video-GIF trigger: counts the capture and reports whether it earned a flourish, without
    // producing copy — those paths keep their own functional toast text ("OCR copied", file size)
    // and only ride the celebratory sweep on top. Returns false when celebrations are off or nothing
    // fires. The milestone is still surfaced afterwards by the Settings rail.
    private bool TryRegisterCaptureFlourish(AppSettings settings)
    {
        var reg = RegisterCapture(settings);

        if (!settings.CelebrationsEnabled)
            return false;

        return CelebrationMilestones.IsMilestone(reg.Count)
            || (reg.IsFirstToday && CelebrationMilestones.IsStreakMilestone(reg.Streak))
            || reg.IsFirstToday;
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

            // Count toward milestones and streak (always, regardless of CelebrationsEnabled).
            app.RegisterCapture(settings);

            // First-time achievement flags.
            if (isOcr)
                app.MarkFirstTime(settings.HasFirstOcr, () => settings.HasFirstOcr = true);
            if (isScan)
                app.MarkFirstTime(settings.HasFirstScan, () => settings.HasFirstScan = true);
            if (isEditor)
                app.MarkFirstTime(settings.HasFirstEditor, () => settings.HasFirstEditor = true);
            if (isColor)
                app.MarkFirstTime(settings.HasFirstColorPicker, () => settings.HasFirstColorPicker = true);
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
                    app.MarkFirstTime(settings.HasFirstRuler, () => settings.HasFirstRuler = true);
                    break;
                case "editor":
                    app.MarkFirstTime(settings.HasFirstEditor, () => settings.HasFirstEditor = true);
                    break;
            }
        });
    }

    private static AfterCaptureAction NormalizeAfterCaptureAction(AfterCaptureAction action) =>
        Enum.IsDefined(typeof(AfterCaptureAction), action)
            ? action
            : AfterCaptureAction.PreviewAndCopy;

    private static bool ShouldCopyAfterCapture(AfterCaptureAction action) =>
        action is AfterCaptureAction.CopyToClipboard or AfterCaptureAction.PreviewAndCopy;

    private static bool ShouldPreviewAfterCapture(AfterCaptureAction action) =>
        action is AfterCaptureAction.PreviewAndCopy or AfterCaptureAction.PreviewOnly;

    private static bool TryCopyCaptureOutputToClipboard(Bitmap output)
    {
        try
        {
            ClipboardService.CopyToClipboard(output);
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
                    // workbench window). When it earns a flourish, the "OCR copied" toast rides the
                    // celebratory sweep while keeping its functional text; the no-toast workbench
                    // path just counts silently and surfaces the milestone in the Settings rail.
                    var ocrFlourish = TryRegisterCaptureFlourish(_settingsService.Settings);
                    MarkFirstTime(_settingsService.Settings.HasFirstOcr,
                        () => _settingsService.Settings.HasFirstOcr = true);

                    if (_settingsService.Settings.OcrAutoCopyToClipboard)
                    {
                        var copied = TryCopyCaptureTextToClipboard(text);
                        ToastWindow.Show(copied
                            ? ToastSpec.Standard(LocalizationService.Translate("OCR copied"), FormatOcrAutoCopyToastPreview(text)) with { SuppressSound = true, Celebrate = ocrFlourish }
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
