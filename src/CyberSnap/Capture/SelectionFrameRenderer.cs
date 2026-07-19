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
    /// Provisional window/desktop hover frame — same calibrated outline + size-scaled HUD
    /// brackets as the locked selection, slightly softer so it reads as "not locked yet".
    /// </summary>
    public static void DrawAutoDetectRectangle(Graphics g, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        DrawSelectionChrome(g, rect, fill: false, provisional: true);
    }

    /// <summary>
    /// Shared selection / auto-detect chrome.
    /// Single solid accent stroke (no dual dash — the two dashes mixed poorly) plus a thin
    /// dark understroke for contrast. HUD L-brackets scale with selection size so they stay
    /// readable on full-window captures, not only on small regions.
    /// </summary>
    private static void DrawSelectionChrome(Graphics g, Rectangle rect, bool fill, bool provisional)
    {
        var oldSmoothing = g.SmoothingMode;
        // Axis-aligned frames stay crisp without AA (matches the dim/desaturate hole edge).
        g.SmoothingMode = SmoothingMode.None;

        var accent = UiChrome.AccentColor;
        float scale = Math.Max(1f, (float)UiChrome.UiScale);

        // Stroke weights
        float edgeWidth = provisional ? 1.5f * scale : 1.75f * scale;
        int edgeAlpha = provisional ? 220 : 255;
        int underAlpha = provisional ? 90 : 110;
        int glowAlpha = provisional ? 32 : 42;
        float glowWidth = provisional ? 3.5f * scale : 4.5f * scale;

        // Brackets grow with the shorter side so window-sized holes still show the tool language.
        // ~7% of min side, clamped — small regions keep compact corners, large ones get presence.
        int minSide = Math.Min(rect.Width, rect.Height);
        int cornerLen = Math.Clamp(
            (int)Math.Round(minSide * 0.07f),
            UiChrome.ScaleInt(14),
            UiChrome.ScaleInt(52));
        float cornerPenWidth = Math.Clamp(minSide * 0.01f, 2f * scale, 3.5f * scale);
        if (provisional)
            cornerPenWidth = Math.Max(1.75f * scale, cornerPenWidth * 0.92f);
        int cornerOffset = Math.Max(1, (int)Math.Round(scale)); // snug to outline

        // Pixel-calibrated outline (GDI+ exclusive bottom-right).
        var outline = rect;
        outline.Width = Math.Max(1, outline.Width - 1);
        outline.Height = Math.Max(1, outline.Height - 1);

        // Soft ambient glow — tight, does not compete with the true edge.
        var glowOutline = outline;
        glowOutline.Inflate(Math.Max(1, (int)Math.Round(scale)), Math.Max(1, (int)Math.Round(scale)));
        using (var glowPen = new Pen(Color.FromArgb(glowAlpha, accent), glowWidth))
            g.DrawRectangle(glowPen, glowOutline);

        if (fill)
            g.FillRectangle(FillBrush, rect);

        // Dark understroke (solid) for contrast on light and busy wallpapers — not a second dash.
        using (var underPen = new Pen(Color.FromArgb(underAlpha, 0, 0, 0), edgeWidth + scale)
        {
            LineJoin = LineJoin.Miter
        })
            g.DrawRectangle(underPen, outline);

        // Single clean accent edge (solid).
        using (var edgePen = new Pen(Color.FromArgb(edgeAlpha, accent), edgeWidth)
        {
            LineJoin = LineJoin.Miter
        })
            g.DrawRectangle(edgePen, outline);

        // HUD L-brackets — the signature of the region tool; scale with selection size.
        using (var cornerGlow = new Pen(Color.FromArgb(provisional ? 50 : 70, accent), cornerPenWidth + 2f * scale)
        {
            LineJoin = LineJoin.Miter,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        using (var cornerPen = new Pen(Color.FromArgb(edgeAlpha, accent), cornerPenWidth)
        {
            LineJoin = LineJoin.Miter,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        using (var cornerCore = new Pen(Color.FromArgb(provisional ? 200 : 230, 255, 255, 255), Math.Max(1f, cornerPenWidth - scale))
        {
            LineJoin = LineJoin.Miter,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            DrawCornerBrackets(g, outline, cornerLen, cornerOffset, cornerGlow);
            DrawCornerBrackets(g, outline, cornerLen, cornerOffset, cornerPen);
            // Thin white core on top so brackets stay sharp over accent glow.
            DrawCornerBrackets(g, outline, Math.Max(cornerLen - 1, cornerLen * 3 / 4), cornerOffset, cornerCore);
        }

        g.SmoothingMode = oldSmoothing;
    }

    private static void DrawCornerBrackets(Graphics g, Rectangle outline, int cornerLen, int cornerOffset, Pen pen)
    {
        int x0 = outline.X - cornerOffset;
        int y0 = outline.Y - cornerOffset;
        int x1 = outline.Right + cornerOffset;
        int y1 = outline.Bottom + cornerOffset;

        // Top-left
        g.DrawLine(pen, x0, y0, x0 + cornerLen, y0);
        g.DrawLine(pen, x0, y0, x0, y0 + cornerLen);
        // Top-right
        g.DrawLine(pen, x1, y0, x1 - cornerLen, y0);
        g.DrawLine(pen, x1, y0, x1, y0 + cornerLen);
        // Bottom-left
        g.DrawLine(pen, x0, y1, x0 + cornerLen, y1);
        g.DrawLine(pen, x0, y1, x0, y1 - cornerLen);
        // Bottom-right
        g.DrawLine(pen, x1, y1, x1 - cornerLen, y1);
        g.DrawLine(pen, x1, y1, x1, y1 - cornerLen);
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
