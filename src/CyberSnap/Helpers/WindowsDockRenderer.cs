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

    public static void PaintButton(Graphics g, RectangleF rect, bool active, bool hovered, float radius = -1f)
    {
        if (!active && !hovered)
            return;

        int alpha = active
            ? (UiChrome.IsDark ? 28 : 20)
            : (UiChrome.IsDark ? 18 : 14);
        if (radius < 0)
            radius = UiChrome.ScaleFloat(5f);
        using var path = RoundedRect(rect, radius);
        g.FillPath(SketchRenderer.GetToolColorBrush(Color.FromArgb(alpha, 255, 255, 255)), path);
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
