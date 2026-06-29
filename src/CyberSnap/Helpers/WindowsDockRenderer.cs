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

    public static void PaintButton(Graphics g, RectangleF rect, bool active, bool hovered, float radius = -1f, Color? accent = null)
    {
        if (!active && !hovered)
            return;

        if (radius < 0)
            radius = UiChrome.ScaleFloat(5f);

        var accentColor = accent ?? UiChrome.AccentColor;
        using var path = RoundedRect(rect, radius);
        if (active)
        {
            using (var brush = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 36 : 28, accentColor)))
                g.FillPath(brush, path);
            using (var pen = new Pen(Color.FromArgb(UiChrome.IsDark ? 140 : 100, accentColor), 1f))
                g.DrawPath(pen, path);
        }
        else // Hovered
        {
            using (var brush = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 20 : 16, accentColor)))
                g.FillPath(brush, path);
        }
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
        using var path = RoundedRect(face, corner);
        path.Flatten();
        var pts = path.PathPoints;
        int n = pts.Length;
        if (n < 2) return;

        var seg = new float[n];
        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            float d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            seg[i] = d;
            total += d;
        }
        if (total <= 0f) return;

        PointF PointAt(float dist)
        {
            dist = ((dist % total) + total) % total;
            for (int i = 0; i < n; i++)
            {
                if (dist <= seg[i] || i == n - 1)
                {
                    float t = seg[i] > 0 ? dist / seg[i] : 0f;
                    var a = pts[i];
                    var b = pts[(i + 1) % n];
                    return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                }
                dist -= seg[i];
            }
            return pts[0];
        }

        float head = phase * total;
        float tailLen = total * (0.32f * Math.Clamp(thicknessScale, 0.35f, 1.25f));
        const int segments = 64;
        float glowWidth = UiChrome.ScaleFloat(3.5f * thicknessScale);
        float coreWidth = UiChrome.ScaleFloat(1.8f * thicknessScale);
        float centerWidth = UiChrome.ScaleFloat(0.8f * thicknessScale);

        using (var glowPen = new Pen(Color.Transparent, 1f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            var prev = PointAt(head);
            for (int k = 1; k <= segments; k++)
            {
                float p01 = k / (float)segments;
                var cur = PointAt(head - tailLen * p01);
                float factor = 1f - Math.Abs(0.5f - p01) * 2f;
                int a = (int)(95 * intensity * factor * factor);
                if (a > 0)
                {
                    glowPen.Width = Math.Max(0.5f, glowWidth * factor);
                    glowPen.Color = Color.FromArgb(a, glowColor);
                    g.DrawLine(glowPen, prev, cur);
                }
                prev = cur;
            }
        }

        using (var corePen = new Pen(Color.Transparent, 1f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            var prev = PointAt(head);
            for (int k = 1; k <= segments; k++)
            {
                float p01 = k / (float)segments;
                var cur = PointAt(head - tailLen * p01);
                float factor = 1f - Math.Abs(0.5f - p01) * 2f;
                int a = (int)(200 * intensity * factor * factor);
                if (a > 0)
                {
                    corePen.Width = Math.Max(0.5f, coreWidth * factor);
                    corePen.Color = Color.FromArgb(a, coreColor);
                    g.DrawLine(corePen, prev, cur);
                }
                prev = cur;
            }
        }

        using (var centerPen = new Pen(Color.Transparent, 1f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            var prev = PointAt(head);
            for (int k = 1; k <= segments; k++)
            {
                float p01 = k / (float)segments;
                var cur = PointAt(head - tailLen * p01);
                float factor = 1f - Math.Abs(0.5f - p01) * 2f;
                int a = (int)(255 * intensity * factor * factor * factor);
                if (a > 0)
                {
                    centerPen.Width = Math.Max(0.5f, centerWidth * factor);
                    centerPen.Color = Color.FromArgb(a, Color.White);
                    g.DrawLine(centerPen, prev, cur);
                }
                prev = cur;
            }
        }
    }
}
