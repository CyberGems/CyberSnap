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

        // Premium ambient accent glow behind the selection frame
        var glowRect = rect;
        glowRect.Inflate(2, 2);
        using (var glowPen = new Pen(Color.FromArgb(40, UiChrome.AccentColor), 5f))
            g.DrawRectangle(glowPen, glowRect);

        if (fill)
            g.FillRectangle(FillBrush, rect);

        var outline = rect;
        outline.Width = Math.Max(1, outline.Width - 1);
        outline.Height = Math.Max(1, outline.Height - 1);

        // Draw outer premium neon accent line
        using (var outerPen = new Pen(UiChrome.AccentColor, 1.5f))
        {
            outerPen.DashStyle = DashStyle.Dash;
            outerPen.DashPattern = new[] { 6f, 4f };
            g.DrawRectangle(outerPen, outline);
        }

        // Draw inner crisp white line for high contrast
        var innerOutline = outline;
        innerOutline.Inflate(-1, -1);
        using (var innerPen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f))
        {
            innerPen.DashStyle = DashStyle.Dash;
            innerPen.DashPattern = new[] { 6f, 4f };
            g.DrawRectangle(innerPen, innerOutline);
        }

        // Draw tactical HUD-style corner brackets
        const int cornerLen = 12;
        const int cornerOffset = 3;
        using (var cornerPen = new Pen(UiChrome.AccentColor, 2f) { LineJoin = LineJoin.Miter })
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

        // No fill tint — only the outline/brackets mark the detected window, leaving its content untouched.

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

    /// <summary>
    /// Draws premium resize handles for the confirmation frame: bold accent L-brackets
    /// at the four corners (indices 0-3) and rounded grab-bars at the four mid-edges
    /// (4=top, 5=left, 6=right, 7=bottom). Each handle has a white core, accent ring,
    /// and a soft glow so it reads as a tactile grab target over any background.
    /// </summary>
    public static void DrawConfirmHandles(Graphics g, Rectangle[] handles)
    {
        if (handles.Length < 8)
            return;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var accent = UiChrome.AccentColor;
        float scale = (float)UiChrome.UiScale;
        float thickness = 3f * scale;     // bracket / bar thickness
        float armLen = 11f * scale;       // corner bracket arm length
        float barLen = 18f * scale;       // mid-edge bar length
        float radius = thickness / 2f;

        using var glowBrush = new SolidBrush(Color.FromArgb(70, accent));
        using var coreBrush = new SolidBrush(Color.White);
        using var ringBrush = new SolidBrush(accent);

        // ── Corner L-brackets (TL, TR, BL, BR) ──
        float coreThick = Math.Max(1f, thickness - 2f * scale);
        for (int i = 0; i < 4; i++)
        {
            var c = CenterOf(handles[i]);
            // hx/hy point the two arms inward toward the selection center
            float hx = i is 0 or 2 ? 1f : -1f;
            float hy = i is 0 or 1 ? 1f : -1f;
            // soft glow halo
            DrawBar(g, glowBrush, c.X, c.Y, hx * armLen, thickness + 3f * scale, true, radius);
            DrawBar(g, glowBrush, c.X, c.Y, hy * armLen, thickness + 3f * scale, false, radius);
            // solid accent ring
            DrawBar(g, ringBrush, c.X, c.Y, hx * armLen, thickness, true, radius);
            DrawBar(g, ringBrush, c.X, c.Y, hy * armLen, thickness, false, radius);
            // white core
            DrawBar(g, coreBrush, c.X, c.Y, hx * armLen, coreThick, true, coreThick / 2f);
            DrawBar(g, coreBrush, c.X, c.Y, hy * armLen, coreThick, false, coreThick / 2f);
        }

        // ── Mid-edge rounded bars (top, left, right, bottom) ──
        for (int i = 4; i < 8; i++)
        {
            var c = CenterOf(handles[i]);
            bool horizontal = i is 4 or 7; // top/bottom bars run horizontally
            float len = barLen;
            RectangleF core = horizontal
                ? new RectangleF(c.X - len / 2f, c.Y - thickness / 2f, len, thickness)
                : new RectangleF(c.X - thickness / 2f, c.Y - len / 2f, thickness, len);

            RectangleF glow = RectangleF.Inflate(core, 2f * scale, 2f * scale);
            using (var glowPath = WindowsDockRenderer.RoundedRect(glow, radius + 2f * scale))
                g.FillPath(glowBrush, glowPath);
            using (var corePath = WindowsDockRenderer.RoundedRect(core, radius))
                g.FillPath(ringBrush, corePath);
            var inner = RectangleF.Inflate(core, -1f * scale, -1f * scale);
            if (inner.Width > 0 && inner.Height > 0)
                using (var innerPath = WindowsDockRenderer.RoundedRect(inner, Math.Max(1f, radius - 1f * scale)))
                    g.FillPath(coreBrush, innerPath);
        }

        g.SmoothingMode = oldSmoothing;
    }

    private static PointF CenterOf(Rectangle r) =>
        new(r.X + r.Width / 2f, r.Y + r.Height / 2f);

    // Draws one arm of a corner bracket as a rounded bar starting at (cx,cy).
    private static void DrawBar(Graphics g, Brush brush, float cx, float cy, float length, float thickness, bool horizontal, float radius)
    {
        RectangleF rect = horizontal
            ? new RectangleF(length < 0 ? cx + length : cx, cy - thickness / 2f, Math.Abs(length), thickness)
            : new RectangleF(cx - thickness / 2f, length < 0 ? cy + length : cy, thickness, Math.Abs(length));
        using var path = WindowsDockRenderer.RoundedRect(rect, radius);
        g.FillPath(brush, path);
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
