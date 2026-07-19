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

        DrawSelectionChrome(g, rect, fill, provisional: false);
    }

    /// <summary>
    /// Provisional window/desktop hover frame. Same geometry language as
    /// <see cref="DrawRectangle"/> (dash + dual stroke + HUD corners) so it sits on the
    /// dim/desaturate hole edge, but slightly softer to read as "not locked yet".
    /// </summary>
    public static void DrawAutoDetectRectangle(Graphics g, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        // Same outline math as the real selection frame so the stroke shares the hole edge
        // used by dim/desaturate (no heavier glow that optically shifts the border outward).
        DrawSelectionChrome(g, rect, fill: false, provisional: true);
    }

    /// <summary>
    /// Shared selection / auto-detect chrome. <paramref name="provisional"/> softens glow and
    /// accent alpha for hover auto-detect; confirmed drag selection stays full strength.
    /// </summary>
    private static void DrawSelectionChrome(Graphics g, Rectangle rect, bool fill, bool provisional)
    {
        var oldSmoothing = g.SmoothingMode;
        // Axis-aligned frames read sharper without AA (avoids half-pixel "float" vs the hole).
        g.SmoothingMode = provisional ? SmoothingMode.None : SmoothingMode.AntiAlias;

        var accent = UiChrome.AccentColor;
        int glowAlpha = provisional ? 28 : 40;
        float glowWidth = provisional ? 4f : 5f;
        int glowInflate = provisional ? 1 : 2;
        float outerWidth = provisional ? 1.25f : 1.5f;
        int outerAlpha = provisional ? 210 : 255;
        int innerAlpha = provisional ? 160 : 200;
        int cornerLen = provisional ? 10 : 12;
        int cornerOffset = 2; // sit tight to the outline — old auto-detect used 4 and felt offset
        float cornerPenWidth = provisional ? 1.75f : 2f;

        // Ambient accent glow — kept tight so it does not read as the true border.
        var glowRect = rect;
        glowRect.Inflate(glowInflate, glowInflate);
        // GDI+ DrawRectangle uses an exclusive bottom-right for the logical box.
        var glowOutline = glowRect;
        glowOutline.Width = Math.Max(1, glowOutline.Width - 1);
        glowOutline.Height = Math.Max(1, glowOutline.Height - 1);
        using (var glowPen = new Pen(Color.FromArgb(glowAlpha, accent), glowWidth))
            g.DrawRectangle(glowPen, glowOutline);

        if (fill)
            g.FillRectangle(FillBrush, rect);

        // Pixel-calibrated outline: same -1 width/height as the locked selection frame.
        var outline = rect;
        outline.Width = Math.Max(1, outline.Width - 1);
        outline.Height = Math.Max(1, outline.Height - 1);

        float[] dash = { 6f, 4f };

        // Outer accent dash
        using (var outerPen = new Pen(Color.FromArgb(outerAlpha, accent), outerWidth)
        {
            LineJoin = LineJoin.Miter,
            DashStyle = DashStyle.Dash,
            DashPattern = dash
        })
            g.DrawRectangle(outerPen, outline);

        // Inner crisp white dash for contrast on any wallpaper
        var innerOutline = outline;
        innerOutline.Inflate(-1, -1);
        if (innerOutline.Width > 0 && innerOutline.Height > 0)
        {
            using var innerPen = new Pen(Color.FromArgb(innerAlpha, 255, 255, 255), 1f)
            {
                LineJoin = LineJoin.Miter,
                DashStyle = DashStyle.Dash,
                DashPattern = dash
            };
            g.DrawRectangle(innerPen, innerOutline);
        }

        // HUD corner brackets — same inset language as locked selection
        using (var cornerPen = new Pen(Color.FromArgb(outerAlpha, accent), cornerPenWidth)
        {
            LineJoin = LineJoin.Miter
        })
        {
            int x0 = outline.X - cornerOffset;
            int y0 = outline.Y - cornerOffset;
            int x1 = outline.Right + cornerOffset;
            int y1 = outline.Bottom + cornerOffset;

            // Top-left
            g.DrawLine(cornerPen, x0, y0, x0 + cornerLen, y0);
            g.DrawLine(cornerPen, x0, y0, x0, y0 + cornerLen);
            // Top-right
            g.DrawLine(cornerPen, x1, y0, x1 - cornerLen, y0);
            g.DrawLine(cornerPen, x1, y0, x1, y0 + cornerLen);
            // Bottom-left
            g.DrawLine(cornerPen, x0, y1, x0 + cornerLen, y1);
            g.DrawLine(cornerPen, x0, y1, x0, y1 - cornerLen);
            // Bottom-right
            g.DrawLine(cornerPen, x1, y1, x1 - cornerLen, y1);
            g.DrawLine(cornerPen, x1, y1, x1, y1 - cornerLen);
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
