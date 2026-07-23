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

        // HUD L-brackets — the signature of the region tool; scale with selection size.
        int minSide = Math.Min(rect.Width, rect.Height);
        int cornerLen = Math.Clamp(
            (int)Math.Round(minSide * 0.06f),
            UiChrome.ScaleInt(14),
            UiChrome.ScaleInt(38));
        float cornerPenWidth = Math.Clamp(minSide * 0.009f, 4f * scale, 6f * scale); // double thickness
        if (provisional)
            cornerPenWidth = Math.Max(1.5f * scale, cornerPenWidth * 0.92f);

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

        // The stroke centerline is exactly on 'outline' because DrawRectangle uses PenAlignment.Center by default
        float x0 = outline.X;
        float y0 = outline.Y;
        float x1 = outline.Right;
        float y1 = outline.Bottom;

        Color bracketAccent = Color.FromArgb(0x00, 0xFF, 0xFF); // #00FFFF — matches the widget capture icon cyan

        using (var cornerGlow = new Pen(Color.FromArgb(provisional ? 50 : 70, bracketAccent), cornerPenWidth + 3f * scale)
        {
            LineJoin = LineJoin.Miter,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        using (var cornerPen = new Pen(Color.FromArgb(edgeAlpha, bracketAccent), cornerPenWidth)
        {
            LineJoin = LineJoin.Miter,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            DrawCornerBrackets(g, x0, y0, x1, y1, cornerLen, cornerGlow);
            DrawCornerBrackets(g, x0, y0, x1, y1, cornerLen, cornerPen);
        }

        g.SmoothingMode = oldSmoothing;
    }

    private static void DrawCornerBrackets(Graphics g, float x0, float y0, float x1, float y1, float len, Pen pen)
    {
        // Top-left
        g.DrawLine(pen, x0, y0, x0 + len, y0);
        g.DrawLine(pen, x0, y0, x0, y0 + len);
        // Top-right
        g.DrawLine(pen, x1, y0, x1 - len, y0);
        g.DrawLine(pen, x1, y0, x1, y0 + len);
        // Bottom-left
        g.DrawLine(pen, x0, y1, x0 + len, y1);
        g.DrawLine(pen, x0, y1, x0, y1 - len);
        // Bottom-right
        g.DrawLine(pen, x1, y1, x1 - len, y1);
        g.DrawLine(pen, x1, y1, x1, y1 - len);
    }

    /// <summary>
    /// Draws the mid-edge circular dot handles (4) for the confirmation frame.
    /// Corners use L-brackets drawn by the main selection chrome.
    /// </summary>
    public static void DrawConfirmHandles(Graphics g, Rectangle[] handles)
    {
        if (handles.Length < 8)
            return;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var accent = Color.FromArgb(0x00, 0xFF, 0xFF); // #00FFFF — matches the widget capture icon cyan
        float scale = Math.Max(1f, (float)UiChrome.UiScale);
        float dotRadius = 4.5f * scale;     // main dot radius
        float glowRadius = dotRadius + 3f * scale;
        float coreRadius = dotRadius - 1.5f * scale;

        using var glowBrush = new SolidBrush(Color.FromArgb(50, accent));
        using var ringBrush = new SolidBrush(accent);
        using var coreBrush = new SolidBrush(Color.White);

        // Only draw the 4 mid-edge circular dots. Indices 4-7 are Top, Left, Right, Bottom.
        for (int i = 4; i < 8; i++)
        {
            var c = CenterOf(handles[i]);

            // For mid-edge dots on the selection rect, we use the handle center exactly.
            // (The stroke is drawn on outline, which matches rect's Left/Top, and is -1 for Right/Bottom).
            float cx = c.X;
            float cy = c.Y;
            
            // Adjust Right/Bottom inward by 1px to match the stroke's GDI+ -1 correction
            if (i == 6) cx -= 1f; // Right
            if (i == 7) cy -= 1f; // Bottom

            // Glow halo
            g.FillEllipse(glowBrush, cx - glowRadius, cy - glowRadius, glowRadius * 2, glowRadius * 2);
            // Accent ring
            g.FillEllipse(ringBrush, cx - dotRadius, cy - dotRadius, dotRadius * 2, dotRadius * 2);
            // White core
            g.FillEllipse(coreBrush, cx - coreRadius, cy - coreRadius, coreRadius * 2, coreRadius * 2);
        }

        g.SmoothingMode = oldSmoothing;
    }

    private static PointF CenterOf(Rectangle r) =>
        new(r.X + r.Width / 2f, r.Y + r.Height / 2f);

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
