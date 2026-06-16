using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap;

public partial class App
{
    public void RegisterHotkeys(bool showReadyNotification = false)
    {
        _hotkeyService?.Dispose();
        _hotkeyService = new HotkeyService();
        _hotkeyService.UnregisterAll();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.OcrHotkeyPressed += OnOcrHotkeyPressed;
        _hotkeyService.PickerHotkeyPressed += OnPickerHotkeyPressed;
        _hotkeyService.ScanHotkeyPressed += () => OnToolHotkeyPressed(CaptureMode.Scan);
        _hotkeyService.CenterHotkeyPressed += () => OnToolHotkeyPressed(CaptureMode.Center);
        _hotkeyService.RulerHotkeyPressed += () => OnToolHotkeyPressed(CaptureMode.Ruler);
        _hotkeyService.GifHotkeyPressed += OnGifHotkeyPressed;
        _hotkeyService.FullscreenHotkeyPressed += OnFullscreenHotkeyPressed;
        _hotkeyService.ActiveWindowHotkeyPressed += OnActiveWindowHotkeyPressed;
        _hotkeyService.ScrollCaptureHotkeyPressed += OnScrollCaptureHotkeyPressed;
        _hotkeyService.StandaloneRulerHotkeyPressed += OnStandaloneRulerHotkeyPressed;

        var s = _settingsService!.Settings;
        var failed = new List<string>();

        void TryRegister(bool ok, string label, uint mod, uint key)
        {
            if (!ok) failed.Add($"{label} ({HotkeyFormatter.Format(mod, key)})");
        }

        TryRegister(_hotkeyService.Register(s.HotkeyModifiers, s.HotkeyKey), "Capture", s.HotkeyModifiers, s.HotkeyKey);
        TryRegister(_hotkeyService.RegisterOcr(s.OcrHotkeyModifiers, s.OcrHotkeyKey), "OCR", s.OcrHotkeyModifiers, s.OcrHotkeyKey);
        TryRegister(_hotkeyService.RegisterPicker(s.PickerHotkeyModifiers, s.PickerHotkeyKey), "Color Picker", s.PickerHotkeyModifiers, s.PickerHotkeyKey);
        TryRegister(_hotkeyService.RegisterScan(s.ScanHotkeyModifiers, s.ScanHotkeyKey), "Scanner", s.ScanHotkeyModifiers, s.ScanHotkeyKey);
        TryRegister(_hotkeyService.RegisterCenter(s.CenterHotkeyModifiers, s.CenterHotkeyKey), "From Center", s.CenterHotkeyModifiers, s.CenterHotkeyKey);
        TryRegister(_hotkeyService.RegisterRuler(s.RulerHotkeyModifiers, s.RulerHotkeyKey), "Ruler", s.RulerHotkeyModifiers, s.RulerHotkeyKey);
        TryRegister(_hotkeyService.RegisterGif(s.GifHotkeyModifiers, s.GifHotkeyKey), "GIF", s.GifHotkeyModifiers, s.GifHotkeyKey);
        TryRegister(_hotkeyService.RegisterFullscreen(s.FullscreenHotkeyModifiers, s.FullscreenHotkeyKey), "Fullscreen", s.FullscreenHotkeyModifiers, s.FullscreenHotkeyKey);
        TryRegister(_hotkeyService.RegisterActiveWindow(s.ActiveWindowHotkeyModifiers, s.ActiveWindowHotkeyKey), "Active Window", s.ActiveWindowHotkeyModifiers, s.ActiveWindowHotkeyKey);
        TryRegister(_hotkeyService.RegisterScrollCapture(s.ScrollCaptureHotkeyModifiers, s.ScrollCaptureHotkeyKey), "Scroll Capture", s.ScrollCaptureHotkeyModifiers, s.ScrollCaptureHotkeyKey);
        TryRegister(_hotkeyService.RegisterStandaloneRuler(s.StandaloneRulerHotkeyModifiers, s.StandaloneRulerHotkeyKey), "Standalone Ruler", s.StandaloneRulerHotkeyModifiers, s.StandaloneRulerHotkeyKey);
        if (failed.Count > 0)
            ToastWindow.ShowError("Hotkey conflict", $"{string.Join(", ", failed)} — already in use by another app");
        else if (showReadyNotification)
        {
            var name = HotkeyFormatter.Format(s.HotkeyModifiers, s.HotkeyKey);
            var bodyTemplate = LocalizationService.Translate("Press {0} to capture");
            var body = string.Format(bodyTemplate, name);
            ToastWindow.Show($"CyberSnap {UpdateService.GetCurrentVersionLabel()} ready", body);
            SoundService.PlayStartupSound();
        }
    }

