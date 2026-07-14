using System.Drawing;
using System.Text.Json.Serialization;

namespace CyberSnap.Models;

/// <summary>Base for all annotation types stored in the undo stack.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(DrawStroke), "DrawStroke")]
[JsonDerivedType(typeof(BlurRect), "BlurRect")]
[JsonDerivedType(typeof(ArrowAnnotation), "ArrowAnnotation")]
[JsonDerivedType(typeof(CurvedArrowAnnotation), "CurvedArrowAnnotation")]
[JsonDerivedType(typeof(HighlightAnnotation), "HighlightAnnotation")]
[JsonDerivedType(typeof(StepNumberAnnotation), "StepNumberAnnotation")]
[JsonDerivedType(typeof(EraserFill), "EraserFill")]
[JsonDerivedType(typeof(TextAnnotation), "TextAnnotation")]
[JsonDerivedType(typeof(MagnifierAnnotation), "MagnifierAnnotation")]
[JsonDerivedType(typeof(EmojiAnnotation), "EmojiAnnotation")]
[JsonDerivedType(typeof(LineAnnotation), "LineAnnotation")]
[JsonDerivedType(typeof(RulerAnnotation), "RulerAnnotation")]
[JsonDerivedType(typeof(RectShapeAnnotation), "RectShapeAnnotation")]
[JsonDerivedType(typeof(CircleShapeAnnotation), "CircleShapeAnnotation")]
// IMPORTANT FOR DEVELOPERS: If you add a new Canvas Tool or Annotation type,
// you must register its type here with a unique type string identifier in order for 
// the Save/Load (.csnp) feature to serialize/deserialize it properly.
public abstract record Annotation;

public sealed record DrawStroke(List<Point> Points, Color Color, float StrokeWidth = 4f) : Annotation;
public sealed record BlurRect(Rectangle Rect) : Annotation;
public sealed record ArrowAnnotation(Point From, Point To, Color Color, float StrokeWidth = 4f) : Annotation;
public sealed record CurvedArrowAnnotation(List<Point> Points, Color Color, float StrokeWidth = 4f) : Annotation;
public sealed record HighlightAnnotation(Rectangle Rect, Color Color) : Annotation;
public sealed record StepNumberAnnotation(Point Pos, int Number, Color Color) : Annotation;
public sealed record EraserFill(Rectangle Rect, Color Color) : Annotation;
public sealed record TextAnnotation(
    Point Pos,
    string Text,
    float FontSize,
    Color Color,
    bool Bold,
    bool Italic,
    bool Stroke,
    bool Shadow,
    bool Background,
    string FontFamily,
    /// <summary>Horizontal alignment for multi-line text. Default Left for older projects.</summary>
    TextHAlign Alignment = TextHAlign.Left,
    /// <summary>0 = auto width; &gt;0 wraps lines at this pixel width.</summary>
    float MaxWidth = 0f) : Annotation;
public sealed record MagnifierAnnotation(Point Pos, Rectangle SrcRect) : Annotation;
public sealed record EmojiAnnotation(Point Pos, string Emoji, float Size) : Annotation;
public sealed record LineAnnotation(Point From, Point To, Color Color, float StrokeWidth = 4f) : Annotation;
public sealed record RulerAnnotation(Point From, Point To) : Annotation;
public sealed record RectShapeAnnotation(Rectangle Rect, Color Color, float StrokeWidth = 4f) : Annotation;
public sealed record CircleShapeAnnotation(Rectangle Rect, Color Color, float StrokeWidth = 4f) : Annotation;
