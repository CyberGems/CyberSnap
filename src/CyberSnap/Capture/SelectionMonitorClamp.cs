using System.Drawing;
using System.Windows.Forms;

namespace CyberSnap.Capture;

/// <summary>
/// Confines a capture selection to the monitor where the gesture started,
/// matching image-capture overlay behavior on multi-monitor setups.
/// </summary>
internal static class SelectionMonitorClamp
{
    /// <summary>Monitor Bounds containing a client point, in overlay client coords.</summary>
    public static Rectangle GetMonitorClientBounds(Rectangle virtualBounds, Point clientPt)
    {
        var screenPt = new Point(virtualBounds.X + clientPt.X, virtualBounds.Y + clientPt.Y);
        var bounds = Screen.FromPoint(screenPt).Bounds;
        return new Rectangle(
            bounds.X - virtualBounds.X,
            bounds.Y - virtualBounds.Y,
            bounds.Width,
            bounds.Height);
    }

    public static Point ClampPoint(Point p, Rectangle monitorClient)
    {
        if (monitorClient.IsEmpty || monitorClient.Width <= 0 || monitorClient.Height <= 0)
            return p;
        int maxX = Math.Max(monitorClient.Left, monitorClient.Right - 1);
        int maxY = Math.Max(monitorClient.Top, monitorClient.Bottom - 1);
        return new Point(Math.Clamp(p.X, monitorClient.Left, maxX), Math.Clamp(p.Y, monitorClient.Top, maxY));
    }

    /// <summary>
    /// Keeps a rectangle fully inside the selection monitor (shrinks if larger than the monitor).
    /// </summary>
    public static Rectangle ClampRect(Rectangle rect, Rectangle monitorClient)
    {
        var b = monitorClient;
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0 || rect.Width <= 0 || rect.Height <= 0)
            return rect;

        int w = Math.Min(rect.Width, b.Width);
        int h = Math.Min(rect.Height, b.Height);
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        int x = Math.Clamp(rect.X, b.Left, b.Right - w);
        int y = Math.Clamp(rect.Y, b.Top, b.Bottom - h);
        return new Rectangle(x, y, w, h);
    }
}
