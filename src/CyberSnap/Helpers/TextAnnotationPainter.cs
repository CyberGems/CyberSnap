using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>
/// Shared measure/paint/caret logic for text annotations in Capture and Editor.
/// Uses <see cref="GraphicsUnit.Pixel"/> so both surfaces render the same size.
/// </summary>
public static class TextAnnotationPainter
{
    private static readonly SolidBrush ShadowBrush1 = new(Color.FromArgb(50, 0, 0, 0));
    private static readonly SolidBrush ShadowBrush2 = new(Color.FromArgb(25, 0, 0, 0));
    private static readonly SolidBrush StrokeBrush = new(Color.FromArgb(90, 0, 0, 0));
    private static readonly SolidBrush BgShadowBrush = new(Color.FromArgb(55, 0, 0, 0));
    private static readonly Pen BgStrokePen = new(Color.FromArgb(60, 0, 0, 0), 1.25f);
    private static readonly Dictionary<(string, float, FontStyle), Font> FontCache = new();

    public static string PlaceholderText =>
        LocalizationService.Translate("Type here...");

    public static Font GetFont(string family, float sizePx, bool bold, bool italic)
    {
        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;
        sizePx = Math.Max(8f, sizePx);
        var key = (family, sizePx, style);
        if (FontCache.TryGetValue(key, out var cached))
            return cached;

        Font font;
        try { font = new Font(family, sizePx, style, GraphicsUnit.Pixel); }
        catch
        {
            font = new Font(FontFamily.GenericSansSerif, sizePx, style, GraphicsUnit.Pixel);
        }
        FontCache[key] = font;
        return font;
    }

    public static string NormalizeNewlines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Layout lines with optional hard wraps at maxWidth (0 = no wrap).</summary>
    public static List<string> LayoutLines(string text, Font font, float maxWidth)
    {
        text = NormalizeNewlines(text);
        if (text.Length == 0)
            return new List<string> { "" };

        var raw = text.Split('\n');
        if (maxWidth <= 0)
            return raw.ToList();

        var result = new List<string>();
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        foreach (var paragraph in raw)
        {
            if (paragraph.Length == 0)
            {
                result.Add("");
                continue;
            }

            var words = paragraph.Split(' ');
            var line = new StringBuilder();
            foreach (var word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                var sz = g.MeasureString(candidate, font);
                if (sz.Width <= maxWidth || line.Length == 0)
                {
                    if (line.Length == 0) line.Append(word);
                    else { line.Append(' '); line.Append(word); }
                }
                else
                {
                    result.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                }
            }
            result.Add(line.ToString());
        }
        return result;
    }

    public static RectangleF Measure(
        Point pos,
        string text,
        float fontSize,
        string fontFamily,
        bool bold,
        bool italic,
        bool background,
        float maxWidth = 0,
        TextHAlign align = TextHAlign.Left)
    {
        var font = GetFont(fontFamily, fontSize, bold, italic);
        string display = string.IsNullOrEmpty(text) ? PlaceholderText : NormalizeNewlines(text);
        var lines = LayoutLines(display, font, maxWidth);

        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float maxLineW = 0;
        float totalH = 0;
        float lineH = font.GetHeight(g);
        foreach (var line in lines)
        {
            var sz = g.MeasureString(line.Length > 0 ? line : " ", font);
            maxLineW = Math.Max(maxLineW, sz.Width);
            totalH += lineH;
        }
        if (totalH <= 0) totalH = lineH;
        if (maxWidth > 0) maxLineW = Math.Max(maxLineW, maxWidth);

        int padX = background ? 16 : 8;
        int padY = background ? 12 : 8;
        return new RectangleF(
            pos.X - padX / 2f,
            pos.Y - padY / 2f,
            maxLineW + padX,
            totalH + padY);
    }

    public static RectangleF Measure(TextAnnotation ta) =>
        Measure(ta.Pos, ta.Text, ta.FontSize, ta.FontFamily, ta.Bold, ta.Italic, ta.Background, ta.MaxWidth, ta.Alignment);

