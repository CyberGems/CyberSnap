using System.Drawing;
using CyberSnap.Capture;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

namespace CyberSnap;

public partial class App
{
    private int _isZooming;
    public void OnStandaloneZoomProxy() => OnStandaloneZoomHotkeyPressed();

    private void OnStandaloneZoomHotkeyPressed()
    {
        _trayIcon?.CloseContextMenu();
        if (Volatile.Read(ref _activeStandaloneTools) != 0 || !TryClaimCaptureSlotAllowingPreviewReplace()) return;
        try
        {
            Volatile.Write(ref _isZooming, 1);
            _widgetWindow?.HideForExternalSession(this);
            var pointer = Forms.Cursor.Position;
            var settings = _settingsService!.Settings;
            var options = new Models.AppSettings
            {
                ZoomInitialLevel = settings.ZoomInitialLevel,
                ZoomSmoothImage = settings.ZoomSmoothImage,
                ZoomAnimate = settings.ZoomAnimate && !settings.DisableAnimations,
                ShowCursor = settings.ShowCursor
            };
            RunAfterPreviewChromeHidden(() =>
            {
                var thread = new Thread(() =>
                {
                    Bitmap? result = null;
                    try
                    {
                        // Let the tray/widget dismissal reach the compositor before freezing the monitor.
                        Thread.Sleep(100);
                        var bounds = Forms.Screen.FromPoint(pointer).Bounds;
                        var frozen = ScreenCapture.CaptureRegion(bounds);
                        StandaloneZoomForm form;
                        try { form = new StandaloneZoomForm(frozen, bounds, pointer, options); }
                        catch { frozen.Dispose(); throw; }
                        using (form)
                        {
                            form.FormClosed += (_, _) => result = form.TakeResult();
                            Forms.Application.Run(form);
                            var action = form.CommitAction;
                            var monitor = form.MonitorPoint;
                            if (Dispatcher.HasShutdownStarted) { result?.Dispose(); return; }
                            var ownedResult = result;
                            Dispatcher.BeginInvoke(() => DeliverZoomCapture(ownedResult, action, monitor));
                            result = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        result?.Dispose();
                        AppDiagnostics.LogError("standalone-zoom", ex);
                        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
                        {
                            Volatile.Write(ref _isZooming, 0);
                            ResetCapturing();
                            ToastWindow.ShowError(LocalizationService.Translate("Zoom could not start"), ex.Message);
                        });
                    }
                }) { IsBackground = true, Name = "CyberSnap Zoom" };
                thread.SetApartmentState(ApartmentState.STA);
                try { thread.Start(); }
                catch { Volatile.Write(ref _isZooming, 0); ResetCapturing(); throw; }
            });
        }
        catch
        {
            Volatile.Write(ref _isZooming, 0);
            ResetCapturing();
            throw;
        }
    }

    private void DeliverZoomCapture(Bitmap? result, RegionOverlayForm.ConfirmCommitAction action, Point monitor)
    {
        Volatile.Write(ref _isZooming, 0);
        if (result is null) { ResetCapturing(); return; }
        try
        {
            var latest = SettingsService.LoadStatic();
            if (latest is not null) _settingsService!.Settings = latest;
            PopupWindowHelper.SetMonitorHintPoint(monitor);
            var settings = _settingsService!.Settings;
            if (settings.ShowCapturePreview && action == RegionOverlayForm.ConfirmCommitAction.Default)
            {
                bool copied = Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Image)
                    && TryCopyCaptureOutputToClipboard(result, null);
                var saved = TrySaveCaptureFileEarly(result, settings);
                PresentCapturePreview(result, monitor, saved, copied);
            }
            else HandleCaptureResult(result, action);
        }
        catch (Exception ex)
        {
            result.Dispose();
            AppDiagnostics.LogError("zoom.capture-dispatch", ex);
            ResetCapturing();
            ToastWindow.ShowError(LocalizationService.Translate("Zoom capture failed"), ex.Message);
        }
    }
}
