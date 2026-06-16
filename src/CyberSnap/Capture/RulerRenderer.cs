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
    private static Pen? _tickPen;
    private static SolidBrush? _bgBrush;
    private static Pen? _borderPen;
    private static SolidBrush? _fgBrush;
    private static SolidBrush? _accentBrush;
    private static Font? _font;
    private static Font? _distFont;
    private static int _themeKey;

    public static void EnsureChrome(bool isDark)
    {
        var text = isDark
            ? Color.FromArgb(240, 240, 245)
            : Color.FromArgb(24, 24, 24);
        var pill = isDark
            ? Color.FromArgb(26, 27, 31)
            : Color.FromArgb(252, 252, 252);
        var border = isDark
            ? Color.FromArgb(160, 0, 200, 215)
            : Color.FromArgb(160, 0, 110, 205);
        int key = HashCode.Combine(text.ToArgb(), pill.ToArgb(), border.ToArgb());
        if (_linePen != null && _themeKey == key) return;

        _linePen?.Dispose();
        _tickPen?.Dispose();
        _bgBrush?.Dispose();
        _borderPen?.Dispose();
        _fgBrush?.Dispose();
        _accentBrush?.Dispose();

        var accent = isDark
            ? Color.FromArgb(0, 200, 215)
            : Color.FromArgb(0, 110, 205);
        _linePen = new Pen(text, 1.8f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        _tickPen = new Pen(text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        _bgBrush = new SolidBrush(pill);
        _borderPen = new Pen(border, 1.0f);
        _fgBrush = new SolidBrush(text);
        _accentBrush = new SolidBrush(accent);
        _font ??= new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        _distFont ??= new Font("Segoe UI Variable Text", 11.5f, FontStyle.Bold, GraphicsUnit.Point);
        _themeKey = key;
    }

    /// <summary>Paint the ruler line, ticks, and floating distance/angle label.</summary>
    public static void Paint(Graphics g, Point from, Point to, Rectangle clientBounds, bool isDark)
    {
        EnsureChrome(isDark);

        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        float nx = 0, ny = 0;
        if (dist > 1) { nx = -dy / dist; ny = dx / dist; }
        const float tickHalf = 6f;

        g.DrawLine(ShadowPen, from.X + 1, from.Y + 1, to.X + 1, to.Y + 1);
        g.DrawLine(_linePen!, from, to);
        g.DrawLine(_tickPen!, from.X - nx * tickHalf, from.Y - ny * tickHalf,
                            from.X + nx * tickHalf, from.Y + ny * tickHalf);
        g.DrawLine(_tickPen!, to.X - nx * tickHalf, to.Y - ny * tickHalf,
                            to.X + nx * tickHalf, to.Y + ny * tickHalf);

        // Build label text in two parts: distance (larger, accent) + rest (normal)
        string distText = $"{(int)dist}px";
        string restText = $"  \u2022  W: {Math.Abs(dx):0}px  H: {Math.Abs(dy):0}px  \u2022  {angle:0.0}\u00b0";
        var distSz = TextRenderer.MeasureText(g, distText, _distFont!);
        var restSz = TextRenderer.MeasureText(g, restText, _font!);
        float totalW = distSz.Width + restSz.Width;
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

        float padH = 14;
        float padV = 8;
        float w = totalW + padH * 2;
        float h = maxH + padV * 2;
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
        label = new RectangleF(lx, ly, w, h);

        PaintLabelShadow(g, label, 8f, 48, 1f);
        using var path = RoundedRect(label, 8f);
        g.FillPath(_bgBrush!, path);
        g.DrawPath(_borderPen!, path);
        // Center the text block horizontally within the label
        // Use TextRenderer (GDI) for accurate measurement and rendering
        var distRect = new Rectangle((int)(label.X + padH), (int)(label.Y + padV), distSz.Width, (int)maxH);
        var restRect = new Rectangle(distRect.Right, (int)(label.Y + padV), restSz.Width, (int)maxH);
        TextRenderer.DrawText(g, distText, _distFont!, distRect, _accentBrush!.Color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, restText, _font!, restRect, _fgBrush!.Color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

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