    public static void Paint(
        Graphics g,
        Point pos,
        string text,
        float fontSize,
        Color color,
        bool bold,
        bool italic,
        bool stroke,
        bool shadow,
        bool background,
        string fontFamily,
        float maxWidth = 0,
        TextHAlign align = TextHAlign.Left,
        bool isPlaceholder = false)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var font = GetFont(fontFamily, fontSize, bold, italic);
        string display = isPlaceholder || string.IsNullOrEmpty(text)
            ? (isPlaceholder ? PlaceholderText : text)
            : NormalizeNewlines(text);

        if (isPlaceholder || string.IsNullOrEmpty(text))
            display = PlaceholderText;

        var lines = LayoutLines(display, font, maxWidth);
        float lineH = font.GetHeight(g);

        float contentW = 0;
        foreach (var line in lines)
        {
            var sz = g.MeasureString(line.Length > 0 ? line : " ", font);
            contentW = Math.Max(contentW, sz.Width);
        }
        if (maxWidth > 0) contentW = Math.Max(contentW, maxWidth);

        var drawColor = color;

        if (background && !isPlaceholder)
        {
            var bgRect = Measure(pos, text, fontSize, fontFamily, bold, italic, true, maxWidth, align);
            using var bgPath = SketchRenderer.RoundedRect(bgRect, 8f);
            if (shadow)
            {
                using var shadowPath = SketchRenderer.RoundedRect(
                    new RectangleF(bgRect.X + 2, bgRect.Y + 2, bgRect.Width, bgRect.Height), 8f);
                g.FillPath(BgShadowBrush, shadowPath);
            }
            using var fill = new SolidBrush(color);
            g.FillPath(fill, bgPath);
            if (stroke)
                g.DrawPath(BgStrokePen, bgPath);
            drawColor = Color.White;
        }

        if (isPlaceholder)
        {
            using var ph = new SolidBrush(Color.FromArgb(120, 180, 180, 180));
            float y = pos.Y;
            foreach (var line in lines)
            {
                float x = AlignedX(pos.X, contentW, line, font, g, align);
                g.DrawString(line, font, ph, x, y);
                y += lineH;
            }
            g.TextRenderingHint = TextRenderingHint.SystemDefault;
            return;
        }

        float yPos = pos.Y;
        foreach (var line in lines)
        {
            float x = AlignedX(pos.X, contentW, line, font, g, align);
            PaintLine(g, line, font, drawColor, x, yPos, stroke, shadow, background);
            yPos += lineH;
        }