    private void OnHotkeyPressed()
    {
        if (TrySwitchActiveOverlay(_settingsService!.Settings.DefaultCaptureMode))
            return;

        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchOverlay(_settingsService!.Settings.DefaultCaptureMode);
    }

    private void OnToolHotkeyPressed(CaptureMode mode)
    {
        if (TrySwitchActiveOverlay(mode))
            return;

        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchOverlay(mode);
    }

    private void OnOcrHotkeyPressed()
    {
        if (TrySwitchActiveOverlay(CaptureMode.Ocr))
            return;

        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchOverlay(CaptureMode.Ocr);
    }

    private void OnPickerHotkeyPressed()
    {
        if (TrySwitchActiveOverlay(CaptureMode.ColorPicker))
            return;

        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchOverlay(CaptureMode.ColorPicker);
    }

    private void OnGifHotkeyPressed()
    {
        if (RecordingForm.Current != null)
        {
            RecordingForm.Current.RequestStop();
            return;
        }

        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchGifRecording();
    }

    private void OnScrollCaptureHotkeyPressed()
    {
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchScrollingCapture();
    }


    private void OnFullscreenHotkeyPressed()
    {
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchWithDelay(CaptureFullscreenNow);
    }

    private void OnActiveWindowHotkeyPressed()
    {
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0) return;
        HideSettingsForCapture();
        LaunchWithDelay(CaptureActiveWindowNow);
    }

    private void LaunchWithDelay(Action action)
    {
        int delay = _settingsService!.Settings.CaptureDelaySeconds;
        if (delay > 0)
        {
            int remaining = delay;
            ToastWindow.Show($"Capturing in {remaining}...", "");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                remaining--;
                if (remaining > 0)
                    ToastWindow.Show($"Capturing in {remaining}...", "");
                else
                {
                    timer.Stop();
                    ToastWindow.DismissCurrent();
                    action();
                }
            };
            timer.Start();
            return;
        }

        action();
    }

    private static bool TrySwitchActiveOverlay(CaptureMode mode) =>
        RegionOverlayForm.TrySwitchCurrentOverlayMode(mode);

    public bool IsCapturingActive() => Volatile.Read(ref _isCapturing) != 0;

    private void OnStandaloneRulerHotkeyPressed()
    {
        // Dismiss tray context menu so it doesn't appear frozen in the screenshot
        _trayIcon?.CloseContextMenu();

        // Capture the target screen's working area on this thread for banner placement
        var targetScreen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var workingArea = targetScreen.WorkingArea;

        var thread = new Thread(() =>
        {
            try
            {
                Theme.Refresh();
                using var form = new StandaloneRulerForm(workingArea);
                System.Windows.Forms.Application.Run(form);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("standalone-ruler", ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    public void OnHotkeyPressedProxy() => OnHotkeyPressed();
    public void OnOcrHotkeyPressedProxy() => OnOcrHotkeyPressed();
    public void OnGifHotkeyPressedProxy() => OnGifHotkeyPressed();
    public void OnScrollCaptureHotkeyPressedProxy() => OnScrollCaptureHotkeyPressed();
    public void OnScanHotkeyPressedProxy() => OnToolHotkeyPressed(CaptureMode.Scan);
    public void OnCenterHotkeyPressedProxy() => OnToolHotkeyPressed(CaptureMode.Center);
    public void OnPickerHotkeyPressedProxy() => OnPickerHotkeyPressed();
    public void OnRulerHotkeyPressedProxy() => OnToolHotkeyPressed(CaptureMode.Ruler);
}
