using CyberSnap.Models;

namespace CyberSnap.Helpers;

public enum AutoCopyKind
{
    Image,
    Ocr,
    Video,
    Gif,
    Scan,
    Color
}

/// <summary>Independent clipboard preferences for each result type.</summary>
public static class AutoCopyPreferences
{
    public static bool ShouldCopy(AppSettings settings, AutoCopyKind kind) => kind switch
    {
        AutoCopyKind.Image => settings.ImageAutoCopyToClipboard,
        AutoCopyKind.Ocr => settings.OcrAutoCopyToClipboard,
        AutoCopyKind.Video => settings.VideoAutoCopyToClipboard,
        AutoCopyKind.Gif => settings.GifAutoCopyToClipboard,
        AutoCopyKind.Scan => settings.ScanAutoCopyToClipboard,
        AutoCopyKind.Color => settings.ColorDetailAutoCopy,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static void SetKindEnabled(AppSettings settings, AutoCopyKind kind, bool enabled)
    {
        switch (kind)
        {
            case AutoCopyKind.Image:
                settings.ImageAutoCopyToClipboard = enabled;
                SyncImageCopyPreference(settings);
                break;
            case AutoCopyKind.Ocr:
                settings.OcrAutoCopyToClipboard = enabled;
                break;
            case AutoCopyKind.Video:
                settings.VideoAutoCopyToClipboard = enabled;
                break;
            case AutoCopyKind.Gif:
                settings.GifAutoCopyToClipboard = enabled;
                break;
            case AutoCopyKind.Scan:
                settings.ScanAutoCopyToClipboard = enabled;
                break;
            case AutoCopyKind.Color:
                settings.ColorDetailAutoCopy = enabled;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>Keep the image destination's copy action aligned with its own preference.</summary>
    public static void SyncImageCopyPreference(AppSettings settings)
    {
        var view = AfterCapturePreferences.FromSettingsDestinationOnly(settings);
        AfterCapturePreferences.ApplyDestinationAndCopy(view.WindowIndex, settings.ImageAutoCopyToClipboard, settings);
    }
}
