using System;
using System.Drawing;

namespace CyberSnap.Helpers;

public static class LineSnapHelper
{
    /// <summary>Snap endpoint to the nearest 45° direction from start (0°, 45°, 90°, …).</summary>
    public static Point SnapEndTo45Degrees(Point start, Point current)
    {
        int dx = current.X - start.X;
        int dy = current.Y - start.Y;
        if (dx == 0 && dy == 0)
            return current;

        double angle = Math.Atan2(dy, dx);
        const double step = Math.PI / 4;
        double snapped = Math.Round(angle / step) * step;

        double dist = Math.Sqrt((double)dx * dx + dy * dy);
        int x = start.X + (int)Math.Round(Math.Cos(snapped) * dist);
        int y = start.Y + (int)Math.Round(Math.Sin(snapped) * dist);
        return new Point(x, y);
    }
}
