using System.Collections.Generic;
using System.Drawing;
using System.Linq;

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

    public static Annotation Scale(Annotation a, Rectangle oldBounds, Rectangle newBounds)
    {
        if (oldBounds.Width <= 0 || oldBounds.Height <= 0) return a;
        double sx = (double)newBounds.Width / oldBounds.Width;
        double sy = (double)newBounds.Height / oldBounds.Height;
        int ox = newBounds.X - (int)(oldBounds.X * sx);
        int oy = newBounds.Y - (int)(oldBounds.Y * sy);

        Point ScalePt(Point p) => new((int)(p.X * sx) + ox, (int)(p.Y * sy) + oy);
        Rectangle ScaleRect(Rectangle r) => new((int)(r.X * sx) + ox, (int)(r.Y * sy) + oy,
            Math.Max(1, (int)(r.Width * sx)), Math.Max(1, (int)(r.Height * sy)));

        return a switch
        {
            ArrowAnnotation arr => arr with { From = ScalePt(arr.From), To = ScalePt(arr.To) },
            LineAnnotation ln => ln with { From = ScalePt(ln.From), To = ScalePt(ln.To) },
            RulerAnnotation ru => ru with { From = ScalePt(ru.From), To = ScalePt(ru.To) },
            BlurRect br => br with { Rect = ScaleRect(br.Rect) },
            HighlightAnnotation hl => hl with { Rect = ScaleRect(hl.Rect) },
            RectShapeAnnotation rs => rs with { Rect = ScaleRect(rs.Rect) },
            CircleShapeAnnotation cs => cs with { Rect = ScaleRect(cs.Rect) },
            EraserFill ef => ef with { Rect = ScaleRect(ef.Rect) },
            EmojiAnnotation em => em with { Pos = ScalePt(em.Pos), Size = Math.Max(8f, em.Size * (float)Math.Max(sx, sy)) },
            TextAnnotation ta => ta with { Pos = ScalePt(ta.Pos), FontSize = Math.Clamp(ta.FontSize * (float)Math.Max(sx, sy), 10f, 120f) },
            StepNumberAnnotation sn => sn with { Pos = ScalePt(sn.Pos) },
            DrawStroke ds => ds with { Points = ds.Points.Select(p => ScalePt(p)).ToList() },
            CurvedArrowAnnotation ca => ca with { Points = ca.Points.Select(p => ScalePt(p)).ToList() },
            _ => a
        };
    }

    public static Rectangle GetBounds(Annotation a) => a switch
    {
        BlurRect br => br.Rect,
        HighlightAnnotation hl => hl.Rect,
        RectShapeAnnotation rs => rs.Rect,
        CircleShapeAnnotation cs => cs.Rect,
        EraserFill ef => ef.Rect,
        ArrowAnnotation ar => RectangleFromPoints(ar.From, ar.To),
        LineAnnotation ln => RectangleFromPoints(ln.From, ln.To),
        RulerAnnotation ru => RectangleFromPoints(ru.From, ru.To),
        CurvedArrowAnnotation ca => ca.Points.Count > 0 ? BoundingBox(ca.Points) : Rectangle.Empty,
        DrawStroke ds => ds.Points.Count > 0 ? BoundingBox(ds.Points) : Rectangle.Empty,
        StepNumberAnnotation sn => new Rectangle(sn.Pos.X - 20, sn.Pos.Y - 20, 40, 40),
        EmojiAnnotation em => new Rectangle(em.Pos.X, em.Pos.Y, (int)em.Size, (int)em.Size),
        MagnifierAnnotation mg => new Rectangle(mg.Pos.X - 30, mg.Pos.Y - 30, 60, 60),
        TextAnnotation ta => new Rectangle(ta.Pos.X - 8, ta.Pos.Y - 6, (int)(ta.Text.Length * ta.FontSize * 0.7) + 16, (int)ta.FontSize + 12),
        _ => Rectangle.Empty
    };

    private static Rectangle RectangleFromPoints(Point a, Point b)
    {
        int minX = Math.Min(a.X, b.X);
        int minY = Math.Min(a.Y, b.Y);
        int maxX = Math.Max(a.X, b.X);
        int maxY = Math.Max(a.Y, b.Y);
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    private static Rectangle BoundingBox(IReadOnlyList<Point> pts)
    {
        int minX = pts[0].X, minY = pts[0].Y, maxX = pts[0].X, maxY = pts[0].Y;
        for (int i = 1; i < pts.Count; i++)
        {
            minX = Math.Min(minX, pts[i].X);
            minY = Math.Min(minY, pts[i].Y);
            maxX = Math.Max(maxX, pts[i].X);
            maxY = Math.Max(maxY, pts[i].Y);
        }
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    private static Point Off(Point p, int dx, int dy) => new(p.X + dx, p.Y + dy);
    private static Rectangle OffRect(Rectangle r, int dx, int dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);
}
