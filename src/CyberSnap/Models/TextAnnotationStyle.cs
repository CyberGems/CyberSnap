namespace CyberSnap.Models;

/// <summary>Horizontal alignment for multi-line text annotations.</summary>
public enum TextHAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>Snapshot of Text-tool formatting defaults (session + settings persistence).</summary>
public sealed class TextAnnotationStyle
{
    public float FontSize { get; set; } = 24f;
    public string FontFamily { get; set; } = "Segoe UI";
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public bool Stroke { get; set; } = true;
    public bool Shadow { get; set; } = true;
    public bool Background { get; set; }
    public TextHAlign Alignment { get; set; } = TextHAlign.Left;
    /// <summary>0 = auto width (no forced wrap); &gt;0 wraps at this pixel width.</summary>
    public float MaxWidth { get; set; }
}
