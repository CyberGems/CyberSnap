using CyberSnap.Models;

namespace CyberSnap.Helpers;

public enum AutoCopyKind
{
    Image,
    Ocr,
    Recording
}

/// <summary>
/// Global auto-copy master + per-kind exclusions.
/// Effective rule: AutoCopyToClipboard &amp;&amp; !Exclude(kind).
/// </summary>
public static class AutoCopyPreferences
{
    public const int SchemaVersion = 1;

    public static bool ShouldCopy(AppSettings settings, AutoCopyKind kind)
    {
        if (settings is null || !settings.AutoCopyToClipboard)
            return false;

        return kind switch
        {
            AutoCopyKind.Image => !settings.AutoCopyExcludeImages,
            AutoCopyKind.Ocr => !settings.AutoCopyExcludeOcr,
            AutoCopyKind.Recording => !settings.AutoCopyExcludeRecording,
            _ => false
        };
    }

    public static bool IsExcluded(AppSettings settings, AutoCopyKind kind) =>
        kind switch
        {
            AutoCopyKind.Image => settings.AutoCopyExcludeImages,
            AutoCopyKind.Ocr => settings.AutoCopyExcludeOcr,
            AutoCopyKind.Recording => settings.AutoCopyExcludeRecording,
            _ => false
        };

    public static void SetMaster(AppSettings settings, bool enabled)
    {
        settings.AutoCopyToClipboard = enabled;
        SyncLegacyAliases(settings);
    }

    public static void SetExcluded(AppSettings settings, AutoCopyKind kind, bool excluded)
    {
        switch (kind)
        {
            case AutoCopyKind.Image:
                settings.AutoCopyExcludeImages = excluded;
                break;
            case AutoCopyKind.Ocr:
                settings.AutoCopyExcludeOcr = excluded;
                break;
            case AutoCopyKind.Recording:
                settings.AutoCopyExcludeRecording = excluded;
                break;
        }

        SyncLegacyAliases(settings);
    }

    /// <summary>
    /// Enables or disables auto-copy for a single kind without turning the global master off.
    /// Enabling a kind turns the master on and clears that kind's exclusion.
    /// Disabling a kind only sets its exclusion.
    /// </summary>
    public static void SetKindEnabled(AppSettings settings, AutoCopyKind kind, bool enabled)
    {
        if (enabled)
        {
            settings.AutoCopyToClipboard = true;
            SetExcluded(settings, kind, excluded: false);
        }
        else
        {
            SetExcluded(settings, kind, excluded: true);
        }
    }

    /// <summary>
    /// One-time migration from AfterCapture.Copy + OcrAutoCopyToClipboard.
    /// Recording historically always copied, so the master is turned on and kinds that
    /// were previously off become exclusions.
    /// </summary>
    public static void MigrateIfNeeded(AppSettings settings)
    {
        if (settings.AutoCopySettingsSchemaVersion >= SchemaVersion)
        {
            SyncLegacyAliases(settings);
            return;
        }

        var action = AfterCapturePreferences.NormalizeAction(settings.AfterCapture);
        bool imageCopy = action is AfterCaptureAction.CopyToClipboard or AfterCaptureAction.PreviewAndCopy;
        bool ocrCopy = settings.OcrAutoCopyToClipboard;

        // Preserve prior behavior: recordings always copied to the clipboard.
        settings.AutoCopyToClipboard = true;
        settings.AutoCopyExcludeImages = !imageCopy;
        settings.AutoCopyExcludeOcr = !ocrCopy;
        settings.AutoCopyExcludeRecording = false;
        settings.AutoCopySettingsSchemaVersion = SchemaVersion;

        SyncLegacyAliases(settings);
        SyncAfterCaptureCopyBits(settings);
    }

    /// <summary>
    /// Keeps <see cref="AppSettings.OcrAutoCopyToClipboard"/> aligned for residual readers
    /// and menus that still treat OCR copy as a single bool.
    /// </summary>
    public static void SyncLegacyAliases(AppSettings settings)
    {
        settings.OcrAutoCopyToClipboard = ShouldCopy(settings, AutoCopyKind.Ocr);
    }

    /// <summary>
    /// Rewrites AfterCapture so its historical copy variants match the image auto-copy flag
    /// (except System viewer, which never encoded copy in the enum).
    /// </summary>
    public static void SyncAfterCaptureCopyBits(AppSettings settings)
    {
        var view = AfterCapturePreferences.FromSettingsDestinationOnly(settings);
        bool copy = ShouldCopy(settings, AutoCopyKind.Image);
        AfterCapturePreferences.ApplyDestinationAndLegacyCopy(view.WindowIndex, copy, settings);
    }
}
