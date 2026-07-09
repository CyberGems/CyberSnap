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

    public static string GetSummaryLocalizationKey(AfterCaptureViewPreference preference, bool wizardLabels = false) =>
        preference switch
        {
            (0, true) when wizardLabels  => "Wizard outcome: show notification and copy to clipboard.",
            (0, false) when wizardLabels => "Wizard outcome: show notification only.",
            (0, true)  => "Current outcome: open preview and copy to clipboard.",
            (0, false) => "Current outcome: open preview only.",
            (1, true)  => "Current outcome: open editor and copy to clipboard.",
            (1, false) => "Current outcome: open editor only.",
            (2, true)  => "Current outcome: save the file and copy to clipboard.",
            (3, _)     => "Current outcome: open in system viewer.",
            _          => "Current outcome: save the file only."
        };
}
