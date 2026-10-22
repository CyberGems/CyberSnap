using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace CyberSnap;

public partial class App
{
    private PinSourceWindow? _pinChooser;
    private readonly List<PinImageForm> _pinnedImages = new();
    private bool _pinKeyboardInteropEnabled;

    public void OnPinToolProxy()
    {
        _trayIcon?.CloseContextMenu();
        if (IsSessionBusy()) return;
        if (_pinChooser is { IsVisible: true }) { _pinChooser.Activate(); return; }
        var chooser = new PinSourceWindow();
        _pinChooser = chooser;
        chooser.Closed += (_, _) => { if (ReferenceEquals(_pinChooser, chooser)) _pinChooser = null; };
        chooser.SourceRequested += source =>
        {
            try
            {
                if (source == "region")
                {
                    if (!TryClaimCaptureSlotAllowingPreviewReplace()) return;
                    try
                    {
                        chooser.Close(); HideSettingsForCapture();
                        _widgetWindow?.HideForExternalSession(this);
                        RunAfterPreviewChromeHidden(() => LaunchWithDelay(StartPinRegionCapture));
                    }
                    catch { ResetCapturing(); throw; }
                }
                else if (source == "clipboard")
                {
                    var image = System.Windows.Clipboard.GetImage();
                    if (image is null) { ToastWindow.ShowError("Pin to screen", "The clipboard does not contain an image."); return; }
                    var owned = ReadPinClipboardImage(image);
                    chooser.Close(); PinImage(owned);
                }
                else
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Title = LocalizationService.Translate("Select image..."), Filter = LocalizationService.Translate("Image files") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
                    if (dialog.ShowDialog(chooser) != true) return;
                    using var decoded = new Bitmap(dialog.FileName);
                    var owned = PinImageForm.CopyPixels(decoded);
                    chooser.Close(); PinImage(owned);
                }
            }
            catch (Exception ex) { AppDiagnostics.LogError("pin.source", ex); ToastWindow.ShowError("Pin failed", ex.Message); }
        };
        chooser.Show(); chooser.Activate();
    }

    private void PinCapturedRegion(Bitmap image, Point point)
    {
        try { PinImage(image, point); }
        finally { ResetCapturing(); }
    }

    private void StartPinRegionCapture()
    {
        try { LaunchOverlayNow(CaptureMode.Pin, imageReady: PinCapturedRegion); }
        catch (Exception ex)
        {
            ResetCapturing();
            AppDiagnostics.LogError("pin.capture-start", ex);
            ToastWindow.ShowError("Pin failed", ex.Message);
        }
    }

    internal static Bitmap ReadPinClipboardImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
        stream.Position = 0;
        using var decoded = new Bitmap(stream);
        return PinImageForm.CopyPixels(decoded);
    }

    /// <summary>Transfers ownership of an independent bitmap to a modeless reference window.</summary>
    public void PinImage(Bitmap ownedImage, Point? point = null)
    {
        if (Dispatcher.HasShutdownStarted) { ownedImage.Dispose(); return; }
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => PinImage(ownedImage, point)); return; }
        PinImageForm? form = null;
        try
        {
            form = new PinImageForm(ownedImage, point ?? System.Windows.Forms.Cursor.Position, SettingsService.Settings);
            _pinnedImages.Add(form);
            form.FormClosed += (_, _) => _pinnedImages.Remove(form);
            form.CopyRequested += image =>
            {
                using (image)
                {
                    try { ClipboardService.CopyToClipboard(image); }
                    catch (Exception ex)
                    {
                        AppDiagnostics.LogError("pin.copy", ex);
                        ToastWindow.ShowError("Pin to screen", "The image could not be copied. Try again.");
                    }
                }
            };
            form.SaveRequested += image =>
            {
                using (image)
                {
                    try
                    {
                        using var dialog = new System.Windows.Forms.SaveFileDialog { Title = LocalizationService.Translate("Save as..."), Filter = "PNG|*.png", DefaultExt = ".png", FileName = $"CyberSnap-pin-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png" };
                        if (dialog.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) image.Save(dialog.FileName, ImageFormat.Png);
                    }
                    catch (Exception ex) { ToastWindow.ShowError("Pin failed", ex.Message); }
                }
            };
            form.EditRequested += image =>
            {
                try { UI.Editor.EditorForm.ShowEditor(image); }
                catch (Exception ex) { image.Dispose(); ToastWindow.ShowError("Editor failed", ex.Message); }
            };
            form.OptionsRequested += () => ShowSettings("pin");
            if (!_pinKeyboardInteropEnabled)
            {
                System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
                _pinKeyboardInteropEnabled = true;
            }
            form.Show();
        }
        catch
        {
            if (form is null) ownedImage.Dispose();
            else { _pinnedImages.Remove(form); form.Dispose(); }
            throw;
        }
    }

    public void PinImageFile(string path)
    {
        try
        {
            using var image = new Bitmap(path);
            PinImage(PinImageForm.CopyPixels(image));
        }
        catch (Exception ex) { AppDiagnostics.LogError("pin.file", ex); ToastWindow.ShowError("Pin failed", ex.Message); }
    }

    public void RefreshPinnedImagePreferences()
    {
        foreach (var image in _pinnedImages.ToArray())
        {
            image.ApplyCapturePreferences(SettingsService.Settings.PinIncludeInCaptures, SettingsService.Settings.PinAlwaysOnTop);
            image.ApplyRenderingPreference(SettingsService.Settings.PinSmoothImage);
        }
    }

    private void ClosePinnedImages()
    {
        _pinChooser?.Close();
        foreach (var image in _pinnedImages.ToArray()) image.Close();
    }
}
