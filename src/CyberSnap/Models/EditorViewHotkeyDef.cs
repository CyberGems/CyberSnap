namespace CyberSnap.Models;

/// <summary>Editor view/zoom shortcuts in Settings → Hotkeys (Annotations Editor → View).</summary>
public static class EditorViewHotkeyDef
{
    public static readonly (string Id, string Label, char Icon)[] Shortcuts =
    {
        ("editorZoomOut",   "Zoom out",    '\uE711'),
        ("editorZoomIn",    "Zoom in",     '\uE710'),
        ("editorZoomFit",   "Zoom to fit", '\uE740'),
        ("editorZoomReset", "Reset zoom",  '\uE72C'),
    };
}
