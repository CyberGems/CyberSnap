using System.Drawing;
using System.Drawing.Drawing2D;

namespace CyberSnap.Capture;

public static partial class SketchRenderer
{
    // Elegant soft drop shadow — 4-step diagonal fade (lower-right).
    // Alphas tuned for visibility on both light and dark backgrounds.
    private static readonly (int dx, int dy, int alpha)[] SoftShadowSteps =
    {
        (5, 5, 22),
        (3, 3, 38),
        (1, 1, 62),
        (0, 0, 78),
    };
    private static readonly Color ShadowColor = Color.FromArgb(78, 0, 0, 0);

    // Pre-cached brushes for the four fixed SoftShadowSteps alphas — avoids re-alloc per call.
    private static readonly SolidBrush[] SoftShadowBrushes =
    {
        new(Color.FromArgb(22, 0, 0, 0)),
        new(Color.FromArgb(38, 0, 0, 0)),
        new(Color.FromArgb(62, 0, 0, 0)),
        new(Color.FromArgb(78, 0, 0, 0)),
    };

    // Shadow pens are black with one of 4 fixed alphas; thickness varies by caller's annotation width.
    // Cache keyed on (alpha, width-quantized) â€” bounded since thicknesses come from a small set.
    private static readonly Dictionary<long, Pen> _shadowPens = new();
    private static readonly Dictionary<long, Pen> _shadowPensFlatEnd = new();

    private static Pen GetShadowPen(int alpha, float width)
    {
        long key = ((long)alpha << 32) | (uint)(int)Math.Round(width * 16f);
        if (_shadowPens.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        _shadowPens[key] = pen;
        return pen;
    }

    /// <summary>Shadow pen with a flat end so curve shafts stop cleanly before the arrowhead.</summary>
    private static Pen GetShadowPenFlatEnd(int alpha, float width)
    {
        long key = ((long)alpha << 32) | (uint)(int)Math.Round(width * 16f);
        if (_shadowPensFlatEnd.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Flat,
            LineJoin = LineJoin.Round
        };
        _shadowPensFlatEnd[key] = pen;
        return pen;
    }

    private static void DrawSoftLineShadow(Graphics g, PointF from, PointF to, float thickness)
    {
        foreach (var step in SoftShadowSteps)
        {
            float w = thickness + (step.dx > 0 ? 1.2f : 0.5f);
            g.DrawLine(GetShadowPen(step.alpha, w),
                from.X + step.dx, from.Y + step.dy,
                to.X + step.dx, to.Y + step.dy);
        }
    }

    [ThreadStatic] private static PointF[]? _curveShadowBuffer;

    /// <summary>Flatten a curve/lines path into a dense polyline (matches visible DrawCurve geometry).</summary>
    private static PointF[] FlattenPathPoints(PointF[] points, float tension)
    {
        if (points.Length < 2)
            return points;

        using var path = new GraphicsPath();
        if (points.Length >= 4)
            path.AddCurve(points, tension);
        else
            path.AddLines(points);

        path.Flatten(null, 0.25f);
        return path.PathPoints;
    }

    private static void DrawSoftPolylineShadow(Graphics g, PointF[] points, float thickness)
    {
        if (points.Length < 2)
            return;

        if (_curveShadowBuffer == null || _curveShadowBuffer.Length < points.Length)
            _curveShadowBuffer = new PointF[points.Length];
        var buffer = _curveShadowBuffer;
        int n = points.Length;

        foreach (var step in SoftShadowSteps)
        {
            for (int i = 0; i < n; i++)
                buffer[i] = new PointF(points[i].X + step.dx, points[i].Y + step.dy);

            float w = thickness + (step.dx > 0 ? 1.2f : 0.5f);
            var pen = GetShadowPenFlatEnd(step.alpha, w);
            g.DrawLines(pen, buffer.AsSpan(0, n));
        }
    }

    private static void DrawSoftArrowheadShadow(Graphics g, PointF tip, float nx, float ny, float shaftLen, float thickness, PointF? clipTip = null)
    {
        float headSize = GetArrowheadSize(shaftLen);
        float angle = 25f * MathF.PI / 180f;
        float bx = tip.X - nx * headSize, by = tip.Y - ny * headSize;
        var left = RotatePoint(new PointF(bx, by), tip, -angle);
        var right = RotatePoint(new PointF(bx, by), tip, angle);

        // Pull wing roots slightly toward the tip so shadow does not fill the shaft/head gap.
        const float wingBaseInset = 0.14f;
        left = InsetToward(left, tip, headSize * wingBaseInset);
        right = InsetToward(right, tip, headSize * wingBaseInset);

        const float tipInset = 3.5f;

        foreach (var step in SoftShadowSteps)
        {
            if (step.dx == 0 && step.dy == 0)
                continue;

            var offsetTip = new PointF(tip.X + step.dx, tip.Y + step.dy);
            var offsetLeft = new PointF(left.X + step.dx, left.Y + step.dy);
            var offsetRight = new PointF(right.X + step.dx, right.Y + step.dy);
            // Outer shadow layers sit further out — pull wing ends back a bit more.
            float stepInset = tipInset + step.dx * 0.55f;
            var wingLeftEnd = InsetToward(offsetLeft, offsetTip, stepInset);
            var wingRightEnd = InsetToward(offsetRight, offsetTip, stepInset);

            if (clipTip is PointF clip)
            {
                wingLeftEnd = ClampNotBeyondTip(wingLeftEnd, clip, nx, ny);
                wingRightEnd = ClampNotBeyondTip(wingRightEnd, clip, nx, ny);
            }

            float w = (thickness + (step.dx > 0 ? 1.2f : 0.5f)) * 0.92f;
            var pen = GetShadowPenFlatEnd(step.alpha, w);
            g.DrawLine(pen, offsetLeft, wingLeftEnd);
            g.DrawLine(pen, offsetRight, wingRightEnd);
        }
    }

    /// <summary>Drop trailing polyline points that sit forward of the arrow tip (curve flatten overshoot).</summary>
    private static PointF[] TrimPolylineBeyondTip(PointF[] pts, PointF tip, float nx, float ny, float maxAhead = 0f)
    {
        if (pts.Length < 2)
            return pts;

        int last = pts.Length - 1;
        while (last > 0 && IsBeyondClipBounds(pts[last], tip, nx, ny, maxAhead))
            last--;

        return last == pts.Length - 1 ? pts : pts[..(last + 1)];
    }

    private static float AheadOfTip(PointF p, PointF tip, float nx, float ny) =>
        (p.X - tip.X) * nx + (p.Y - tip.Y) * ny;

    private static bool IsBeyondClipBounds(PointF p, PointF tip, float nx, float ny, float maxAhead)
    {
        if (AheadOfTip(p, tip, nx, ny) > maxAhead)
            return true;

        // Shadow falls down-right; when the head opens toward +X/+Y, trim screen-space bleed
        // (right/down protrusion) without affecting heads that open left/up.
        float maxScreen = maxAhead + 1.25f;
        if (nx > 0.25f && p.X > tip.X + maxScreen)
            return true;
        if (ny > 0.25f && p.Y > tip.Y + maxScreen)
            return true;
        return false;
    }

    private static PointF ClampNotBeyondTip(PointF p, PointF tip, float nx, float ny, float maxAhead = 0f)
    {
        float ahead = AheadOfTip(p, tip, nx, ny);
        if (ahead > maxAhead)
            p = new PointF(p.X - nx * (ahead - maxAhead), p.Y - ny * (ahead - maxAhead));

        float maxScreen = maxAhead + 1.25f;
        if (nx > 0.25f)
            p = new PointF(Math.Min(p.X, tip.X + maxScreen), p.Y);
        if (ny > 0.25f)
            p = new PointF(p.X, Math.Min(p.Y, tip.Y + maxScreen));
        return p;
    }

    private static PointF InsetToward(PointF from, PointF toward, float inset)
    {
        float dx = toward.X - from.X, dy = toward.Y - from.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= inset)
            return from;
        float t = (len - inset) / len;
        return new PointF(from.X + dx * t, from.Y + dy * t);
    }

