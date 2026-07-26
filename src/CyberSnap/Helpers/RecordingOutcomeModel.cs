using CyberSnap.Models;

namespace CyberSnap.Helpers;

/// <summary>Video or GIF post-recording outcome editor scope.</summary>
public enum RecordingOutcomeKind
{
    Video,
    Gif
}

public enum RecordingOutcomePillKind
{
    Save,
    Clipboard,
    Trimmer
}

/// <summary>
/// Composable post-recording outcome for MP4 / GIF.
/// Save is per-media (<see cref="AppSettings.SaveVideoToFile"/> / <see cref="AppSettings.SaveGifToFile"/>);
/// Clipboard uses per-kind auto-copy; Trimmer is media-specific.
/// </summary>
public readonly record struct RecordingOutcomeState(
    bool Save,
    bool Clipboard,
    bool OpenTrimmer);

public static class RecordingOutcomeModel
{
    public static RecordingOutcomePillKind[] AllPills { get; } =
    [
        RecordingOutcomePillKind.Save,
        RecordingOutcomePillKind.Clipboard,
        RecordingOutcomePillKind.Trimmer
    ];

    public static RecordingOutcomeState FromSettings(AppSettings settings, RecordingOutcomeKind kind)
    {
        bool save = kind == RecordingOutcomeKind.Video
            ? settings.SaveVideoToFile
            : settings.SaveGifToFile;

        bool clipboard = kind == RecordingOutcomeKind.Video
            ? AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Video)
            : AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Gif);

        bool trimmer = kind == RecordingOutcomeKind.Video
            ? settings.OpenVideoTrimmerAfterCapture
            : settings.OpenGifTrimmerAfterCapture;

        return new RecordingOutcomeState(save, clipboard, trimmer);
    }

    public static void ApplyToSettings(
        RecordingOutcomeState state,
        AppSettings settings,
        RecordingOutcomeKind kind)
    {
        if (kind == RecordingOutcomeKind.Video)
            settings.SaveVideoToFile = state.Save;
        else
            settings.SaveGifToFile = state.Save;

        var copyKind = kind == RecordingOutcomeKind.Video ? AutoCopyKind.Video : AutoCopyKind.Gif;
        AutoCopyPreferences.SetKindEnabled(settings, copyKind, state.Clipboard);

        if (kind == RecordingOutcomeKind.Video)
            settings.OpenVideoTrimmerAfterCapture = state.OpenTrimmer;
        else
            settings.OpenGifTrimmerAfterCapture = state.OpenTrimmer;
    }

    /// <summary>
    /// One-time: seed per-media save flags from the legacy shared <see cref="AppSettings.SaveToFile"/>.
    /// </summary>
    public static void MigrateSaveMediaIfNeeded(AppSettings settings)
    {
        if (settings.SaveMediaSettingsSchemaVersion >= 1)
            return;

        settings.SaveVideoToFile = settings.SaveToFile;
        settings.SaveGifToFile = settings.SaveToFile;
        settings.SaveMediaSettingsSchemaVersion = 1;
    }

    public static bool IsActive(RecordingOutcomeState state, RecordingOutcomePillKind pill) =>
        pill switch
        {
            RecordingOutcomePillKind.Save => state.Save,
            RecordingOutcomePillKind.Clipboard => state.Clipboard,
            RecordingOutcomePillKind.Trimmer => state.OpenTrimmer,
            _ => false
        };

    public static bool CanRemove(RecordingOutcomeState state, RecordingOutcomePillKind pill) =>
        IsActive(state, pill);

    public static RecordingOutcomeState WithPillAdded(RecordingOutcomeState state, RecordingOutcomePillKind pill) =>
        pill switch
        {
            RecordingOutcomePillKind.Save => state with { Save = true },
            RecordingOutcomePillKind.Clipboard => state with { Clipboard = true },
            RecordingOutcomePillKind.Trimmer => state with { OpenTrimmer = true },
            _ => state
        };

    public static RecordingOutcomeState WithPillRemoved(RecordingOutcomeState state, RecordingOutcomePillKind pill)
    {
        if (!CanRemove(state, pill))
            return state;

        return pill switch
        {
            RecordingOutcomePillKind.Save => state with { Save = false },
            RecordingOutcomePillKind.Clipboard => state with { Clipboard = false },
            RecordingOutcomePillKind.Trimmer => state with { OpenTrimmer = false },
            _ => state
        };
    }

    public static string LabelKey(RecordingOutcomePillKind pill) => pill switch
    {
        RecordingOutcomePillKind.Save => "Outcome step: save file",
        RecordingOutcomePillKind.Clipboard => "Auto-copy",
        RecordingOutcomePillKind.Trimmer => "Outcome step: open trimmer",
        _ => pill.ToString()
    };

    public static string TooltipKey(RecordingOutcomePillKind pill, RecordingOutcomeKind kind) =>
        pill switch
        {
            RecordingOutcomePillKind.Save => "Write the recording to the configured save folder.",
            RecordingOutcomePillKind.Clipboard => kind == RecordingOutcomeKind.Video
                ? "Copy the finished MP4 to the clipboard."
                : "Copy the finished GIF to the clipboard.",
            RecordingOutcomePillKind.Trimmer => kind == RecordingOutcomeKind.Video
                ? "Open the video trimmer when an MP4 recording finishes."
                : "Open the trimmer when a GIF recording finishes.",
            _ => ""
        };
}
