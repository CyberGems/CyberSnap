using CyberSnap.Models;

namespace CyberSnap.Helpers;

/// <summary>
/// UI destination for a finished image capture. At most one may be active
/// (none = save-only path).
/// </summary>
public enum AfterCaptureDestination
{
    None = 0,
    Notification = 1,
    Editor = 2,
    SystemViewer = 3
}

/// <summary>
/// Composable after-capture outcome used by the minipill editor.
/// Maps onto existing settings (AfterCapture, OpenEditorAfterCapture, SaveToFile, AutoCopy*).
/// </summary>
public readonly record struct AfterCaptureOutcomeState(
    bool Save,
    AfterCaptureDestination Destination,
    bool Clipboard)
{
    public bool RequiresSave =>
        Destination is AfterCaptureDestination.Editor or AfterCaptureDestination.SystemViewer;

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
            3 => AfterCaptureDestination.SystemViewer,
            _ => AfterCaptureDestination.None
        };

        bool requiresSave = destination is AfterCaptureDestination.Editor
            or AfterCaptureDestination.SystemViewer;
        bool save = settings.SaveToFile || requiresSave;
        // Same master flag as the widget Auto-copy toggle (not image-exclude alone).
        bool clipboard = settings.AutoCopyToClipboard;

        return Normalize(new AfterCaptureOutcomeState(save, destination, clipboard));
    }

    public static void ApplyToSettings(AfterCaptureOutcomeState state, AppSettings settings)
    {
        state = Normalize(state);

        settings.SaveToFile = state.EffectiveSave;

        // Keep chip ↔ widget in lockstep: both drive the global Auto-copy master.
        AutoCopyPreferences.SetMaster(settings, state.Clipboard);

        int windowIndex = state.Destination switch
        {
            AfterCaptureDestination.Notification => 0,
            AfterCaptureDestination.Editor => 1,
            AfterCaptureDestination.SystemViewer => 3,
            _ => 2 // save only
        };

        AfterCapturePreferences.ApplyDestinationAndLegacyCopy(
            windowIndex,
            AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Image),
            settings);
    }

    /// <summary>
    /// Enforces exclusive destination, forced save for Editor/System viewer,
    /// and never-empty outcome (at least Save or Notification).
    /// </summary>
    public static AfterCaptureOutcomeState Normalize(AfterCaptureOutcomeState state)
    {
        bool requiresSave = state.Destination is AfterCaptureDestination.Editor
            or AfterCaptureDestination.SystemViewer;
        bool save = state.Save || requiresSave;

        // Never empty: if nothing would happen, keep Save.
        if (!save
            && state.Destination == AfterCaptureDestination.None
            && !state.Clipboard)
        {
            save = true;
        }

        return new AfterCaptureOutcomeState(save, state.Destination, state.Clipboard);
    }

    public static bool IsActive(AfterCaptureOutcomeState state, AfterCapturePillKind pill) =>
        pill switch
        {
            AfterCapturePillKind.Save => state.EffectiveSave,
            AfterCapturePillKind.Notification => state.Destination == AfterCaptureDestination.Notification,
            AfterCapturePillKind.Editor => state.Destination == AfterCaptureDestination.Editor,
            AfterCapturePillKind.SystemViewer => state.Destination == AfterCaptureDestination.SystemViewer,
            AfterCapturePillKind.Clipboard => state.Clipboard,
            _ => false
        };

    public static bool CanRemove(AfterCaptureOutcomeState state, AfterCapturePillKind pill)
    {
        if (!IsActive(state, pill))
            return false;

        // Save is forced while Editor / System viewer is selected.
        if (pill == AfterCapturePillKind.Save && state.RequiresSave)
            return false;

        return true;
    }

    public static AfterCaptureOutcomeState WithPillAdded(AfterCaptureOutcomeState state, AfterCapturePillKind pill)
    {
        state = pill switch
        {
            AfterCapturePillKind.Save => state with { Save = true },
            AfterCapturePillKind.Notification => state with { Destination = AfterCaptureDestination.Notification },
            AfterCapturePillKind.Editor => state with { Destination = AfterCaptureDestination.Editor, Save = true },
            AfterCapturePillKind.SystemViewer => state with { Destination = AfterCaptureDestination.SystemViewer, Save = true },
            AfterCapturePillKind.Clipboard => state with { Clipboard = true },
            _ => state
        };
        return Normalize(state);
    }

    public static AfterCaptureOutcomeState WithPillRemoved(AfterCaptureOutcomeState state, AfterCapturePillKind pill)
    {
        if (!CanRemove(state, pill))
            return state;

        state = pill switch
        {
            AfterCapturePillKind.Save => state with { Save = false },
            AfterCapturePillKind.Notification
                when state.Destination == AfterCaptureDestination.Notification
                => state with { Destination = AfterCaptureDestination.None },
            AfterCapturePillKind.Editor
                when state.Destination == AfterCaptureDestination.Editor
                => state with { Destination = AfterCaptureDestination.None },
            AfterCapturePillKind.SystemViewer
                when state.Destination == AfterCaptureDestination.SystemViewer
                => state with { Destination = AfterCaptureDestination.None },
            AfterCapturePillKind.Clipboard => state with { Clipboard = false },
            _ => state
        };
        return Normalize(state);
    }

    public static string LabelKey(AfterCapturePillKind pill) => pill switch
    {
        AfterCapturePillKind.Save => "Outcome step: save file",
        AfterCapturePillKind.Notification => "Outcome step: show notification",
        AfterCapturePillKind.Editor => "Outcome step: open editor",
        AfterCapturePillKind.SystemViewer => "Outcome step: open in system viewer",
        // Same label as the widget toggle.
        AfterCapturePillKind.Clipboard => "Auto-copy",
        _ => pill.ToString()
    };

    public static string TooltipKey(AfterCapturePillKind pill) => pill switch
    {
        AfterCapturePillKind.Save => "Write the capture to the configured save folder.",
        AfterCapturePillKind.Notification => "Show the post-capture notification window.",
        AfterCapturePillKind.Editor => "Open the capture in the annotation editor.",
        AfterCapturePillKind.SystemViewer => "Open the saved file in the system default viewer.",
        // Same help text as the widget Auto-copy toggle.
        AfterCapturePillKind.Clipboard => "Copy captures, OCR text, and recordings to the clipboard when they finish.",
        _ => ""
    };

    public static string ForcedSaveTooltipKey =>
        "Save is required when opening the editor or system viewer.";
}
