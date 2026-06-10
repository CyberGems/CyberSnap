using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Capture;       // SketchRenderer.RoundedRect
using CyberSnap.Services;      // LocalizationService
using CyberSnap.UI.Editor;     // EditorColors

namespace CyberSnap.UI.Controls;

public sealed partial class AnnotationCanvas
{
    // ── Text formatting state (persists between text annotations so choices stick) ──
    private bool _textBold = true;
    private bool _textItalic;
    private bool _textStroke = true;
    private bool _textShadow = true;
    private bool _textBackground;
    private float _textFontSize = 24f;
    private string _textFontFamily = "Segoe UI";

    // Toolbar hit rects (screen/client-space, recomputed each paint)
    private RectangleF _textToolbarRect;
    private RectangleF _textBoldBtnRect, _textItalicBtnRect, _textStrokeBtnRect,
                       _textShadowBtnRect, _textBackgroundBtnRect, _textFontBtnRect,
                       _textSizeMinusBtnRect, _textSizePlusBtnRect, _textGripRect;
    private int _hoveredTextBtn = -1; // 0=B 1=I 2=Stroke 3=Shadow 4=Bg 5=Font 6=Size- 7=Size+ 8=Grip

    // Font dropdown
    private bool _fontDropdownOpen;
    private RectangleF _fontDropdownRect;
    private RectangleF[] _fontDropdownItemRects = Array.Empty<RectangleF>();
    private static readonly string[] CommonFonts =
    {
        "Segoe UI", "Arial", "Calibri", "Consolas", "Times New Roman", "Georgia",
        "Verdana", "Tahoma", "Trebuchet MS", "Courier New", "Comic Sans MS", "Impact",
    };

    // Grip drag (moves the inline text box + toolbar together)
    private bool _textGripDragging;
    private Point _textGripDragOffset;

    // Layout constants
    private const float TbBtnW = 26f, TbBtnH = 26f, TbBtnPad = 3f, TbPad = 6f,
                        TbSepW = 8f, TbSizeW = 30f, TbGripW = 14f, TbGap = 10f;

    // Cached chrome (EditorColors are constant, so these never need rebuilding)
    private static readonly Font TbFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Font TbFontBold = new("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
    private static readonly Font TbFontItalic = new("Segoe UI", 9.5f, FontStyle.Italic, GraphicsUnit.Point);
    private static readonly Pen TbSepPen = new(EditorColors.BorderSubtle, 1f);
    private static readonly StringFormat TbCenter = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoClip,
    };

    private enum EffectGlyphKind { Stroke, Shadow, Background }

    private string TextFontLabel()
        => _textFontFamily.Length > 14 ? _textFontFamily[..13] + ".." : _textFontFamily;

    /// <summary>Returns the screen-space bounds of the inline text being edited.</summary>
    private RectangleF GetInlineTextScreenBounds()
    {
        if (_inlineTextBox is null) return RectangleF.Empty;
        var rect = MeasureInlineTextRect(_inlineTextOrigin, _inlineTextBox.Text, _textFontSize, _textFontFamily, _textBold, _textItalic, _textBackground);
        return ImageToScreenRect(rect);
    }

    // ── Painting ───────────────────────────────────────────────────────────

