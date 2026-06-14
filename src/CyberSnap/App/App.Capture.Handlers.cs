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
    // total, stamps the local day for the daily greeting, and persists (Save is debounced, so
    // per-capture saving is cheap). Returns null when celebrations are off — then nothing counts,
    // matching the previous behavior. Callers pick how to surface the flourish from the result.
    private (int Count, bool IsFirstToday)? RegisterCaptureForCelebration(AppSettings settings)
    {
        if (!settings.CelebrationsEnabled)
            return null;

        var count = ++settings.CelebrationCaptureCount;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var isFirstToday = settings.LastCelebrationDate != today;
        if (isFirstToday)
            settings.LastCelebrationDate = today;

        try { _settingsService!.Save(); }
        catch (Exception ex) { AppDiagnostics.LogWarning("capture.celebration-save", ex.Message, ex); }

        return (count, isFirstToday);
    }

    // Image-capture trigger: counts the capture, then picks the highest-priority flourish and
    // returns its copy (the image toast replaces its text with this celebration copy), or null
    // when nothing fires:
    //   1. A milestone count (50, 100, 250, ...) — rarer, so it outranks the daily greeting.
    //   2. The first capture of the local day.
    private (string Title, string Body)? TryGetCaptureCelebration(AppSettings settings)
    {
        if (RegisterCaptureForCelebration(settings) is not { } reg)
            return null;

        // Milestones win when both land on the same capture; the daily date is still stamped above
        // so tomorrow's greeting fires normally.
        // The number is formatted into a translatable template; the toast translates the
        // raw title key ("Milestone reached!") on its own.
        if (CelebrationMilestones.IsMilestone(reg.Count))
            return ("Milestone reached!", string.Format(
                LocalizationService.Translate("{0} captures and counting"), reg.Count));

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
        if (RegisterCaptureForCelebration(settings) is not { } reg)
            return false;

        return CelebrationMilestones.IsMilestone(reg.Count) || reg.IsFirstToday;
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

                    if (_settingsService.Settings.OcrAutoCopyToClipboard)
                    {
                        var copied = TryCopyCaptureTextToClipboard(text);
                        ToastWindow.Show(copied
                            ? ToastSpec.Standard("OCR copied", FormatOcrAutoCopyToastPreview(text)) with { SuppressSound = true, Celebrate = ocrFlourish }
                            : ToastSpec.Standard("OCR ready", "Clipboard copy failed."));
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
                    ToastWindow.Show("OCR", "No text found");
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
