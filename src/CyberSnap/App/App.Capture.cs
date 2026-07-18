using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Helpers;
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
        NotifySessionBecameIdleIfQuiet();
    }

    /// <summary>
    /// True while a capture overlay/recording is active or a standalone tool form is open.
    /// Used by the floating widget to know when it is safe to re-show itself.
    /// </summary>
    public bool IsSessionBusy() =>
        Volatile.Read(ref _isCapturing) != 0 || Volatile.Read(ref _activeStandaloneTools) != 0;

    /// <summary>
    /// Raised on the UI dispatcher when the app transitions from busy to idle
    /// (capture ended and no standalone tool is open).
    /// </summary>
    public event Action? SessionBecameIdle;

    private void BeginStandaloneToolSession() => Interlocked.Increment(ref _activeStandaloneTools);

    private void EndStandaloneToolSession()
    {
        var remaining = Interlocked.Decrement(ref _activeStandaloneTools);
        if (remaining < 0)
            Interlocked.Exchange(ref _activeStandaloneTools, 0);
        NotifySessionBecameIdleIfQuiet();
    }

    private void NotifySessionBecameIdleIfQuiet()
    {
        if (IsSessionBusy()) return;

        void Raise()
        {
            try { SessionBecameIdle?.Invoke(); }
            catch (Exception ex) { AppDiagnostics.LogError("session.became-idle", ex); }
        }

        if (Dispatcher.CheckAccess())
            Raise();
        else
            _ = Dispatcher.BeginInvoke(Raise);
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

                string ext = fmt switch { RecordingFormat.MP4 => ".mp4", _ => ".gif" };
                // Respect SaveToFile: permanent folder vs session temp (deleted after toast/trimmer).
                bool persistRecording = s.SaveToFile;
                string savePath;
                if (persistRecording)
                {
                    string baseDir = s.SaveDirectory;
                    string saveRoot = fmt == RecordingFormat.GIF
                        ? Path.Combine(baseDir, "GIFs")
                        : Path.Combine(baseDir, "Videos");
                    string saveDir = s.SaveInMonthlyFolders
                        ? Helpers.CaptureSavePath.GetMonthDirectory(saveRoot)
                        : saveRoot;
                    Directory.CreateDirectory(saveDir);
                    string fileName = $"{Helpers.FileNameTemplate.Format(s.FileNameTemplate, 0, 0)}{ext}";
                    savePath = Helpers.CaptureSavePath.GetAvailablePath(Path.Combine(saveDir, fileName));
                }
                else
                {
                    savePath = Helpers.CaptureSavePath.BuildTempRecordingPath(ext);
                }
                int maxH = s.RecordingQuality switch { RecordingQuality.P1080 => 1080, RecordingQuality.P720 => 720, RecordingQuality.P480 => 480, _ => 0 };
                int fps = fmt == RecordingFormat.GIF ? s.GifFps : s.RecordingFps;

                bool recMic = fmt != RecordingFormat.GIF && s.RecordMicrophone;
                bool recDesktop = fmt != RecordingFormat.GIF && s.RecordDesktopAudio;
                Capture.SelectionSizeReadout.ShowDimensions = _settingsService!.Settings.ShowSelectionSize;
                bool openTrimmer = s.OpenVideoTrimmerAfterCapture;
                Action<string>? onGifEncodedForTrimmer = null;
                if (openTrimmer && fmt == RecordingFormat.GIF)
                {
                    onGifEncodedForTrimmer = path =>
                    {
                        try
                        {
                            Dispatcher.Invoke(DispatcherPriority.Send, () =>
                            {
                                _trayIcon?.UpdateRecordingState(false);
                                OpenVideoTrimmerAfterRecording(
                                    path,
                                    firstFrame: null,
                                    isGif: true,
                                    ephemeral: !persistRecording,
                                    onFailure: () => { });
                            });
                        }
                        catch (Exception ex)
                        {
                            AppDiagnostics.LogError("capture.auto-open-trimmer-immediate", ex);
                        }
                    };
                }

                var form = new RecordingForm(selectionScreenshot, bounds, fps, savePath, fmt, maxH,
                    showCursor, recMic, s.MicrophoneDeviceId, recDesktop, s.DesktopAudioDeviceId,
                    _settingsService!.Settings.ShowCaptureMagnifier,
                    openTrimmer,
                    onGifEncodedForTrimmer);

                form.Shown += (_, _) =>
                {
                    Dispatcher.BeginInvoke(() => _trayIcon?.UpdateRecordingState(true));
                };

                form.RecordingCompleted += (path, firstFrame) =>
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        bool isGif = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
                        if (!(openTrimmer && isGif))
                            _trayIcon?.UpdateRecordingState(false);

                        // Gallery only indexes permanently saved recordings (SaveToFile).
                        if (persistRecording && s.SaveHistory)
                        {
                            try
                            {
                                EnsureHistoryService().SaveMediaEntry(path);
                            }
                            catch (Exception ex)
                            {
                                AppDiagnostics.LogError("capture.recording-history", ex, $"Failed to save recording history for {Path.GetFileName(path)}.");
                            }
                        }

                        bool autoCopyRecording = Helpers.AutoCopyPreferences.ShouldCopy(s, Helpers.AutoCopyKind.Recording);
                        bool? copiedToClipboard = autoCopyRecording
                            ? TryCopyRecordingFileToClipboard(path)
                            : null;

                        // Count toward milestones; any earned celebration shows as a separate
                        // delayed follow-up toast, so the recording toast keeps its own text.
                        CelebrateCaptureIfEarned(s, CaptureKind.Recording);
                        MarkFirstTime(s.HasFirstRecording, () => s.HasFirstRecording = true, "First recording", "record");

                        if (openTrimmer)
                        {
                            if (isGif)
                            {
                                // Trimmer already opened from onGifEncodedForTrimmer.
                                firstFrame?.Dispose();
                            }
                            else
                            {
                                try
                                {
                                    OpenVideoTrimmerAfterRecording(
                                        path,
                                        firstFrame,
                                        isGif: false,
                                        ephemeral: !persistRecording,
                                        onFailure: () => ShowRecordingToast(path, firstFrame, copiedToClipboard, isGif: false, ephemeral: !persistRecording));
                                }
                                catch (Exception ex)
                                {
                                    AppDiagnostics.LogError("capture.auto-open-trimmer", ex);
                                    ShowRecordingToast(path, firstFrame, copiedToClipboard, isGif: false, ephemeral: !persistRecording);
                                }
                            }
                        }
                        else
                        {
                            ShowRecordingToast(path, firstFrame, copiedToClipboard, isGif, ephemeral: !persistRecording);
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

    /// <param name="copiedToClipboard">
    /// true = copied, false = copy attempted and failed, null = auto-copy skipped for recordings.
    /// </param>
    /// <param name="ephemeral">When true, recording is temp (SaveToFile off); toast deletes it on dismiss.</param>
    private void ShowRecordingToast(string path, Bitmap? firstFrame, bool? copiedToClipboard, bool isGif, bool ephemeral = false)
    {
        string copyStatus = copiedToClipboard switch
        {
            true => LocalizationService.Translate("File copied to clipboard"),
            false => ephemeral
                ? LocalizationService.Translate("Clipboard copy failed")
                : LocalizationService.Translate("Saved; clipboard copy failed"),
            null => ephemeral
                ? LocalizationService.Translate("Ready")
                : LocalizationService.Translate("Saved")
        };

        if (firstFrame != null)
        {
            ToastWindow.Show(ToastSpec.ImagePreview(
                firstFrame,
                isGif ? LocalizationService.Translate("GIF recorded") : LocalizationService.Translate("Video recorded"),
                copyStatus,
                path,
                false,
                transparentShell: false,
                showOverlayButtons: true,
                hideEditButton: false,
                deleteFileOnDismiss: ephemeral));
        }
        else
        {
            var fi = new FileInfo(path);
            string label = fi.Extension.TrimStart('.').ToUpper();
            string size = fi.Length > 1024 * 1024
                ? $"{fi.Length / 1024.0 / 1024.0:F1} MB"
                : $"{fi.Length / 1024:N0} KB";
            ToastWindow.Show(new ToastSpec
            {
                Title = $"{label} recorded",
                Body = $"{fi.Name} · {size} · {copyStatus}",
                FilePath = path,
                IsSystemMessage = true,
                DeleteFileOnDismiss = ephemeral
            });
        }
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
                _settingsService!.Load();
                var captureMode = _settingsService.Settings.ScrollingCaptureMode;
                var (selectionScreenshot, bounds) = ScreenCapture.CaptureAllScreens(showCursor);
                Capture.SelectionSizeReadout.ShowDimensions = _settingsService.Settings.ShowSelectionSize;
                var form = new ScrollingCaptureForm(selectionScreenshot, bounds, showCursor,
                    _settingsService.Settings.ShowCaptureMagnifier,
                    captureMode,
                    preSelectedRegion);
                form.CaptureModeChanged += mode => _settingsService.Settings.ScrollingCaptureMode = mode;

                form.CaptureCompleted += result =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        HandleCaptureResult(result);
                        MarkFirstTime(_settingsService!.Settings.HasFirstScrollingCapture,
                            () => _settingsService!.Settings.HasFirstScrollingCapture = true, "First scrolling capture", "scrollCapture");
                        ScheduleIdleMemoryTrim();
                    });
                };

                form.CaptureFailed += message =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        ResetCapturing();
                        ShowCaptureProcessingFailed(
                            LocalizationService.Translate("Scroll capture error"),
                            LocalizationService.Translate("CyberSnap could not finish the scrolling capture. Try a smaller scroll area or a visible scrollable window."),
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
                        LocalizationService.Translate("Scroll capture error"),
                        LocalizationService.Translate("CyberSnap could not start scrolling capture. Try again with a visible scrollable window."),
                        LocalizationService.Translate("Scrolling capture failed."));
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

    /// <summary>Captures only the screen that currently contains the cursor.
    /// Used by standalone tools (ruler, color picker, etc.) for targeted captures.</summary>
    private void CaptureCurrentScreenNow()
    {
        Bitmap? bmp = null;
        try
        {
            (bmp, _) = ScreenCapture.CaptureCurrentScreen(_settingsService!.Settings.ShowCursor);
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

    /// <summary>Captures an arbitrary screen region in VirtualScreen coordinates.</summary>
    private void CaptureRegionNow(Rectangle region)
    {
        Bitmap? bmp = null;
        try
        {
            bmp = ScreenCapture.CaptureRegion(region, _settingsService!.Settings.ShowCursor);
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

    private void CaptureRepeatLastAreaNow()
    {
        Bitmap? bmp = null;
        try
        {
            var settings = _settingsService!.Settings;
            if (!LastCaptureArea.TryGetScreenRect(settings, out var screenRect))
            {
                ResetCapturing();
                ToastWindow.Show(
                    LocalizationService.Translate("Repeat last area"),
                    LocalizationService.Translate("No saved capture area yet. Select a region first."));
                return;
            }

            UI.PopupWindowHelper.SetMonitorHintPoint(new System.Drawing.Point(screenRect.Right, screenRect.Bottom));
            bmp = ScreenCapture.CaptureRegion(screenRect, settings.ShowCursor);
            LastCaptureArea.PersistScreenRect(settings, _settingsService, screenRect);
            HandleCaptureResult(bmp);
            bmp = null;
        }
        catch (Exception ex)
        {
            bmp?.Dispose();
            ResetCapturing();
            ShowCaptureProcessingFailed(
                "Capture error",
                "CyberSnap could not repeat the last capture area. Try a normal region capture.",
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
                var captureBounds = bounds;

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
                overlay.QuickStartGuideDismissed += () =>
                {
                    if (_settingsService!.Settings.HasSeenQuickStartGuide) return;
                    _settingsService.Settings.HasSeenQuickStartGuide = true;
                    _settingsService.Save();
                };

                overlay.RegionSelected += sel =>
                {
                    if (overlay.ActiveMode == CaptureMode.Rectangle)
                        LastCaptureArea.PersistFromOverlaySelection(_settingsService!.Settings, _settingsService, sel, captureBounds);

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
                                MarkFirstTime(_settingsService!.Settings.HasFirstScan,
                                    () => _settingsService!.Settings.HasFirstScan = true, "First scan", "scan");
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

                        MarkFirstTime(_settingsService.Settings.HasFirstColorPicker,
                            () => _settingsService.Settings.HasFirstColorPicker = true, "First color pick", "picker");
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

    private void OpenVideoTrimmerAfterRecording(
        string path,
        Bitmap? firstFrame,
        bool isGif,
        Action onFailure,
        bool ephemeral = false)
    {
        void ShowTrimmer()
        {
            try
            {
                ToastWindow.ForceDismissCurrent();
                var trimmer = new VideoTrimmerWindow(path, _settingsService!, firstFrame);
                if (ephemeral)
                {
                    // Drop the temp recording when the trimmer closes (Save As New keeps the export).
                    trimmer.Closed += (_, _) => Helpers.CaptureSavePath.TryDeleteTempRecording(path);
                }
                trimmer.Show();
                trimmer.Activate();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("capture.auto-open-trimmer-deferred", ex);
                if (ephemeral)
                    Helpers.CaptureSavePath.TryDeleteTempRecording(path);
                onFailure();
            }
        }

        // WMF needs a brief moment to release the new MP4 file; GIF opens immediately with in-window loading UI.
        if (isGif)
        {
            ShowTrimmer();
            return;
        }

        Task.Delay(500).ContinueWith(_ => Dispatcher.BeginInvoke(ShowTrimmer));
    }

    private static bool TryCopyRecordingFileToClipboard(string path)
    {
        try
        {
            ClipboardService.CopyFileToClipboard(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

}
