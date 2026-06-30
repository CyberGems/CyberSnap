namespace CyberSnap.Models;

/// <summary>Editor view/zoom shortcuts in Settings → Hotkeys (Annotations Editor → View).</summary>
public static class EditorViewHotkeyDef
{
    public static readonly (string Id, string Label, char Icon)[] Shortcuts =
    {
        ("editorZoomIn",    "Zoom in",     '\0'),
        ("editorZoomOut",   "Zoom out",    '\0'),
        ("editorZoomReset", "Reset zoom",  '\0'),
        ("editorZoomFit",   "Zoom to fit", '\0'),
    };
}
