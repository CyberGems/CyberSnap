using CyberSnap.Models;

namespace CyberSnap.Helpers;

public readonly record struct AfterCaptureViewPreference(int WindowIndex, bool Copy, bool OpenSystemViewer = false);

public static class AfterCapturePreferences
{
    public static AfterCaptureAction NormalizeAction(AfterCaptureAction action) =>
        Enum.IsDefined(typeof(AfterCaptureAction), action)
            ? action
            : AfterCaptureAction.PreviewAndCopy;

    // WindowIndex mapping (primary UI destination — not system viewer):
    //   0 = Notification
    //   1 = Editor
    //   2 = Save only / no preview window
    // System viewer is OpenInSystemViewerAfterCapture (can stack with Notification).
    public static AfterCaptureViewPreference FromSettings(AppSettings settings)
    {
        var destination = FromSettingsDestinationOnly(settings);
        bool copy = AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Image);
        bool viewer = settings.OpenInSystemViewerAfterCapture
            || settings.AfterCapture == AfterCaptureAction.OpenInSystemViewer;
        return new AfterCaptureViewPreference(destination.WindowIndex, copy, viewer);
    }

    /// <summary>
    /// Destination only (window index / open-editor), ignoring auto-copy and system viewer.
    /// </summary>
    public static AfterCaptureViewPreference FromSettingsDestinationOnly(AppSettings settings)
    {
        var action = NormalizeAction(settings.AfterCapture);
        var openEditor = settings.OpenEditorAfterCapture;

        // OpenInSystemViewer is a legacy exclusive mode; after migration it is a flag.
        // Treat unmigrated enum as "no preview destination" (index 2).
        int windowIndex = action switch
        {
            AfterCaptureAction.OpenInSystemViewer => 2,
            AfterCaptureAction.CopyToClipboard => 2,
            AfterCaptureAction.None => 2,
            _ => openEditor ? 1 : 0
        };

        return new AfterCaptureViewPreference(windowIndex, Copy: false, OpenSystemViewer: false);
    }

    /// <summary>
    /// One-time: move exclusive AfterCapture.OpenInSystemViewer into the stackable flag.
    /// Preserves old behavior (viewer + no image preview).
    /// </summary>
    public static void MigrateSystemViewerFlagIfNeeded(AppSettings settings)
    {
        if (settings.AfterCapture != AfterCaptureAction.OpenInSystemViewer)
            return;

        settings.OpenInSystemViewerAfterCapture = true;
        settings.OpenEditorAfterCapture = false;
        // None = no image-preview notification (matches previous exclusive-viewer path).
        settings.AfterCapture = AfterCaptureAction.None;
    }

    public static void ApplyToSettings(AfterCaptureViewPreference preference, AppSettings settings)
    {
        ApplyDestinationAndLegacyCopy(preference.WindowIndex, preference.Copy, settings);
        settings.OpenInSystemViewerAfterCapture = preference.OpenSystemViewer
            && preference.WindowIndex != 1; // never stack viewer with editor
        AutoCopyPreferences.SetKindEnabled(settings, AutoCopyKind.Image, preference.Copy);
    }

    /// <summary>
    /// Applies only the after-capture destination. Image auto-copy and system viewer are left unchanged.
    /// </summary>
    public static void ApplyDestinationToSettings(int windowIndex, AppSettings settings)
    {
        bool copy = AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Image);
        ApplyDestinationAndLegacyCopy(windowIndex, copy, settings);
    }

    /// <summary>
    /// Writes AfterCapture + OpenEditorAfterCapture from a window index and copy flag.
    /// Does not mutate AutoCopy* or OpenInSystemViewerAfterCapture.
    /// Never writes the legacy OpenInSystemViewer enum value.
    /// </summary>
    public static void ApplyDestinationAndLegacyCopy(int windowIndex, bool copy, AppSettings settings)
    {
        (AfterCaptureAction action, bool openEditor) = windowIndex switch
        {
            0 => (copy ? AfterCaptureAction.PreviewAndCopy : AfterCaptureAction.PreviewOnly, false),
            1 => (copy ? AfterCaptureAction.PreviewAndCopy : AfterCaptureAction.PreviewOnly, true),
            // Index 3 was legacy exclusive system viewer — map to save-only destination.
            3 => (copy ? AfterCaptureAction.CopyToClipboard : AfterCaptureAction.None, false),
            _ => (copy ? AfterCaptureAction.CopyToClipboard : AfterCaptureAction.None, false)
        };

        settings.AfterCapture = action;
        settings.OpenEditorAfterCapture = openEditor;
    }

    /// <summary>
    /// Builds the outcome summary string by composing localized step labels
    /// separated by ᐧ. Pass LocalizationService.Translate (or a test stub).
    /// <paramref name="saveToFile"/> reflects the current SaveToFile toggle state.
    /// </summary>
    public static string BuildSummary(
        AfterCaptureViewPreference preference,
        bool saveToFile,
        Func<string, string> translate)
    {
        const string sep = " ᐧ ";
        var parts = new List<string>();

        bool willSave = saveToFile
            || preference.WindowIndex == 1
            || preference.OpenSystemViewer
            || preference.WindowIndex == 3;
        if (willSave)
            parts.Add(translate("Outcome step: save file"));

        string? actionKey = preference.WindowIndex switch
        {
            0 => "Outcome step: show notification",
            1 => "Outcome step: open editor",
            _ => null
        };
        if (actionKey is not null)
            parts.Add(translate(actionKey));

        if (preference.OpenSystemViewer || preference.WindowIndex == 3)
            parts.Add(translate("Outcome step: open in system viewer"));

        if (preference.Copy)
            parts.Add(translate("Outcome step: copy to clipboard"));

        string prefix = translate("Outcome prefix");
        return prefix + string.Join(sep, parts);
    }
}
