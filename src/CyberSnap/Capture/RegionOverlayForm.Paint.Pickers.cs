using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    // Theme-keyed picker chrome cache. Picker repaints on hover/scroll, so per-paint
    // brush/pen allocations are observable. Rebuild only when the theme changes.
    private static SolidBrush? _pickerSearchBg, _pickerSearchHintBrush;
    private static SolidBrush? _pickerScrollTrackBrush, _pickerScrollThumbBrush;
    private static SolidBrush? _emojiPlaceholderBrush;
    private static Pen? _pickerFocusBorder, _pickerActiveBorder, _pickerCursorPen;
    private static Pen? _pickerSeparatorPen, _emojiPlaceholderPen;
    private static Font? _pickerSearchFont, _pickerSearchHintFont;
    private static int _pickerChromeKey;

    private static void EnsurePickerChrome()
    {
        int key = HashCode.Combine(
            UiChrome.SurfaceHover.ToArgb(),
            UiChrome.SurfaceTextPrimary.ToArgb(),
            UiChrome.SurfaceTextMuted.ToArgb(),
            UiChrome.SurfaceBorderStrong.ToArgb(),
            UiChrome.SurfaceBorderSubtle.ToArgb());
        if (_pickerSearchBg != null && _pickerChromeKey == key) return;

        _pickerSearchBg?.Dispose(); _pickerSearchHintBrush?.Dispose();
        _pickerScrollTrackBrush?.Dispose(); _pickerScrollThumbBrush?.Dispose();
        _emojiPlaceholderBrush?.Dispose();
        _pickerFocusBorder?.Dispose(); _pickerActiveBorder?.Dispose();
        _pickerCursorPen?.Dispose(); _pickerSeparatorPen?.Dispose();
        _emojiPlaceholderPen?.Dispose();

        var t = UiChrome.SurfaceTextPrimary;
        _pickerSearchBg = new SolidBrush(UiChrome.SurfaceHover);
        _pickerSearchHintBrush = new SolidBrush(UiChrome.SurfaceTextMuted);
        _pickerScrollTrackBrush = new SolidBrush(Color.FromArgb(12, t.R, t.G, t.B));
        _pickerScrollThumbBrush = new SolidBrush(Color.FromArgb(80, t.R, t.G, t.B));
        _emojiPlaceholderBrush = new SolidBrush(Color.FromArgb(18, t.R, t.G, t.B));
        _pickerFocusBorder = new Pen(UiChrome.SurfaceBorderStrong, 1f);
        _pickerActiveBorder = new Pen(UiChrome.SurfaceBorderSubtle, 1f);
        _pickerCursorPen = new Pen(t, 1.2f);
        _pickerSeparatorPen = new Pen(UiChrome.SurfaceBorderSubtle, 1f);
        _emojiPlaceholderPen = new Pen(Color.FromArgb(55, t.R, t.G, t.B), 1f);
        _pickerChromeKey = key;
    }

    private static Font GetPickerSearchFont() => _pickerSearchFont ??= UiChrome.ChromeFont(10f);
    private static Font GetPickerSearchHintFont() => _pickerSearchHintFont ??= UiChrome.ChromeFont(8f);

    private void PaintEmojiPicker(Graphics g)
    {
        try
        {
            // Filter emojis by search
            var filtered = GetFilteredEmojiPalette();

            int cols = EmojiPickerColumns, emojiSize = EmojiPickerIconSize, pad = EmojiPickerPadding;
            int visibleRows = EmojiPickerVisibleRows;
            int totalRows = (filtered.Length + cols - 1) / cols;
            int gridH = visibleRows * (emojiSize + pad);
            int searchBarH = EmojiPickerSearchBarHeight;
            int pw = cols * (emojiSize + pad) + pad;
            int ph = searchBarH + pad + gridH + pad;

            _emojiPickerRect = PositionPopupFromAnchor(_toolbarRect, pw, ph);
            int px = _emojiPickerRect.X;
            int py = _emojiPickerRect.Y;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            WindowsDockRenderer.PaintSurface(g, _emojiPickerRect);
            EnsurePickerChrome();

            // Search bar
            var searchRect = new Rectangle(px + pad, py + pad, pw - pad * 2, searchBarH);
            using (var searchPath = RRect(searchRect, 6))
            {
                g.FillPath(_pickerSearchBg!, searchPath);
                g.DrawPath(_pickerFocusBorder!, searchPath);
            }
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var searchFont = GetPickerSearchFont();
            string searchDisplay = _emojiSearch.Length > 0 ? _emojiSearch : "Search emoji...";
            var searchBrush = SketchRenderer.GetToolColorBrush(_emojiSearch.Length > 0
                ? UiChrome.SurfaceTextPrimary
                : UiChrome.SurfaceTextMuted);
            g.DrawString(searchDisplay, searchFont, searchBrush, searchRect.X + 10, searchRect.Y + 7);
            // Text cursor
            {
                float cursorX = _emojiSearch.Length > 0
                    ? searchRect.X + 10 + g.MeasureString(_emojiSearch, searchFont).Width - 2
                    : searchRect.X + 10;
                g.DrawLine(_pickerCursorPen!, cursorX, searchRect.Y + 8, cursorX, searchRect.Bottom - 8);
            }

            // Hint text (right aligned)
            var searchHintFont = GetPickerSearchHintFont();
            var hintSize = g.MeasureString("Type to search", searchHintFont);
            g.DrawString("Type to search", searchHintFont, _pickerSearchHintBrush!, searchRect.Right - hintSize.Width - 6, searchRect.Y + 9);
            g.TextRenderingHint = TextRenderingHint.SystemDefault;

            // Emoji grid
            int gridY = py + pad + searchBarH + pad;
            int scrollRow = _emojiScrollOffset;
            int startIdx = scrollRow * cols;

            for (int i = 0; i < visibleRows * cols && (startIdx + i) < filtered.Length; i++)
            {
                int idx = startIdx + i;
                int col = i % cols, row = i / cols;
                int ex = px + pad + col * (emojiSize + pad);
                int ey = gridY + row * (emojiSize + pad);

                bool hovered = _emojiHovered == idx;
                if (hovered)
                {
                    using var hoverPath = RRect(new RectangleF(ex - 3, ey - 3, emojiSize + 6, emojiSize + 6), 6);
                    g.FillPath(_pickerSearchBg!, hoverPath);
                }

                try
                {
                    if (_emojiRenderer.TryGetCachedEmoji(filtered[idx].emoji, EmojiPickerRenderSize, out var emojiBmp))
                    {
                        g.DrawImage(emojiBmp, ex + 2, ey + 2);
                    }
                    else
                    {
                        DrawEmojiPlaceholder(g, ex, ey, emojiSize);
                    }
                }
                catch
                {
                    DrawEmojiPlaceholder(g, ex, ey, emojiSize);
                }
            }

            // Scroll indicator (rounded track + thumb)
            if (totalRows > visibleRows)
            {
                int trackH = gridH - 8;
                int trackX = px + pw - pad - 4;
                int trackY = gridY + 4;
                using var trackPath = RRect(new RectangleF(trackX, trackY, 4, trackH), 2);
                g.FillPath(_pickerScrollTrackBrush!, trackPath);
                int thumbH = Math.Max(12, trackH * visibleRows / totalRows);
                int thumbY = trackY + (int)((float)scrollRow / (totalRows - visibleRows) * (trackH - thumbH));
                using var thumbPath = RRect(new RectangleF(trackX, trackY, 4, thumbH), 2);
                g.FillPath(_pickerScrollThumbBrush!, trackPath);
            }

            g.SmoothingMode = SmoothingMode.Default;
        }
        catch (Exception ex)
        {
            Services.AppDiagnostics.LogError("emoji.picker-render", ex);
        }
    }

    private static void DrawEmojiPlaceholder(Graphics g, int x, int y, int size)
    {
        EnsurePickerChrome();
        var rect = new RectangleF(x + 5, y + 5, size - 10, size - 10);
        g.FillEllipse(_emojiPlaceholderBrush!, rect);
        g.DrawEllipse(_emojiPlaceholderPen!, rect);
    }

    private void PaintFontPicker(Graphics g)
    {
        var fonts = GetFilteredFonts();
        int itemH = 30, pad = 8, visibleCount = 8;
        int searchBarH = 32;
        int bottomPad = pad + 4; // extra space so last item doesn't clip against rounded corners
        int pw = 240, ph = searchBarH + pad + visibleCount * itemH + pad + bottomPad;

        // Position near the text input area
        int px, py;
        if (_isTyping)
        {
            px = _textPos.X;
            py = _textPos.Y - ph - 10;
            if (py < 10) py = _textPos.Y + 40;
        }
        else
        {
            var popupRect = PositionPopupFromAnchor(_toolbarRect, pw, ph);
            px = popupRect.X;
            py = popupRect.Y;
        }
        _fontPickerRect = new Rectangle(px, py, pw, ph);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        WindowsDockRenderer.PaintSurface(g, _fontPickerRect);
        EnsurePickerChrome();

        // Search bar
        var searchRect = new Rectangle(px + pad, py + pad, pw - pad * 2, searchBarH);
        using (var searchPath = RRect(searchRect, 6))
        {
            g.FillPath(_pickerSearchBg!, searchPath);
            g.DrawPath(_pickerFocusBorder!, searchPath);
        }
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        string searchDisplay = _fontSearch.Length > 0 ? _fontSearch : "Search fonts...";
        var searchBrush = SketchRenderer.GetToolColorBrush(_fontSearch.Length > 0
            ? UiChrome.SurfaceTextPrimary : UiChrome.SurfaceTextMuted);
        var searchFont = GetPickerSearchFont();
        g.DrawString(searchDisplay, searchFont, searchBrush, searchRect.X + 10, searchRect.Y + 7);
        if (_fontSearch.Length > 0)
        {
            float cursorX = searchRect.X + 10 + g.MeasureString(_fontSearch, searchFont).Width - 2;
            g.DrawLine(_pickerCursorPen!, cursorX, searchRect.Y + 8, cursorX, searchRect.Bottom - 8);
        }
        g.TextRenderingHint = TextRenderingHint.SystemDefault;

        // Font list â€” clip to popup bounds so items don't bleed outside rounded corners
        int listY = py + pad + searchBarH + pad;
        int maxScroll = Math.Max(0, fonts.Length - visibleCount);
        var listClipRect = new Rectangle(px, listY, pw, _fontPickerRect.Bottom - listY);
        var clipState = g.Save();
        using (var clipPath = RRect(_fontPickerRect, UiChrome.ScaledPopupRadius))
        {
            g.SetClip(clipPath);
        }
        for (int i = 0; i < visibleCount && (_fontPickerScroll + i) < fonts.Length; i++)
        {
            int idx = _fontPickerScroll + i;
            string name = fonts[idx];
            int iy = listY + i * itemH;
            bool active = name == _textFontFamily;
            bool hovered = idx == _fontPickerHovered;

            if (active || hovered)
            {
                var itemRect = new Rectangle(px + pad, iy, pw - pad * 2, itemH);
                using var itemPath = RRect(itemRect, 5);
                int alpha = active ? 40 : 20;
                var itemBg = SketchRenderer.GetToolColorBrush(Color.FromArgb(alpha, UiChrome.SurfaceHover.R, UiChrome.SurfaceHover.G, UiChrome.SurfaceHover.B));
                g.FillPath(itemBg, itemPath);
                if (active)
                {
                    g.DrawPath(_pickerActiveBorder!, itemPath);
                }
            }

            // Cache font objects for perf
            if (!_fontCache.TryGetValue(name, out var font))
            {
                try { font = new Font(name, 11f); }
                catch { font = UiChrome.ChromeFont(11f); }
                _fontCache[name] = font;
            }
            int textAlpha = active ? 255 : hovered ? 220 : 160;
            var brush = SketchRenderer.GetToolColorBrush(Color.FromArgb(textAlpha, UiChrome.SurfaceTextPrimary.R, UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(name, font, brush, px + pad + 8, iy + 6);
            g.TextRenderingHint = TextRenderingHint.SystemDefault;
        }

        // Scroll indicator (rounded)
        if (fonts.Length > visibleCount)
        {
            int trackH = visibleCount * itemH - 8;
            int trackX = px + pw - pad - 4;
            int trackY = listY + 4;
            using var trackPath = RRect(new RectangleF(trackX, trackY, 4, trackH), 2);
            g.FillPath(_pickerScrollTrackBrush!, trackPath);
            int thumbH = Math.Max(12, trackH * visibleCount / fonts.Length);
            int thumbY = maxScroll > 0 ? trackY + (int)((float)_fontPickerScroll / maxScroll * (trackH - thumbH)) : trackY;
            using var thumbPath = RRect(new RectangleF(trackX, thumbY, 4, thumbH), 2);
            g.FillPath(_pickerScrollThumbBrush!, thumbPath);
        }
        g.Restore(clipState);

        g.SmoothingMode = SmoothingMode.Default;
    }

    private static Font? _textToolbarFont, _textToolbarFontBold, _textToolbarFontItalic, _textToolbarFontSmall;

    private static (Font font, Font bold, Font italic, Font small) GetTextToolbarFonts()
    {
        _textToolbarFont ??= UiChrome.ChromeFont(9.5f);
        _textToolbarFontBold ??= UiChrome.ChromeFont(10f, FontStyle.Bold);
        _textToolbarFontItalic ??= UiChrome.ChromeFont(10f, FontStyle.Italic);
        _textToolbarFontSmall ??= UiChrome.ChromeFont(8f);
        return (_textToolbarFont, _textToolbarFontBold, _textToolbarFontItalic, _textToolbarFontSmall);
    }

    // Shared layout constants for the inline text formatting toolbar. Used by both the
    // paint path and the bounds/measure path so they can never drift out of sync.
    private const float TextTbBtnW = 28f;
    private const float TextTbBtnH = 28f;
    private const float TextTbBtnPad = 3f;
    private const float TextTbPad = 6f;
    private const float TextTbSepW = 8f;
    private const float TextTbSizeW = 34f; // width of the numeric size readout
    private const float TextTbGripW = 16f; // drag-handle column
    private const float TextTbGap = 14f;   // vertical gap between toolbar and text

    private string TextFontLabel()
        => _textFontFamily.Length > 14 ? _textFontFamily[..13] + ".." : _textFontFamily;

    // Single source of truth for the toolbar's total size. Layout: B I [Stroke] [Shadow]
    // [Bg] | Font | [-] size [+] | grip
    private void MeasureTextToolbar(out float totalW, out float totalH)
    {
        var (uiFont, _, _, _) = GetTextToolbarFonts();
        using var tmpBmp = new Bitmap(1, 1);
        using var tmpG = Graphics.FromImage(tmpBmp);
        float fontW = tmpG.MeasureString(TextFontLabel(), uiFont).Width + 20;

        totalW = TextTbBtnW * 5 + TextTbBtnPad * 4                       // B I Stroke Shadow Bg
               + TextTbSepW + fontW                                      // | font selector
               + TextTbSepW + TextTbBtnW + TextTbSizeW + TextTbBtnW      // | [-] size [+]
               + TextTbSepW + TextTbGripW                                // | grip
               + TextTbPad * 2;
        totalH = TextTbBtnH + TextTbPad * 2;
    }

    private void PaintTextToolbar(Graphics g, RectangleF textRect)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        EnsurePickerChrome();

        var (uiFont, uiFontBold, uiFontItalic, _) = GetTextToolbarFonts();
        string fontLabel = TextFontLabel();
        float fontW = g.MeasureString(fontLabel, uiFont).Width + 20;

        MeasureTextToolbar(out float totalW, out float totalH);
        _textToolbarRect = GetTextToolbarBounds(textRect, totalW, totalH);
        float tx = _textToolbarRect.X;
        float ty = _textToolbarRect.Y;

        WindowsDockRenderer.PaintSurface(g, _textToolbarRect);
        // Outline so the bar stays legible over dark backgrounds (matches tooltip chrome).
        using (var borderPath = WindowsDockRenderer.RoundedRect(_textToolbarRect, WindowsDockRenderer.SurfaceRadius))
        using (var borderPen = new Pen(UiChrome.SurfaceBorderStrong, 1f))
            g.DrawPath(borderPen, borderPath);

        float cx = tx + TextTbPad;
        float cy = ty + TextTbPad;

        SolidBrush LabelBrush(int alpha) => SketchRenderer.GetToolColorBrush(
            Color.FromArgb(alpha, UiChrome.SurfaceTextPrimary.R, UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B));

        int btnIdx = 0;
        void DrawToggleBtn(ref RectangleF rect, float x, string label, Font f, bool active)
        {
            rect = new RectangleF(x, cy, TextTbBtnW, TextTbBtnH);
            bool hovered = _hoveredTextBtn == btnIdx;
            WindowsDockRenderer.PaintButton(g, rect, active, hovered);
            g.DrawString(label, f, LabelBrush(active ? 255 : hovered ? 210 : 130), rect, _iconFmt);
            btnIdx++;
        }
        void DrawEffectBtn(ref RectangleF rect, float x, EffectGlyph kind, bool active)
        {
            rect = new RectangleF(x, cy, TextTbBtnW, TextTbBtnH);
            bool hovered = _hoveredTextBtn == btnIdx;
            WindowsDockRenderer.PaintButton(g, rect, active, hovered);
            DrawEffectGlyph(g, rect, kind, uiFontBold, active, hovered);
            btnIdx++;
        }
        void DrawSeparator()
        {
            float sepX = cx + TextTbSepW / 2f;
            g.DrawLine(_pickerSeparatorPen!, sepX, cy + 5, sepX, cy + TextTbBtnH - 5);
            cx += TextTbSepW;
        }

        // B, I, then the three effect previews (self-documenting sample "A")
        DrawToggleBtn(ref _textBoldBtnRect, cx, "B", uiFontBold, _textBold);
        cx += TextTbBtnW + TextTbBtnPad;
        DrawToggleBtn(ref _textItalicBtnRect, cx, "I", uiFontItalic, _textItalic);
        cx += TextTbBtnW + TextTbBtnPad;
        DrawEffectBtn(ref _textStrokeBtnRect, cx, EffectGlyph.Stroke, _textStroke);
        cx += TextTbBtnW + TextTbBtnPad;
        DrawEffectBtn(ref _textShadowBtnRect, cx, EffectGlyph.Shadow, _textShadow);
        cx += TextTbBtnW + TextTbBtnPad;
        DrawEffectBtn(ref _textBackgroundBtnRect, cx, EffectGlyph.Background, _textBackground);
        cx += TextTbBtnW;

        DrawSeparator();

        // Font selector (index 5)
        _textFontBtnRect = new RectangleF(cx, cy, fontW, TextTbBtnH);
        bool fontHovered = _hoveredTextBtn == 5;
        WindowsDockRenderer.PaintButton(g, _textFontBtnRect, _fontPickerOpen, fontHovered);
        g.DrawString(fontLabel, uiFont, LabelBrush(fontHovered || _fontPickerOpen ? 255 : 190), _textFontBtnRect, _iconFmt);
        cx += fontW;

        DrawSeparator();

        // Size group: [-] <size px> [+]
        btnIdx = 6; // minus
        DrawToggleBtn(ref _textSizeMinusBtnRect, cx, "−", uiFontBold, false);
        cx += TextTbBtnW;
        var sizeRect = new RectangleF(cx, cy, TextTbSizeW, TextTbBtnH);
        g.DrawString(((int)Math.Round(_textFontSize)).ToString(), uiFont, LabelBrush(230), sizeRect, _iconFmt);
        cx += TextTbSizeW;
        DrawToggleBtn(ref _textSizePlusBtnRect, cx, "+", uiFontBold, false);
        cx += TextTbBtnW;

        DrawSeparator();

        // Drag grip (index 8): drags the toolbar and the text together
        _textGripRect = new RectangleF(cx, cy, TextTbGripW, TextTbBtnH);
        DrawGrip(g, _textGripRect, _hoveredTextBtn == 8);

        g.TextRenderingHint = TextRenderingHint.SystemDefault;
        g.SmoothingMode = SmoothingMode.Default;
    }

    // Six-dot drag handle, brighter on hover.
    private void DrawGrip(Graphics g, RectangleF rect, bool hovered)
    {
        int alpha = hovered ? 235 : 150;
        var brush = SketchRenderer.GetToolColorBrush(
            Color.FromArgb(alpha, UiChrome.SurfaceTextMuted.R, UiChrome.SurfaceTextMuted.G, UiChrome.SurfaceTextMuted.B));
        const float dot = 2.4f, gapX = 5f, gapY = 5f;
        float midX = rect.X + rect.Width / 2f;
        float midY = rect.Y + rect.Height / 2f;
        for (int col = 0; col < 2; col++)
            for (int row = 0; row < 3; row++)
            {
                float dx = midX + (col == 0 ? -gapX / 2f : gapX / 2f) - dot / 2f;
                float dy = midY + (row - 1) * gapY - dot / 2f;
                g.FillEllipse(brush, dx, dy, dot, dot);
            }
    }

    private enum EffectGlyph { Stroke, Shadow, Background }

    // Draws a sample "A" that demonstrates the text effect, mirroring how B/I already
    // preview themselves with bold/italic fonts.
    private void DrawEffectGlyph(Graphics g, RectangleF btnRect, EffectGlyph kind, Font font, bool active, bool hovered)
    {
        int alpha = active ? 255 : hovered ? 210 : 130;
        var fg = Color.FromArgb(alpha, UiChrome.SurfaceTextPrimary.R, UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B);

        switch (kind)
        {
            case EffectGlyph.Background:
            {
                var chip = RectangleF.Inflate(btnRect, -6f, -6f);
                using var path = WindowsDockRenderer.RoundedRect(chip, 4f);
                g.FillPath(SketchRenderer.GetToolColorBrush(fg), path);
                // "A" punched in the surface color for contrast against the chip
                g.DrawString("A", font, SketchRenderer.GetToolColorBrush(UiChrome.SurfaceBackground), btnRect, _iconFmt);
                break;
            }
            case EffectGlyph.Shadow:
            {
                var shadowRect = btnRect;
                shadowRect.Offset(1.5f, 1.5f);
                g.DrawString("A", font, SketchRenderer.GetToolColorBrush(Color.FromArgb(alpha, 0, 0, 0)), shadowRect, _iconFmt);
                g.DrawString("A", font, SketchRenderer.GetToolColorBrush(fg), btnRect, _iconFmt);
                break;
            }
            case EffectGlyph.Stroke:
            {
                using var gp = new GraphicsPath();
                using var fmt = (StringFormat)_iconFmt.Clone();
                float emPx = font.SizeInPoints * g.DpiY / 72f;
                gp.AddString("A", font.FontFamily, (int)FontStyle.Bold, emPx, btnRect, fmt);
                using var pen = new Pen(fg, 1.2f) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, gp);
                break;
            }
        }
    }

    private RectangleF GetTextToolbarBounds()
        => GetTextToolbarBounds(GetActiveTextRect());

    private RectangleF GetTextToolbarBounds(RectangleF textRect)
    {
        if (textRect.IsEmpty)
            return RectangleF.Empty;

        MeasureTextToolbar(out float totalW, out float totalH);
        return GetTextToolbarBounds(textRect, totalW, totalH);
    }

    private RectangleF GetTextToolbarBounds(RectangleF textRect, float totalW, float totalH)
    {
        float tx = textRect.X;
        float ty = textRect.Y - totalH - TextTbGap;
        if (ty < 4) ty = textRect.Bottom + TextTbGap;
        tx = Math.Clamp(tx, 4f, Math.Max(4f, ClientSize.Width - totalW - 4f));
        ty = Math.Clamp(ty, 4f, Math.Max(4f, ClientSize.Height - totalH - 4f));
        return new RectangleF(tx, ty, totalW, totalH);
    }
}
