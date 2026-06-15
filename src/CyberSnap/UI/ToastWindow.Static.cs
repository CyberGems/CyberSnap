using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Services;
using Color = System.Windows.Media.Color;

namespace CyberSnap.UI;

public partial class ToastWindow
{
    private const string DefaultImagePreviewTitle = "";

    public static void SetPosition(CyberSnap.Models.ToastPosition position) => _position = position;
    public static void SetMonitorIndex(int index) => _monitorIndex = index;
    public static void SetDuration(double seconds) => _durationSeconds = Math.Clamp(seconds, 1, 60);
    public static void SetSystemDuration(double seconds) => _systemDurationSeconds = Math.Clamp(seconds, 1, 60);
    public static double GetSystemDuration() => _systemDurationSeconds;

    // Master switch: when disabled, no toasts are shown at all (previews, system messages, errors).
    public static void SetNotificationsEnabled(bool enabled) => _notificationsEnabled = enabled;
    // Sub-toggle: when disabled, brief text-only system messages are suppressed while capture
    // previews and error alerts still appear. Ignored entirely when the master switch is off.
    public static void SetSystemNotificationsEnabled(bool enabled) => _systemNotificationsEnabled = enabled;
    public static void SetButtonLayout(Models.AppSettings.ToastButtonLayoutSettings? layout)
    {
        _buttonLayout = layout is null
            ? new Models.AppSettings.ToastButtonLayoutSettings()
            : new Models.AppSettings.ToastButtonLayoutSettings
            {
                ShowClose = layout.ShowClose,
                CloseSlot = layout.CloseSlot,
                ShowPin = layout.ShowPin,
                PinSlot = layout.PinSlot,
                ShowSave = layout.ShowSave,
                SaveSlot = layout.SaveSlot,
                ShowCopy = layout.ShowCopy,
                CopySlot = layout.CopySlot,
                ShowOffice = layout.ShowOffice,
                OfficeSlot = layout.OfficeSlot,
                ShowDelete = layout.ShowDelete,
                DeleteSlot = layout.DeleteSlot,
                ShowHistory = layout.ShowHistory,
                HistorySlot = layout.HistorySlot,
                ShowEdit = layout.ShowEdit,
                EditSlot = layout.EditSlot
            };

        _current?.RefreshOverlayButtonLayout();
    }

    // Toasts always fade out now; this only sets how long the fade animation lasts.
    public static void SetFadeOutSeconds(double seconds)
        => _fadeOutSeconds = Math.Clamp(seconds, 1, 10);
    public static double GetDuration() => _durationSeconds;

    public static void Show(string title, string body = "", string? filePath = null)
        => Show(ToastSpec.Standard(title, body, filePath));

    internal static void Show(ToastSpec spec)
    {
        // Master switch: nothing is shown when notifications are off.
        if (!_notificationsEnabled)
            return;

        // Sub-toggle: suppress brief text-only system messages while leaving previews/errors.
        if (spec.IsSystemMessage && !_systemNotificationsEnabled)
            return;

        // Guard: skip completely empty toasts (no text, no image, no color)
        if (string.IsNullOrWhiteSpace(spec.Title)
            && string.IsNullOrWhiteSpace(spec.Body)
            && spec.PreviewBitmap is null
            && spec.InlinePreviewBitmap is null
            && !spec.SwatchColor.HasValue)
            return;

        if (!spec.SuppressSound)
        {
            if (spec.PlayErrorSound)
                Services.SoundService.PlayErrorSound();
            else
                Services.SoundService.PlayCaptureSound();
        }

        if (_current?.TryUpdateInPlace(spec) == true)
            return;

        ReplaceCurrentToast();
        var toast = new ToastWindow(spec);
        _current = toast;
        toast.Show();
    }

    public static void ShowSticker(Bitmap sticker)
        => Show(ToastSpec.Sticker(sticker));

    public static void ShowWithColor(string title, string body, Color color, bool suppressSound = false)
        => Show(ToastSpec.WithColor(title, body, color) with { SuppressSound = suppressSound });

    public static void ShowInlinePreview(Bitmap preview, string title, string body, string? filePath = null, bool suppressSound = false)
        => Show(ToastSpec.InlinePreview(preview, title, body, filePath) with { SuppressSound = suppressSound });

    public static void ShowError(string title, string body = "", string? filePath = null)
        => Show(ToastSpec.Error(title, body, filePath));

    public static void ShowImagePreview(Bitmap screenshot, string? filePath, bool autoPin, bool celebrate = false)
    {
        ShowImagePreview(screenshot, DefaultImagePreviewTitle, "", filePath, autoPin, celebrate);
    }

    public static void ShowImagePreview(Bitmap screenshot, string title, string body, string? filePath, bool autoPin, bool celebrate = false)
    {
        Show(ToastSpec.ImagePreview(
            screenshot,
            title,
            body,
            filePath,
            autoPin,
            transparentShell: false,
            showOverlayButtons: true) with { Celebrate = celebrate });
    }

    public static void ShowImagePreview(Bitmap screenshot, string title, string body, string? filePath, bool autoPin, string? clickActionUrl, string? clickActionLabel)
    {
        Show(ToastSpec.ImagePreview(
            screenshot,
            title,
            body,
            filePath,
            autoPin,
            transparentShell: false,
            showOverlayButtons: true,
            clickActionUrl,
            clickActionLabel));
    }

    private static bool OpenFileLocation(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("toast.open-file-location", $"Failed to open file location: {ex.Message}", ex);
            ShowError(
                "Open failed",
                $"CyberSnap could not open the saved file location. Try again from the toast, or open the folder manually.\n{ex.Message}",
                filePath);
            return false;
        }
    }

    public static void DismissCurrent()
    {
        _current?.RequestDismiss();
    }

    private static void ReplaceCurrentToast()
    {
        _current?.TryForceClose(force: true);
    }

    private const double Edge = 8;

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        return BitmapPerf.ToBitmapSource(bitmap);
    }
}
