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

    public static void DrawRectangle(
        Graphics g,
        Rectangle rect,
        bool fill = true,
        Color? accentOverride = null,
        Color? bracketAccentOverride = null)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        DrawSelectionChrome(
            g,
            rect,
            fill,
            provisional: false,
            accentOverride: accentOverride,
            bracketAccentOverride: bracketAccentOverride);
    }

    /// <summary>
    /// Provisional window/desktop hover frame — same calibrated outline + size-scaled HUD
    /// brackets as the locked selection, slightly softer so it reads as "not locked yet".
    /// </summary>
    public static void DrawAutoDetectRectangle(Graphics g, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        DrawSelectionChrome(
            g,
            rect,
            fill: false,
            provisional: true,
            accentOverride: null,
            bracketAccentOverride: null);
    }

    /// <summary>
    /// Shared selection / auto-detect chrome.
    /// Mid-edge accent stroke (segments stop before the corners) plus a thin dark understroke
    /// for contrast. HUD L-brackets own the corners alone — no second glow bracket and no
    /// thick rectangle miters sitting behind them.
    /// </summary>
    private static void DrawSelectionChrome(
        Graphics g,
        Rectangle rect,
        bool fill,
        bool provisional,
        Color? accentOverride,
        Color? bracketAccentOverride)
    {
        var oldSmoothing = g.SmoothingMode;
        // Axis-aligned frames stay crisp without AA (matches the dim/desaturate hole edge).
        g.SmoothingMode = SmoothingMode.None;

        var accent = accentOverride ?? UiChrome.AccentColor;
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

        // Soft ambient glow — mid-edge only so corners stay clean under the brackets.
        float glowClear = Math.Max(cornerLen * 0.85f, cornerPenWidth);
        using (var glowPen = new Pen(Color.FromArgb(glowAlpha, accent), glowWidth)
        {
            LineJoin = LineJoin.Miter,
            StartCap = LineCap.Flat,
            EndCap = LineCap.Flat
        })
            DrawEdgeSegments(g, outline.X, outline.Y, outline.Right, outline.Bottom, glowClear, glowPen);

        if (fill)
            g.FillRectangle(FillBrush, rect);

        float x0 = outline.X;
        float y0 = outline.Y;
        float x1 = outline.Right;
        float y1 = outline.Bottom;

        // Leave the corner zone to the L-brackets so thick edge pens never mint miter blobs
        // (or a second "handle") behind the cyan corners.
        float edgeClear = Math.Max(cornerLen * 0.92f, cornerPenWidth * 0.75f);

        // Dark understroke for contrast on light/busy wallpapers.
        using (var underPen = new Pen(Color.FromArgb(underAlpha, 0, 0, 0), edgeWidth + scale)
        {
            LineJoin = LineJoin.Miter,
            StartCap = LineCap.Flat,
            EndCap = LineCap.Flat
        })
            DrawEdgeSegments(g, x0, y0, x1, y1, edgeClear, underPen);

        // Single clean accent edge (solid), mid-sides only.
        using (var edgePen = new Pen(Color.FromArgb(edgeAlpha, accent), edgeWidth)
        {
            LineJoin = LineJoin.Miter,
            StartCap = LineCap.Flat,
            EndCap = LineCap.Flat
        })
            DrawEdgeSegments(g, x0, y0, x1, y1, edgeClear, edgePen);

        // Thin white segmented highlight adds the same layered readability as area selection.
        var dashSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var whiteDashPen = new Pen(
            Color.FromArgb(provisional ? 190 : 255, Color.White),
            Math.Max(1.25f, scale)))
        {
            whiteDashPen.DashStyle = DashStyle.Dash;
            whiteDashPen.DashPattern = new[] { 5f * scale, 4f * scale };
            whiteDashPen.LineJoin = LineJoin.Miter;
            whiteDashPen.StartCap = LineCap.Flat;
            whiteDashPen.EndCap = LineCap.Flat;
            DrawEdgeSegments(g, x0, y0, x1, y1, edgeClear, whiteDashPen);
        }
        g.SmoothingMode = dashSmoothing;

        Color bracketAccent = bracketAccentOverride ?? UiChrome.SelectionBracketColor;

        // One crisp bracket pass — no behind-glow L (that read as extra corner chrome).
        // Flat caps on two separate DrawLines leave a bite at the outer corner; a single
        // polyline with Miter join keeps the escuadra sealed.
        using (var cornerPen = new Pen(Color.FromArgb(edgeAlpha, bracketAccent), cornerPenWidth)
        {
            LineJoin = LineJoin.Miter,
            MiterLimit = 2.5f,
            StartCap = LineCap.Flat,
            EndCap = LineCap.Flat
        })
            DrawCornerBrackets(g, x0, y0, x1, y1, cornerLen, cornerPen);

        g.SmoothingMode = oldSmoothing;
    }

    /// <summary>Four mid-edge strokes that stop short of the corners (brackets own those).</summary>
    private static void DrawEdgeSegments(
        Graphics g, float x0, float y0, float x1, float y1, float cornerClear, Pen pen)
    {
        float clear = Math.Min(cornerClear, Math.Max(0f, (x1 - x0) * 0.45f));
        clear = Math.Min(clear, Math.Max(0f, (y1 - y0) * 0.45f));
        if (x1 - x0 > clear * 2f + 1f)
        {
            g.DrawLine(pen, x0 + clear, y0, x1 - clear, y0);
            g.DrawLine(pen, x0 + clear, y1, x1 - clear, y1);
        }
        if (y1 - y0 > clear * 2f + 1f)
        {
            g.DrawLine(pen, x0, y0 + clear, x0, y1 - clear);
            g.DrawLine(pen, x1, y0 + clear, x1, y1 - clear);
        }
    }

    private static void DrawCornerBrackets(Graphics g, float x0, float y0, float x1, float y1, float len, Pen pen)
    {
        // Each L is one polyline through the outer corner so the join fills the escuadra tip.
        DrawBracketL(g, pen, x0 + len, y0, x0, y0, x0, y0 + len); // top-left
        DrawBracketL(g, pen, x1 - len, y0, x1, y0, x1, y0 + len); // top-right
        DrawBracketL(g, pen, x0 + len, y1, x0, y1, x0, y1 - len); // bottom-left
        DrawBracketL(g, pen, x1 - len, y1, x1, y1, x1, y1 - len); // bottom-right
    }

    private static void DrawBracketL(
        Graphics g, Pen pen,
        float armAx, float armAy,
        float cornerX, float cornerY,
        float armBx, float armBy)
    {
        using var path = new GraphicsPath();
        path.AddLines(new[]
        {
            new PointF(armAx, armAy),
            new PointF(cornerX, cornerY),
            new PointF(armBx, armBy)
        });
        g.DrawPath(pen, path);
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