    /// <summary>Floating formatting toolbar over the inline text box. Screen-space (called
    /// outside the zoom/pan transform in OnPaint).</summary>
    private void RenderInlineTextToolbar(Graphics g)
    {
        if (_inlineTextBox is null) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        string fontLabel = TextFontLabel();
        float fontW = g.MeasureString(fontLabel, TbFont).Width + 18;
        float totalW = TbBtnW * 5 + TbBtnPad * 4          // B I Stroke Shadow Bg
                     + TbSepW + fontW                      // | font
                     + TbSepW + TbBtnW + TbSizeW + TbBtnW  // | [-] size [+]
                     + TbSepW + TbGripW                    // | grip
                     + TbPad * 2;
        float totalH = TbBtnH + TbPad * 2;

        var tb = GetInlineTextScreenBounds();
        float tx = tb.X;
        float ty = tb.Y - totalH - TbGap;
        if (ty < 4) ty = tb.Bottom + TbGap;
        tx = Math.Clamp(tx, 4f, Math.Max(4f, ClientSize.Width - totalW - 4f));
        ty = Math.Clamp(ty, 4f, Math.Max(4f, ClientSize.Height - totalH - 4f));
        _textToolbarRect = new RectangleF(tx, ty, totalW, totalH);

        PaintToolbarSurface(g, _textToolbarRect);

        float cx = tx + TbPad;
        float cy = ty + TbPad;

        void Btn(ref RectangleF rect, float x, string label, Font f, bool active, int hoverIndex)
        {
            rect = new RectangleF(x, cy, TbBtnW, TbBtnH);
            PaintButtonBg(g, rect, active, _hoveredTextBtn == hoverIndex);
            int a = active ? 255 : _hoveredTextBtn == hoverIndex ? 225 : 150;
            DrawCenteredText(g, label, f, EditorColors.TextPrimary, rect, a);
        }
        void Eff(ref RectangleF rect, float x, EffectGlyphKind kind, bool active, int hoverIndex)
        {
            rect = new RectangleF(x, cy, TbBtnW, TbBtnH);
            PaintButtonBg(g, rect, active, _hoveredTextBtn == hoverIndex);
            DrawEffectGlyph(g, rect, kind, active, _hoveredTextBtn == hoverIndex);
        }
        float Sep(float x)
        {
            float sx = x + TbSepW / 2f;
            g.DrawLine(TbSepPen, sx, cy + 4, sx, cy + TbBtnH - 4);
            return x + TbSepW;
        }

        Btn(ref _textBoldBtnRect, cx, "B", TbFontBold, _textBold, 0); cx += TbBtnW + TbBtnPad;
        Btn(ref _textItalicBtnRect, cx, "I", TbFontItalic, _textItalic, 1); cx += TbBtnW + TbBtnPad;
        Eff(ref _textStrokeBtnRect, cx, EffectGlyphKind.Stroke, _textStroke, 2); cx += TbBtnW + TbBtnPad;
        Eff(ref _textShadowBtnRect, cx, EffectGlyphKind.Shadow, _textShadow, 3); cx += TbBtnW + TbBtnPad;
        Eff(ref _textBackgroundBtnRect, cx, EffectGlyphKind.Background, _textBackground, 4); cx += TbBtnW;

        cx = Sep(cx);

        // Font selector (index 5)
        _textFontBtnRect = new RectangleF(cx, cy, fontW, TbBtnH);
        PaintButtonBg(g, _textFontBtnRect, _fontDropdownOpen, _hoveredTextBtn == 5);
        DrawCenteredText(g, fontLabel, TbFont, EditorColors.TextPrimary, _textFontBtnRect,
            _hoveredTextBtn == 5 || _fontDropdownOpen ? 255 : 200);
        cx += fontW;

        cx = Sep(cx);

        // Size group: [-] <px> [+]
        Btn(ref _textSizeMinusBtnRect, cx, "−", TbFontBold, false, 6); cx += TbBtnW;
        var sizeRect = new RectangleF(cx, cy, TbSizeW, TbBtnH);
        DrawCenteredText(g, ((int)Math.Round(_textFontSize)).ToString(), TbFont, EditorColors.TextPrimary, sizeRect, 235);
        cx += TbSizeW;
        Btn(ref _textSizePlusBtnRect, cx, "+", TbFontBold, false, 7); cx += TbBtnW;

        cx = Sep(cx);

        // Drag grip (index 8)
        _textGripRect = new RectangleF(cx, cy, TbGripW, TbBtnH);
        DrawGrip(g, _textGripRect, _hoveredTextBtn == 8);

        DrawTextToolbarTooltip(g);
        if (_fontDropdownOpen) RenderFontDropdown(g);

        g.SmoothingMode = SmoothingMode.Default;
        g.TextRenderingHint = TextRenderingHint.SystemDefault;
    }

