using System.Drawing;
using System.Drawing.Drawing2D;
using CyberSnap.Capture;

namespace CyberSnap.Helpers;

public static class WindowsDockRenderer
{
    public static int SurfaceHeight => UiChrome.ScaledToolbarHeight;
    public static int IconButtonSize => UiChrome.ScaledToolbarButtonSize;
    public static int ButtonSpacing => UiChrome.ScaledToolbarButtonSpacing;
    public static int SurfacePadding => UiChrome.ScaledToolbarInnerPadding;
    public static int SurfaceRadius => UiChrome.ScaledSurfaceRadius;

    private static SolidBrush? _surfaceBgBrush;
    private static int _surfaceBgKey;
    private static Pen? _dividerPen;
    private static int _dividerPenKey;

    private static SolidBrush GetSurfaceBgBrush()
    {
        int key = UiChrome.SurfacePill.ToArgb();
        if (_surfaceBgBrush is null || _surfaceBgKey != key)
        {
            _surfaceBgBrush?.Dispose();
            _surfaceBgBrush = new SolidBrush(UiChrome.SurfacePill);
            _surfaceBgKey = key;
        }
        return _surfaceBgBrush;
    }

    private static Pen GetDividerPen()
    {
        float scaled = Math.Max(1f, UiChrome.ScaleFloat(1f));
        int key = HashCode.Combine(UiChrome.SurfaceBorderSubtle.ToArgb(), (int)(scaled * 16));
        if (_dividerPen is null || _dividerPenKey != key)
        {
            _dividerPen?.Dispose();
            _dividerPen = new Pen(UiChrome.SurfaceBorderSubtle, scaled);
            _dividerPenKey = key;
        }
        return _dividerPen;
    }

    public static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Path with only bottom corners rounded (for horizontal tier-2 overlay).</summary>
    public static GraphicsPath RoundedRectBottom(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        float x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        path.AddLine(x, y, x + w, y);
        path.AddLine(x + w, y, x + w, y + h - radius);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddLine(x + w - radius, y + h, x + radius, y + h);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.AddLine(x, y + h - radius, x, y);
        path.CloseFigure();
        return path;
    }

    /// <summary>Path with only right corners rounded (for vertical tier-2 overlay).</summary>
    public static GraphicsPath RoundedRectRight(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        float x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        path.AddLine(x, y, x + w - radius, y);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddLine(x + w, y + radius, x + w, y + h - radius);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddLine(x + w - radius, y + h, x, y + h);
        path.AddLine(x, y + h, x, y);
        path.CloseFigure();
        return path;
    }

    /// <summary>Paints the fill only (no shadow) with selective corner rounding for two-tier toolbar overlays.</summary>
    public static void PaintSurfaceBg(Graphics g, RectangleF rect, Color color, float radius, bool vertical)
    {
        using var path = vertical ? RoundedRectRight(rect, radius) : RoundedRectBottom(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void PaintSurface(Graphics g, RectangleF rect, float radius = -1f)
    {
        if (radius < 0)
            radius = SurfaceRadius;
        PaintShadow(g, rect, radius);

        using var path = RoundedRect(rect, radius);
        g.FillPath(GetSurfaceBgBrush(), path);
    }

    public static void PaintShadow(Graphics g, RectangleF rect, float radius)
    {
        var ambient = rect;
        ambient.Inflate(6f, 6f);
        ambient.Offset(0, 1.5f);
        var ambientBrush = SketchRenderer.GetToolColorBrush(Color.FromArgb(UiChrome.IsDark ? 10 : 8, 0, 0, 0));
        using (var path = RoundedRect(ambient, radius + 8f))
            g.FillPath(ambientBrush, path);

        var key = rect;
        key.Inflate(2f, 2f);
        key.Offset(0, 3f);
        var keyBrush = SketchRenderer.GetToolColorBrush(Color.FromArgb(UiChrome.IsDark ? 14 : 10, 0, 0, 0));
        using (var path = RoundedRect(key, radius + 3f))
            g.FillPath(keyBrush, path);
    }

    public static void PaintButton(Graphics g, RectangleF rect, bool active, bool hovered, float radius = -1f, Color? accent = null, float welcomePulse = 0f)
    {
        if (!active && !hovered && welcomePulse <= 0f)
            return;

        if (radius < 0)
            radius = UiChrome.ScaleFloat(5f);

        var accentColor = accent ?? UiChrome.AccentColor;
        using var path = RoundedRect(rect, radius);
        if (active || welcomePulse > 0f)
        {
            // Base active fill; welcomePulse (0..1) briefly boosts glow after tool select.
            float pulse = Math.Clamp(welcomePulse, 0f, 1f);
            int fillA = (int)((UiChrome.IsDark ? 52 : 40) + 50 * pulse);
            int ringA = (int)((UiChrome.IsDark ? 180 : 140) + 60 * pulse);
            using (var brush = new SolidBrush(Color.FromArgb(Math.Clamp(fillA, 0, 255), accentColor)))
                g.FillPath(brush, path);
            using (var pen = new Pen(Color.FromArgb(Math.Clamp(ringA, 0, 255), accentColor), 1.35f + pulse * 0.6f))
                g.DrawPath(pen, path);
        }
        else // Hovered
        {
            using (var brush = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 20 : 16, accentColor)))
                g.FillPath(brush, path);
        }
    }

