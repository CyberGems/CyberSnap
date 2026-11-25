using CyberSnap.Models.Commands;

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
        ("editorCutOut",      "Cut Out",          '\uE8C6'),
    };

    /// <summary>
    /// Canvas transforms on the editor toolbar. They act on the image instead of switching
    /// tools, but they share the same configurable bindings as the tool hotkeys.
    /// </summary>
    public static readonly (string Id, string Label, char Icon, CanvasTransformKind Kind)[] Actions =
    {
        ("editorRotateCw",  "Rotate 90° clockwise",         '\uE7AD', CanvasTransformKind.Rotate90Clockwise),
        ("editorRotateCcw", "Rotate 90° counter-clockwise", '\uE7AD', CanvasTransformKind.Rotate90CounterClockwise),
        ("editorFlipH",     "Flip horizontal",              '\uE7AD', CanvasTransformKind.FlipHorizontal),
        ("editorFlipV",     "Flip vertical",                '\uE7AD', CanvasTransformKind.FlipVertical),
    };
}
