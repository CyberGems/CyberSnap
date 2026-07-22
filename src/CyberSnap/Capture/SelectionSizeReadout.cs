using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;

namespace CyberSnap.Capture;

/// <summary>
/// Draws selection dimensions as premium pills anchored to the corner of the selection
/// nearest the cursor (so dragging any direction keeps the readout under the hand).
/// Prefer split pills (width on the horizontal edge, height on the vertical edge of that
/// corner); when they would clip, collide with each other, or hit reserved chrome
/// (e.g. confirm buttons) they try other corners / fuse into one pill.
/// </summary>
internal static class SelectionSizeReadout
{
    private const int PadX = 8;
    private const int PadY = 4;
    private const int EdgeGap = 7;
    private const int ObstaclePad = 8;  // gap kept clear of reserved chrome (confirm buttons)
    private const int SegGap = 9;
    private const int IconTextGap = 4;
    private const int LineGap = 2;
    private const float Radius = 6f;

    /// <summary>
    /// Global toggle for the dimension pills, driven by the
    /// "Show selection size" preference. Set before launching a capture.
    /// </summary>
    public static bool ShowDimensions { get; set; } = true;

    private enum Arrow { None, Horizontal, Vertical }

    private readonly record struct Seg(Arrow Icon, string Text);

    private sealed class Pill
    {
        public required List<Seg[]> Lines;
        public Rectangle Rect;
    }

    public static Rectangle GetBounds(
        Point cursor,
        Rectangle selection,
        Font font,
        Rectangle clientBounds,
        IReadOnlyList<string>? details = null,
        IReadOnlyList<Rectangle>? avoidRects = null)
    {
        if (!ShowDimensions)
            return Rectangle.Empty;

        var pills = Layout(cursor, selection, font, clientBounds, details, avoidRects);
        if (pills.Count == 0)
            return Rectangle.Empty;

        var union = pills[0].Rect;
        for (int i = 1; i < pills.Count; i++)
            union = Rectangle.Union(union, pills[i].Rect);
        return union;
    }

    public static void Draw(
        Graphics g,
        Point cursor,
        Rectangle selection,
        Font font,
        Rectangle clientBounds,
        IReadOnlyList<string>? details = null,
        IReadOnlyList<Rectangle>? avoidRects = null)
    {
        if (!ShowDimensions)
            return;

        var pills = Layout(cursor, selection, font, clientBounds, details, avoidRects);
        if (pills.Count == 0)
            return;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int lineH = LineHeight(font);
        int iconBox = IconBox(lineH);
        foreach (var pill in pills)
            DrawPill(g, pill, font, lineH, iconBox);

        g.SmoothingMode = oldSmoothing;
    }

    /// <summary>Width of the drag-grip chip that sits left of the confirm size pill.</summary>
    public static int ConfirmDragGripWidth(Font font)
    {
        int lineH = LineHeight(font);
        return Math.Max(UiChrome.ScaleInt(18), lineH + PadY * 2);
    }

    /// <summary>
    /// Confirm-mode size chip: above the frame, left-aligned (top-left outside).
    /// Falls back to interior top-left. Includes a left grip handle in the returned hit bounds.
    /// </summary>
    public static Rectangle GetConfirmDragPillBounds(
        Rectangle selection,
        Font font,
        Rectangle clientBounds,
        IReadOnlyList<Rectangle>? avoidRects = null)
    {
        if (!ShowDimensions || selection.Width <= 2 || selection.Height <= 2)
            return Rectangle.Empty;

        if (!TryLayoutConfirmDragPill(selection, font, clientBounds, avoidRects, out var pillRect, out var gripRect, out _))
            return Rectangle.Empty;

        return Rectangle.Union(pillRect, gripRect);
    }

