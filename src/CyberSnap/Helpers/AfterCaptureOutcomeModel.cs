using CyberSnap.Models;

namespace CyberSnap.Helpers;

/// <summary>
/// Primary post-capture UI destination. At most one of Notification/Editor.
/// System viewer is a separate flag and can stack with Notification.
/// </summary>
public enum AfterCaptureDestination
{
    None = 0,
    Notification = 1,
    Editor = 2
}

/// <summary>
/// Composable after-capture outcome used by the minipill editor.
/// Maps onto existing settings (AfterCapture, OpenEditorAfterCapture,
/// OpenInSystemViewerAfterCapture, SaveToFile, AutoCopy*, AutoShareAfterCapture).
/// </summary>
public readonly record struct AfterCaptureOutcomeState(
    bool Save,
    AfterCaptureDestination Destination,
    bool SystemViewer,
    bool Clipboard,
    bool Preview,
    bool Share = false)
{
    // Saving to disk is no longer forced for any destination: Editor works from
    // the in-memory bitmap, and System viewer gets a temp file when SaveToFile
    // is off. Kept (always false) as an explicit, documented invariant so the
    // outcome never silently regresses to requiring a save.
    public bool RequiresSave => false;

    public bool EffectiveSave => Save;
}

public enum AfterCapturePillKind
{
    Save,
    Preview,
    Notification,
    Editor,
    SystemViewer,
    Clipboard,
    Share
}

/// <summary>Whether an active pill already ran or still waits for Done/Continue.</summary>
public enum AfterCapturePillTiming
{
    /// <summary>Already applied when the preview window opened (e.g. auto-copy).</summary>
    Done,
    /// <summary>Runs when the user confirms the preview (save, editor, viewer, share).</summary>
    Pending
}

public static class AfterCaptureOutcomeModel
{
    public static AfterCapturePillKind[] AllPills { get; } =
    [
        AfterCapturePillKind.Save,
        AfterCapturePillKind.Preview,
        AfterCapturePillKind.Notification,
        AfterCapturePillKind.Editor,
        AfterCapturePillKind.SystemViewer,
        AfterCapturePillKind.Clipboard,
        AfterCapturePillKind.Share
    ];

    public static AfterCaptureOutcomeState FromSettings(AppSettings settings)
    {
        var destOnly = AfterCapturePreferences.FromSettingsDestinationOnly(settings);
        var destination = destOnly.WindowIndex switch
        {
            0 => AfterCaptureDestination.Notification,
            1 => AfterCaptureDestination.Editor,
            _ => AfterCaptureDestination.None
        };

        // Prefer the dedicated flag; still honor unmigrated enum values.
        bool systemViewer = settings.OpenInSystemViewerAfterCapture
            || settings.AfterCapture == AfterCaptureAction.OpenInSystemViewer;

        // Editor remains exclusive vs notification in the UI model.
        if (destination == AfterCaptureDestination.Editor)
            systemViewer = false;

        bool save = settings.SaveToFile;
        bool clipboard = settings.AutoCopyToClipboard;
        bool preview = settings.ShowCapturePreview;
        bool share = settings.AutoShareAfterCapture;

        return Normalize(new AfterCaptureOutcomeState(save, destination, systemViewer, clipboard, preview, share));
    }

