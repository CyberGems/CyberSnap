using CyberSnap.Models;

namespace CyberSnap.Helpers;

public readonly record struct AfterCaptureViewPreference(int WindowIndex, bool Copy);

public static class AfterCapturePreferences
{
    public static AfterCaptureAction NormalizeAction(AfterCaptureAction action) =>
        Enum.IsDefined(typeof(AfterCaptureAction), action)
            ? action
            : AfterCaptureAction.PreviewAndCopy;

    // WindowIndex mapping:
    //   0 = Notification
    //   1 = Editor
    //   2 = Save only
    //   3 = System viewer
    public static AfterCaptureViewPreference FromSettings(AppSettings settings)
    {
        var destination = FromSettingsDestinationOnly(settings);
        bool copy = AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Image);
        return new AfterCaptureViewPreference(destination.WindowIndex, copy);
    }

    /// <summary>
    /// Destination only (window index / open-editor), ignoring the global auto-copy flag.
    /// </summary>
    public static AfterCaptureViewPreference FromSettingsDestinationOnly(AppSettings settings)
    {
        var action = NormalizeAction(settings.AfterCapture);
        var openEditor = settings.OpenEditorAfterCapture;

        int windowIndex = action switch
        {
            AfterCaptureAction.OpenInSystemViewer => 3,
            AfterCaptureAction.CopyToClipboard => 2,
            AfterCaptureAction.None => 2,
            _ => openEditor ? 1 : 0
        };

        return new AfterCaptureViewPreference(windowIndex, Copy: false);
    }

    public static void ApplyToSettings(AfterCaptureViewPreference preference, AppSettings settings)
    {
        ApplyDestinationAndLegacyCopy(preference.WindowIndex, preference.Copy, settings);
        AutoCopyPreferences.SetKindEnabled(settings, AutoCopyKind.Image, preference.Copy);
    }

    /// <summary>
    /// Applies only the after-capture destination. Image auto-copy is left unchanged.
    /// </summary>
    public static void ApplyDestinationToSettings(int windowIndex, AppSettings settings)
    {
        bool copy = AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Image);
        ApplyDestinationAndLegacyCopy(windowIndex, copy, settings);
    }

    /// <summary>
    /// Writes AfterCapture + OpenEditorAfterCapture from a window index and copy flag.
    /// Does not mutate AutoCopy* settings.
    /// </summary>
    public static void ApplyDestinationAndLegacyCopy(int windowIndex, bool copy, AppSettings settings)
    {
        (AfterCaptureAction action, bool openEditor) = windowIndex switch
        {
            0 => (copy ? AfterCaptureAction.PreviewAndCopy : AfterCaptureAction.PreviewOnly, false),
            1 => (copy ? AfterCaptureAction.PreviewAndCopy : AfterCaptureAction.PreviewOnly, true),
            3 => (AfterCaptureAction.OpenInSystemViewer, false),
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

        // Step 1: save to file (shown whenever the file will actually be written)
        bool willSave = saveToFile || preference.WindowIndex >= 1;
        if (willSave)
            parts.Add(translate("Outcome step: save file"));

        // Step 2: main action
        string? actionKey = preference.WindowIndex switch
        {
            0 => "Outcome step: show notification",
            1 => "Outcome step: open editor",
            3 => "Outcome step: open in system viewer",
            _ => null   // index 2 = save only — no extra action label
        };
        if (actionKey is not null)
            parts.Add(translate(actionKey));

        // Step 3: copy to clipboard (optional modifier from global auto-copy)
        if (preference.Copy)
            parts.Add(translate("Outcome step: copy to clipboard"));

        // Prefix
        string prefix = translate("Outcome prefix");
        return prefix + string.Join(sep, parts);
    }
}
