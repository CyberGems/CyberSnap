using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>One measured, rasterized help surface shared by the CPU and GPU renderers.</summary>
internal sealed class ZoomHelpPanel : IDisposable
{
    internal sealed record Hint(string Keys, string Label);
    internal sealed record HintLayout(Hint Hint, Rectangle Keys, Rectangle Label);
    public Bitmap Image { get; }
    public IReadOnlyList<HintLayout> Items { get; }

    public ZoomHelpPanel(Size viewport, float requestedScale, string level)
    {
        var hints = new[]
        {
            new Hint(T("Wheel / + / - / ↑ / ↓"), T("Adjust zoom")),
            new Hint("C", T("Select and annotate")),
            new Hint("Enter", T("Confirm full view")),
            new Hint("Ctrl+C", T("Copy full view")),
            new Hint("Ctrl+S", T("Save full view")),
            new Hint(T("Esc / right-click"), T("Exit"))
        };
        float scale = requestedScale;
        using var measureSurface = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(measureSurface);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        HintLayout[] items = Array.Empty<HintLayout>();
        Size size = Size.Empty;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            using var keyFont = Font(11.5f * scale);
            using var labelFont = Font(12.5f * scale);
            int S(float value) => Math.Max(1, (int)Math.Ceiling(value * scale));
            int pad = S(12), gap = S(8), columnGap = S(22), keyPad = S(8);
            int available = Math.Max(80, viewport.Width - S(24));
            int keyHeight = S(24), header = S(36);
            var keyWidths = hints.Select(h => (int)Math.Ceiling(measure.MeasureString(h.Keys, keyFont, int.MaxValue, format).Width) + keyPad * 2).ToArray();
            int naturalWidth = hints.Select((h, i) => keyWidths[i] + gap
                + (int)Math.Ceiling(measure.MeasureString(h.Label, labelFont, int.MaxValue, format).Width)).Max();
            int columns = Math.Clamp((available - pad * 2 + columnGap) / (naturalWidth + columnGap), 1, 3);
            int columnWidth = Math.Min(naturalWidth, Math.Max(1, (available - pad * 2 - columnGap * (columns - 1)) / columns));
            int rowHeight = keyHeight;
            var labelSizes = hints.Select((h, i) => measure.MeasureString(h.Label, labelFont,
                new SizeF(Math.Max(1, columnWidth - keyWidths[i] - gap), 2000), format)).ToArray();
            foreach (var label in labelSizes) rowHeight = Math.Max(rowHeight, (int)Math.Ceiling(label.Height));
            int rows = (hints.Length + columns - 1) / columns;
            size = new Size(pad * 2 + columnWidth * columns + columnGap * (columns - 1),
                pad * 2 + header + rows * rowHeight + (rows - 1) * gap);
            if (size.Height > viewport.Height - S(24) || size.Width > viewport.Width - 4)
            {
                scale *= .9f;
                continue;
            }
            items = hints.Select((h, i) =>
            {
                int x = pad + i % columns * (columnWidth + columnGap);
                int y = pad + header + i / columns * (rowHeight + gap);
                return new HintLayout(h,
                    new Rectangle(x, y + (rowHeight - keyHeight) / 2, keyWidths[i], keyHeight),
                    new Rectangle(x + keyWidths[i] + gap, y + (rowHeight - (int)Math.Ceiling(labelSizes[i].Height)) / 2,
                        columnWidth - keyWidths[i] - gap, (int)Math.Ceiling(labelSizes[i].Height)));
            }).ToArray();
            break;
        }
        Items = items;
        Image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(Image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int Sc(float value) => Math.Max(1, (int)Math.Ceiling(value * scale));
        var accent = Color.FromArgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B);
        var primary = Color.FromArgb(Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B);
        var secondary = Color.FromArgb(Theme.TextSecondary.R, Theme.TextSecondary.G, Theme.TextSecondary.B);
        using var face = WindowsDockRenderer.RoundedRect(new RectangleF(.5f, .5f, size.Width - 1, size.Height - 1), Sc(10));
        using var background = new SolidBrush(Color.FromArgb(248, Theme.BgSecondary.R, Theme.BgSecondary.G, Theme.BgSecondary.B));
        using var border = new Pen(Color.FromArgb(100, accent));
        graphics.FillPath(background, face);
        graphics.DrawPath(border, face);
        using var titleFont = Font(13 * scale, FontStyle.Bold);
        using var bodyFont = Font(12.5f * scale);
        using var shortcutFont = Font(11.5f * scale);
        using var primaryBrush = new SolidBrush(primary);
        using var secondaryBrush = new SolidBrush(secondary);
        using var accentBrush = new SolidBrush(accent);
        int padding = Sc(12);
        string title = T("Zoom");
        int titleWidth = (int)Math.Ceiling(graphics.MeasureString(title, titleFont, int.MaxValue, format).Width);
        graphics.DrawString(title, titleFont, primaryBrush, new PointF(padding, padding + Sc(4)), format);
        int badgeWidth = (int)Math.Ceiling(graphics.MeasureString(level, titleFont, int.MaxValue, format).Width) + Sc(16);
        var badge = new Rectangle(padding + titleWidth + Sc(10), padding, badgeWidth, Sc(25));
        PaintKey(graphics, badge, accent, scale);
        using var centered = (StringFormat)format.Clone();
        centered.Alignment = StringAlignment.Center;
        centered.LineAlignment = StringAlignment.Center;
        graphics.DrawString(level, titleFont, accentBrush, badge, centered);
        using var separator = new Pen(Color.FromArgb(35, primary));
        graphics.DrawLine(separator, padding, padding + Sc(30), size.Width - padding, padding + Sc(30));
        foreach (var item in items)
        {
            PaintKey(graphics, item.Keys, accent, scale);
            graphics.DrawString(item.Hint.Keys, shortcutFont, accentBrush, item.Keys, centered);
            graphics.DrawString(item.Hint.Label, bodyFont, secondaryBrush, item.Label, format);
        }
    }

    private static string T(string key) => LocalizationService.Translate(key);
    private static Font Font(float pixels, FontStyle style = FontStyle.Regular) =>
        new(UiChrome.FontFamily.Name, pixels, style, GraphicsUnit.Pixel);
    private static void PaintKey(Graphics graphics, Rectangle bounds, Color accent, float scale)
    {
        using var path = WindowsDockRenderer.RoundedRect(bounds, 5 * scale);
        using var fill = new SolidBrush(Color.FromArgb(18, accent));
        using var border = new Pen(Color.FromArgb(55, accent));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }
    public void Dispose() => Image.Dispose();
}
