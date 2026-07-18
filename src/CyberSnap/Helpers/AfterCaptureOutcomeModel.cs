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
/// OpenInSystemViewerAfterCapture, SaveToFile, AutoCopy*).
/// </summary>
public readonly record struct AfterCaptureOutcomeState(
    bool Save,
    AfterCaptureDestination Destination,
    bool SystemViewer,
    bool Clipboard)
{
    public bool RequiresSave =>
        Destination is AfterCaptureDestination.Editor || SystemViewer;

    public bool EffectiveSave => Save || RequiresSave;
}

public enum AfterCapturePillKind
{
    Save,
    Notification,
    Editor,
    SystemViewer,
    Clipboard
}

public static class AfterCaptureOutcomeModel
{
    public static AfterCapturePillKind[] AllPills { get; } =
    [
        AfterCapturePillKind.Save,
        AfterCapturePillKind.Notification,
        AfterCapturePillKind.Editor,
        AfterCapturePillKind.SystemViewer,
        AfterCapturePillKind.Clipboard
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

        bool requiresSave = destination is AfterCaptureDestination.Editor || systemViewer;
        bool save = settings.SaveToFile || requiresSave;
        bool clipboard = settings.AutoCopyToClipboard;

        return Normalize(new AfterCaptureOutcomeState(save, destination, systemViewer, clipboard));
    }

    public static void ApplyToSettings(AfterCaptureOutcomeState state, AppSettings settings)
    {
        state = Normalize(state);

        settings.SaveToFile = state.EffectiveSave;
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
    /// Enforces Editor exclusivity (clears SystemViewer), forced save for Editor/Viewer,
    /// and never-empty outcome (at least Save when nothing else is on).
    /// Notification + SystemViewer is allowed.
    /// </summary>
    public static AfterCaptureOutcomeState Normalize(AfterCaptureOutcomeState state)
    {
        var destination = state.Destination;
        bool systemViewer = state.SystemViewer;

        // Editor owns the post-capture surface: no stacked notification/viewer.
        if (destination == AfterCaptureDestination.Editor)
            systemViewer = false;

        bool requiresSave = destination is AfterCaptureDestination.Editor || systemViewer;
        bool save = state.Save || requiresSave;

        // Never empty: if nothing would happen, keep Save.
        if (!save
            && destination == AfterCaptureDestination.None
            && !systemViewer
            && !state.Clipboard)
        {
            save = true;
        }

        return new AfterCaptureOutcomeState(save, destination, systemViewer, state.Clipboard);
    }

    public static bool IsActive(AfterCaptureOutcomeState state, AfterCapturePillKind pill) =>
        pill switch
        {
            AfterCapturePillKind.Save => state.EffectiveSave,
            AfterCapturePillKind.Notification => state.Destination == AfterCaptureDestination.Notification,
            AfterCapturePillKind.Editor => state.Destination == AfterCaptureDestination.Editor,
            AfterCapturePillKind.SystemViewer => state.SystemViewer,
            AfterCapturePillKind.Clipboard => state.Clipboard,
            _ => false
        };

    public static bool CanRemove(AfterCaptureOutcomeState state, AfterCapturePillKind pill)
    {
        if (!IsActive(state, pill))
            return false;

        // Save is forced while Editor or System viewer needs a file on disk.
        if (pill == AfterCapturePillKind.Save && state.RequiresSave)
            return false;

        // If Normalize would put this pill back, offering × is a no-op.
        var trial = ApplyRemove(state, pill);
        var normalized = Normalize(trial);
        return !IsActive(normalized, pill);
    }

    public static AfterCaptureOutcomeState WithPillAdded(AfterCaptureOutcomeState state, AfterCapturePillKind pill)
    {
        state = pill switch
        {
            AfterCapturePillKind.Save => state with { Save = true },
            AfterCapturePillKind.Notification => state with
            {
                Destination = AfterCaptureDestination.Notification
                // SystemViewer kept — Notification + Viewer is allowed.
            },
            AfterCapturePillKind.Editor => state with
            {
                Destination = AfterCaptureDestination.Editor,
                SystemViewer = false,
                Save = true
            },
            AfterCapturePillKind.SystemViewer => state with
            {
                SystemViewer = true,
                Save = true,
                // Adding Viewer while Editor is active replaces Editor with Notification-capable
                // surface only if Destination was Editor — drop Editor so Viewer can stack
                // with Notification or stand alone.
                Destination = state.Destination == AfterCaptureDestination.Editor
                    ? AfterCaptureDestination.None
                    : state.Destination
            },
            AfterCapturePillKind.Clipboard => state with { Clipboard = true },
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
            AfterCapturePillKind.Notification
                when state.Destination == AfterCaptureDestination.Notification
                => state with { Destination = AfterCaptureDestination.None },
            AfterCapturePillKind.Editor
                when state.Destination == AfterCaptureDestination.Editor
                => state with { Destination = AfterCaptureDestination.None },
            AfterCapturePillKind.SystemViewer => state with { SystemViewer = false },
            AfterCapturePillKind.Clipboard => state with { Clipboard = false },
            _ => state
        };

    public static string LabelKey(AfterCapturePillKind pill) => pill switch
    {
        AfterCapturePillKind.Save => "Outcome step: save file",
        AfterCapturePillKind.Notification => "Outcome step: show notification",
        AfterCapturePillKind.Editor => "Outcome step: open editor",
        AfterCapturePillKind.SystemViewer => "Outcome step: open in system viewer",
        AfterCapturePillKind.Clipboard => "Auto-copy",
        _ => pill.ToString()
    };

    public static string TooltipKey(AfterCapturePillKind pill) => pill switch
    {
        AfterCapturePillKind.Save => "Write the capture to the configured save folder.",
        AfterCapturePillKind.Notification => "Show the post-capture notification window.",
        AfterCapturePillKind.Editor => "Open the capture in the annotation editor.",
        AfterCapturePillKind.SystemViewer =>
            "Open the saved file in the system default viewer. Can be combined with the notification.",
        AfterCapturePillKind.Clipboard => "Copy captures, OCR text, and recordings to the clipboard when they finish.",
        _ => ""
    };

    public static string ForcedSaveTooltipKey =>
        "Save is required when opening the editor or system viewer.";

    /// <summary>Shown on a locked Save chip when it is the only remaining outcome step.</summary>
    public static string RequiredOutcomeTooltipKey =>
        "Keep at least one after-capture step.";
}
