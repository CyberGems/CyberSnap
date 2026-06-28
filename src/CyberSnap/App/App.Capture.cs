using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Native;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap;

public partial class App
{
    private void ResetCapturing()
    {
        Volatile.Write(ref _isCapturing, 0);
        RestoreSettingsAfterCapture();
    }

    private void HideSettingsForCapture()
    {
        // Keep app windows capturable. Hiding Settings here made attempts to
        // capture CyberSnap's own UI disappear before the screenshot started, and
        // could also change the active window before active-window capture.
    }

    private void RestoreSettingsAfterCapture()
    {
        if (Interlocked.Exchange(ref _settingsHiddenForCapture, 0) == 0)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_settingsWindow is not null)
                _settingsWindow.Show();
        });
    }

    private sealed class PersistedCaptureResult
    {
        public required Bitmap Output { get; init; }
        public string? FilePath { get; init; }
        public Services.HistoryEntry? HistoryEntry { get; init; }
    }

    private void LaunchRecordingWithFormat(RecordingFormat fmt)
    {
        if (RecordingForm.Current != null)
        {
            RecordingForm.Current.RequestStop();
            return;
        }

        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchGifRecording(fmt);
    }

    private void LaunchGifRecording(RecordingFormat? formatOverride = null)
    {
        var thread = new Thread(() =>
        {
            try
            {
                Theme.Refresh();
                var settings = _settingsService!.Settings;
                Helpers.UiChrome.SetUiScale(settings.UiScale);
                bool showCursor = settings.ShowCursor;
                var (selectionScreenshot, bounds) = ScreenCapture.CaptureAllScreens(showCursor);
                var s = settings;
                var fmt = formatOverride ?? s.RecordingFormat;

                string baseDir = s.SaveDirectory;
                string ext = fmt switch { RecordingFormat.MP4 => ".mp4", _ => ".gif" };
                string saveRoot = fmt == RecordingFormat.GIF ? baseDir : Path.Combine(baseDir, "Videos");
                string saveDir = s.SaveInMonthlyFolders
                    ? Helpers.CaptureSavePath.GetMonthDirectory(saveRoot)
                    : saveRoot;
                Directory.CreateDirectory(saveDir);
                string fileName = $"{Helpers.FileNameTemplate.Format(s.FileNameTemplate, 0, 0)}{ext}";
                string savePath = Helpers.CaptureSavePath.GetAvailablePath(Path.Combine(saveDir, fileName));
                int maxH = s.RecordingQuality switch { RecordingQuality.P1080 => 1080, RecordingQuality.P720 => 720, RecordingQuality.P480 => 480, _ => 0 };
                int fps = fmt == RecordingFormat.GIF ? s.GifFps : s.RecordingFps;

                bool recMic = fmt != RecordingFormat.GIF && s.RecordMicrophone;
                bool recDesktop = fmt != RecordingFormat.GIF && s.RecordDesktopAudio;
                Capture.SelectionSizeReadout.ShowDimensions = _settingsService!.Settings.ShowSelectionSize;
                var form = new RecordingForm(selectionScreenshot, bounds, fps, savePath, fmt, maxH,
                    showCursor, recMic, s.MicrophoneDeviceId, recDesktop, s.DesktopAudioDeviceId,
                    _settingsService!.Settings.ShowCaptureMagnifier);

                form.Shown += (_, _) =>
                {
                    Dispatcher.BeginInvoke(() => _trayIcon?.UpdateRecordingState(true));
                };

                form.RecordingCompleted += (path, firstFrame) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _trayIcon?.UpdateRecordingState(false);

                        Services.HistoryEntry? historyEntry = null;
                        try
                        {
                            if (s.SaveHistory)
                                historyEntry = EnsureHistoryService().SaveMediaEntry(path);
                        }
                        catch (Exception ex)
                        {
                            AppDiagnostics.LogError("capture.recording-history", ex, $"Failed to save recording history for {Path.GetFileName(path)}.");
                        }

                        var copiedToClipboard = TryCopyRecordingFileToClipboard(path);
                        bool isGif = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

                        // Recordings count toward milestones too; when one earns a flourish, its toast
                        // rides the celebratory sweep while keeping its functional text (file size, copy
                        // status). The milestone also surfaces afterwards in the Settings rail.
                        bool flourish = TryRegisterCaptureFlourish(s);
                        MarkFirstTime(s.HasFirstRecording, () => s.HasFirstRecording = true);

                        if (firstFrame != null)
                        {
                            ToastWindow.Show(ToastSpec.ImagePreview(
                                firstFrame,
                                isGif ? LocalizationService.Translate("GIF recorded") : LocalizationService.Translate("Video recorded"),
                                copiedToClipboard ? LocalizationService.Translate("File copied to clipboard") : LocalizationService.Translate("Saved; clipboard copy failed"),
                                path,
                                false,
                                transparentShell: false,
                                showOverlayButtons: true,
                                hideEditButton: true) with { Celebrate = flourish });
                        }
                        else
                        {
                            var fi = new FileInfo(path);
                            string label = fi.Extension.TrimStart('.').ToUpper();
                            string size = fi.Length > 1024 * 1024
                                ? $"{fi.Length / 1024.0 / 1024.0:F1} MB"
                                : $"{fi.Length / 1024:N0} KB";
                            var copyStatus = copiedToClipboard ? LocalizationService.Translate("File copied to clipboard") : LocalizationService.Translate("Saved; clipboard copy failed");
                            ToastWindow.Show(ToastSpec.Standard($"{label} recorded", $"{fi.Name} Â· {size} Â· {copyStatus}", path) with { Celebrate = flourish });
                        }

                        ScheduleIdleMemoryTrim();
                    });
                };

                form.RecordingFailed += ex =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _trayIcon?.UpdateRecordingState(false);
                        ResetCapturing();
                        ShowCaptureProcessingFailed(
                            "Recording error",
                            "CyberSnap could not finish the recording. Try again, or check Config -> Recording.",
                            ex.Message);
                        ScheduleIdleMemoryTrim();
                    });
                };

                form.RecordingCancelled += () =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _trayIcon?.UpdateRecordingState(false);
                        ResetCapturing();
                    });
                };

                form.FormClosed += (_, _) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _trayIcon?.UpdateRecordingState(false);
                        ResetCapturing();
                    });
                };

                System.Windows.Forms.Application.Run(form);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ResetCapturing();
                    ShowCaptureProcessingFailed(
                        "Recording error",
                        "CyberSnap could not start recording. Try again, or check Config -> Recording.",
                        ex.Message);
                });
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void LaunchScrollingCapture(Rectangle? preSelectedRegion = null)
    {
        _isCapturing = 1;
        var thread = new Thread(() =>
        {
            try
            {
                Theme.Refresh();
                bool showCursor = false;
                var (selectionScreenshot, bounds) = ScreenCapture.CaptureAllScreens(showCursor);
                Capture.SelectionSizeReadout.ShowDimensions = _settingsService!.Settings.ShowSelectionSize;
                var form = new ScrollingCaptureForm(selectionScreenshot, bounds, showCursor,
                    _settingsService!.Settings.ShowCaptureMagnifier,
                    _settingsService!.Settings.ScrollingCaptureMode,
                    preSelectedRegion);

                form.CaptureCompleted += result =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        HandleCaptureResult(result);
                        MarkFirstTime(_settingsService!.Settings.HasFirstScrollingCapture,
                            () => _settingsService!.Settings.HasFirstScrollingCapture = true);
                        ScheduleIdleMemoryTrim();
                    });
                };

                form.CaptureFailed += message =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        ResetCapturing();
                        ShowCaptureProcessingFailed(
                            "Scroll capture error",
                            "CyberSnap could not finish the scrolling capture. Try a smaller scroll area or a visible scrollable window.",
                            message);
                        ScheduleIdleMemoryTrim();
                    });
                };

                form.CaptureCancelled += () => Dispatcher.BeginInvoke(ResetCapturing);

                form.FormClosed += (_, _) => Dispatcher.BeginInvoke(ResetCapturing);

                System.Windows.Forms.Application.Run(form);
            }
            catch
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ResetCapturing();
                    ShowCaptureProcessingFailed(
                        "Scroll capture error",
                        "CyberSnap could not start scrolling capture. Try again with a visible scrollable window.",
                        "Scrolling capture failed.");
                });
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void CaptureFullscreenNow()
    {
        Bitmap? bmp = null;
        try
        {
            (bmp, _) = ScreenCapture.CaptureAllScreens(_settingsService!.Settings.ShowCursor);
            HandleCaptureResult(bmp);
            bmp = null;
        }
        catch (Exception ex)
        {
            bmp?.Dispose();
            ResetCapturing();
            ShowCaptureProcessingFailed(
                "Capture error",
                "CyberSnap could not capture the screen. Try again, or choose another capture mode.",
                ex.Message);
        }
    }

    private void CaptureActiveWindowNow()
    {
        Bitmap? bmp = null;
        try
        {
            (bmp, var bounds) = ScreenCapture.CaptureAllScreens(_settingsService!.Settings.ShowCursor);
            var hwnd = Native.User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                bmp.Dispose();
                ResetCapturing();
                ToastWindow.ShowError("Capture error", "Couldn't find the active window. Focus a visible window and try again.");
                return;
            }

            var dwmRect = Native.Dwm.GetExtendedFrameBounds(hwnd);
            var windowRect = Native.User32.GetWindowRect(hwnd, out var rawRect)
                ? WindowDetector.ChoosePreferredBounds(dwmRect, rawRect.ToRectangle())
                : dwmRect;
            if (windowRect.Width <= 1 || windowRect.Height <= 1)
            {
                bmp.Dispose();
                ResetCapturing();
                ToastWindow.ShowError("Capture error", "Couldn't find the active window. Focus a visible window and try again.");
                return;
            }

            var crop = new Rectangle(windowRect.Left - bounds.X, windowRect.Top - bounds.Y, windowRect.Width, windowRect.Height);
            crop.Intersect(new Rectangle(System.Drawing.Point.Empty, bmp.Size));
            if (crop.Width <= 1 || crop.Height <= 1)
            {
                bmp.Dispose();
                ResetCapturing();
                ToastWindow.ShowError("Capture error", "Active window is out of bounds. Use region capture or move the window onscreen.");
                return;
            }

            var cropped = ScreenCapture.CropRegion(bmp, crop);
            HandleCaptureResult(cropped);
            bmp.Dispose();
        }
        catch (Exception ex)
        {
            bmp?.Dispose();
            ResetCapturing();
            ShowCaptureProcessingFailed(
                "Capture error",
                "CyberSnap could not capture the active window. Try again, or use region capture.",
                ex.Message);
        }
    }

    private void LaunchOverlay(CaptureMode initialMode)
    {
        LaunchWithDelay(() => LaunchOverlayNow(initialMode));
    }

    private void LaunchOverlayNow(CaptureMode initialMode)
    {
        var thread = new Thread(() =>
        {
            Bitmap? screenshot = null;
            try
            {
                Theme.Refresh();
                bool showCursor = _settingsService!.Settings.ShowCursor;
                var (bmp, bounds) = _settingsService.Settings.OverlayCaptureAllMonitors
                    ? ScreenCapture.CaptureAllScreens(showCursor)
                    : ScreenCapture.CaptureCurrentScreen(showCursor);
                screenshot = bmp;

                Capture.SelectionSizeReadout.ShowDimensions = _settingsService!.Settings.ShowSelectionSize;
                var overlay = new RegionOverlayForm(
                    screenshot,
                    bounds,
                    initialMode,
                    _settingsService!.Settings.WindowDetection,
                    _settingsService.Settings.CenterSelectionAspectRatio)
                {
                    ShowCrosshairGuides = _settingsService!.Settings.ShowCrosshairGuides,
                    DetectWindows = _settingsService.Settings.DetectWindows,
                    ShowCaptureMagnifier = _settingsService.Settings.ShowCaptureMagnifier,
                    AnnotationStrokeShadow = _settingsService.Settings.AnnotationStrokeShadow,
                    StrokeWidth = _settingsService.Settings.StrokeWidth,
                    CaptureDockSide = _settingsService.Settings.CaptureDockSide,
                    UiScale = _settingsService.Settings.UiScale,
                    ConfirmRegionBeforeCapture = _settingsService.Settings.ConfirmRegionBeforeCapture
                };
                overlay.SetEnabledTools(_settingsService.Settings.EnabledTools);
                overlay.EnabledToolsChanged += enabledTools =>
                {
                    // Merge with latest cached settings to avoid overwriting changes
                    // made by the chevron toggles (which may not be flushed to disk yet).
                    var latest = Services.SettingsService.LoadStatic();
                    if (latest != null)
                        _settingsService!.Settings = latest;
                    _settingsService.Settings.EnabledTools = enabledTools;
                    _settingsService.Save();
                };
                overlay.SetShowToolNumberBadges(_settingsService.Settings.ShowToolNumberBadges);
                overlay.SetToolColor(System.Drawing.Color.FromArgb(_settingsService.Settings.ToolColorArgb));
                overlay.ToolColorChanged += color =>
                {
                    _settingsService!.Settings.ToolColorArgb = color.ToArgb();
                    _settingsService.Save();
                };
                overlay.DockSideChanged += dockSide =>
                {
                    _settingsService!.Settings.CaptureDockSide = dockSide;
                    _settingsService.Save();
                };
                overlay.StrokeWidthChanged += width =>
                {
                    _settingsService!.Settings.StrokeWidth = width;
                    _settingsService.Save();
                };
                overlay.DefaultCaptureModeChanged += mode =>
                {
                    _settingsService!.Settings.DefaultCaptureMode = mode;
                    _settingsService.Save();
                    RefreshWidgetWindowLayout();
                };
                overlay.CaptureBannerDismissed += () =>
                {
                    if (_settingsService!.Settings.HasSeenCaptureBanner) return;
                    _settingsService.Settings.HasSeenCaptureBanner = true;
                    _settingsService.Save();
                };

                overlay.RegionSelected += sel =>
                {
                    overlay.Hide();
                    UI.PopupWindowHelper.SetMonitorHintPoint(new System.Drawing.Point(sel.Right, sel.Bottom));
                    using var annotated = overlay.RenderAnnotatedBitmap();
                    var cropped = ScreenCapture.CropRegion(annotated, sel);
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                    HandleCaptureResult(cropped);
                };


                overlay.OcrRegionSelected += sel =>
                {
                    overlay.Hide();
                    UI.PopupWindowHelper.SetMonitorHintPoint(new System.Drawing.Point(sel.Right, sel.Bottom));
                    using var annotated = overlay.RenderAnnotatedBitmap();
                    var cropped = ScreenCapture.CropRegion(annotated, sel);
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                    HandleOcrResult(cropped);
                };

                overlay.ScrollRegionSelected += sel =>
                {
                    overlay.Hide();
                    UI.PopupWindowHelper.SetMonitorHintPoint(new System.Drawing.Point(sel.Right, sel.Bottom));
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                    Dispatcher.BeginInvoke(() => LaunchScrollingCapture(sel));
                };

                overlay.ScanRegionSelected += sel =>
                {
                    overlay.Hide();
                    SoundService.PlayScanSound();
                    UI.PopupWindowHelper.SetMonitorHintPoint(new System.Drawing.Point(sel.Right, sel.Bottom));
                    using var annotated = overlay.RenderAnnotatedBitmap();
                    var scanned = ScreenCapture.CropRegion(annotated, sel);
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            var decoded = BarcodeService.DecodeDetailed(scanned);
                            if (decoded is not null)
                            {
                                var copySucceeded = TryCopyCaptureTextToClipboard(decoded.Text);
                                _historyService?.SaveCodeEntry(decoded.Text, decoded.Format.ToString());
                                var prev = decoded.Text.Length > 100 ? decoded.Text[..100] + "..." : decoded.Text;
                                var preview = BarcodeService.RenderPreview(decoded.Text, decoded.Format);
                                var title = decoded.Format == ZXing.BarcodeFormat.QR_CODE
                                    ? copySucceeded ? "QR Code copied" : "QR Code found"
                                    : copySucceeded ? "Barcode copied" : "Barcode found";
                                ToastWindow.ShowInlinePreview(preview, title, prev, suppressSound: true);
                            }
                            else
                            {
                                ToastWindow.Show("Scan", "No QR & Barcode found");
                            }
                        }
                        catch (Exception ex)
                        {
                            ShowCaptureProcessingFailed(
                                "Scan failed",
                                "CyberSnap could not scan this region. Try a clearer QR & Barcode region.",
                                ex.Message);
                        }
                        finally
                        {
                            scanned.Dispose();
                        }
                    });
                };

                overlay.RecordingRequested += fmt =>
                {
                    overlay.Hide();
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                    Dispatcher.BeginInvoke(() => LaunchGifRecording(fmt));
                };

                overlay.ColorPicked += hex =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        SoundService.PlayColorSound();
                        string bare = hex.TrimStart('#');
                        string formatted = $"#{bare}";
                        var copySucceeded = TryCopyCaptureTextToClipboard(formatted);
                        byte r = Convert.ToByte(bare[..2], 16);
                        byte g = Convert.ToByte(bare[2..4], 16);
                        byte b = Convert.ToByte(bare[4..6], 16);
                        ToastWindow.ShowWithColor(copySucceeded ? "Color copied" : "Color picked", formatted,
                            System.Windows.Media.Color.FromRgb(r, g, b), suppressSound: true);

                        if (_settingsService!.Settings.SaveHistory)
                            EnsureHistoryService().SaveColorEntry(bare);
                    });
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                };

                overlay.FormClosed += (_, _) =>
                {
                    var mode = overlay.CurrentMode;
                    if (mode is CaptureMode.Rectangle or CaptureMode.Center)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            _settingsService!.Settings.LastCaptureMode = mode;
                            _settingsService.Save();
                        });
                    }

                    Dispatcher.BeginInvoke(ResetCapturing);
                };

                try
                {
                    System.Windows.Forms.Application.Run(overlay);
                }
                finally
                {
                    screenshot.Dispose();
                }
            }
            catch (Exception ex)
            {
                screenshot?.Dispose();
                Dispatcher.BeginInvoke(() =>
                {
                    ResetCapturing();
                    ShowCaptureProcessingFailed(
                        "Capture error",
                        "CyberSnap could not start the capture overlay. Try again, or check capture settings.",
                        ex.Message);
                });
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static bool TryCopyCaptureTextToClipboard(string text)
    {
        try
        {
            ClipboardService.CopyTextToClipboard(text);
            return true;
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Copy failed",
                $"CyberSnap could not copy this capture result. The result will still be shown and saved when history is enabled.\n{ex.Message}");
            return false;
        }
    }

    private static bool TryCopyRecordingFileToClipboard(string path)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            System.Windows.Clipboard.SetFileDropList(files);
            return true;
        }
        catch
        {
            return false;
        }
    }

}
