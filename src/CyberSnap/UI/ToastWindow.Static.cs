using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Services;
using Color = System.Windows.Media.Color;

namespace CyberSnap.UI;

public partial class ToastWindow
{
    private static readonly List<ToastSpec> _celebrationQueue = new();
    private static ToastSpec? _currentSpec;
    private static int _showDepth;

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
                ShowShare = layout.ShowShare,
                ShareSlot = layout.ShareSlot,
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

    /// <summary>Pinned status toast for long-running encode/save work. Dismiss with <see cref="ForceDismissCurrent"/>.</summary>
    public static void ShowEncodingWait(string title, string body)
        => Show(new ToastSpec
        {
            Title = title,
            Body = body,
            IsSystemMessage = true,
            AutoPin = true,
            SuppressSound = true,
            DurationSeconds = 600,
        });

    internal static void Show(ToastSpec spec)
    {
        // Master switch: nothing is shown when notifications are off.
        if (!_notificationsEnabled)
            return;

        // Sub-toggle: suppress brief text-only system messages while leaving previews/errors.
        if (spec.IsSystemMessage && !_systemNotificationsEnabled)
            return;

        // Quiet hours: mute everything except critical error alerts (and settings test previews).
        if (!spec.IsError && !spec.BypassQuietHours
            && QuietHours.IsActive(SettingsService.LoadStatic()))
            return;

        // Guard: skip completely empty toasts (no text, no image, no color, no status rows)
        if (string.IsNullOrWhiteSpace(spec.Title)
            && string.IsNullOrWhiteSpace(spec.Body)
            && spec.InlinePreviewBitmap is null
            && string.IsNullOrEmpty(spec.InlineIconId)
            && !spec.SwatchColor.HasValue
            && spec.StatusLines is not { Count: > 0 })
            return;

        var wpfDispatcher = System.Windows.Application.Current?.Dispatcher;
        if (wpfDispatcher != null && !wpfDispatcher.CheckAccess())
        {
            wpfDispatcher.BeginInvoke(() => Show(spec));
            return;
        }

        _showDepth++;
        try
        {
            if (IsCelebration(spec))
            {
                // Settings test: show now so the user sees the flourish immediately.
                if (spec.BypassQuietHours)
                {
                    PresentToast(spec);
                    return;
                }

                EnqueueCelebration(spec);
                TryPresentNextCelebration();
                return;
            }

            // A celebration on screen keeps the slot until it dismisses. Errors still interrupt.
            if (!spec.IsError && _currentSpec is { } showing && IsCelebration(showing))
                return;

            if (spec.IsError && _currentSpec is { } interrupted && IsCelebration(interrupted))
                EnqueueCelebration(interrupted);

            PresentToast(spec);
        }
        finally
        {
            _showDepth--;
        }
    }

    private static bool IsCelebration(ToastSpec spec) => spec.Celebrate && !spec.IsError;

    private static void EnqueueCelebration(ToastSpec spec)
    {
        int i = 0;
        while (i < _celebrationQueue.Count && _celebrationQueue[i].CelebrationRank <= spec.CelebrationRank)
            i++;
        _celebrationQueue.Insert(i, spec);
    }

    internal static void NotifyHostSlotCleared()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            TryPresentNextCelebration();
            return;
        }

        dispatcher.BeginInvoke(TryPresentNextCelebration, DispatcherPriority.Background);
    }

    private static void TryPresentNextCelebration()
    {
        if (_celebrationQueue.Count == 0 || _current != null)
            return;

        if (!_notificationsEnabled)
        {
            DiscardQueuedCelebrations();
            return;
        }

        var settings = SettingsService.LoadStatic();
        if (settings?.CelebrationsEnabled != true)
        {
            DiscardQueuedCelebrations();
            return;
        }

        var next = _celebrationQueue[0];
        _celebrationQueue.RemoveAt(0);

        if (!next.BypassQuietHours && QuietHours.IsActive(settings))
        {
            DiscardCelebration(next);
            TryPresentNextCelebration();
            return;
        }

        PresentToast(next);
    }

    private static void DiscardQueuedCelebrations()
    {
        foreach (var spec in _celebrationQueue)
            DiscardCelebration(spec);
        _celebrationQueue.Clear();
    }

    private static void DiscardCelebration(ToastSpec spec)
        => spec.InlinePreviewBitmap?.Dispose();

    private static void PresentToast(ToastSpec spec)
    {
        if (IsCelebration(spec) && spec.DurationSeconds is null)
            spec = spec with { DurationSeconds = _systemDurationSeconds };

        if (!spec.SuppressSound && !IsCelebration(spec))
        {
            if (spec.PlayErrorSound)
                Services.SoundService.PlayErrorSound();
            else if (spec.PlayCaptureSound)
                Services.SoundService.PlayCaptureSound();
            else if (spec.IsSystemMessage)
                Services.SoundService.PlaySystemSound();
            else
                Services.SoundService.PlayCaptureSound();
        }

        if (!IsCelebration(spec) && _current?.TryUpdateInPlace(spec) == true)
        {
            _currentSpec = spec;
            return;
        }

        ReplaceCurrentToast();
        var toast = new ToastWindow(spec);
        _current = toast;
        _currentSpec = spec;
        toast.Show();

        if (IsCelebration(spec))
            Services.SoundService.PlayAchievementSound();
    }

    public static void ShowWithColor(string title, string body, Color color, bool suppressSound = false)
        => Show(ToastSpec.WithColor(title, body, color) with { SuppressSound = suppressSound });

    public static void ShowInlinePreview(Bitmap preview, string title, string body, string? filePath = null, bool suppressSound = false)
        => Show(ToastSpec.InlinePreview(preview, title, body, filePath) with { SuppressSound = suppressSound });

    public static void ShowError(string title, string body = "", string? filePath = null)
        => Show(ToastSpec.Error(title, body, filePath));

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

    public static void ForceDismissCurrent()
    {
        _current?.RequestDismiss(force: true);
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
