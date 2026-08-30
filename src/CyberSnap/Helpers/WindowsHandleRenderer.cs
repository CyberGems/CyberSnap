using System.Drawing;
using System.Drawing.Drawing2D;

namespace CyberSnap.Helpers;

public static class WindowsHandleRenderer
{
    public const int Size = 9;
    public const int HitSize = 22;
    /// <summary>Grab target for the center plus. Smaller than <see cref="HitSize"/> so
    /// empty interior around it still deselects instead of starting a move.</summary>
    public const int CenterPlusHitSize = 18;
    /// <summary>Wrap box must be at least this many screen pixels on both axes so the
    /// plus does not sit on top of the corner handles.</summary>
    public const int CenterPlusMinSize = 24;

    private static readonly SolidBrush HandleShadowBrush = new(Color.FromArgb(55, 0, 0, 0));
    private static SolidBrush? _handleFillBrush;
    private static int _handleFillKey;

    public static RectangleF CenteredAt(PointF point) =>
        new(point.X - Size / 2f, point.Y - Size / 2f, Size, Size);

    public static Rectangle HitRect(Point point) =>
        new(point.X - HitSize / 2, point.Y - HitSize / 2, HitSize, HitSize);

    public static Rectangle CenterPlusHitRect(Point center) =>
        new(center.X - CenterPlusHitSize / 2, center.Y - CenterPlusHitSize / 2,
            CenterPlusHitSize, CenterPlusHitSize);

    public static bool FitsCenterPlus(int width, int height) =>
        width >= CenterPlusMinSize && height >= CenterPlusMinSize;

