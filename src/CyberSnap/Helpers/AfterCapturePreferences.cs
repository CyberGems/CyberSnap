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
        var action = NormalizeAction(settings.AfterCapture);
        var openEditor = settings.OpenEditorAfterCapture;

        return action switch
        {
            AfterCaptureAction.OpenInSystemViewer => new AfterCaptureViewPreference(3, false),
            AfterCaptureAction.CopyToClipboard    => new AfterCaptureViewPreference(2, true),
            AfterCaptureAction.None               => new AfterCaptureViewPreference(2, false),
            _ => openEditor
                ? new AfterCaptureViewPreference(1, action == AfterCaptureAction.PreviewAndCopy)
                : new AfterCaptureViewPreference(0, action == AfterCaptureAction.PreviewAndCopy)
        };
    }

    public static void ApplyToSettings(AfterCaptureViewPreference preference, AppSettings settings)
    {
        (AfterCaptureAction action, bool openEditor) = preference.WindowIndex switch
        {
            0 => preference.Copy
                ? (AfterCaptureAction.PreviewAndCopy, false)
                : (AfterCaptureAction.PreviewOnly, false),
            1 => preference.Copy
                ? (AfterCaptureAction.PreviewAndCopy, true)
                : (AfterCaptureAction.PreviewOnly, true),
            3 => (AfterCaptureAction.OpenInSystemViewer, false),
            _ => preference.Copy
                ? (AfterCaptureAction.CopyToClipboard, false)
                : (AfterCaptureAction.None, false)
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

        // Step 3: copy to clipboard (optional modifier)
        if (preference.Copy)
            parts.Add(translate("Outcome step: copy to clipboard"));

        // Prefix
        string prefix = translate("Outcome prefix");
        return prefix + string.Join(sep, parts);
    }
}
