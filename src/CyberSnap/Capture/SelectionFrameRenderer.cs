using System.Drawing;
using System.Drawing.Drawing2D;
using CyberSnap.Helpers;

namespace CyberSnap.Capture;

internal static class SelectionFrameRenderer
{
    private static readonly Color FillTint = Color.FromArgb(34, 0, 0, 0);
    private static readonly Color Stroke = Color.FromArgb(248, 255, 255, 255);
    private static readonly SolidBrush FillBrush = new(FillTint);
    private static readonly Pen RectangleStrokePen = new(Stroke, 2f) { LineJoin = LineJoin.Miter };
    private static readonly Pen PathStrokePen = new(Stroke, 2f)
    {
        LineJoin = LineJoin.Round,
        StartCap = LineCap.Round,
        EndCap = LineCap.Round
    };

    public static void DrawRectangle(Graphics g, Rectangle rect, bool fill = true)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Very subtle accent glow behind the frame
        var glowRect = rect;
        glowRect.Inflate(2, 2);
        using (var glowPen = new Pen(Color.FromArgb(22, UiChrome.AccentColor), 5f))
            g.DrawRectangle(glowPen, glowRect);

        if (fill)
            g.FillRectangle(FillBrush, rect);

        var outline = rect;
        outline.Width = Math.Max(1, outline.Width - 1);
        outline.Height = Math.Max(1, outline.Height - 1);

        g.DrawRectangle(RectangleStrokePen, outline);

        g.SmoothingMode = oldSmoothing;
    }

    public static void DrawAutoDetectRectangle(Graphics g, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var accent = UiChrome.AccentColor;

        // Strong neon glow behind the frame
        var glowRect = rect;
        glowRect.Inflate(4, 4);
        using (var glowPen = new Pen(Color.FromArgb(70, accent), 8f))
            g.DrawRectangle(glowPen, glowRect);

        // Barely-there accent fill tint so content stays fully readable
        using (var fillBrush = new SolidBrush(Color.FromArgb(4, accent.R, accent.G, accent.B)))
            g.FillRectangle(fillBrush, rect);

        // Accent-colored outline
        var outline = rect;
        outline.Width = Math.Max(1, outline.Width - 1);
        outline.Height = Math.Max(1, outline.Height - 1);
        using (var accentPen = new Pen(accent, 2f) { LineJoin = LineJoin.Miter })
            g.DrawRectangle(accentPen, outline);

        // HUD corner brackets
        const int cornerLen = 10;
        const int cornerOffset = 4;
        using (var cornerPen = new Pen(accent, 2f) { LineJoin = LineJoin.Miter })
        {
            // Top-left
            g.DrawLine(cornerPen, outline.X - cornerOffset, outline.Y - cornerOffset, outline.X - cornerOffset + cornerLen, outline.Y - cornerOffset);
            g.DrawLine(cornerPen, outline.X - cornerOffset, outline.Y - cornerOffset, outline.X - cornerOffset, outline.Y - cornerOffset + cornerLen);

            // Top-right
            g.DrawLine(cornerPen, outline.Right + cornerOffset, outline.Y - cornerOffset, outline.Right + cornerOffset - cornerLen, outline.Y - cornerOffset);
            g.DrawLine(cornerPen, outline.Right + cornerOffset, outline.Y - cornerOffset, outline.Right + cornerOffset, outline.Y - cornerOffset + cornerLen);

            // Bottom-left
            g.DrawLine(cornerPen, outline.X - cornerOffset, outline.Bottom + cornerOffset, outline.X - cornerOffset + cornerLen, outline.Bottom + cornerOffset);
            g.DrawLine(cornerPen, outline.X - cornerOffset, outline.Bottom + cornerOffset, outline.X - cornerOffset, outline.Bottom + cornerOffset - cornerLen);

            // Bottom-right
            g.DrawLine(cornerPen, outline.Right + cornerOffset, outline.Bottom + cornerOffset, outline.Right + cornerOffset - cornerLen, outline.Bottom + cornerOffset);
            g.DrawLine(cornerPen, outline.Right + cornerOffset, outline.Bottom + cornerOffset, outline.Right + cornerOffset, outline.Bottom + cornerOffset - cornerLen);
        }

        g.SmoothingMode = oldSmoothing;
    }

    public static void DrawPath(Graphics g, IReadOnlyList<Point> points, bool closed, bool fill = true)
    {
        if (points.Count < 2)
            return;

        using var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(points[0], points[1]);
        for (int i = 2; i < points.Count; i++)
            path.AddLine(points[i - 1], points[i]);
        if (closed && points.Count >= 3)
            path.CloseFigure();

        DrawPath(g, path, fill && closed);
    }

    public static void DrawPath(Graphics g, GraphicsPath path, bool fill = true)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (fill)
            g.FillPath(FillBrush, path);

        g.DrawPath(PathStrokePen, path);

        g.SmoothingMode = oldSmoothing;
    }
}
