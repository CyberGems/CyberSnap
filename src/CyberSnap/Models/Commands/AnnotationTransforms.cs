using System.Drawing;

namespace CyberSnap.Models.Commands;

/// <summary>Shared helpers for translating annotation records in image-space.</summary>
internal static class AnnotationTransforms
{
    public static Annotation Translate(Annotation a, int dx, int dy) => a switch
    {
        ArrowAnnotation arr => arr with { From = Off(arr.From, dx, dy), To = Off(arr.To, dx, dy) },
        CurvedArrowAnnotation ca => ca with { Points = ca.Points.Select(p => Off(p, dx, dy)).ToList() },
        LineAnnotation ln => ln with { From = Off(ln.From, dx, dy), To = Off(ln.To, dx, dy) },
        RulerAnnotation ru => ru with { From = Off(ru.From, dx, dy), To = Off(ru.To, dx, dy) },
        DrawStroke ds => ds with { Points = ds.Points.Select(p => Off(p, dx, dy)).ToList() },
        BlurRect br => br with { Rect = OffRect(br.Rect, dx, dy) },
        HighlightAnnotation hl => hl with { Rect = OffRect(hl.Rect, dx, dy) },
        RectShapeAnnotation rs => rs with { Rect = OffRect(rs.Rect, dx, dy) },
        CircleShapeAnnotation cs => cs with { Rect = OffRect(cs.Rect, dx, dy) },
        EraserFill ef => ef with { Rect = OffRect(ef.Rect, dx, dy) },
        StepNumberAnnotation sn => sn with { Pos = Off(sn.Pos, dx, dy) },
        EmojiAnnotation em => em with { Pos = Off(em.Pos, dx, dy) },
        MagnifierAnnotation mg => mg with { Pos = Off(mg.Pos, dx, dy), SrcRect = OffRect(mg.SrcRect, dx, dy) },
        TextAnnotation ta => ta with { Pos = Off(ta.Pos, dx, dy) },
        _ => a
    };

    private static Point Off(Point p, int dx, int dy) => new(p.X + dx, p.Y + dy);
    private static Rectangle OffRect(Rectangle r, int dx, int dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);
}
