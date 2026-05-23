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
}
