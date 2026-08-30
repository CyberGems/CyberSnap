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
