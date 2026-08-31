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
        float o = 17.5f * px;
        return new PointF(corner.X + dx / len * o, corner.Y + dy / len * o);
    }

    public static Rectangle RotateArrowHitRect(PointF arrowCenter, float px)
    {
        int s = Math.Max(30, (int)Math.Round(32f * px));
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
        using (var rim = new Pen(Color.FromArgb(220, accent), 1.15f * px))
        {
            foreach (var c in corners)
            {
                var d = new RectangleF(c.X - dotR, c.Y - dotR, dotR * 2f, dotR * 2f);
                g.FillEllipse(white, d);
                g.DrawEllipse(rim, d);
            }
        }

        foreach (var c in corners)
            PaintRotateArrow(g, c, pivot, accent, px);

        PaintCenterBullseye(g, pivot, accent, px);
        g.SmoothingMode = old;
    }

    /// <summary>
    /// Compact double-headed arc sitting just outside a corner, oriented along the
    /// rotation tangent. Arc and heads share GDI+ angles so they join as one glyph.
    /// </summary>
    private static void PaintRotateArrow(Graphics g, PointF corner, PointF pivot, Color accent, float px)
    {
        var at = RotateArrowCenter(corner, pivot, px);
        float radial = MathF.Atan2(at.Y - pivot.Y, at.X - pivot.X);
        float r = 13.2f * px;
        // Screen atan2 (Y-down) matches GDI+ DrawArc: 0° = east, clockwise.
        // Mid of the arc along the radial so the C follows the rotation circle
        // (opening toward the object, belly facing out) instead of sitting 90° off.
        float midDeg = radial * 180f / MathF.PI;
        const float halfDeg = 52f;
        float a0 = midDeg - halfDeg;
        float a1 = midDeg + halfDeg;
        var box = new RectangleF(at.X - r, at.Y - r, r * 2f, r * 2f);

        float headLen = 4.5f * px;
        float trimDeg = headLen / r * 180f / MathF.PI * 0.70f;
        float arcStart = a0 + trimDeg;
        float arcSweep = (a1 - a0) - 2f * trimDeg;
        if (arcSweep < 20f) return;

        var outline = Color.FromArgb(230, accent);
        using (var halo = new Pen(outline, 2.8f * px)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(halo, box, arcStart, arcSweep);
        using (var pen = new Pen(Color.White, 1.7f * px)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, box, arcStart, arcSweep);

        PaintArcArrowHead(g, at, r, a0, clockwise: false, headLen, px, outline);
        PaintArcArrowHead(g, at, r, a1, clockwise: true, headLen, px, outline);
    }

    private static void PaintArcArrowHead(Graphics g, PointF center, float r, float deg, bool clockwise, float len, float px, Color outlineColor)
    {
        float rad = deg * MathF.PI / 180f;
        float ax = center.X + r * MathF.Cos(rad);
        float ay = center.Y + r * MathF.Sin(rad);
        // Clockwise tangent in GDI space (Y-down).
        float tx = -MathF.Sin(rad);
        float ty = MathF.Cos(rad);
        if (!clockwise) { tx = -tx; ty = -ty; }

        float wid = len * 0.40f;
        var tip = new PointF(ax + tx * len, ay + ty * len);
        var back = new PointF(ax - tx * len * 0.12f, ay - ty * len * 0.12f);
        float nx = -ty, ny = tx;
        PointF[] tri =
        [
            tip,
            new(back.X + nx * wid, back.Y + ny * wid),
            new(back.X - nx * wid, back.Y - ny * wid),
        ];
        using (var outline = new SolidBrush(outlineColor))
        {
            var inflated = new PointF[]
            {
                new(tip.X + tx * 0.55f * px, tip.Y + ty * 0.55f * px),
                new(tri[1].X - tx * 0.35f * px + nx * 0.55f * px, tri[1].Y - ty * 0.35f * px + ny * 0.55f * px),
                new(tri[2].X - tx * 0.35f * px - nx * 0.55f * px, tri[2].Y - ty * 0.35f * px - ny * 0.55f * px),
            };
            g.FillPolygon(outline, inflated);
        }
        using var fill = new SolidBrush(Color.White);
        g.FillPolygon(fill, tri);
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
