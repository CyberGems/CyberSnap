using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;

namespace CyberSnap.Capture;

/// <summary>
/// Draws selection dimensions as premium pills anchored to the corner of the selection
/// nearest the cursor (so dragging any direction keeps the readout under the hand).
/// Prefer split pills (width on the horizontal edge, height on the vertical edge of that
/// corner); when they would clip or collide they fuse into one combined pill still near
/// that corner.
/// </summary>
internal static class SelectionSizeReadout
{
    private const int PadX = 8;
    private const int PadY = 4;
    private const int EdgeGap = 7;
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

    public static Rectangle GetBounds(Point cursor, Rectangle selection, Font font, Rectangle clientBounds, IReadOnlyList<string>? details = null)
    {
        if (!ShowDimensions)
            return Rectangle.Empty;

        var pills = Layout(cursor, selection, font, clientBounds, details);
        if (pills.Count == 0)
            return Rectangle.Empty;

        var union = pills[0].Rect;
        for (int i = 1; i < pills.Count; i++)
            union = Rectangle.Union(union, pills[i].Rect);
        return union;
    }

    public static void Draw(Graphics g, Point cursor, Rectangle selection, Font font, Rectangle clientBounds, IReadOnlyList<string>? details = null)
    {
        if (!ShowDimensions)
            return;

        var pills = Layout(cursor, selection, font, clientBounds, details);
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

    // ── Layout ────────────────────────────────────────────────────────────────

    private static List<Pill> Layout(Point cursor, Rectangle selection, Font font, Rectangle clientBounds, IReadOnlyList<string>? details)
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

        // Corner of the selection nearest the cursor (drag end / hover hand).
        var (preferRight, preferBottom) = NearestCorner(cursor, selection);

        // Width pill: outside the top or bottom edge, aligned to the preferred horizontal corner.
        int wY = preferBottom
            ? selection.Bottom + EdgeGap
            : selection.Top - EdgeGap - wSize.Height;
        int wX = preferRight
            ? selection.Right - wSize.Width
            : selection.Left;
        var topOrBottomRect = new Rectangle(wX, wY, wSize.Width, wSize.Height);

        // Height pill: outside the left or right edge, aligned to the preferred vertical corner.
        int hX = preferRight
            ? selection.Right + EdgeGap
            : selection.Left - EdgeGap - hSize.Width;
        int hY = preferBottom
            ? selection.Bottom - hSize.Height
            : selection.Top;
        var sideRect = new Rectangle(hX, hY, hSize.Width, hSize.Height);

        bool wFits = FitsInClient(topOrBottomRect, clientBounds);
        bool hFits = FitsInClient(sideRect, clientBounds);
        bool collide = topOrBottomRect.IntersectsWith(sideRect);

        if (wFits && hFits && !collide)
        {
            topOrBottomRect = ClampToClient(topOrBottomRect, clientBounds);
            sideRect = ClampToClient(sideRect, clientBounds);
            // Re-check after clamp (edge cases near screen corners).
            if (!topOrBottomRect.IntersectsWith(sideRect))
            {
                result.Add(new Pill { Lines = wLines, Rect = topOrBottomRect });
                result.Add(new Pill { Lines = hLines, Rect = sideRect });
                return result;
            }
        }

        // Merge: one pill with both chips, still parked at the nearest corner.
        var mergedLines = new List<Seg[]>(detailLines);
        mergedLines.Add(new[] { widthSeg, heightSeg });
        var mSize = MeasurePill(mergedLines, font, lineH, iconBox);
        var mRect = PlaceMergedNearCorner(selection, mSize, preferRight, preferBottom, clientBounds);
        result.Add(new Pill { Lines = mergedLines, Rect = mRect });
        return result;
    }

    /// <summary>
    /// Which corner of <paramref name="selection"/> is closest to <paramref name="cursor"/>.
    /// Empty cursor defaults to bottom-right (common release end when dragging top-left → bottom-right).
    /// </summary>
    private static (bool PreferRight, bool PreferBottom) NearestCorner(Point cursor, Rectangle selection)
    {
        if (cursor.IsEmpty)
            return (true, true);

        // Distances to the four corners (squared).
        long dTL = Dist2(cursor, selection.Left, selection.Top);
        long dTR = Dist2(cursor, selection.Right, selection.Top);
        long dBL = Dist2(cursor, selection.Left, selection.Bottom);
        long dBR = Dist2(cursor, selection.Right, selection.Bottom);

        long best = dTL;
        bool right = false, bottom = false;
        if (dTR < best) { best = dTR; right = true; bottom = false; }
        if (dBL < best) { best = dBL; right = false; bottom = true; }
        if (dBR < best) { right = true; bottom = true; }
        return (right, bottom);
    }

    private static long Dist2(Point p, int x, int y)
    {
        long dx = p.X - x;
        long dy = p.Y - y;
        return dx * dx + dy * dy;
    }

    private static Rectangle PlaceMergedNearCorner(
        Rectangle selection, Size mSize, bool preferRight, bool preferBottom, Rectangle clientBounds)
    {
        // Prefer outside the preferred vertical side, aligned to the preferred horizontal corner.
        int xOutside = preferRight ? selection.Right - mSize.Width : selection.Left;
        int yOutside = preferBottom
            ? selection.Bottom + EdgeGap
            : selection.Top - EdgeGap - mSize.Height;

        var outside = new Rectangle(xOutside, yOutside, mSize.Width, mSize.Height);
        if (FitsInClient(outside, clientBounds))
            return ClampToClient(outside, clientBounds);

        // Flip vertical outside.
        int yFlip = preferBottom
            ? selection.Top - EdgeGap - mSize.Height
            : selection.Bottom + EdgeGap;
        var flipped = new Rectangle(xOutside, yFlip, mSize.Width, mSize.Height);
        if (FitsInClient(flipped, clientBounds))
            return ClampToClient(flipped, clientBounds);

        // Park just inside the preferred corner of the selection.
        int xIn = preferRight
            ? selection.Right - EdgeGap - mSize.Width
            : selection.Left + EdgeGap;
        int yIn = preferBottom
            ? selection.Bottom - EdgeGap - mSize.Height
            : selection.Top + EdgeGap;
        return ClampToClient(new Rectangle(xIn, yIn, mSize.Width, mSize.Height), clientBounds);
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

    private static void DrawPill(Graphics g, Pill pill, Font font, int lineH, int iconBox)
    {
        var accent = UiChrome.AccentColor;
        var rect = pill.Rect;

        using (var shadowPath = WindowsDockRenderer.RoundedRect(new RectangleF(rect.X, rect.Y + 1.5f, rect.Width, rect.Height), Radius))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillPath(shadowBrush, shadowPath);

        using (var path = WindowsDockRenderer.RoundedRect(rect, Radius))
        {
            using var bg = new SolidBrush(Color.FromArgb(225, 18, 18, 20));
            g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(150, accent), 1f);
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
