using System.Drawing;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

namespace CyberSnap;

public partial class App
{
    private int _isZooming;
    private StandaloneZoomForm? _activeZoomForm;
    public void OnStandaloneZoomProxy() => OnStandaloneZoomHotkeyPressed();

    private void OnStandaloneZoomHotkeyPressed()
    {
        if (TrySwitchActiveOverlay(CaptureMode.Zoom)) return;
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
                ZoomShowHelpBar = settings.ZoomShowHelpBar,
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
                            Volatile.Write(ref _activeZoomForm, form);
                            form.CaptureRequested += request => LaunchOverlayNow(request.SelectionMode, request);
                            form.FormClosed += (_, _) => result = form.TakeResult();
                            try { Forms.Application.Run(form); }
                            finally { Interlocked.CompareExchange(ref _activeZoomForm, null, form); }
                            if (form.CaptureOutputDelegated) return;
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

    private bool TryCaptureFromZoom(Models.CaptureMode mode)
    {
        if (Volatile.Read(ref _isZooming) == 0) return false;
        var form = Volatile.Read(ref _activeZoomForm);
        if (form?.CapturePending == true) return false; // The usual hotkey can still switch the active overlay.
        if (form is null) return true; // Zoom is still freezing the monitor.
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                if (!form.IsDisposed && !form.CapturePending) form.BeginCapture(false, mode);
            }));
        }
        catch (InvalidOperationException) when (form.IsDisposed || form.Disposing || !form.IsHandleCreated) { }
        return true;
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
