namespace CyberSnap.Models;

/// <summary>Editor view/zoom shortcuts in Settings → Hotkeys (Annotations Editor → View).</summary>
public static class EditorViewHotkeyDef
{
    public static readonly (string Id, string Label, char Icon)[] Shortcuts =
    {
        ("editorZoomIn",    "Zoom in",     '\uE710'),
        ("editorZoomOut",   "Zoom out",    '\uE711'),
        ("editorZoomReset", "Reset zoom",  '\uE72C'),
        ("editorZoomFit",   "Zoom to fit", '\uE740'),
    };
}
