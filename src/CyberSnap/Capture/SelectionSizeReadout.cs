using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Helpers;

namespace CyberSnap.Capture;

/// <summary>
/// Draws the selection dimensions as premium pills anchored to the selection edges:
/// a width pill centered above the top edge (↔) and a height pill centered on the left
/// edge (↕). When the selection sits too close to a screen edge — so a pill would clip
/// off-screen or the two pills would collide — they fuse into a single combined pill.
/// Anchoring to the selection (not the cursor) keeps the readout clear of the magnifier.
/// </summary>
internal static class SelectionSizeReadout
{
    private const int PadX = 8;          // pill horizontal inner padding
    private const int PadY = 4;          // pill vertical inner padding
    private const int EdgeGap = 7;       // gap between pill and selection edge
    private const int SegGap = 9;        // gap between width/height chips when merged
    private const int IconTextGap = 4;   // gap between arrow icon and its number
    private const int LineGap = 2;       // gap between stacked lines inside a pill
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

        var pills = Layout(selection, font, clientBounds, details);
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

        var pills = Layout(selection, font, clientBounds, details);
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

    private static List<Pill> Layout(Rectangle selection, Font font, Rectangle clientBounds, IReadOnlyList<string>? details)
    {
        var result = new List<Pill>(2);
        if (selection.Width <= 2 || selection.Height <= 2)
            return result;

        int lineH = LineHeight(font);
        int iconBox = IconBox(lineH);

        var widthSeg = new Seg(Arrow.Horizontal, selection.Width.ToString());
        var heightSeg = new Seg(Arrow.Vertical, selection.Height.ToString());
        var detailLines = BuildDetailLines(details);

        // Candidate split pills: width on top edge, height on left edge.
        var wLines = new List<Seg[]>(detailLines);
        wLines.Add(new[] { widthSeg });
        var hLines = new List<Seg[]> { new[] { heightSeg } };

        var wSize = MeasurePill(wLines, font, lineH, iconBox);
        var hSize = MeasurePill(hLines, font, lineH, iconBox);

        int cx = selection.Left + selection.Width / 2;
        int cy = selection.Top + selection.Height / 2;

        var topRect = new Rectangle(cx - wSize.Width / 2, selection.Top - EdgeGap - wSize.Height, wSize.Width, wSize.Height);
        var leftRect = new Rectangle(selection.Left - EdgeGap - hSize.Width, cy - hSize.Height / 2, hSize.Width, hSize.Height);

        bool topFits = topRect.Top >= clientBounds.Top && topRect.Left >= clientBounds.Left && topRect.Right <= clientBounds.Right;
        bool leftFits = leftRect.Left >= clientBounds.Left && leftRect.Top >= clientBounds.Top && leftRect.Bottom <= clientBounds.Bottom;
        bool collide = topRect.IntersectsWith(leftRect);

        if (topFits && leftFits && !collide)
        {
            topRect.X = Clamp(topRect.X, clientBounds.Left, clientBounds.Right - topRect.Width);
            leftRect.Y = Clamp(leftRect.Y, clientBounds.Top, clientBounds.Bottom - leftRect.Height);
            result.Add(new Pill { Lines = wLines, Rect = topRect });
            result.Add(new Pill { Lines = hLines, Rect = leftRect });
            return result;
        }

        // Merge: one pill carrying both chips (plus any detail lines).
        var mergedLines = new List<Seg[]>(detailLines);
        mergedLines.Add(new[] { widthSeg, heightSeg });
        var mSize = MeasurePill(mergedLines, font, lineH, iconBox);

        Rectangle mRect;
        int aboveY = selection.Top - EdgeGap - mSize.Height;
        int belowY = selection.Bottom + EdgeGap;
        if (aboveY >= clientBounds.Top)
            mRect = new Rectangle(cx - mSize.Width / 2, aboveY, mSize.Width, mSize.Height);
        else if (belowY + mSize.Height <= clientBounds.Bottom)
            mRect = new Rectangle(cx - mSize.Width / 2, belowY, mSize.Width, mSize.Height);
        else
            mRect = new Rectangle(selection.Left + EdgeGap, selection.Top + EdgeGap, mSize.Width, mSize.Height);

        mRect.X = Clamp(mRect.X, clientBounds.Left, Math.Max(clientBounds.Left, clientBounds.Right - mRect.Width));
        mRect.Y = Clamp(mRect.Y, clientBounds.Top, Math.Max(clientBounds.Top, clientBounds.Bottom - mRect.Height));
        result.Add(new Pill { Lines = mergedLines, Rect = mRect });
        return result;
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

        // Soft shadow for depth
        using (var shadowPath = WindowsDockRenderer.RoundedRect(new RectangleF(rect.X, rect.Y + 1.5f, rect.Width, rect.Height), Radius))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillPath(shadowBrush, shadowPath);

        // Pill background + accent border
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
        else // Vertical
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
