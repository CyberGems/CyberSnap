using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CyberSnap.Capture;

/// <summary>
/// Shared ruler rendering used by both RegionOverlayForm (overlay annotation)
/// and StandaloneRulerForm (independent measurement tool).
/// </summary>
public static class RulerRenderer
{
    private static readonly Pen ShadowPen = new(Color.FromArgb(70, 0, 0, 0), 3f)
        { StartCap = LineCap.Flat, EndCap = LineCap.Flat };

    private static Pen? _linePen;
    private static SolidBrush? _bgBrush;
    private static Pen? _borderPen;
    private static SolidBrush? _fgBrush;
    private static SolidBrush? _accentBrush;
    private static Font? _font;
    private static Font? _distFont;
    private static int _themeKey;

    // Label padding (must match between layout and text placement)
    private const float LabelPadH = 14;
    private const float LabelPadV = 8;

    // Close button (standalone ruler)
    private const float CloseButtonSize = 20f;
    private const float CloseButtonPad = 6f; // extra space between text and close button

    /// <summary>Last painted close-button bounds (cached during Paint for accurate hit-testing).</summary>
    public static RectangleF LastCloseButtonBounds { get; private set; }

    /// <summary>Create the (theme-independent) label fonts if not already created.
    /// Safe to call outside a paint pass — needed for bounds computation during drag.</summary>
    private static void EnsureFonts()
    {
        _font ??= new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        _distFont ??= new Font("Segoe UI Variable Text", 11.5f, FontStyle.Bold, GraphicsUnit.Point);
    }

