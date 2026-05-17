using System.Drawing;

namespace CyberSnap.Helpers;

public static class WindowsHandleRenderer
{
    public const int Size = 9;
    public const int HitSize = 22;

    private static readonly SolidBrush HandleShadowBrush = new(Color.FromArgb(55, 0, 0, 0));
    private static SolidBrush? _handleFillBrush;
    private static int _handleFillKey;

    public static RectangleF CenteredAt(PointF point) =>
        new(point.X - Size / 2f, point.Y - Size / 2f, Size, Size);

    public static Rectangle HitRect(Point point) =>
        new(point.X - HitSize / 2, point.Y - HitSize / 2, HitSize, HitSize);

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