    /// <summary>Small accent pill under an active toolbar icon (extra active-state cue).</summary>
    public static void PaintActiveIndicator(Graphics g, RectangleF button, Color accent, float welcomePulse = 0f)
    {
        float pulse = Math.Clamp(welcomePulse, 0f, 1f);
        float w = Math.Max(8f, button.Width * (0.36f + 0.12f * pulse));
        float h = Math.Max(2f, UiChrome.ScaleFloat(2.5f + pulse));
        float x = button.X + (button.Width - w) / 2f;
        float y = button.Bottom - h - UiChrome.ScaleFloat(3f);
        using var path = RoundedRect(new RectangleF(x, y, w, h), h / 2f);
        int a = (int)((UiChrome.IsDark ? 230 : 210) * (0.75f + 0.25f * pulse));
        using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(a, 0, 255), accent));
        g.FillPath(brush, path);
    }

    public static void PaintDivider(Graphics g, Point a, Point b)
    {
        g.DrawLine(GetDividerPen(), a, b);
    }

    public static void PaintIcon(Graphics g, string iconId, Rectangle bounds, Color color, bool active = false)
    {
        FluentIcons.DrawIcon(g, iconId, bounds, color, UiChrome.ScaleFloat(active ? 5.5f : 6.5f), active);
    }

    /// <summary>
    /// Traveling border glint for pill buttons. <paramref name="thicknessScale"/> &lt; 1 draws a finer beam.
    /// </summary>
    public static void PaintBorderShine(
        Graphics g, RectangleF face, float corner, float phase,
        Color glowColor, Color coreColor, float intensity, float thicknessScale = 1f)
    {
        var (total, pointAt) = CreateRoundedRectPerimeter(face, corner);
        if (total <= 0f)
            return;

        float head = phase * total;
        float tailLen = total * (0.32f * Math.Clamp(thicknessScale, 0.35f, 1.25f));
        const int segments = 96;
        float glowWidth = UiChrome.ScaleFloat(3.5f * thicknessScale);
        float coreWidth = UiChrome.ScaleFloat(1.8f * thicknessScale);
        float centerWidth = UiChrome.ScaleFloat(0.8f * thicknessScale);
        float tailStart = head - tailLen;
        float step = tailLen / segments;
        float maxChord = step * 2.75f;

        void DrawShinePass(float penWidth, Color color, int alphaPower)
        {
            using var pen = new Pen(Color.Transparent, 1f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            var prev = pointAt(tailStart);
            for (int k = 1; k <= segments; k++)
            {
                float p01 = k / (float)segments;
                var cur = pointAt(tailStart + step * k);
                float factor = 1f - Math.Abs(0.5f - p01) * 2f;
                int alphaBase = alphaPower switch
                {
                    3 => 255,
                    2 => 200,
                    _ => 95
                };
                int a = alphaPower switch
                {
                    3 => (int)(alphaBase * intensity * factor * factor * factor),
                    2 => (int)(alphaBase * intensity * factor * factor),
                    _ => (int)(alphaBase * intensity * factor * factor)
                };
                if (a > 0)
                {
                    float dx = cur.X - prev.X;
                    float dy = cur.Y - prev.Y;
                    if (dx * dx + dy * dy <= maxChord * maxChord)
                    {
                        pen.Width = Math.Max(0.5f, penWidth * factor);
                        pen.Color = Color.FromArgb(a, color);
                        g.DrawLine(pen, prev, cur);
                    }
                }

                prev = cur;
            }
        }

        DrawShinePass(glowWidth, glowColor, 1);
        DrawShinePass(coreWidth, coreColor, 2);
        DrawShinePass(centerWidth, Color.White, 3);
    }

    private readonly record struct PerimeterSegment(float Length, Func<float, PointF> Evaluate);

    /// <summary>
    /// Uniform clockwise perimeter (top-left → top → …) without GDI+ flatten seams.
    /// </summary>
    private static (float Total, Func<float, PointF> PointAt) CreateRoundedRectPerimeter(RectangleF rect, float radius)
    {
        float r = Math.Clamp(radius, 0f, Math.Min(rect.Width, rect.Height) * 0.5f);
        float left = rect.X;
        float top = rect.Y;
        float right = rect.Right;
        float bottom = rect.Bottom;

        float straightTop = Math.Max(0f, rect.Width - 2f * r);
        float straightSide = Math.Max(0f, rect.Height - 2f * r);
        float arcQuarter = MathF.PI * r * 0.5f;

        var segments = new List<PerimeterSegment>(8);

        void AddLine(float length, Func<float, PointF> eval)
        {
            if (length > 0.0001f)
                segments.Add(new PerimeterSegment(length, eval));
        }

        AddLine(straightTop, t => new PointF(left + r + straightTop * t, top));

        float trCx = right - r;
        float trCy = top + r;
        AddLine(arcQuarter, t =>
        {
            float a = -MathF.PI * 0.5f + MathF.PI * 0.5f * t;
            return new PointF(trCx + r * MathF.Cos(a), trCy + r * MathF.Sin(a));
        });

        AddLine(straightSide, t => new PointF(right, top + r + straightSide * t));

        float brCx = right - r;
        float brCy = bottom - r;
        AddLine(arcQuarter, t =>
        {
            float a = MathF.PI * 0.5f * t;
            return new PointF(brCx + r * MathF.Cos(a), brCy + r * MathF.Sin(a));
        });

        AddLine(straightTop, t => new PointF(right - r - straightTop * t, bottom));

        float blCx = left + r;
        float blCy = bottom - r;
        AddLine(arcQuarter, t =>
        {
            float a = MathF.PI * 0.5f + MathF.PI * 0.5f * t;
            return new PointF(blCx + r * MathF.Cos(a), blCy + r * MathF.Sin(a));
        });

        AddLine(straightSide, t => new PointF(left, bottom - r - straightSide * t));

        float tlCx = left + r;
        float tlCy = top + r;
        AddLine(arcQuarter, t =>
        {
            float a = MathF.PI + MathF.PI * 0.5f * t;
            return new PointF(tlCx + r * MathF.Cos(a), tlCy + r * MathF.Sin(a));
        });

        float total = 0f;
        foreach (var seg in segments)
            total += seg.Length;

        if (total <= 0f)
            return (0f, _ => new PointF(left, top));

        PointF PointAt(float dist)
        {
            dist = ((dist % total) + total) % total;
            foreach (var seg in segments)
            {
                if (dist <= seg.Length + 0.0001f)
                {
                    float u = seg.Length > 0f ? dist / seg.Length : 0f;
                    return seg.Evaluate(u);
                }

                dist -= seg.Length;
            }

            return segments[0].Evaluate(0f);
        }

        return (total, PointAt);
    }
}