    public static void ApplyToSettings(AfterCaptureOutcomeState state, AppSettings settings)
    {
        state = Normalize(state);

        settings.SaveToFile = state.EffectiveSave;
        settings.ShowCapturePreview = state.Preview;
        settings.AutoShareAfterCapture = state.Share;
        settings.OpenInSystemViewerAfterCapture = state.SystemViewer
            && state.Destination != AfterCaptureDestination.Editor;

        // Keep chip ↔ widget in lockstep: both drive the global Auto-copy master.
        AutoCopyPreferences.SetMaster(settings, state.Clipboard);

        int windowIndex = state.Destination switch
        {
            AfterCaptureDestination.Notification => 0,
            AfterCaptureDestination.Editor => 1,
            _ => 2 // save only / no preview window
        };

        AfterCapturePreferences.ApplyDestinationAndLegacyCopy(
            windowIndex,
            AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Image),
            settings);
    }

    /// <summary>
    /// Enforces Editor exclusivity (clears SystemViewer) and a never-empty
    /// outcome: when nothing else is active, Preview is enabled instead of
    /// forcing a save. Saving to disk is never forced by a destination.
    /// Notification + SystemViewer is allowed.
    /// </summary>
    public static AfterCaptureOutcomeState Normalize(AfterCaptureOutcomeState state)
    {
        var destination = state.Destination;
        bool systemViewer = state.SystemViewer;

        // Editor owns the post-capture surface: no stacked notification/viewer.
        if (destination == AfterCaptureDestination.Editor)
            systemViewer = false;

        bool save = state.Save;

        // Never empty: when nothing else is active, fall back to Preview
        // instead of forcing a save to disk.
        if (!save
            && destination == AfterCaptureDestination.None
            && !systemViewer
            && !state.Clipboard
            && !state.Preview
            && !state.Share)
        {
            return new AfterCaptureOutcomeState(save, destination, systemViewer, state.Clipboard, Preview: true, Share: false);
        }

        return new AfterCaptureOutcomeState(save, destination, systemViewer, state.Clipboard, state.Preview, state.Share);
    }

    public static bool IsActive(AfterCaptureOutcomeState state, AfterCapturePillKind pill) =>
        pill switch
        {
            AfterCapturePillKind.Save => state.EffectiveSave,
            AfterCapturePillKind.Preview => state.Preview,
            AfterCapturePillKind.Notification => state.Destination == AfterCaptureDestination.Notification,
            AfterCapturePillKind.Editor => state.Destination == AfterCaptureDestination.Editor,
            AfterCapturePillKind.SystemViewer => state.SystemViewer,
            AfterCapturePillKind.Clipboard => state.Clipboard,
            AfterCapturePillKind.Share => state.Share,
            _ => false
        };

    /// <summary>
    /// Timing for pills shown inside the capture preview dialog.
    /// Clipboard (and Save when not asking for a file name) run before/as the dialog opens;
    /// Editor / Viewer / Share wait for confirm.
    /// </summary>
    public static AfterCapturePillTiming GetPreviewTiming(AfterCapturePillKind pill, AppSettings? settings = null)
    {
        if (pill == AfterCapturePillKind.Clipboard)
            return AfterCapturePillTiming.Done;

        // Save is immediate when the path is known up front (no Save-As prompt).
        if (pill == AfterCapturePillKind.Save
            && settings is not null
            && settings.SaveToFile
            && !settings.AskForFileNameOnSave)
            return AfterCapturePillTiming.Done;

        return AfterCapturePillTiming.Pending;
    }

    public static bool CanRemove(AfterCaptureOutcomeState state, AfterCapturePillKind pill)
    {
        if (!IsActive(state, pill))
            return false;

        // No pill is locked. Only suppress a no-op remove that Normalize
        // (e.g. empty-fallback to Preview) would immediately undo.
        var trial = ApplyRemove(state, pill);
        var normalized = Normalize(trial);
        return !IsActive(normalized, pill);
    }

    public static AfterCaptureOutcomeState WithPillAdded(AfterCaptureOutcomeState state, AfterCapturePillKind pill)
    {
        state = pill switch
        {
            AfterCapturePillKind.Save => state with { Save = true },
            AfterCapturePillKind.Preview => state with { Preview = true },
            AfterCapturePillKind.Notification => state with
            {
                Destination = AfterCaptureDestination.Notification
                // SystemViewer kept — Notification + Viewer is allowed.
            },
            AfterCapturePillKind.Editor => state with
            {
                Destination = AfterCaptureDestination.Editor,
                SystemViewer = false
            },
            AfterCapturePillKind.SystemViewer => state with
            {
                SystemViewer = true,
                // Adding Viewer while Editor is active replaces Editor with Notification-capable
                // surface only if Destination was Editor — drop Editor so Viewer can stack
                // with Notification or stand alone.
                Destination = state.Destination == AfterCaptureDestination.Editor
                    ? AfterCaptureDestination.None
                    : state.Destination
            },
            AfterCapturePillKind.Clipboard => state with { Clipboard = true },
            AfterCapturePillKind.Share => state with { Share = true },
            _ => state
        };
        return Normalize(state);
    }

    public static AfterCaptureOutcomeState WithPillRemoved(AfterCaptureOutcomeState state, AfterCapturePillKind pill)
    {
        if (!CanRemove(state, pill))
            return state;

        return Normalize(ApplyRemove(state, pill));
    }

    /// <summary>Raw pill clear without CanRemove / Normalize (used by CanRemove prediction).</summary>
    private static AfterCaptureOutcomeState ApplyRemove(AfterCaptureOutcomeState state, AfterCapturePillKind pill) =>
        pill switch
        {
            AfterCapturePillKind.Save => state with { Save = false },
            AfterCapturePillKind.Preview => state with { Preview = false },
            AfterCapturePillKind.Notification
                when state.Destination == AfterCaptureDestination.Notification
                => state with { Destination = AfterCaptureDestination.None },
            AfterCapturePillKind.Editor
                when state.Destination == AfterCaptureDestination.Editor
                => state with { Destination = AfterCaptureDestination.None },
            AfterCapturePillKind.SystemViewer => state with { SystemViewer = false },
            AfterCapturePillKind.Clipboard => state with { Clipboard = false },
            AfterCapturePillKind.Share => state with { Share = false },
            _ => state
        };

    public static string LabelKey(AfterCapturePillKind pill) => pill switch
    {
        AfterCapturePillKind.Save => "Outcome step: save file",
        AfterCapturePillKind.Preview => "Outcome step: preview",
        AfterCapturePillKind.Notification => "Outcome step: show notification",
        AfterCapturePillKind.Editor => "Outcome step: open editor",
        AfterCapturePillKind.SystemViewer => "Outcome step: open in system viewer",
        AfterCapturePillKind.Clipboard => "Auto-copy",
        AfterCapturePillKind.Share => "Outcome step: share",
        _ => pill.ToString()
    };

    public static string TooltipKey(AfterCapturePillKind pill) => pill switch
    {
        AfterCapturePillKind.Save => "Write the capture to the configured save folder.",
        AfterCapturePillKind.Preview => "Show the capture preview window after selection.",
        AfterCapturePillKind.Notification =>
            "Show a compact status toast after capture (or status chips when Preview is on).",
        AfterCapturePillKind.Editor => "Open the capture in the annotation editor.",
        AfterCapturePillKind.SystemViewer =>
            "Open the saved file in the system default viewer. Can be combined with the notification.",
        AfterCapturePillKind.Clipboard => "Copy the image capture to the clipboard when it finishes.",
        AfterCapturePillKind.Share => "Open the share flow after capture (off by default).",
        _ => ""
    };

}
