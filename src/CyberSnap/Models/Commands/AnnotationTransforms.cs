using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CyberSnap.Helpers;

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

    public static bool CanRotate(Annotation a) => a is
        RectShapeAnnotation or CircleShapeAnnotation or
        ArrowAnnotation or LineAnnotation or CurvedArrowAnnotation or
        DrawStroke or RulerAnnotation;

    public static float GetRotation(Annotation a) => a switch
    {
        RectShapeAnnotation rs => rs.Rotation,
        CircleShapeAnnotation cs => cs.Rotation,
        _ => 0f
    };

    /// <summary>Rotates <paramref name="a"/> by <paramref name="degrees"/> around
    /// <paramref name="pivot"/>. Rect/circle store an angle; point-based types move vertices.</summary>
    public static Annotation Rotate(Annotation a, PointF pivot, float degrees)
    {
        if (Math.Abs(degrees) < 0.001f) return a;
        double rad = degrees * Math.PI / 180.0;
        Point Rot(Point p) => RotatePoint(p, pivot, rad);

        return a switch
        {
            RectShapeAnnotation rs => rs with { Rotation = rs.Rotation + degrees },
            CircleShapeAnnotation cs => cs with { Rotation = cs.Rotation + degrees },
            ArrowAnnotation arr => arr with { From = Rot(arr.From), To = Rot(arr.To) },
            LineAnnotation ln => ln with { From = Rot(ln.From), To = Rot(ln.To) },
            RulerAnnotation ru => ru with { From = Rot(ru.From), To = Rot(ru.To) },
            DrawStroke ds => ds with { Points = ds.Points.Select(Rot).ToList() },
            CurvedArrowAnnotation ca => ca with { Points = ca.Points.Select(Rot).ToList() },
            _ => a
        };
    }

    public static PointF CenterOf(Rectangle r) =>
        new(r.X + r.Width / 2f, r.Y + r.Height / 2f);

    public static PointF[] GetRotatedCorners(Rectangle rect, float degrees)
    {
        var c = CenterOf(rect);
        double rad = degrees * Math.PI / 180.0;
        return
        [
            RotatePointF(new PointF(rect.Left, rect.Top), c, rad),
            RotatePointF(new PointF(rect.Right, rect.Top), c, rad),
            RotatePointF(new PointF(rect.Right, rect.Bottom), c, rad),
            RotatePointF(new PointF(rect.Left, rect.Bottom), c, rad),
        ];
    }

    /// <summary>OBB corners (TL, TR, BR, BL) for rotate-mode chrome, padded outward from center.</summary>
    public static PointF[] GetRotateHandleCorners(Annotation a, float pad)
    {
        PointF[] corners = a switch
        {
            RectShapeAnnotation rs => GetRotatedCorners(rs.Rect, rs.Rotation),
            CircleShapeAnnotation cs => GetRotatedCorners(cs.Rect, cs.Rotation),
            _ => AxisAlignedCorners(GetBounds(a))
        };
        return pad == 0f ? corners : PadCornersOutward(corners, pad);
    }

    public static PointF PivotOf(Annotation a)
    {
        var c = GetRotateHandleCorners(a, 0f);
        return new PointF(
            (c[0].X + c[1].X + c[2].X + c[3].X) / 4f,
            (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4f);
    }

    /// <summary>Smallest signed turn from <paramref name="fromDegrees"/> to <paramref name="toDegrees"/>.</summary>
    public static float SignedDeltaDegrees(float fromDegrees, float toDegrees)
    {
        float d = toDegrees - fromDegrees;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    private static PointF[] AxisAlignedCorners(Rectangle r) =>
    [
        new PointF(r.Left, r.Top),
        new PointF(r.Right, r.Top),
        new PointF(r.Right, r.Bottom),
        new PointF(r.Left, r.Bottom),
    ];

    public static PointF[] PadCornersOutward(PointF[] corners, float pad)
    {
        if (corners is not { Length: 4 } || Math.Abs(pad) < 0.001f) return corners;
        var c = new PointF(
            (corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4f,
            (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4f);
        var padded = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            float dx = corners[i].X - c.X, dy = corners[i].Y - c.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            padded[i] = len < 0.1f
                ? corners[i]
                : new PointF(corners[i].X + dx / len * pad, corners[i].Y + dy / len * pad);
        }
        return padded;
    }

    public static Rectangle GetAxisAlignedBounds(Rectangle rect, float degrees)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return rect;
        if (Math.Abs(degrees % 360f) < 0.05f) return rect;
        var pts = GetRotatedCorners(rect, degrees);
        float minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        float maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        int x = (int)Math.Floor(minX), y = (int)Math.Floor(minY);
        return new Rectangle(x, y, Math.Max(1, (int)Math.Ceiling(maxX) - x), Math.Max(1, (int)Math.Ceiling(maxY) - y));
    }

    public static PointF InverseRotatePoint(PointF p, PointF pivot, float degrees)
    {
        if (Math.Abs(degrees) < 0.001f) return p;
        return RotatePointF(p, pivot, -degrees * Math.PI / 180.0);
    }

    public static void DrawWithRectRotation(Graphics g, Rectangle rect, float degrees, Action draw)
    {
        if (Math.Abs(degrees % 360f) < 0.05f)
        {
            draw();
            return;
        }
        var c = CenterOf(rect);
        var state = g.Save();
        try
        {
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform(degrees);
            g.TranslateTransform(-c.X, -c.Y);
            draw();
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static Point RotatePoint(Point p, PointF pivot, double rad) =>
        Point.Round(RotatePointF(new PointF(p.X, p.Y), pivot, rad));

    private static PointF RotatePointF(PointF p, PointF pivot, double rad)
    {
        double dx = p.X - pivot.X, dy = p.Y - pivot.Y;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return new PointF(
            (float)(pivot.X + dx * c - dy * s),
            (float)(pivot.Y + dx * s + dy * c));
    }

    public static Rectangle GetBounds(Annotation a) => a switch
    {
        BlurRect br => br.Rect,
        HighlightAnnotation hl => hl.Rect,
        RectShapeAnnotation rs => GetAxisAlignedBounds(rs.Rect, rs.Rotation),
        CircleShapeAnnotation cs => GetAxisAlignedBounds(cs.Rect, cs.Rotation),
        EraserFill ef => ef.Rect,
        ArrowAnnotation ar => RectangleFromPoints(ar.From, ar.To),
        LineAnnotation ln => RectangleFromPoints(ln.From, ln.To),
        RulerAnnotation ru => RectangleFromPoints(ru.From, ru.To),
        CurvedArrowAnnotation ca => ca.Points.Count > 0 ? BoundingBox(ca.Points) : Rectangle.Empty,
        DrawStroke ds => ds.Points.Count > 0 ? BoundingBox(ds.Points) : Rectangle.Empty,
        StepNumberAnnotation sn => new Rectangle(sn.Pos.X - 20, sn.Pos.Y - 20, 40, 40),
        EmojiAnnotation em => new Rectangle(em.Pos.X, em.Pos.Y, (int)(em.Size * 1.4f) + 4, (int)(em.Size * 1.4f) + 4),
        MagnifierAnnotation mg => new Rectangle(mg.Pos.X - 30, mg.Pos.Y - 30, 60, 60),
        TextAnnotation ta => Rectangle.Round(TextAnnotationPainter.Measure(ta)),
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
