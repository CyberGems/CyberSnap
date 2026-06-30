namespace CyberSnap.Models;

/// <summary>Editor toolbar tools exposed in Settings → Hotkeys (Annotations Editor section).</summary>
public static class EditorToolHotkeyDef
{
    public static readonly (string Id, string Label, char Icon)[] Tools =
    {
        ("editorPan",         "Pan",              '\uE1E3'),
        ("editorMove",        "Move & Resize",    '\uE1E3'),
        ("editorDraw",        "FreeHand",         '\uE70F'),
        ("editorArrow",       "Arrow",            '\uE051'),
        ("editorCurvedArrow", "Curved Arrow",     '\uE146'),
        ("editorLine",        "Line",             '\uE11F'),
        ("editorRect",        "Rectangle",        '\uE16A'),
        ("editorCircle",      "Circle",           '\uE07A'),
        ("editorText",        "Text",             '\uE197'),
        ("editorCrop",        "Crop",             '\uE7C8'),
        ("editorEraser",      "Eraser",           '\uE28E'),
        ("editorHighlight",   "Highlight",        '\uE0F7'),
        ("editorBlur",        "Blur",             '\uE5A0'),
        ("editorStep",        "Steps",            '\uE1D0'),
        ("editorMagnifier",   "Magnifier",        '\uE721'),
        ("editorEmoji",       "Emoji",            '\uE167'),
    };
}