    public static void DrawConfirmDragPill(
        Graphics g,
        Rectangle selection,
        Font font,
        Rectangle clientBounds,
        IReadOnlyList<Rectangle>? avoidRects = null,
        bool hovered = false)
    {
        if (!ShowDimensions)
            return;

        if (!TryLayoutConfirmDragPill(selection, font, clientBounds, avoidRects, out var pillRect, out var gripRect, out var lines))
            return;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int lineH = LineHeight(font);
        int iconBox = IconBox(lineH);

        DrawPill(g, new Pill { Lines = lines, Rect = pillRect }, font, lineH, iconBox, emphasize: hovered);
        DrawDragGrip(g, gripRect, hovered);

        g.SmoothingMode = oldSmoothing;
    }

    private static bool TryLayoutConfirmDragPill(
        Rectangle selection,
        Font font,
        Rectangle clientBounds,
        IReadOnlyList<Rectangle>? avoidRects,
        out Rectangle pillRect,
        out Rectangle gripRect,
        out List<Seg[]> lines)
    {
        pillRect = Rectangle.Empty;
        gripRect = Rectangle.Empty;
        lines = new List<Seg[]>();

        if (selection.Width <= 2 || selection.Height <= 2)
            return false;

        int lineH = LineHeight(font);
        int iconBox = IconBox(lineH);
        var widthSeg = new Seg(Arrow.Horizontal, selection.Width.ToString());
        var heightSeg = new Seg(Arrow.Vertical, selection.Height.ToString());
        lines = new List<Seg[]> { new[] { widthSeg, heightSeg } };
        var size = MeasurePill(lines, font, lineH, iconBox);
        int gripW = ConfirmDragGripWidth(font);
        int gripGap = UiChrome.ScaleInt(4);
        int unitW = gripW + gripGap + size.Width;
        int unitH = Math.Max(size.Height, gripW); // grip is square-ish

        // 1) Outside top edge, left-aligned (preferred: above the selection).
        var unitTop = new Rectangle(
            selection.Left,
            selection.Top - EdgeGap - unitH,
            unitW,
            unitH);

        // 2) Interior top-left when the exterior clips the monitor or hits chrome.
        var unitInterior = new Rectangle(
            selection.Left + EdgeGap,
            selection.Top + EdgeGap,
            unitW,
            unitH);

        foreach (var unit in new[] { unitTop, unitInterior })
        {
            if (!FitsInClient(unit, clientBounds))
                continue;
            if (HitsObstacle(unit, avoidRects))
                continue;

            // Grip on the left of the size chip.
            gripRect = new Rectangle(unit.X, unit.Y + (unit.Height - unitH) / 2, gripW, unitH);
            // Center grip vertically in unit (unitH == grip height here).
            gripRect = new Rectangle(unit.X, unit.Y, gripW, unitH);
            pillRect = new Rectangle(
                unit.X + gripW + gripGap,
                unit.Y + (unitH - size.Height) / 2,
                size.Width,
                size.Height);
            return true;
        }

        // Last resort: clamp interior unit into client / selection.
        var clampedUnit = ClampToClient(unitInterior, clientBounds);
        if (selection.Width > unitW + EdgeGap * 2 && selection.Height > unitH + EdgeGap * 2)
        {
            clampedUnit.X = Math.Clamp(clampedUnit.X, selection.Left + EdgeGap, selection.Right - unitW - EdgeGap);
            clampedUnit.Y = Math.Clamp(clampedUnit.Y, selection.Top + EdgeGap, selection.Bottom - unitH - EdgeGap);
        }
        gripRect = new Rectangle(clampedUnit.X, clampedUnit.Y, gripW, unitH);
        pillRect = new Rectangle(
            clampedUnit.X + gripW + gripGap,
            clampedUnit.Y + (unitH - size.Height) / 2,
            size.Width,
            size.Height);
        return true;
    }

    private static void DrawDragGrip(Graphics g, Rectangle rect, bool hovered)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var accent = UiChrome.AccentColor;
        float radius = Radius;