    public static void EnsureChrome(bool isDark)
    {
        var text = isDark
            ? Color.FromArgb(240, 240, 245)
            : Color.FromArgb(24, 24, 24);
        var pill = isDark
            ? Color.FromArgb(26, 27, 31)
            : Helpers.UiChrome.SurfaceElevated;
        var border = isDark
            ? Color.FromArgb(160, 0, 200, 215)
            : Color.FromArgb(160, 0, 110, 205);
        int key = HashCode.Combine(text.ToArgb(), pill.ToArgb(), border.ToArgb());
        if (_linePen != null && _themeKey == key) return;

        _linePen?.Dispose();
        _bgBrush?.Dispose();
        _borderPen?.Dispose();
        _fgBrush?.Dispose();
        _accentBrush?.Dispose();

        var accent = isDark
            ? Color.FromArgb(0, 200, 215)
            : Color.FromArgb(0, 110, 205);
        _linePen = new Pen(text, 1.8f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        _bgBrush = new SolidBrush(pill);
        _borderPen = new Pen(border, 1.0f);
        _fgBrush = new SolidBrush(text);
        _accentBrush = new SolidBrush(accent);
        EnsureFonts();
        _themeKey = key;
    }

    /// <summary>Paint the ruler line, ticks, and floating distance/angle label.</summary>
    public static void Paint(Graphics g, Point from, Point to, Rectangle clientBounds, bool isDark, bool showCloseButton = false, float dpiScale = 1f)
    {
        EnsureChrome(isDark);

        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        g.DrawLine(ShadowPen, from.X + 1, from.Y + 1, to.X + 1, to.Y + 1);
        g.DrawLine(_linePen!, from, to);

        // Endpoint arrowheads — triangles pointing inward along the ruler line.
        // The tip IS the exact measurement point. The base gives a perpendicular reference.
        // Works at any scale: from tiny 5px measures to full-screen spans.
        float nx = 0, ny = 0;
        if (dist > 1) { nx = -dy / dist; ny = dx / dist; }
        float arrowH = 9f * dpiScale;   // height along ruler direction
        float arrowHW = 4.5f * dpiScale; // half-width perpendicular
        // Direction from→to: (dx/dist, dy/dist). Arrow at 'from' points toward 'to'.
        float dirX = dist > 1 ? dx / dist : 0;
        float dirY = dist > 1 ? dy / dist : 0;

        DrawArrowhead(g, from, dirX, dirY, nx, ny, arrowH, arrowHW);
        DrawArrowhead(g, to, -dirX, -dirY, nx, ny, arrowH, arrowHW);

        // Build label text in two parts: distance (larger, accent) + rest (normal)
        string distText = $"{(int)dist}px";
        string restText = $"  \u2022  W: {Math.Abs(dx):0}px  H: {Math.Abs(dy):0}px  \u2022  {angle:0.0}\u00b0";
        var distSz = TextRenderer.MeasureText(g, distText, _distFont!);
        var restSz = TextRenderer.MeasureText(g, restText, _font!);
        float maxH = Math.Max(distSz.Height, restSz.Height);

        float scaledCloseBtn = CloseButtonSize * dpiScale;
        float scaledClosePad = CloseButtonPad * dpiScale;
        float extraWidth = showCloseButton ? scaledCloseBtn + scaledClosePad : 0;
        var label = ComputeLabelRect(from, to, clientBounds, distSz, restSz, extraWidth);

        PaintLabelShadow(g, label, 8f, 48, 1f);
        using var path = RoundedRect(label, 8f);
        g.FillPath(_bgBrush!, path);
        g.DrawPath(_borderPen!, path);
        // Center the text block horizontally within the label
        // Use TextRenderer (GDI) for accurate measurement and rendering
        var distRect = new Rectangle((int)(label.X + LabelPadH), (int)(label.Y + LabelPadV), distSz.Width, (int)maxH);
        var restRect = new Rectangle(distRect.Right, (int)(label.Y + LabelPadV), restSz.Width, (int)maxH);
        TextRenderer.DrawText(g, distText, _distFont!, distRect, _accentBrush!.Color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, restText, _font!, restRect, _fgBrush!.Color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // Draw close button (standalone ruler mode)
        if (showCloseButton)
        {
            var closeRect = new RectangleF(
                label.Right - LabelPadH - scaledCloseBtn,
                label.Y + (label.Height - scaledCloseBtn) / 2f,
                scaledCloseBtn, scaledCloseBtn);

            LastCloseButtonBounds = closeRect;

            using var closePath = RoundedRect(closeRect, scaledCloseBtn / 2f);
            g.FillPath(_bgBrush!, closePath);
            g.DrawPath(_borderPen!, closePath);

            // Draw "×" centered
            float crossInset = scaledCloseBtn * 0.28f;
            float cx = closeRect.X + scaledCloseBtn / 2f;
            float cy = closeRect.Y + scaledCloseBtn / 2f;
            float half = scaledCloseBtn / 2f - crossInset;
            using var crossPen = new Pen(_fgBrush!.Color, 1.5f * dpiScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(crossPen, cx - half, cy - half, cx + half, cy + half);
            g.DrawLine(crossPen, cx + half, cy - half, cx - half, cy + half);
        }
        else
        {
            LastCloseButtonBounds = RectangleF.Empty;
        }

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
        g.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>Conservative bounding rectangle for the ruler's live paint.</summary>
    public static Rectangle GetPaintBounds(Point from, Point to)
    {
        int minX = Math.Min(from.X, to.X);
        int minY = Math.Min(from.Y, to.Y);
        int maxX = Math.Max(from.X, to.X);
        int maxY = Math.Max(from.Y, to.Y);

        var lineRect = Rectangle.FromLTRB(minX - 12, minY - 12, maxX + 12, maxY + 12);

        int midX = (from.X + to.X) / 2;
        int midY = (from.Y + to.Y) / 2;
        // Generous padding: label can be offset perpendicularly and is wider with the new format
        var labelRect = new Rectangle(midX - 420, midY - 260, 840, 520);

        return Rectangle.Union(lineRect, labelRect);
    }

    /// <summary>Tight bounds for live drag preview — only covers the line, not the full label.</summary>
    public static Rectangle GetLiveBounds(Point from, Point to)
    {
        int minX = Math.Min(from.X, to.X);
        int minY = Math.Min(from.Y, to.Y);
        int maxX = Math.Max(from.X, to.X);
        int maxY = Math.Max(from.Y, to.Y);
        return Rectangle.FromLTRB(minX - 40, minY - 40, maxX + 40, maxY + 40);
    }

    /// <summary>Tighter bounds for hit-testing and selection frame — covers the line
    /// plus enough room for the floating measurement label (up to ~400px wide).</summary>
    public static Rectangle GetSelectionBounds(Point from, Point to)
    {
        int minX = Math.Min(from.X, to.X);
        int minY = Math.Min(from.Y, to.Y);
        int maxX = Math.Max(from.X, to.X);
        int maxY = Math.Max(from.Y, to.Y);

        var lineRect = Rectangle.FromLTRB(minX - 16, minY - 16, maxX + 16, maxY + 16);

        int midX = (from.X + to.X) / 2;
        int midY = (from.Y + to.Y) / 2;
        // Large enough for "1234px · W: 800px H: 600px · -45.0°" + padding
        var labelRect = new Rectangle(midX - 300, midY - 180, 600, 360);

        return Rectangle.Union(lineRect, labelRect);
    }

    /// <summary>Precise dirty rectangle for one live ruler frame: the line plus the *actual*
    /// floating label rect (not the conservative box). Tight bounds keep the per-frame repaint
    /// — and the overlay's dimming alpha-blend over it — cheap enough to stay fluid while dragging.</summary>
    public static Rectangle GetLivePreviewBounds(Point from, Point to, Rectangle clientBounds)
    {
        EnsureFonts();
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        string distText = $"{(int)dist}px";
        string restText = $"  •  W: {Math.Abs(dx):0}px  H: {Math.Abs(dy):0}px  •  {angle:0.0}°";
        var distSz = TextRenderer.MeasureText(distText, _distFont!);
        var restSz = TextRenderer.MeasureText(restText, _font!);

        var labelF = ComputeLabelRect(from, to, clientBounds, distSz, restSz);
        var labelRect = Rectangle.Round(labelF);
        labelRect.Inflate(22, 22); // cover the drop shadow + anti-aliasing

        int minX = Math.Min(from.X, to.X);
        int minY = Math.Min(from.Y, to.Y);
        int maxX = Math.Max(from.X, to.X);
        int maxY = Math.Max(from.Y, to.Y);
        var lineRect = Rectangle.FromLTRB(minX - 12, minY - 12, maxX + 12, maxY + 12);

        return Rectangle.Union(lineRect, labelRect);
    }

    /// <summary>Returns the floating label chip bounds for a ruler (for hit-testing drag-from-chip).</summary>
    public static RectangleF GetLabelBounds(Point from, Point to, Rectangle clientBounds, float dpiScale = 1f)
    {
        EnsureFonts();
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        string distText = $"{(int)dist}px";
        string restText = $"  •  W: {Math.Abs(dx):0}px  H: {Math.Abs(dy):0}px  •  {angle:0.0}°";
        var distSz = TextRenderer.MeasureText(distText, _distFont!);
        var restSz = TextRenderer.MeasureText(restText, _font!);
        float extraWidth = (CloseButtonSize + CloseButtonPad) * dpiScale;
        return ComputeLabelRect(from, to, clientBounds, distSz, restSz, extraWidth);
    }

    /// <summary>Returns the close (×) button bounds inside the ruler label chip (for standalone mode).</summary>
    public static RectangleF GetCloseButtonBounds(Point from, Point to, Rectangle clientBounds, float dpiScale = 1f)
    {
        var label = GetLabelBounds(from, to, clientBounds, dpiScale);
        float scaledSize = CloseButtonSize * dpiScale;
        return new RectangleF(
            label.Right - LabelPadH - scaledSize,
            label.Y + (label.Height - scaledSize) / 2f,
            scaledSize, scaledSize);
    }

    /// <summary>Draws a filled triangular arrowhead. Tip is at (x,y), pointing in (dirX,dirY).
    /// (nx,ny) is the perpendicular normal for the base width.</summary>
    private static void DrawArrowhead(Graphics g, Point tip, float dirX, float dirY,
        float nx, float ny, float height, float halfWidth)
    {
        // Tip of the arrow = exact measurement point
        float tx = tip.X;
        float ty = tip.Y;
        // Base corners
        float bx1 = tx + dirX * height + nx * halfWidth;
        float by1 = ty + dirY * height + ny * halfWidth;
        float bx2 = tx + dirX * height - nx * halfWidth;
        float by2 = ty + dirY * height - ny * halfWidth;

        // Shadow
        using var shadowBrush = new SolidBrush(Color.FromArgb(48, 0, 0, 0));
        g.FillPolygon(shadowBrush, new PointF[]
        {
            new(tx + 1, ty + 1),
            new(bx1 + 1, by1 + 1),
            new(bx2 + 1, by2 + 1),
        });

        // Accent fill
        g.FillPolygon(_accentBrush!, new PointF[]
        {
            new(tx, ty),
            new(bx1, by1),
            new(bx2, by2),
        });
    }

    /// <summary>Computes the floating label rectangle for the given ruler, matching Paint()'s layout
    /// exactly. Factored out so live-drag invalidation can target the label's true position.</summary>
    private static RectangleF ComputeLabelRect(Point from, Point to, Rectangle clientBounds, Size distSz, Size restSz, float extraWidth = 0)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        float totalW = distSz.Width + restSz.Width + extraWidth;
        float maxH = Math.Max(distSz.Height, restSz.Height);
        var mid = new PointF((from.X + to.X) / 2f, (from.Y + to.Y) / 2f);

        // Determine preferred label normal direction (pointing up, or left if horizontal)
        float lnx = 0, lny = -1;
        if (dist > 1)
        {
            lnx = -dy / dist;
            lny = dx / dist;
            if (lny > 0 || (lny == 0 && lnx > 0))
            {
                lnx = -lnx;
                lny = -lny;
            }
        }

        float w = totalW + LabelPadH * 2;
        float h = maxH + LabelPadV * 2;
        float ext = MathF.Abs(lnx * w / 2f) + MathF.Abs(lny * h / 2f);
        float d = ext + 14f;

        var labelCenter = new PointF(mid.X + lnx * d, mid.Y + lny * d);
        var label = new RectangleF(labelCenter.X - w / 2f, labelCenter.Y - h / 2f, w, h);

        // If preferred direction goes off top/left, try the opposite side of the line
        if (label.Left < 4 || label.Top < 4)
        {
            var altCenter = new PointF(mid.X - lnx * d, mid.Y - lny * d);
            var altLabel = new RectangleF(altCenter.X - w / 2f, altCenter.Y - h / 2f, w, h);
            if (altLabel.Left >= 4 && altLabel.Top >= 4 &&
                altLabel.Right <= clientBounds.Width - 4 &&
                altLabel.Bottom <= clientBounds.Height - 4)
            {
                label = altLabel;
            }
        }

        // Clamp to client bounds to ensure it never goes off-screen
        float lx = Math.Clamp(label.X, 4f, clientBounds.Width - w - 4f);
        float ly = Math.Clamp(label.Y, 4f, clientBounds.Height - h - 4f);
        return new RectangleF(lx, ly, w, h);
    }

    // ── Internal helpers ──

    private static GraphicsPath RoundedRect(RectangleF r, float rad)
    {
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
        path.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
        path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        path.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void PaintLabelShadow(Graphics g, RectangleF rect, float radius, int alpha = 52, float yOffset = 1f)
    {
        var oldSmooth = g.SmoothingMode;
        var oldComp = g.CompositingMode;
        var oldCompQual = g.CompositingQuality;
        var oldPix = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var ambient = rect;
        ambient.Inflate(8f, 8f);
        ambient.Offset(0, yOffset + 1f);
        int ambientAlpha = Math.Clamp((int)(alpha * 0.10f), 1, 255);
        using (var path = RoundedRect(ambient, radius + 8f))
        using (var brush = new SolidBrush(Color.FromArgb(ambientAlpha, 0, 0, 0)))
            g.FillPath(brush, path);

        var directional = rect;
        directional.Inflate(3f, 3f);
        directional.Offset(0, yOffset + 4f);
        int dirAlpha = Math.Clamp((int)(alpha * 0.22f), 1, 255);
        using (var path = RoundedRect(directional, radius + 3f))
        using (var brush = new SolidBrush(Color.FromArgb(dirAlpha, 0, 0, 0)))
            g.FillPath(brush, path);

        g.SmoothingMode = oldSmooth;
        g.CompositingMode = oldComp;
        g.CompositingQuality = oldCompQual;
        g.PixelOffsetMode = oldPix;
    }
}