    /// <summary>
    /// Fine plus (+) at the geometric center of a selection wrap box.
    /// <paramref name="px"/> is one screen pixel in the current Graphics space
    /// (1 in overlay screen-space, 1/zoom in the editor).
    /// </summary>
    public static void PaintCenterPlus(Graphics g, PointF center, Color color, float px)
    {
        if (px <= 0) px = 1f;
        float arm = 5.25f * px;
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var halo = new Pen(Color.FromArgb(100, 0, 0, 0), 2.15f * px)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(halo, center.X - arm, center.Y, center.X + arm, center.Y);
            g.DrawLine(halo, center.X, center.Y - arm, center.X, center.Y + arm);
        }
        using (var pen = new Pen(color, 1.2f * px)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, center.X - arm, center.Y, center.X + arm, center.Y);
            g.DrawLine(pen, center.X, center.Y - arm, center.X, center.Y + arm);
        }
        g.SmoothingMode = old;
    }

    public static PointF RotateArrowCenter(PointF corner, PointF pivot, float px)
    {
        float dx = corner.X - pivot.X, dy = corner.Y - pivot.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.1f) return corner;
        float o = 15f * px;
        return new PointF(corner.X + dx / len * o, corner.Y + dy / len * o);
    }

    public static Rectangle RotateArrowHitRect(PointF arrowCenter, float px)
    {
        int s = Math.Max(26, (int)Math.Round(28f * px));
        int cx = (int)Math.Round(arrowCenter.X);
        int cy = (int)Math.Round(arrowCenter.Y);
        return new Rectangle(cx - s / 2, cy - s / 2, s, s);
    }

    /// <summary>
    /// Rotate-mode chrome: corner dots, compact curved double-arrows, center bullseye.
    /// No wrap box — the OBB outline fights rotated shapes and hides the arrows.
    /// <paramref name="corners"/> is TL, TR, BR, BL in the current Graphics space.
    /// </summary>
    public static void PaintRotateMode(Graphics g, PointF[] corners, Color accent, float px)
    {
        if (corners is not { Length: 4 } || px <= 0) return;
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var pivot = new PointF(
            (corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4f,
            (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4f);

        float dotR = 2.45f * px;
        using (var white = new SolidBrush(Color.White))
        using (var rim = new Pen(Color.FromArgb(210, 0, 0, 0), 1.05f * px))
        {
            foreach (var c in corners)
            {
                var d = new RectangleF(c.X - dotR, c.Y - dotR, dotR * 2f, dotR * 2f);
                g.FillEllipse(white, d);
                g.DrawEllipse(rim, d);
            }
        }

        foreach (var c in corners)
            PaintRotateArrow(g, c, pivot, px);

        PaintCenterBullseye(g, pivot, accent, px);
        g.SmoothingMode = old;
    }

    /// <summary>
    /// Compact double-headed arc (~100°) sitting just outside a corner, oriented
    /// along the rotation tangent. Fixed screen size — not an arc of the object's circle.
    /// </summary>
    private static void PaintRotateArrow(Graphics g, PointF corner, PointF pivot, float px)
    {
        var at = RotateArrowCenter(corner, pivot, px);
        float dx = at.X - pivot.X, dy = at.Y - pivot.Y;
        float radial = MathF.Atan2(dy, dx);
        float r = 10.6f * px;
        const float half = 0.90f;
        float mid = radial + MathF.PI / 2f;
        float a0 = mid - half;
        float a1 = mid + half;
        var box = new RectangleF(at.X - r, at.Y - r, r * 2f, r * 2f);
        float startDeg = -a1 * 180f / MathF.PI;
        float sweepDeg = (2f * half) * 180f / MathF.PI;

        using (var halo = new Pen(Color.FromArgb(235, 0, 0, 0), 2.7f * px)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(halo, box, startDeg, sweepDeg);
        using (var pen = new Pen(Color.White, 1.55f * px)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, box, startDeg, sweepDeg);

        PointF On(float a) => new(at.X + MathF.Cos(a) * r, at.Y + MathF.Sin(a) * r);
        float head = 5.35f * px;
        float inset = 0.32f;
        using var outline = new SolidBrush(Color.FromArgb(235, 0, 0, 0));
        using var fill = new SolidBrush(Color.White);
        PaintTinyArrowHead(g, outline, On(a0 + inset), On(a0), head + 0.85f * px);
        PaintTinyArrowHead(g, fill, On(a0 + inset), On(a0), head);
        PaintTinyArrowHead(g, outline, On(a1 - inset), On(a1), head + 0.85f * px);
        PaintTinyArrowHead(g, fill, On(a1 - inset), On(a1), head);
    }

    private static void PaintTinyArrowHead(Graphics g, Brush brush, PointF from, PointF tip, float size)
    {
        float dx = tip.X - from.X, dy = tip.Y - from.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.01f) return;
        dx /= len; dy /= len;
        float px = -dy, py = dx;
        g.FillPolygon(brush,
        [
            tip,
            new PointF(tip.X - dx * size + px * size * 0.45f, tip.Y - dy * size + py * size * 0.45f),
            new PointF(tip.X - dx * size - px * size * 0.45f, tip.Y - dy * size - py * size * 0.45f),
        ]);
    }

    public static void PaintCenterBullseye(Graphics g, PointF c, Color accent, float px)
    {
        float outer = 5.4f * px;
        float mid = 3.5f * px;
        float inner = 1.55f * px;
        using (var halo = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillEllipse(halo, c.X - outer - 0.6f * px, c.Y - outer - 0.2f * px, (outer + 0.8f * px) * 2, (outer + 0.8f * px) * 2);
        using (var white = new SolidBrush(Color.White))
            g.FillEllipse(white, c.X - outer, c.Y - outer, outer * 2, outer * 2);
        using (var ring = new SolidBrush(Color.FromArgb(235, accent)))
            g.FillEllipse(ring, c.X - mid, c.Y - mid, mid * 2, mid * 2);
        using (var pip = new SolidBrush(Color.White))
            g.FillEllipse(pip, c.X - inner, c.Y - inner, inner * 2, inner * 2);
    }

    private static SolidBrush GetFillBrush()
    {
        int key = UiChrome.SurfaceTextPrimary.ToArgb();
        if (_handleFillBrush is null || _handleFillKey != key)
        {
            _handleFillBrush?.Dispose();
            _handleFillBrush = new SolidBrush(UiChrome.SurfaceTextPrimary);
            _handleFillKey = key;
        }
        return _handleFillBrush;
    }

    public static void Paint(Graphics g, RectangleF rect)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var shadowPath = WindowsDockRenderer.RoundedRect(new RectangleF(rect.X + 1.2f, rect.Y + 1.2f, rect.Width, rect.Height), 3f);
        g.FillPath(HandleShadowBrush, shadowPath);

        using var path = WindowsDockRenderer.RoundedRect(rect, 3f);
        g.FillPath(GetFillBrush(), path);
    }
}