        using (var shadowPath = WindowsDockRenderer.RoundedRect(
                   new RectangleF(rect.X, rect.Y + 1.5f, rect.Width, rect.Height), radius))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(hovered ? 100 : 70, 0, 0, 0)))
            g.FillPath(shadowBrush, shadowPath);

        using (var path = WindowsDockRenderer.RoundedRect(rect, radius))
        {
            int bgA = hovered ? 240 : 225;
            using var bg = new SolidBrush(Color.FromArgb(bgA, 18, 18, 20));
            g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(hovered ? 220 : 150, accent), hovered ? 1.4f : 1f);
            g.DrawPath(border, path);
        }

        // 2×3 grip dots (reorder / drag affordance).
        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;
        float stepX = UiChrome.ScaleFloat(4.2f);
        float stepY = UiChrome.ScaleFloat(4.2f);
        float r = UiChrome.ScaleFloat(1.35f);
        int a = hovered ? 240 : 190;
        using var dot = new SolidBrush(Color.FromArgb(a, accent));
        for (int row = -1; row <= 1; row++)
        {
            for (int col = -1; col <= 0; col++)
            {
                float dx = (col + 0.5f) * stepX;
                float dy = row * stepY;
                g.FillEllipse(dot, cx + dx - r, cy + dy - r, r * 2f, r * 2f);
            }
        }
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private static List<Pill> Layout(
        Point cursor,
        Rectangle selection,
        Font font,
        Rectangle clientBounds,
        IReadOnlyList<string>? details,
        IReadOnlyList<Rectangle>? avoidRects)
    {
        var result = new List<Pill>(2);
        if (selection.Width <= 2 || selection.Height <= 2)
            return result;

        int lineH = LineHeight(font);
        int iconBox = IconBox(lineH);

        var widthSeg = new Seg(Arrow.Horizontal, selection.Width.ToString());
        var heightSeg = new Seg(Arrow.Vertical, selection.Height.ToString());
        var detailLines = BuildDetailLines(details);

        var wLines = new List<Seg[]>(detailLines);
        wLines.Add(new[] { widthSeg });
        var hLines = new List<Seg[]> { new[] { heightSeg } };

        var wSize = MeasurePill(wLines, font, lineH, iconBox);
        var hSize = MeasurePill(hLines, font, lineH, iconBox);

        var mergedLines = new List<Seg[]>(detailLines);
        mergedLines.Add(new[] { widthSeg, heightSeg });
        var mSize = MeasurePill(mergedLines, font, lineH, iconBox);

        // Try corners nearest the cursor first; skip any placement that hits reserved chrome.
        foreach (var (preferRight, preferBottom) in CornersByDistance(cursor, selection))
        {
            if (TryPlaceSplit(
                    selection, wSize, hSize, preferRight, preferBottom, clientBounds, avoidRects,
                    out var topOrBottomRect, out var sideRect))
            {
                result.Add(new Pill { Lines = wLines, Rect = topOrBottomRect });
                result.Add(new Pill { Lines = hLines, Rect = sideRect });
                return result;
            }

            if (TryPlaceMerged(
                    selection, mSize, preferRight, preferBottom, clientBounds, avoidRects,
                    out var mRect))
            {
                result.Add(new Pill { Lines = mergedLines, Rect = mRect });
                return result;
            }
        }

        // Last resort: merged at nearest corner, clamp into client (may still graze obstacles
        // if the selection is tiny and chrome fills every side).
        var (fallbackRight, fallbackBottom) = CornersByDistance(cursor, selection)[0];
        var fallback = PlaceMergedNearCorner(selection, mSize, fallbackRight, fallbackBottom, clientBounds, avoidRects);
        result.Add(new Pill { Lines = mergedLines, Rect = fallback });
        return result;
    }

    private static bool TryPlaceSplit(
        Rectangle selection,
        Size wSize,
        Size hSize,
        bool preferRight,
        bool preferBottom,
        Rectangle clientBounds,
        IReadOnlyList<Rectangle>? avoidRects,
        out Rectangle topOrBottomRect,
        out Rectangle sideRect)
    {
        int wY = preferBottom
            ? selection.Bottom + EdgeGap
            : selection.Top - EdgeGap - wSize.Height;
        int wX = preferRight
            ? selection.Right - wSize.Width
            : selection.Left;
        topOrBottomRect = new Rectangle(wX, wY, wSize.Width, wSize.Height);

        int hX = preferRight
            ? selection.Right + EdgeGap
            : selection.Left - EdgeGap - hSize.Width;
        int hY = preferBottom
            ? selection.Bottom - hSize.Height
            : selection.Top;
        sideRect = new Rectangle(hX, hY, hSize.Width, hSize.Height);

        if (!FitsInClient(topOrBottomRect, clientBounds) || !FitsInClient(sideRect, clientBounds))
            return false;
        if (topOrBottomRect.IntersectsWith(sideRect))
            return false;
        if (HitsObstacle(topOrBottomRect, avoidRects) || HitsObstacle(sideRect, avoidRects))
            return false;

        topOrBottomRect = ClampToClient(topOrBottomRect, clientBounds);
        sideRect = ClampToClient(sideRect, clientBounds);
        if (topOrBottomRect.IntersectsWith(sideRect))
            return false;
        if (HitsObstacle(topOrBottomRect, avoidRects) || HitsObstacle(sideRect, avoidRects))
            return false;

        return true;
    }

    private static bool TryPlaceMerged(
        Rectangle selection,
        Size mSize,
        bool preferRight,
        bool preferBottom,
        Rectangle clientBounds,
        IReadOnlyList<Rectangle>? avoidRects,
        out Rectangle mRect)
    {
        foreach (var candidate in EnumerateMergedCandidates(selection, mSize, preferRight, preferBottom))
        {
            if (!FitsInClient(candidate, clientBounds))
                continue;
            if (HitsObstacle(candidate, avoidRects))
                continue;
            mRect = ClampToClient(candidate, clientBounds);
            if (HitsObstacle(mRect, avoidRects))
                continue;
            return true;
        }

        mRect = Rectangle.Empty;
        return false;
    }

    private static IEnumerable<Rectangle> EnumerateMergedCandidates(
        Rectangle selection, Size mSize, bool preferRight, bool preferBottom)
    {
        int xAligned = preferRight ? selection.Right - mSize.Width : selection.Left;

        // Outside preferred horizontal edge.
        yield return new Rectangle(
            xAligned,
            preferBottom ? selection.Bottom + EdgeGap : selection.Top - EdgeGap - mSize.Height,
            mSize.Width, mSize.Height);

        // Outside opposite horizontal edge.
        yield return new Rectangle(
            xAligned,
            preferBottom ? selection.Top - EdgeGap - mSize.Height : selection.Bottom + EdgeGap,
            mSize.Width, mSize.Height);

        // Outside preferred vertical edge, aligned to preferred vertical corner.
        int yAligned = preferBottom ? selection.Bottom - mSize.Height : selection.Top;
        yield return new Rectangle(
            preferRight ? selection.Right + EdgeGap : selection.Left - EdgeGap - mSize.Width,
            yAligned,
            mSize.Width, mSize.Height);

        // Outside opposite vertical edge.
        yield return new Rectangle(
            preferRight ? selection.Left - EdgeGap - mSize.Width : selection.Right + EdgeGap,
            yAligned,
            mSize.Width, mSize.Height);

        // Inside preferred corner (last within this corner family).
        yield return new Rectangle(
            preferRight ? selection.Right - EdgeGap - mSize.Width : selection.Left + EdgeGap,
            preferBottom ? selection.Bottom - EdgeGap - mSize.Height : selection.Top + EdgeGap,
            mSize.Width, mSize.Height);
    }

    /// <summary>
    /// Corners ordered by distance to cursor (nearest first). Empty cursor → BR first.
    /// </summary>
    private static (bool PreferRight, bool PreferBottom)[] CornersByDistance(Point cursor, Rectangle selection)
    {
        var corners = new (bool Right, bool Bottom, long Dist)[]
        {
            (false, false, cursor.IsEmpty ? long.MaxValue / 4 : Dist2(cursor, selection.Left, selection.Top)),
            (true, false, cursor.IsEmpty ? long.MaxValue / 3 : Dist2(cursor, selection.Right, selection.Top)),
            (false, true, cursor.IsEmpty ? long.MaxValue / 2 : Dist2(cursor, selection.Left, selection.Bottom)),
            (true, true, cursor.IsEmpty ? 0 : Dist2(cursor, selection.Right, selection.Bottom)),
        };

        Array.Sort(corners, (a, b) => a.Dist.CompareTo(b.Dist));
        return new[]
        {
            (corners[0].Right, corners[0].Bottom),
            (corners[1].Right, corners[1].Bottom),
            (corners[2].Right, corners[2].Bottom),
            (corners[3].Right, corners[3].Bottom),
        };
    }

    private static long Dist2(Point p, int x, int y)
    {
        long dx = p.X - x;
        long dy = p.Y - y;
        return dx * dx + dy * dy;
    }

    private static Rectangle PlaceMergedNearCorner(
        Rectangle selection,
        Size mSize,
        bool preferRight,
        bool preferBottom,
        Rectangle clientBounds,
        IReadOnlyList<Rectangle>? avoidRects)
    {
        if (TryPlaceMerged(selection, mSize, preferRight, preferBottom, clientBounds, avoidRects, out var good))
            return good;

        // Absolute fallback: clamp outside preferred edge even if it clips obstacles.
        int xOutside = preferRight ? selection.Right - mSize.Width : selection.Left;
        int yOutside = preferBottom
            ? selection.Bottom + EdgeGap
            : selection.Top - EdgeGap - mSize.Height;
        return ClampToClient(new Rectangle(xOutside, yOutside, mSize.Width, mSize.Height), clientBounds);
    }

    private static bool HitsObstacle(Rectangle r, IReadOnlyList<Rectangle>? avoidRects)
    {
        if (avoidRects is null || avoidRects.Count == 0)
            return false;

        foreach (var raw in avoidRects)
        {
            if (raw.Width <= 0 || raw.Height <= 0)
                continue;
            var pad = raw;
            pad.Inflate(ObstaclePad, ObstaclePad);
            if (r.IntersectsWith(pad))
                return true;
        }

        return false;
    }

    private static bool FitsInClient(Rectangle r, Rectangle client)
        => r.Left >= client.Left
           && r.Top >= client.Top
           && r.Right <= client.Right
           && r.Bottom <= client.Bottom;

    private static Rectangle ClampToClient(Rectangle r, Rectangle client)
    {
        r.X = Clamp(r.X, client.Left, Math.Max(client.Left, client.Right - r.Width));
        r.Y = Clamp(r.Y, client.Top, Math.Max(client.Top, client.Bottom - r.Height));
        return r;
    }

    private static List<Seg[]> BuildDetailLines(IReadOnlyList<string>? details)
    {
        var lines = new List<Seg[]>();
        if (details is null)
            return lines;
        foreach (var d in details)
            if (!string.IsNullOrWhiteSpace(d))
                lines.Add(new[] { new Seg(Arrow.None, d) });
        return lines;
    }

    // ── Measurement ─────────────────────────────────────────────────────────────

    private static Size MeasurePill(List<Seg[]> lines, Font font, int lineH, int iconBox)
    {
        int contentW = 1;
        foreach (var line in lines)
            contentW = Math.Max(contentW, MeasureLine(line, font, iconBox));
        int contentH = lines.Count * lineH + Math.Max(0, lines.Count - 1) * LineGap;
        return new Size(contentW + PadX * 2, contentH + PadY * 2);
    }

    private static int MeasureLine(Seg[] segs, Font font, int iconBox)
    {
        int w = 0;
        for (int i = 0; i < segs.Length; i++)
        {
            if (segs[i].Icon != Arrow.None)
                w += iconBox + IconTextGap;
            w += MeasureText(segs[i].Text, font);
            if (i < segs.Length - 1)
                w += SegGap;
        }
        return w;
    }

    private static int MeasureText(string text, Font font)
        => TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

    private static int LineHeight(Font font)
        => Math.Max(1, TextRenderer.MeasureText("0", font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height);

    private static int IconBox(int lineH) => Math.Max(9, (int)(lineH * 0.72f));

    // ── Rendering ───────────────────────────────────────────────────────────────

    private static void DrawPill(Graphics g, Pill pill, Font font, int lineH, int iconBox, bool emphasize = false)
    {
        var accent = UiChrome.AccentColor;
        var rect = pill.Rect;

        using (var shadowPath = WindowsDockRenderer.RoundedRect(new RectangleF(rect.X, rect.Y + 1.5f, rect.Width, rect.Height), Radius))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(emphasize ? 100 : 70, 0, 0, 0)))
            g.FillPath(shadowBrush, shadowPath);

        using (var path = WindowsDockRenderer.RoundedRect(rect, Radius))
        {
            int bgA = emphasize ? 240 : 225;
            using var bg = new SolidBrush(Color.FromArgb(bgA, 18, 18, 20));
            g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(emphasize ? 220 : 150, accent), emphasize ? 1.4f : 1f);
            g.DrawPath(border, path);
        }

        int contentW = rect.Width - PadX * 2;
        var textColor = Color.FromArgb(245, 255, 255, 255);

        for (int li = 0; li < pill.Lines.Count; li++)
        {
            var segs = pill.Lines[li];
            int lineW = MeasureLine(segs, font, iconBox);
            int x = rect.X + PadX + Math.Max(0, (contentW - lineW) / 2);
            int y = rect.Y + PadY + li * (lineH + LineGap);

            foreach (var seg in segs)
            {
                if (seg.Icon != Arrow.None)
                {
                    DrawArrow(g, seg.Icon, new Rectangle(x, y, iconBox, lineH), accent);
                    x += iconBox + IconTextGap;
                }

                TextRenderer.DrawText(g, seg.Text, font, new Point(x, y), textColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                x += MeasureText(seg.Text, font) + SegGap;
            }
        }
    }

    private static void DrawArrow(Graphics g, Arrow arrow, Rectangle box, Color color)
    {
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float head = Math.Max(2.2f, box.Width * 0.22f);
        float cxf = box.Left + box.Width / 2f;
        float cyf = box.Top + box.Height / 2f;

        if (arrow == Arrow.Horizontal)
        {
            float x0 = box.Left + 1.5f;
            float x1 = box.Right - 1.5f;
            g.DrawLine(pen, x0, cyf, x1, cyf);
            g.DrawLine(pen, x0, cyf, x0 + head, cyf - head);
            g.DrawLine(pen, x0, cyf, x0 + head, cyf + head);
            g.DrawLine(pen, x1, cyf, x1 - head, cyf - head);
            g.DrawLine(pen, x1, cyf, x1 - head, cyf + head);
        }
        else
        {
            float y0 = box.Top + 2.5f;
            float y1 = box.Bottom - 2.5f;
            g.DrawLine(pen, cxf, y0, cxf, y1);
            g.DrawLine(pen, cxf, y0, cxf - head, y0 + head);
            g.DrawLine(pen, cxf, y0, cxf + head, y0 + head);
            g.DrawLine(pen, cxf, y1, cxf - head, y1 - head);
            g.DrawLine(pen, cxf, y1, cxf + head, y1 - head);
        }
    }

    private static int Clamp(int value, int min, int max)
        => max < min ? min : Math.Clamp(value, min, max);
}
