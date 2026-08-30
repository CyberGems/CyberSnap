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

    private void LaunchGifRecording(RecordingFormat? formatOverride = null, System.Drawing.Rectangle? initialSelection = null)
    {
        var thread = new Thread(() =>
        {
            try
            {
                Theme.Refresh();
                var settings = _settingsService!.Settings;
                Helpers.UiChrome.SetUiScale(settings.UiScale);
                var s = settings;
                var fmt = formatOverride ?? s.RecordingFormat;
                bool isGifFormat = fmt == RecordingFormat.GIF;
                bool showCursor = isGifFormat ? s.GifShowCursor : s.VideoShowCursor;
                var (selectionScreenshot, bounds) = ScreenCapture.CaptureAllScreens(showCursor);

                string ext = isGifFormat ? ".gif" : ".mp4";
                // Per-media save: permanent folder vs session temp (deleted after toast/trimmer).
                bool persistRecording = isGifFormat ? s.SaveGifToFile : s.SaveVideoToFile;
                string savePath;
                if (persistRecording)
                {
                    string baseDir = s.SaveDirectory;
                    string saveRoot = isGifFormat
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
                int fps = isGifFormat ? s.GifFps : s.RecordingFps;

                bool recMic = !isGifFormat && s.RecordMicrophone;
                bool recDesktop = !isGifFormat && s.RecordDesktopAudio;
                Capture.SelectionSizeReadout.ShowDimensions = _settingsService!.Settings.ShowSelectionSize;
                bool openTrimmerAtLaunch = isGifFormat ? s.OpenGifTrimmerAfterCapture : s.OpenVideoTrimmerAfterCapture;
                bool wantRecordingNotification = isGifFormat
                    ? s.ShowGifRecordingNotification
                    : s.ShowVideoRecordingNotification;
                // Always wire the GIF early-open callback; it re-checks settings so a mid-session
                // bar toggle still works.
                Action<string>? onGifEncodedForTrimmer = null;
                if (fmt == RecordingFormat.GIF)
                {
                    // Form only invokes this when the live Send-to-Trimmer toggle is on.
                    // Note: the "Show notification" pill fires from the main completion path
                    // below, so this early callback does not need a fallback toast on failure.
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
                    openTrimmerAtLaunch,
                    onGifEncodedForTrimmer,
                    initialSelection);

                form.Shown += (_, _) =>
                {
                    Dispatcher.BeginInvoke(() => _trayIcon?.UpdateRecordingState(true));
                };

                form.RecordingCompleted += (path, firstFrame, openTrimmer) =>
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        bool isGif = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
                        // openTrimmer comes from the recording-bar toggle (live), not a stale snapshot.

                        if (!(openTrimmer && isGif))
                            _trayIcon?.UpdateRecordingState(false);

                        // Gallery only indexes permanently saved recordings.
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

                        var autoCopyKind = isGif
                            ? Helpers.AutoCopyKind.Gif
                            : Helpers.AutoCopyKind.Video;
                        bool autoCopyRecording = Helpers.AutoCopyPreferences.ShouldCopy(s, autoCopyKind);
                        bool? copiedToClipboard = autoCopyRecording
                            ? TryCopyRecordingFileToClipboard(path)
                            : null;

                        // Count toward milestones; any earned celebration shows as a separate
                        // delayed follow-up toast, so the recording toast keeps its own text.
                        CelebrateCaptureIfEarned(s, CaptureKind.Recording);
                        MarkFirstTime(s.HasFirstRecording, () => s.HasFirstRecording = true, "First recording", "record", d => s.FirstRecordingAt = d);

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
                                    // Trimmer takes ownership of firstFrame (disposes poster on load).
                                    OpenVideoTrimmerAfterRecording(
                                        path,
                                        firstFrame,
                                        isGif: false,
                                        ephemeral: !persistRecording,
                                        onFailure: () => { });
                                    firstFrame = null;
                                }
                                catch (Exception ex)
                                {
                                    AppDiagnostics.LogError("capture.auto-open-trimmer", ex);
                                    firstFrame?.Dispose();
                                }
                            }
                        }
                        else
                        {
                            firstFrame?.Dispose();
                        }

                        // Dismiss the pinned "Encoding, please wait..." toast NOW — before the
                        // completion toast appears. Otherwise (MP4 path) the trimmer's 500ms
                        // deferred ShowTrimmer would arrive later and ForceDismissCurrent would
                        // incorrectly close the "Video recorded" toast the user just saw.
                        ToastWindow.ForceDismissCurrent();

                        // Pill semantics: "Show notification" is orthogonal to the trimmer.
                        // When ON, the completion toast is always shown (in addition to —
                        // or instead of — the trimmer window).
                        if (wantRecordingNotification)
                            ShowRecordingToast(path, copiedToClipboard, isGif, ephemeral: !persistRecording);

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
    /// <param name="ephemeral">When true, recording is temp (per-media save off); toast deletes it on dismiss.</param>
    private void ShowRecordingToast(string path, bool? copiedToClipboard, bool isGif, bool ephemeral = false)
    {
        string body = copiedToClipboard switch
        {
            true => LocalizationService.Translate("File copied to clipboard"),
            false => ephemeral
                ? LocalizationService.Translate("Clipboard copy failed")
                : LocalizationService.Translate("Saved; clipboard copy failed"),
            null => ephemeral
                ? LocalizationService.Translate("Ready")
                : LocalizationService.Translate("Saved")
        };

        string title = isGif
            ? LocalizationService.Translate("GIF recorded")
            : LocalizationService.Translate("Video recorded");

        ToastWindow.Show(ToastSpec.Standard(title, body, path) with
        {
            PlayCaptureSound = true,
            DeleteFileOnDismiss = ephemeral,
            InlineIconId = isGif ? "recordGif" : "record"
        });
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
                            () => _settingsService!.Settings.HasFirstScrollingCapture = true, "First scrolling capture", "scrollCapture", d => _settingsService!.Settings.FirstScrollCaptureAt = d);
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

    private void CaptureActiveWindowNow(IntPtr preferredWindow = default)
    {
        Bitmap? bmp = null;
        try
        {
            (bmp, var bounds) = ScreenCapture.CaptureAllScreens(_settingsService!.Settings.ShowCursor);
            // The tray menu takes focus while it is open, so use the window that was
            // foreground before the menu appeared instead of capturing the taskbar/menu.
            var hwnd = preferredWindow != IntPtr.Zero && Native.User32.IsWindow(preferredWindow)
                ? preferredWindow
                : Native.User32.GetForegroundWindow();
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
        if (initialMode == CaptureMode.ColorPicker)
        {
            OnStandaloneColorPickerHotkeyPressed();
            return;
        }

        if (initialMode == CaptureMode.Ruler)
        {
            OnStandaloneRulerHotkeyPressed();
            return;
        }

        LaunchWithDelay(() => LaunchOverlayNow(initialMode));
    }

    private void LaunchOverlayNow(CaptureMode initialMode)
    {
        var thread = new Thread(() =>
        {
            Bitmap? screenshot = null;
            // Set when RegionSelected delegates to the Preview dialog. The Preview then owns
            // the capture session (and calls ResetCapturing when it closes), so the overlay's
            // FormClosed handler skips its default ResetCapturing to avoid prematurely
            // re-arming the hotkey while the Preview is still open.
            bool outcomeDelegatedToPreview = false;
            try
            {
                Theme.Refresh();
                var s = _settingsService!.Settings;

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
                    ConfirmRegionBeforeCapture = true // Permanent confirm for area capture redesign
                };
                overlay.SetEnabledTools(_settingsService.Settings.EnabledTools);
                overlay.SetConfirmPillShowLabels(_settingsService.Settings.ConfirmPillShowLabels);
                overlay.SetConfirmDoneShowLabel(_settingsService.Settings.ConfirmDoneShowLabel);
                overlay.SetRememberAnnotationTool(_settingsService.Settings.RememberAnnotationTool);
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
                overlay.ToastButtonsChanged += toastLayout =>
                {
                    var latest = Services.SettingsService.LoadStatic();
                    if (latest != null)
                        _settingsService!.Settings = latest;
                    _settingsService!.Settings.ToastButtons = toastLayout;
                    _settingsService.Save();
                    // Keep Settings → Notifications designer in sync while capture is open.
                    try
                    {
                        if (_settingsWindow is { IsVisible: true })
                            _settingsWindow.RefreshConfirmPillDesigner();
                    }
                    catch { /* settings may be closing */ }
                };
                overlay.ConfirmPillShowLabelsChanged += showLabels =>
                {
                    var latest = Services.SettingsService.LoadStatic();
                    if (latest != null)
                        _settingsService!.Settings = latest;
                    _settingsService!.Settings.ConfirmPillShowLabels = showLabels;
                    _settingsService.Save();
                    try
                    {
                        if (_settingsWindow is { IsVisible: true })
                            _settingsWindow.SyncConfirmPillShowLabels(showLabels);
                    }
                    catch { }
                };
                overlay.ConfirmDoneShowLabelChanged += showLabel =>
                {
                    var latest = Services.SettingsService.LoadStatic();
                    if (latest != null)
                        _settingsService!.Settings = latest;
                    _settingsService!.Settings.ConfirmDoneShowLabel = showLabel;
                    _settingsService.Save();
                };
                overlay.RememberAnnotationToolChanged += remember =>
                {
                    var latest = Services.SettingsService.LoadStatic();
                    if (latest != null)
                        _settingsService!.Settings = latest;
                    _settingsService!.Settings.RememberAnnotationTool = remember;
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
                overlay.LastAnnotationToolChanged += toolId =>
                {
                    if (string.IsNullOrWhiteSpace(toolId)) return;
                    if (!_settingsService!.Settings.RememberAnnotationTool) return;
                    if (string.Equals(_settingsService!.Settings.LastAnnotationToolId, toolId, StringComparison.OrdinalIgnoreCase))
                        return;
                    _settingsService.Settings.LastAnnotationToolId = toolId;
                    _settingsService.Save();
                };

                overlay.RegionSelected += sel =>
                {
                    // Persist for any confirmed region-based capture, not just plain Rectangle:
                    // with permanent confirm the active tool may have been switched to an
                    // annotation (Arrow/Rect/…) before committing, so the old mode filter was
                    // skipping valid region selections and leaving a stale larger rect.
                    if (overlay.ActiveMode is CaptureMode.Rectangle or CaptureMode.Center
                        || Models.ToolDef.IsAnnotationTool(overlay.ActiveMode))
                        LastCaptureArea.PersistFromOverlaySelection(_settingsService!.Settings, _settingsService, sel, captureBounds);

                    var commitAction = overlay.PendingCommitAction;
                    overlay.Hide();
                    // sel is bitmap/overlay-client relative; convert to virtual-screen pixels.
                    var monitorPoint = new System.Drawing.Point(
                        captureBounds.X + sel.X + sel.Width / 2,
                        captureBounds.Y + sel.Y + sel.Height / 2);
                    UI.PopupWindowHelper.SetMonitorHintPoint(monitorPoint);
                    using var annotated = overlay.RenderAnnotatedBitmap();
                    var cropped = ScreenCapture.CropRegion(annotated, sel);

                    // Decide delegate ownership of the capture session BEFORE closing the
                    // overlay: FormClosed fires synchronously inside Close(). When the Preview
                    // is going to open, we keep _isCapturing=1 until it closes; otherwise
                    // the default FormClosed-reset path applies.
                    var latestSettings = Services.SettingsService.LoadStatic();
                    bool willShowPreview = latestSettings?.ShowCapturePreview
                        ?? _settingsService!.Settings.ShowCapturePreview;
                    if (willShowPreview)
                        outcomeDelegatedToPreview = true;

                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();

                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            var latest = Services.SettingsService.LoadStatic();
                            if (latest != null)
                                _settingsService!.Settings = latest;

                            var settings = _settingsService!.Settings;
                            if (settings.ShowCapturePreview)
                            {
                                bool copiedEarly = false;
                                if (Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Image))
                                {
                                    copiedEarly = TryCopyCaptureOutputToClipboard(cropped, null);
                                }

                                // Save is immediate when the path is known (same timing as auto-copy).
                                string? earlySavePath = TrySaveCaptureFileEarly(cropped, settings);

                                var dialog = new UI.CapturePreviewDialog(cropped, _settingsService, monitorPoint, earlySavePath, copiedEarly);
                                // Show() (non-modal) instead of ShowDialog(): modal loops disable every
                                // other top-level HWND in this thread via EnableWindow(false), which
                                // locked the floating widget and made Windows beep on click. With a single
                                // active preview managed via App.ShowCapturePreviewDialog + CommittedResult,
                                // the widget stays responsive and a follow-up capture replaces this dialog.
                                ShowCapturePreviewDialog(dialog, result =>
                                {
                                    bool isScaled = dialog.ScaleFactor != 1;
                                    var effective = isScaled ? dialog.EffectiveBitmap : cropped;
                                    string? effectiveSavePath = isScaled ? null : earlySavePath;
                                    bool effectiveCopied = isScaled ? false : dialog.ClipboardAlreadyCopied;
                                    if (result == true)
                                    {
                                        if (isScaled && !string.IsNullOrEmpty(earlySavePath) && File.Exists(earlySavePath))
                                        {
                                            try { File.Delete(earlySavePath); } catch { }
                                        }
                                        HandleCaptureResult(effective, dialog.SelectedAction, effectiveSavePath, effectiveCopied, isExplicitScaled: isScaled);
                                        if (isScaled)
                                        {
                                            try { cropped.Dispose(); } catch { }
                                        }
                                    }
                                    else
                                    {
                                        try { effective.Dispose(); } catch { }
                                        if (isScaled)
                                        {
                                            try { cropped.Dispose(); } catch { }
                                        }
                                        if (result == false)
                                            ResetCapturing();
                                    }
                                });
                            }
                            else
                            {
                                HandleCaptureResult(cropped, commitAction);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Defensive: never leave _isCapturing stuck at 1 (widget/tray would
                            // stay hidden forever). Log and reset so the next hotkey works.
                            AppDiagnostics.LogError("capture.preview-dispatch", ex);
                            try { cropped.Dispose(); } catch { }
                            ResetCapturing();
                        }
                    });
                };


                overlay.OcrRegionSelected += sel =>
                {
                    overlay.Hide();
                    UI.PopupWindowHelper.SetMonitorHintPoint(new System.Drawing.Point(
                        captureBounds.X + sel.Right, captureBounds.Y + sel.Bottom));
                    using var annotated = overlay.RenderAnnotatedBitmap();
                    var cropped = ScreenCapture.CropRegion(annotated, sel);
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                    HandleOcrResult(cropped);
                };

                overlay.ImmediateCaptureRequested += actionId =>
                {
                    // Overlay already closed itself; end its message loop, then run the action on
                    // the UI thread. These capture modes are self-contained (no region select).
                    System.Windows.Forms.Application.ExitThread();
                    Dispatcher.BeginInvoke(() =>
                    {
                        switch (actionId)
                        {
                            case "_fullscreen":
                                CaptureFullscreenNow();
                                break;
                            case "_activeWindow":
                                CaptureActiveWindowNow();
                                break;
                            case "_repeatLastArea":
                                CaptureRepeatLastAreaNow();
                                break;
                        }
                    });
                };

                overlay.ScrollRegionSelected += sel =>
                {
                    overlay.Hide();
                    UI.PopupWindowHelper.SetMonitorHintPoint(new System.Drawing.Point(
                        captureBounds.X + sel.Right, captureBounds.Y + sel.Bottom));
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                    Dispatcher.BeginInvoke(() => LaunchScrollingCapture(sel));
                };

                overlay.ScanRegionSelected += sel =>
                {
                    overlay.Hide();
                    SoundService.PlayScanSound();
                    UI.PopupWindowHelper.SetMonitorHintPoint(new System.Drawing.Point(
                        captureBounds.X + sel.Right, captureBounds.Y + sel.Bottom));
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
                                var autoCopy = AutoCopyPreferences.ShouldCopy(
                                    _settingsService!.Settings, AutoCopyKind.Scan);
                                var copySucceeded = autoCopy && TryCopyCaptureTextToClipboard(decoded.Text);
                                _historyService?.SaveCodeEntry(decoded.Text, decoded.Format.ToString());
                                var prev = decoded.Text.Length > 100 ? decoded.Text[..100] + "..." : decoded.Text;
                                if (autoCopy && copySucceeded)
                                {
                                    var preview = BarcodeService.RenderPreview(decoded.Text, decoded.Format);
                                    var title = decoded.Format == ZXing.BarcodeFormat.QR_CODE
                                        ? "QR Code copied"
                                        : "Barcode copied";
                                    ToastWindow.ShowInlinePreview(preview, title, prev, suppressSound: true);
                                }
                                else
                                {
                                    var previewSource = BitmapPerf.ToBitmapSource(scanned);
                                    var window = new QrResultWindow(
                                        decoded.Text, decoded.Format, _settingsService, previewSource);
                                    window.Show();
                                }
                                MarkFirstTime(_settingsService!.Settings.HasFirstScan,
                                    () => _settingsService!.Settings.HasFirstScan = true, "First scan", "scan", d => _settingsService!.Settings.FirstScanAt = d);
                            }
                            else
                            {
                                ToastWindow.Show(
                                    LocalizationService.Translate("Scan"),
                                    LocalizationService.Translate("No QR or Barcode found: Try again"));
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
                    // ConfirmRect is Rectangle.Empty when recording is requested from the
                    // toolbar before any area has been confirmed; treat it as "no selection"
                    // so RecordingForm starts in area-selection mode instead of PreRecording.
                    var rect = overlay.ConfirmRect;
                    overlay.Hide();
                    overlay.Close();
                    System.Windows.Forms.Application.ExitThread();
                    Dispatcher.BeginInvoke(() =>
                        LaunchGifRecording(fmt, rect.IsEmpty ? (System.Drawing.Rectangle?)null : rect));
                };

                bool handoffStandalonePicker = false;
                bool handoffStandaloneOcr = false;
                bool handoffStandaloneScan = false;
                bool handoffStandaloneRuler = false;

                overlay.StandaloneColorPickerRequested += () =>
                {
                    // Close the frozen overlay first so the standalone tool screenshots the
                    // live desktop. Do not ExitThread here — Close() ends Application.Run,
                    // and FormClosed launches the tool after this window is actually gone.
                    handoffStandalonePicker = true;
                    overlay.Hide();
                    overlay.Close();
                };

                overlay.StandaloneOcrRequested += () =>
                {
                    // Close the frozen overlay first so standalone OCR captures the
                    // live desktop and uses the same flow as its dedicated hotkey/tray action.
                    handoffStandaloneOcr = true;
                    overlay.Hide();
                    overlay.Close();
                };

                overlay.StandaloneScanRequested += () =>
                {
                    // Close the frozen overlay first so standalone scanning captures the
                    // live desktop and uses the same flow as its dedicated hotkey/tray action.
                    handoffStandaloneScan = true;
                    overlay.Hide();
                    overlay.Close();
                };

                overlay.StandaloneRulerRequested += () =>
                {
                    handoffStandaloneRuler = true;
                    overlay.Hide();
                    overlay.Close();
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
                            () => _settingsService.Settings.HasFirstColorPicker = true, "First color pick", "picker", d => _settingsService.Settings.FirstColorPickerAt = d);
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

                    // The Preview now owns the session and will call ResetCapturing itself
                    // once the user commits or cancels. Skipping here keeps _isCapturing=1
                    // so the hotkey guard blocks parallel captures while Preview is open.
                    if (outcomeDelegatedToPreview)
                        return;

                    Dispatcher.BeginInvoke(() =>
                    {
                        ResetCapturing();
                        if (handoffStandalonePicker)
                            OnStandaloneColorPickerHotkeyPressed();
                        else if (handoffStandaloneOcr)
                            OnStandaloneOcrHotkeyPressed();
                        else if (handoffStandaloneScan)
                            OnStandaloneScanHotkeyPressed();
                        else if (handoffStandaloneRuler)
                            OnStandaloneRulerHotkeyPressed();
                    });
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

    /// <summary>
    /// Opens the capture Preview for a bitmap produced by a standalone tool (ruler, …)
    /// so the user can save, copy, or edit instead of only getting a toast.
    /// </summary>
    private void OpenStandaloneCapturePreview(Bitmap bmp)
    {
        try
        {
            var latest = Services.SettingsService.LoadStatic();
            if (latest != null)
                _settingsService!.Settings = latest;

            var monitorPoint = System.Windows.Forms.Cursor.Position;
            UI.PopupWindowHelper.SetMonitorHintPoint(monitorPoint);

            var settings = _settingsService!.Settings;
            bool copiedEarly = false;
            if (Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Image))
                copiedEarly = TryCopyCaptureOutputToClipboard(bmp, null);

            string? earlySavePath = TrySaveCaptureFileEarly(bmp, settings);
            var dialog = new UI.CapturePreviewDialog(bmp, _settingsService, monitorPoint, earlySavePath, copiedEarly);
            ShowCapturePreviewDialog(dialog, result =>
            {
                bool isScaled = dialog.ScaleFactor != 1;
                var effective = isScaled ? dialog.EffectiveBitmap : bmp;
                string? effectiveSavePath = isScaled ? null : earlySavePath;
                bool effectiveCopied = isScaled ? false : dialog.ClipboardAlreadyCopied;
                if (result == true)
                {
                    if (isScaled && !string.IsNullOrEmpty(earlySavePath) && File.Exists(earlySavePath))
                    {
                        try { File.Delete(earlySavePath); } catch { }
                    }
                    HandleCaptureResult(effective, dialog.SelectedAction, effectiveSavePath, effectiveCopied, isExplicitScaled: isScaled);
                    if (isScaled)
                    {
                        try { bmp.Dispose(); } catch { }
                    }
                }
                else
                {
                    try { effective.Dispose(); } catch { }
                    if (isScaled)
                    {
                        try { bmp.Dispose(); } catch { }
                    }
                    if (result == false)
                        ResetCapturing();
                }
            });
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("capture.preview-dispatch", ex);
            try { bmp.Dispose(); } catch { }
            ResetCapturing();
        }
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