        g.TextRenderingHint = TextRenderingHint.SystemDefault;
    }

    public static void Paint(Graphics g, TextAnnotation ta) =>
        Paint(g, ta.Pos, ta.Text, ta.FontSize, ta.Color, ta.Bold, ta.Italic, ta.Stroke, ta.Shadow,
            ta.Background, ta.FontFamily, ta.MaxWidth, ta.Alignment);

    private static float AlignedX(float originX, float contentW, string line, Font font, Graphics g, TextHAlign align)
    {
        if (align == TextHAlign.Left) return originX;
        float lineW = g.MeasureString(line.Length > 0 ? line : " ", font).Width;
        return align switch
        {
            TextHAlign.Center => originX + (contentW - lineW) / 2f,
            TextHAlign.Right => originX + contentW - lineW,
            _ => originX,
        };
    }

    private static void PaintLine(Graphics g, string line, Font font, Color color, float x, float y,
        bool stroke, bool shadow, bool background)
    {
        if (string.IsNullOrEmpty(line)) return;

        if (shadow && !background)
        {
            g.DrawString(line, font, ShadowBrush1, x + 2, y + 2);
            g.DrawString(line, font, ShadowBrush2, x + 3, y + 3);
        }

        if (stroke && !background)
        {
            // True outline via GraphicsPath (cleaner than 8 offset DrawString copies).
            using var path = new GraphicsPath();
            float em = font.SizeInPoints * g.DpiY / 72f;
            if (font.Unit == GraphicsUnit.Pixel)
                em = font.Size;
            path.AddString(line, font.FontFamily, (int)font.Style, em,
                new PointF(x, y), StringFormat.GenericTypographic);
            float penW = Math.Clamp(em * 0.08f, 1.1f, 4f);
            using var pen = new Pen(Color.FromArgb(160, 0, 0, 0), penW) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
            using var fill = new SolidBrush(color);
            g.FillPath(fill, path);
            return;
        }

        using var brush = new SolidBrush(color);
        g.DrawString(line, font, brush, x, y);
    }

    /// <summary>Caret top-left for a character index in multi-line text.</summary>
    public static PointF GetCaretPoint(
        Point pos, string text, int caretIndex, float fontSize, string fontFamily,
        bool bold, bool italic, float maxWidth, TextHAlign align)
    {
        var font = GetFont(fontFamily, fontSize, bold, italic);
        text = NormalizeNewlines(text);
        caretIndex = Math.Clamp(caretIndex, 0, text.Length);

        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float lineH = font.GetHeight(g);

        // Map flat caret index (in original text with \n) onto laid-out lines.
        // When maxWidth wraps, indices map by walking the layout.
        var lines = LayoutLines(text.Length > 0 ? text : "", font, maxWidth);
        if (lines.Count == 0) lines.Add("");

        float contentW = 0;
        foreach (var line in lines)
            contentW = Math.Max(contentW, g.MeasureString(line.Length > 0 ? line : " ", font).Width);
        if (maxWidth > 0) contentW = Math.Max(contentW, maxWidth);

        if (text.Length == 0)
            return new PointF(pos.X, pos.Y);

        // Walk original text to find line/col for caretIndex (hard newlines only for index mapping;
        // soft wraps still shift visually — approximate by prefix measure on normalized layout).
        int remaining = caretIndex;
        float y = pos.Y;

        // Prefer hard-newline mapping when maxWidth==0 (exact).
        if (maxWidth <= 0)
        {
            var hard = text.Split('\n');
            for (int i = 0; i < hard.Length; i++)
            {
                int lineLen = hard[i].Length;
                if (remaining <= lineLen)
                {
                    float prefixW = remaining <= 0 ? 0 :
                        g.MeasureString(hard[i][..remaining], font, int.MaxValue, StringFormat.GenericTypographic).Width;
                    float x = AlignedX(pos.X, contentW, hard[i], font, g, align) + prefixW;
                    return new PointF(x, y);
                }
                remaining -= lineLen + 1; // +1 for '\n'
                y += lineH;
            }
            // Past end
            var last = hard[^1];
            float lastW = g.MeasureString(last, font, int.MaxValue, StringFormat.GenericTypographic).Width;
            return new PointF(AlignedX(pos.X, contentW, last, font, g, align) + lastW, pos.Y + lineH * (hard.Length - 1));
        }

        // Soft-wrap: approximate caret on laid-out lines by cumulative character count.
        int consumed = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            int lineLen = lines[i].Length;
            // Soft-wrapped lines may not include spaces the same way; use layout length.
            if (caretIndex <= consumed + lineLen)
            {
                int col = Math.Clamp(caretIndex - consumed, 0, lineLen);
                float prefixW = col <= 0 ? 0 :
                    g.MeasureString(lines[i][..col], font, int.MaxValue, StringFormat.GenericTypographic).Width;
                float x = AlignedX(pos.X, contentW, lines[i], font, g, align) + prefixW;
                return new PointF(x, y);
            }
            consumed += lineLen;
            // Soft wrap: no \n consumed between soft lines; hard \n already in LayoutLines as breaks.
            if (i + 1 < lines.Count)
            {
                // If original still has chars, account for separator space/newline loosely
            }
            y += lineH;
        }

        var endLine = lines[^1];
        float endW = g.MeasureString(endLine, font, int.MaxValue, StringFormat.GenericTypographic).Width;
        return new PointF(AlignedX(pos.X, contentW, endLine, font, g, align) + endW, pos.Y + lineH * (lines.Count - 1));
    }

    /// <summary>Character index nearest to a point (multi-line, hard newlines; soft-wrap approximate).</summary>
    public static int GetCharIndexAt(
        Point pos, Point click, string text, float fontSize, string fontFamily,
        bool bold, bool italic, float maxWidth, TextHAlign align)
    {
        var font = GetFont(fontFamily, fontSize, bold, italic);
        text = NormalizeNewlines(text);
        if (text.Length == 0) return 0;

        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float lineH = font.GetHeight(g);

        var lines = maxWidth <= 0 ? text.Split('\n').ToList() : LayoutLines(text, font, maxWidth);
        float contentW = 0;
        foreach (var ln in lines)
            contentW = Math.Max(contentW, g.MeasureString(ln.Length > 0 ? ln : " ", font).Width);
        if (maxWidth > 0) contentW = Math.Max(contentW, maxWidth);

        int lineIdx = (int)Math.Floor((click.Y - pos.Y) / lineH);
        lineIdx = Math.Clamp(lineIdx, 0, lines.Count - 1);

        float lineX = AlignedX(pos.X, contentW, lines[lineIdx], font, g, align);
        float relX = click.X - lineX;
        string targetLine = lines[lineIdx];

        int col = targetLine.Length;
        for (int i = 1; i <= targetLine.Length; i++)
        {
            float w = g.MeasureString(targetLine[..i], font, int.MaxValue, StringFormat.GenericTypographic).Width;
            float prev = i == 1 ? 0 :
                g.MeasureString(targetLine[..(i - 1)], font, int.MaxValue, StringFormat.GenericTypographic).Width;
            if (relX <= (prev + w) / 2f)
            {
                col = i - 1;
                break;
            }
        }

        if (maxWidth <= 0)
        {
            int index = 0;
            for (int i = 0; i < lineIdx; i++)
                index += lines[i].Length + 1; // +1 newline
            return Math.Clamp(index + col, 0, text.Length);
        }

        // Soft wrap: reconstruct approximate flat index
        int flat = 0;
        for (int i = 0; i < lineIdx; i++)
            flat += lines[i].Length;
        return Math.Clamp(flat + col, 0, text.Length);
    }

    public static FontStyle ToFontStyle(bool bold, bool italic)
    {
        var s = FontStyle.Regular;
        if (bold) s |= FontStyle.Bold;
        if (italic) s |= FontStyle.Italic;
        return s;
    }

    private static string[]? _systemFonts;

    public const int MaxRecentFonts = 8;
    public const int MaxFavoriteFonts = 12;

    /// <summary>One row in a font picker list.</summary>
    public readonly record struct FontListEntry(string Name, bool IsFavorite, bool IsRecent);

    public static string[] GetSystemFonts()
    {
        if (_systemFonts != null) return _systemFonts;
        using var fonts = new InstalledFontCollection();
        _systemFonts = fonts.Families
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return _systemFonts;
    }

    public static string[] FilterFonts(string? search)
    {
        var all = GetSystemFonts();
        if (string.IsNullOrWhiteSpace(search)) return all;
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return all.Where(f =>
        {
            foreach (var term in terms)
                if (f.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            return true;
        }).ToArray();
    }

    public static List<string> ParseFontList(string? serialized, int max)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(serialized)) return result;
        foreach (var part in serialized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (result.Any(r => string.Equals(r, part, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(part);
            if (result.Count >= max) break;
        }
        return result;
    }

    public static List<string> ParseRecentFonts(string? serialized) => ParseFontList(serialized, MaxRecentFonts);
    public static List<string> ParseFavoriteFonts(string? serialized) => ParseFontList(serialized, MaxFavoriteFonts);

    public static string SerializeFontList(IEnumerable<string> fonts, int max) =>
        string.Join(";", fonts.Where(f => !string.IsNullOrWhiteSpace(f)).Take(max));

    public static string SerializeRecentFonts(IEnumerable<string> fonts) => SerializeFontList(fonts, MaxRecentFonts);
    public static string SerializeFavoriteFonts(IEnumerable<string> fonts) => SerializeFontList(fonts, MaxFavoriteFonts);

    /// <summary>Moves <paramref name="family"/> to the front of the recent list (deduped, capped).</summary>
    public static List<string> PushRecentFont(IReadOnlyList<string> current, string family)
    {
        var list = current?.ToList() ?? new List<string>();
        if (string.IsNullOrWhiteSpace(family)) return list;
        list.RemoveAll(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, family);
        if (list.Count > MaxRecentFonts)
            list.RemoveRange(MaxRecentFonts, list.Count - MaxRecentFonts);
        return list;
    }

    public static List<string> ToggleFavoriteFont(IReadOnlyList<string> current, string family)
    {
        var list = current?.ToList() ?? new List<string>();
        if (string.IsNullOrWhiteSpace(family)) return list;
        int existing = list.FindIndex(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            list.RemoveAt(existing);
        else
        {
            list.Insert(0, family);
            if (list.Count > MaxFavoriteFonts)
                list.RemoveRange(MaxFavoriteFonts, list.Count - MaxFavoriteFonts);
        }
        return list;
    }

    public static bool IsInList(IReadOnlyList<string>? list, string family) =>
        list is not null && list.Any(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));

    private static string? CanonicalName(string[] all, string name) =>
        all.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ordered font picker rows: favorites → recents (non-fav) → rest.
    /// <paramref name="pinnedCount"/> is how many leading rows are fav+recent (for the separator).
    /// </summary>
    public static FontListEntry[] GetOrderedFontEntries(
        string? search,
        IReadOnlyList<string>? favorites,
        IReadOnlyList<string>? recents,
        out int pinnedCount)
    {
        var all = FilterFonts(search);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var head = new List<FontListEntry>();

        void AddPinned(IReadOnlyList<string>? source, bool asFavorite, bool asRecent)
        {
            if (source is null) return;
            foreach (var raw in source)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var canonical = CanonicalName(all, raw);
                if (canonical is null) continue;
                if (!used.Add(canonical)) continue;
                bool fav = asFavorite || IsInList(favorites, canonical);
                bool rec = asRecent || IsInList(recents, canonical);
                head.Add(new FontListEntry(canonical, fav, rec));
            }
        }

        // Favorites first (keep user order), then recents not already listed.
        AddPinned(favorites, asFavorite: true, asRecent: false);
        AddPinned(recents, asFavorite: false, asRecent: true);

        pinnedCount = head.Count;

        var rest = all
            .Where(a => !used.Contains(a))
            .Select(a => new FontListEntry(a, IsInList(favorites, a), IsInList(recents, a)));

        return head.Concat(rest).ToArray();
    }

    /// <summary>Legacy helper: ordered names only (favorites+recents first).</summary>
    public static string[] GetOrderedFonts(string? search, IReadOnlyList<string>? recents, out int recentCount)
        => GetOrderedFonts(search, favorites: null, recents, out recentCount);

    public static string[] GetOrderedFonts(
        string? search,
        IReadOnlyList<string>? favorites,
        IReadOnlyList<string>? recents,
        out int pinnedCount)
    {
        var entries = GetOrderedFontEntries(search, favorites, recents, out pinnedCount);
        return entries.Select(e => e.Name).ToArray();
    }

    /// <summary>Draws a compact 5-point star (pixel-snapped). Filled = favorite; stroke = recent/toggle hint.</summary>
    public static void DrawSmallStar(Graphics g, float cx, float cy, float radius, bool filled, Color color)
    {
        // Snap to half-pixels for crisper small glyphs
        cx = MathF.Round(cx * 2f) / 2f;
        cy = MathF.Round(cy * 2f) / 2f;
        radius = Math.Max(3.5f, radius);

        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double ang = -Math.PI / 2 + i * Math.PI / 5;
            float r = (i % 2 == 0) ? radius : radius * 0.42f;
            pts[i] = new PointF(cx + (float)(Math.Cos(ang) * r), cy + (float)(Math.Sin(ang) * r));
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        path.AddPolygon(pts);
        if (filled)
        {
            using var b = new SolidBrush(color);
            g.FillPath(b, path);
        }
        else
        {
            using var pen = new Pen(color, 1.1f) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Applies crisp text settings for font-name lists on dark chrome.</summary>
    public static void ApplyCrispListText(Graphics g)
    {
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }
}