    private static void PaintToolbarSurface(Graphics g, RectangleF rect)
    {
        var shadow = rect;
        shadow.Inflate(3f, 3f);
        shadow.Offset(0, 2f);
        using (var sp = SketchRenderer.RoundedRect(shadow, 10f))
        using (var sb = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            g.FillPath(sb, sp);

        using var path = SketchRenderer.RoundedRect(rect, 8f);
        using (var bg = new SolidBrush(EditorColors.BgCard))
            g.FillPath(bg, path);
        using (var border = new Pen(EditorColors.Border, 1f))
            g.DrawPath(border, path);
    }

    private static void PaintButtonBg(Graphics g, RectangleF rect, bool active, bool hovered)
    {
        if (!active && !hovered) return;
        using var path = SketchRenderer.RoundedRect(rect, 5f);
        if (active)
        {
            using (var fill = new SolidBrush(Color.FromArgb(40, EditorColors.Accent)))
                g.FillPath(fill, path);
            using (var pen = new Pen(Color.FromArgb(150, EditorColors.Accent), 1f))
                g.DrawPath(pen, path);
        }
        else
        {
            using var fill = new SolidBrush(Color.FromArgb(26, EditorColors.Accent));
            g.FillPath(fill, path);
        }
    }

    private static void DrawCenteredText(Graphics g, string text, Font font, Color baseColor, RectangleF rect, int alpha)
    {
        using var brush = new SolidBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        g.DrawString(text, font, brush, rect, TbCenter);
    }

    // Sample "A" that demonstrates the effect (matches the capture toolbar's self-documenting glyphs).
    private static void DrawEffectGlyph(Graphics g, RectangleF rect, EffectGlyphKind kind, bool active, bool hovered)
    {
        int alpha = active ? 255 : hovered ? 225 : 150;
        var fg = Color.FromArgb(alpha, EditorColors.TextPrimary.R, EditorColors.TextPrimary.G, EditorColors.TextPrimary.B);

        switch (kind)
        {
            case EffectGlyphKind.Background:
            {
                var chip = RectangleF.Inflate(rect, -6f, -6f);
                using (var path = SketchRenderer.RoundedRect(chip, 4f))
                using (var b = new SolidBrush(fg))
                    g.FillPath(b, path);
                using (var b2 = new SolidBrush(EditorColors.BgCard))
                    g.DrawString("A", TbFontBold, b2, rect, TbCenter);
                break;
            }
            case EffectGlyphKind.Shadow:
            {
                var sr = rect;
                sr.Offset(1.5f, 1.5f);
                using (var sb = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                    g.DrawString("A", TbFontBold, sb, sr, TbCenter);
                using (var fb = new SolidBrush(fg))
                    g.DrawString("A", TbFontBold, fb, rect, TbCenter);
                break;
            }
            case EffectGlyphKind.Stroke:
            {
                using var gp = new GraphicsPath();
                float emPx = TbFontBold.SizeInPoints * g.DpiY / 72f;
                gp.AddString("A", TbFontBold.FontFamily, (int)FontStyle.Bold, emPx, rect, TbCenter);
                using var pen = new Pen(fg, 1.2f) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, gp);
                break;
            }
        }
    }

    private static void DrawGrip(Graphics g, RectangleF rect, bool hovered)
    {
        int alpha = hovered ? 235 : 150;
        using var brush = new SolidBrush(Color.FromArgb(alpha, EditorColors.TextMuted.R, EditorColors.TextMuted.G, EditorColors.TextMuted.B));
        const float dot = 2.2f, gx = 4.5f, gy = 4.5f;
        float mx = rect.X + rect.Width / 2f;
        float my = rect.Y + rect.Height / 2f;
        for (int c = 0; c < 2; c++)
            for (int r = 0; r < 3; r++)
                g.FillEllipse(brush, mx + (c == 0 ? -gx / 2f : gx / 2f) - dot / 2f, my + (r - 1) * gy - dot / 2f, dot, dot);
    }

    private void DrawTextToolbarTooltip(Graphics g)
    {
        if (_hoveredTextBtn < 0) return;
        string txt = _hoveredTextBtn switch
        {
            0 => LocalizationService.Translate("Bold"),
            1 => LocalizationService.Translate("Italic"),
            2 => LocalizationService.Translate("Stroke"),
            3 => LocalizationService.Translate("Shadow"),
            4 => LocalizationService.Translate("Background"),
            5 => _textFontFamily,
            6 => LocalizationService.Translate("Decrease size"),
            7 => LocalizationService.Translate("Increase size"),
            8 => LocalizationService.Translate("Move"),
            _ => "",
        };
        if (string.IsNullOrEmpty(txt)) return;

        RectangleF anchor = _hoveredTextBtn switch
        {
            0 => _textBoldBtnRect,
            1 => _textItalicBtnRect,
            2 => _textStrokeBtnRect,
            3 => _textShadowBtnRect,
            4 => _textBackgroundBtnRect,
            5 => _textFontBtnRect,
            6 => _textSizeMinusBtnRect,
            7 => _textSizePlusBtnRect,
            8 => _textGripRect,
            _ => RectangleF.Empty,
        };
        if (anchor.IsEmpty) return;

        var size = g.MeasureString(txt, TbFont);
        float w = size.Width + 12, h = size.Height + 6;
        float x = anchor.X + anchor.Width / 2f - w / 2f;
        float y = _textToolbarRect.Top - h - 6;
        if (y < 4) y = _textToolbarRect.Bottom + 6;
        x = Math.Clamp(x, 4f, Math.Max(4f, ClientSize.Width - w - 4f));
        var rect = new RectangleF(x, y, w, h);

        using (var path = SketchRenderer.RoundedRect(rect, 5f))
        using (var bg = new SolidBrush(Color.FromArgb(240, EditorColors.TitleBar)))
            g.FillPath(bg, path);
        using (var path2 = SketchRenderer.RoundedRect(rect, 5f))
        using (var border = new Pen(EditorColors.BorderSubtle, 1f))
            g.DrawPath(border, path2);
        DrawCenteredText(g, txt, TbFont, EditorColors.TextPrimary, rect, 235);
    }

    private void RenderFontDropdown(Graphics g)
    {
        const float itemH = 24f;
        float w = Math.Max(_textFontBtnRect.Width, 150f);
        float x = _textFontBtnRect.X;
        float h = CommonFonts.Length * itemH + 6f;

        // The inline TextBox is a native child control that paints OVER the canvas, so the
        // list must open AWAY from it (otherwise it would be hidden behind the box). The
        // toolbar sits above the text → open upward; if it's below the text → open downward.
        bool toolbarAboveText = _inlineTextBox is not null && _textToolbarRect.Top < GetInlineTextScreenBounds().Top;
        float y = toolbarAboveText ? _textFontBtnRect.Top - h - 3f : _textFontBtnRect.Bottom + 3f;
        y = Math.Clamp(y, 4f, Math.Max(4f, ClientSize.Height - h - 4f));
        x = Math.Clamp(x, 4f, Math.Max(4f, ClientSize.Width - w - 4f));
        _fontDropdownRect = new RectangleF(x, y, w, h);

        using (var sp = SketchRenderer.RoundedRect(new RectangleF(x, y + 2, w, h), 8f))
        using (var sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillPath(sb, sp);
        using (var path = SketchRenderer.RoundedRect(_fontDropdownRect, 8f))
        {
            using (var bg = new SolidBrush(EditorColors.BgSecondary))
                g.FillPath(bg, path);
            using (var border = new Pen(EditorColors.Border, 1f))
                g.DrawPath(border, path);
        }

        _fontDropdownItemRects = new RectangleF[CommonFonts.Length];
        var mouse = PointToClient(Cursor.Position);
        using var itemFmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
        for (int i = 0; i < CommonFonts.Length; i++)
        {
            var ir = new RectangleF(x + 3, y + 3 + i * itemH, w - 6, itemH);
            _fontDropdownItemRects[i] = ir;
            bool hov = ir.Contains(mouse);
            bool sel = string.Equals(CommonFonts[i], _textFontFamily, StringComparison.OrdinalIgnoreCase);
            if (hov || sel)
            {
                using var hb = new SolidBrush(Color.FromArgb(hov ? 40 : 22, EditorColors.Accent));
                using var hp = SketchRenderer.RoundedRect(ir, 4f);
                g.FillPath(hb, hp);
            }
            using var itemFont = new Font(CommonFonts[i], 10f);
            using var tbr = new SolidBrush(sel ? EditorColors.Accent : EditorColors.TextPrimary);
            g.DrawString(CommonFonts[i], itemFont, tbr, new RectangleF(ir.X + 8, ir.Y, ir.Width - 8, ir.Height), itemFmt);
        }
    }

    // ── Shared helpers (used by input hooks in AnnotationCanvas.Tools.cs) ───

    private void UpdateInlineTextBoxStyle()
    {
        if (_inlineTextBox is null) return;
        var style = (_textBold ? FontStyle.Bold : 0) | (_textItalic ? FontStyle.Italic : 0);
        float px = Math.Max(8f, _textFontSize * (float)_zoom * 0.6f);
        _inlineTextBox.Font = new Font(_textFontFamily, px, style);
        _inlineTextBox.ForeColor = ToolColor;
    }

    private void AdjustTextFontSize(float delta)
    {
        if (_inlineTextBox is null) return;
        float ns = Math.Clamp(_textFontSize + delta, 10f, 120f);
        if (Math.Abs(ns - _textFontSize) < 0.01f) return;
        _textFontSize = ns;
        UpdateInlineTextBoxStyle();
        Invalidate();
        TextFontSizeChanged?.Invoke(ns);
    }

    /// <summary>Handles a left click while editing text. Returns true if the toolbar consumed it.</summary>
    private bool HandleTextToolbarMouseDown(Point loc)
    {
        if (_inlineTextBox is null) return false;

        if (_fontDropdownOpen)
        {
            for (int i = 0; i < _fontDropdownItemRects.Length; i++)
                if (_fontDropdownItemRects[i].Contains(loc))
                {
                    _textFontFamily = CommonFonts[i];
                    _fontDropdownOpen = false;
                    UpdateInlineTextBoxStyle();
                    Invalidate();
                    return true;
                }
            if (_fontDropdownRect.Contains(loc)) return true; // absorb clicks inside the open list
            _fontDropdownOpen = false; // click elsewhere closes it, then falls through to toolbar checks
            Invalidate();
        }

        if (_textBoldBtnRect.Contains(loc)) { _textBold = !_textBold; UpdateInlineTextBoxStyle(); Invalidate(); return true; }
        if (_textItalicBtnRect.Contains(loc)) { _textItalic = !_textItalic; UpdateInlineTextBoxStyle(); Invalidate(); return true; }
        if (_textStrokeBtnRect.Contains(loc)) { _textStroke = !_textStroke; Invalidate(); return true; }
        if (_textShadowBtnRect.Contains(loc)) { _textShadow = !_textShadow; Invalidate(); return true; }
        if (_textBackgroundBtnRect.Contains(loc)) { _textBackground = !_textBackground; Invalidate(); return true; }
        if (_textFontBtnRect.Contains(loc)) { _fontDropdownOpen = !_fontDropdownOpen; Invalidate(); return true; }
        if (_textSizeMinusBtnRect.Contains(loc)) { AdjustTextFontSize(-2f); return true; }
        if (_textSizePlusBtnRect.Contains(loc)) { AdjustTextFontSize(+2f); return true; }
        if (_textGripRect.Contains(loc))
        {
            _textGripDragging = true;
            var textScreen = GetInlineTextScreenBounds();
            _textGripDragOffset = new Point(loc.X - (int)textScreen.X, loc.Y - (int)textScreen.Y);
            return true;
        }
        if (_textToolbarRect.Contains(loc)) return true; // absorb background clicks
        return false;
    }

    /// <summary>Tracks hover over the toolbar. Returns true if the cursor is over it (so the
    /// caller can skip normal tool hover/cursor logic).</summary>
    private bool UpdateTextToolbarHover(Point loc)
    {
        if (_inlineTextBox is null) return false;

        if (_fontDropdownOpen && _fontDropdownRect.Contains(loc))
        {
            Cursor = Cursors.Hand;
            Invalidate(); // repaint so the hovered item highlights
            return true;
        }

        int prev = _hoveredTextBtn;
        int h = -1;
        if (_textBoldBtnRect.Contains(loc)) h = 0;
        else if (_textItalicBtnRect.Contains(loc)) h = 1;
        else if (_textStrokeBtnRect.Contains(loc)) h = 2;
        else if (_textShadowBtnRect.Contains(loc)) h = 3;
        else if (_textBackgroundBtnRect.Contains(loc)) h = 4;
        else if (_textFontBtnRect.Contains(loc)) h = 5;
        else if (_textSizeMinusBtnRect.Contains(loc)) h = 6;
        else if (_textSizePlusBtnRect.Contains(loc)) h = 7;
        else if (_textGripRect.Contains(loc)) h = 8;

        if (h != prev)
        {
            _hoveredTextBtn = h;
            Invalidate();
        }

        if (h >= 0)
        {
            Cursor = h == 8 ? Cursors.SizeAll : Cursors.Hand;
            return true;
        }
        if (_textToolbarRect.Contains(loc))
        {
            Cursor = Cursors.Default;
            return true;
        }
        if (prev >= 0) Cursor = Cursors.Default; // just left the toolbar
        return false;
    }
}