    public static void DrawSoftPathShadow(Graphics g, GraphicsPath path, float extraSpread = 0f)
    {
        for (int i = 0; i < SoftShadowSteps.Length; i++)
        {
            var step = SoftShadowSteps[i];
            using var m = new System.Drawing.Drawing2D.Matrix();
            m.Translate(step.dx, step.dy);
            if (step.dx > 0)
                m.Scale(1f + extraSpread * 0.02f, 1f + extraSpread * 0.02f);
            using var shadowPath = (GraphicsPath)path.Clone();
            shadowPath.Transform(m);
            g.FillPath(SoftShadowBrushes[i], shadowPath);
        }
    }

    public static void DrawSoftEllipseShadow(Graphics g, float x, float y, float w, float h)
    {
        for (int i = 0; i < SoftShadowSteps.Length; i++)
        {
            var step = SoftShadowSteps[i];
            g.FillEllipse(SoftShadowBrushes[i], x + step.dx, y + step.dy, w, h);
        }
    }

    // â”€â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static (PointF start, PointF ctrl1, PointF ctrl2, PointF end) WobbleBezier(
        Random rng, PointF p1, PointF p2, float offset, float bow, float nx, float ny)
    {
        float midX = (p1.X + p2.X) / 2f;
        float midY = (p1.Y + p2.Y) / 2f;

        var start = new PointF(
            p1.X + Rand(rng, offset * 0.5f),
            p1.Y + Rand(rng, offset * 0.5f));
        var end = new PointF(
            p2.X + Rand(rng, offset * 0.5f),
            p2.Y + Rand(rng, offset * 0.5f));
        var ctrl1 = new PointF(
            midX + nx * bow * Rand(rng, 1.5f) + Rand(rng, offset),
            midY + ny * bow * Rand(rng, 1.5f) + Rand(rng, offset));
        var ctrl2 = new PointF(
            midX + nx * bow * Rand(rng, 1.5f) + Rand(rng, offset),
            midY + ny * bow * Rand(rng, 1.5f) + Rand(rng, offset));

        return (start, ctrl1, ctrl2, end);
    }

    private static float Rand(Random rng, float scale) =>
        ((float)rng.NextDouble() - 0.5f) * 2f * scale;

    public static float Distance(PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static PointF Midpoint(PointF a, PointF b) =>
        new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);

    private static PointF RotatePoint(PointF point, PointF center, float angle)
    {
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        float dx = point.X - center.X, dy = point.Y - center.Y;
        return new PointF(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);
    }

    public static GraphicsPath RoundedRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
