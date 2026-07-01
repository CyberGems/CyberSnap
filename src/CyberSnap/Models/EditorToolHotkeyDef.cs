namespace CyberSnap.Models;

/// <summary>Editor toolbar tools exposed in Settings → Hotkeys (Annotations Editor section).</summary>
public static class EditorToolHotkeyDef
{
    public static readonly (string Id, string Label, char Icon)[] Tools =
    {
        ("editorPan",         "Pan",              '\uE1E3'),
        ("editorMove",        "Pick",             '\uE1E3'),
        ("editorEraser",      "Eraser",           '\uE28E'),
        ("editorText",        "Text",             '\uE197'),
        ("editorArrow",       "Arrow",            '\uE051'),
        ("editorLine",        "Line",             '\uE11F'),
        ("editorDraw",        "FreeHand",         '\uE70F'),
        ("editorCurvedArrow", "Curved Arrow",     '\uE146'),
        ("editorCircle",      "Circle",           '\uE07A'),
        ("editorRect",        "Rectangle",        '\uE16A'),
        ("editorHighlight",   "Highlight",        '\uE0F7'),
        ("editorStep",        "Steps",            '\uE1D0'),
        ("editorMagnifier",   "Magnifier",        '\uE721'),
        ("editorBlur",        "Blur",             '\uE5A0'),
        ("editorEmoji",       "Emoji",            '\uE167'),
        ("editorCrop",        "Crop",             '\uE7C8'),
    };
}
