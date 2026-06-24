using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Linq;
using CyberSnap.Helpers;
using CyberSnap.Models;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    // This method only renders live previews for the in-progress tool state.
    private void PaintAnnotations(Graphics g)
    {
        var cursorPoint = GetLiveAnnotationCursorPoint();

        // Active tool previews
        if (_mode == CaptureMode.Blur && _isBlurring)
        {
            var pr = NormRect(_blurStart, cursorPoint);
            if (pr.Width > 2 && pr.Height > 2)
                PaintBlurRect(g, pr);
        }
        if (_mode == CaptureMode.Highlight && _isHighlighting)
        {
            var pr = NormRect(_highlightStart, cursorPoint);
            if (pr.Width > 1 && pr.Height > 1)
                SketchRenderer.DrawHighlightRect(g, pr, DefaultHighlightColor);
        }
        if (_mode == CaptureMode.RectShape && _isRectShapeDragging)
        {
            var pr = GetShapeRect(cursorPoint);
            if (pr.Width > 1 && pr.Height > 1)
                SketchRenderer.DrawRectShape(g, pr, _toolColor, AnnotationStrokeShadow, _strokeWidth);
        }
        if (_mode == CaptureMode.CircleShape && _isCircleShapeDragging)
        {
            var pr = GetShapeRect(cursorPoint);
            if (pr.Width > 1 && pr.Height > 1)
                SketchRenderer.DrawCircleShape(g, pr, _toolColor, AnnotationStrokeShadow, _strokeWidth);
        }
        if (_mode == CaptureMode.Line && _isLineDragging)
        {
            SketchRenderer.DrawLine(g, _lineStart, cursorPoint, _toolColor, _lineStart.GetHashCode(), AnnotationStrokeShadow, _strokeWidth);
        }
        if (_mode == CaptureMode.Ruler && _isRulerDragging)
        {
            var cur = GetRulerEnd(cursorPoint);
            PaintRuler(g, _rulerStart, cur);
        }
        if (_mode == CaptureMode.Arrow && _isArrowDragging)
        {
            SketchRenderer.DrawArrow(g, _arrowStart, cursorPoint, _toolColor, _arrowStart.GetHashCode(), strokeShadow: AnnotationStrokeShadow, strokeWidth: _strokeWidth);
        }
        if (_mode == CaptureMode.CurvedArrow && _isCurvedArrowDragging && _currentCurvedArrow is { Count: >= 2 })
            SketchRenderer.DrawCurvedArrow(g, _currentCurvedArrow, _toolColor, 42, AnnotationStrokeShadow, _strokeWidth);
        if (_mode == CaptureMode.Draw && _isSelecting && _currentStroke is { Count: >= 1 })
        {
            if ((ModifierKeys & Keys.Shift) != 0)
            {
                var start = _currentStroke[0];
                var end = GetConstrainedDrawPoint(cursorPoint);
                if (start != end)
                    SketchRenderer.DrawLine(g, start, end, _toolColor, start.GetHashCode(), AnnotationStrokeShadow, _strokeWidth);
            }
            else if (_currentStroke.Count >= 2)
            {
                SketchRenderer.DrawFreehandStroke(g, _currentStroke, _toolColor, 6f, AnnotationStrokeShadow);
            }
        }

        // Active text input (TextBox is off-screen for input, we paint visually here)
        if (_isTyping)
        {
            var fontStyle = FontStyle.Regular;
            if (_textBold) fontStyle |= FontStyle.Bold;
            if (_textItalic) fontStyle |= FontStyle.Italic;
            var font = GetAnnotationFont(_textFontFamily, _textFontSize, fontStyle);
            string display = _textBuffer.Length > 0 ? _textBuffer : "Type here...";
            var textSize = g.MeasureString(display, font);
            int selectionStart = _textBox?.SelectionStart ?? 0;
            int selectionLength = _textBox?.SelectionLength ?? 0;

            // Dashed selection border â€” use cached rect so handles match hit areas
            var textRect = GetActiveTextRect();
            g.DrawRectangle(GetThemeDashPen(), textRect.X, textRect.Y, textRect.Width, textRect.Height);

            foreach (var h in _activeTextHandleCache)
                WindowsHandleRenderer.Paint(g, h);

            if (_textBuffer.Length > 0 && selectionLength > 0)
            {
                float selX = _textPos.X + MeasureTextPrefixWidth(_textBuffer, selectionStart, font);
                float selW = Math.Max(2f, MeasureTextPrefixWidth(_textBuffer, selectionStart + selectionLength, font) - MeasureTextPrefixWidth(_textBuffer, selectionStart, font));
                var selRect = new RectangleF(selX - 1, textRect.Y + 3, selW + 2, Math.Max(16f, textRect.Height - 6));
                g.FillRectangle(GetTextSelectionBrush(), selRect);
            }

            // Render text with stroke/shadow
            if (_textBuffer.Length > 0)
            {
                PaintExcalidrawText(g, _textPos, _textBuffer, _textFontSize, _toolColor,
                    _textBold, _textItalic, _textStroke, _textShadow, _textBackground, _textFontFamily);
            }
            else
            {
                if (_textBackground)
                {
                    var bgRect = GetActiveTextRect();
                    using var bgPath = SketchRenderer.RoundedRect(bgRect, 8f);
                    g.FillPath(SketchRenderer.GetToolColorBrush(_toolColor), bgPath);
                    g.DrawPath(TextBackgroundStrokePen, bgPath);
                }
                g.DrawString(display, font, GetPlaceholderBrush(), _textPos.X, _textPos.Y);
            }

            // Blinking caret: draw a standard I-beam inside the text frame, not inside glyph strokes.
            if (selectionLength == 0)
            {
                float cursorX;
                int caretIndex = _textBox?.SelectionStart ?? _textBuffer.Length;
                if (_textBuffer.Length > 0)
                {
                    cursorX = _textPos.X + MeasureTextPrefixWidth(_textBuffer, caretIndex, font) - 1;
                }
                else
                {
                    cursorX = _textPos.X;
                }

                float blinkAlpha = (float)(Math.Sin(Environment.TickCount64 / 400.0 * Math.PI) * 0.5 + 0.5);
                int alpha = (int)(blinkAlpha * 220);
                var caretColor = Color.FromArgb(alpha, UiChrome.SurfaceTextPrimary.R, UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B);
                float caretTop = textRect.Y + 3;
                float caretBottom = textRect.Bottom - 3;
                _caretPen.Color = caretColor;
                g.DrawLine(_caretPen, cursorX, caretTop, cursorX, caretBottom);
            }

            // Inline text formatting toolbar above text
            PaintTextToolbar(g, textRect);
        }

        // Emoji placing preview (follow cursor)
        if (_mode == CaptureMode.Emoji && _isPlacingEmoji && _selectedEmoji != null)
        {
            PaintEmojiAnnotation(g, new Point(cursorPoint.X - (int)(_emojiPlaceSize / 2), cursorPoint.Y - (int)(_emojiPlaceSize / 2)),
                _selectedEmoji, _emojiPlaceSize, 0.6f);
        }

        // Magnifier placing preview (follow cursor)
        if (_mode == CaptureMode.Magnifier && _isPlacingMagnifier)
        {
            int srcSz = 50;
            int sx2 = Math.Clamp(cursorPoint.X - srcSz / 2, 0, _bmpW - srcSz);
            int sy2 = Math.Clamp(cursorPoint.Y - srcSz / 2, 0, _bmpH - srcSz);
            PaintMagnifierAt(g, cursorPoint, new Rectangle(sx2, sy2, srcSz, srcSz), 0.65f);
        }

        // Step number placing preview (follow cursor): shows the next badge and number
        // exactly where a click will drop it. Suppressed over the overlay toolbar/UI.
        if (_mode == CaptureMode.StepNumber && !IsPointInOverlayUi(cursorPoint))
        {
            PaintStepNumber(g, cursorPoint, _nextStepNumber, _toolColor, 0.6f);
        }

        PaintGlobalSnapGuides(g);

        if (_selectPreviewAnnotation is not null)
            RenderAnnotationTo(g, _selectPreviewAnnotation);

        // Color/emoji/font picker popups are painted on the separate ToolbarForm

        // Snapshot the bounds we just (potentially) drew onto so the next
        // InvalidateLivePreview can guarantee those pixels get cleared.
        _lastLivePreviewPaintExtent = ComputeCurrentLivePreviewExtent();
    }

    internal static Rectangle GetRulerPaintBounds(Point from, Point to)
        => RulerRenderer.GetSelectionBounds(from, to);

    /// <summary>Returns conservative bounds for whatever live preview is currently being painted.
    /// Used as a smear-prevention fallback so the next invalidate always clears the previous paint.</summary>
    private Rectangle ComputeCurrentLivePreviewExtent()
    {
        var cursorPoint = GetLiveAnnotationCursorPoint();
        Rectangle r = Rectangle.Empty;

        static Rectangle U(Rectangle a, Rectangle b)
        {
            if (b.Width <= 0 || b.Height <= 0) return a;
            if (a.Width <= 0 || a.Height <= 0) return b;
            return Rectangle.Union(a, b);
        }

        if (_mode == CaptureMode.Blur && _isBlurring)
            r = U(r, NormRect(_blurStart, cursorPoint));
        if (_mode == CaptureMode.Highlight && _isHighlighting)
            r = U(r, NormRect(_highlightStart, cursorPoint));
        if (_mode == CaptureMode.RectShape && _isRectShapeDragging)
            r = U(r, GetShapeRect(cursorPoint));
        if (_mode == CaptureMode.CircleShape && _isCircleShapeDragging)
            r = U(r, GetShapeRect(cursorPoint));
        if (_mode == CaptureMode.Line && _isLineDragging)
            r = U(r, RectFromPoints(_lineStart, cursorPoint, 8));
        // Ruler excluded from smear-proofing: GetRulerPaintBounds is already accurate
        // and the dimming overlay would accumulate alpha in re-invalidated areas.
        if (_mode == CaptureMode.Arrow && _isArrowDragging)
            r = U(r, RectFromPoints(_arrowStart, cursorPoint, 28));
        if (_mode == CaptureMode.CurvedArrow && _isCurvedArrowDragging && _currentCurvedArrow is { Count: >= 2 })
            r = U(r, BoundsOfPoints(_currentCurvedArrow, 18));
        if (_mode == CaptureMode.Draw && _isSelecting)
            r = U(r, GetDrawPreviewBounds());
        if (_mode == CaptureMode.Emoji && _isPlacingEmoji)
            r = U(r, GetEmojiPreviewRect(cursorPoint));
        if (_mode == CaptureMode.Magnifier && _isPlacingMagnifier)
            r = U(r, GetMagnifierPreviewRect(cursorPoint));
        if (_mode == CaptureMode.StepNumber)
            r = U(r, GetStepPreviewRect(cursorPoint));
        if (_isTyping)
            r = U(r, InflateForRepaint(Rectangle.Round(GetActiveTextRect()), 16));
        if (_selectPreviewAnnotation is not null)
            r = U(r, GetAnnotationBounds(_selectPreviewAnnotation));

        return r.Width > 0 && r.Height > 0 ? InflateForRepaint(r, 8) : Rectangle.Empty;
    }

    private Point GetLiveAnnotationCursorPoint()
        => _lastCursorPos != Point.Empty
            ? _lastCursorPos
            : PointToClient(System.Windows.Forms.Cursor.Position);

    private static readonly Pen SnapGuideShadowPen = new(Color.FromArgb(28, 0, 0, 0), 3f);
    private static readonly Pen TextBackgroundStrokePen = new(Color.FromArgb(60, 0, 0, 0), 1f);
    private static Pen? _snapGuideDashPen;
    private static Pen? _themeDashPen;
    private static SolidBrush? _textSelectionBrush;
    private static SolidBrush? _placeholderBrush;
    private static int _themeChromeKey;

    private static int GetThemeChromeKey()
        => HashCode.Combine(UiChrome.SurfaceTextPrimary.ToArgb(), UiChrome.SurfaceTextMuted.ToArgb());

    private static void EnsureThemeChrome()
    {
        int key = GetThemeChromeKey();
        if (_snapGuideDashPen != null && _themeChromeKey == key) return;

        _snapGuideDashPen?.Dispose();
        _themeDashPen?.Dispose();
        _textSelectionBrush?.Dispose();
        _placeholderBrush?.Dispose();

        var c = UiChrome.SurfaceTextPrimary;
        _snapGuideDashPen = new Pen(Color.FromArgb(150, c.R, c.G, c.B), 1f)
        {
            DashStyle = DashStyle.Dash,
            DashPattern = new[] { 6f, 4f }
        };
        _themeDashPen = new Pen(c, 1f) { DashStyle = DashStyle.Dash };
        _textSelectionBrush = new SolidBrush(Color.FromArgb(90, c.R, c.G, c.B));
        _placeholderBrush = new SolidBrush(UiChrome.SurfaceTextMuted);
        _themeChromeKey = key;
    }

    private static Pen GetSnapGuideDashPen()
    {
        EnsureThemeChrome();
        return _snapGuideDashPen!;
    }

    private static Pen GetThemeDashPen()
    {
        EnsureThemeChrome();
        return _themeDashPen!;
    }

    private static SolidBrush GetTextSelectionBrush()
    {
        EnsureThemeChrome();
        return _textSelectionBrush!;
    }

    private static SolidBrush GetPlaceholderBrush()
    {
        EnsureThemeChrome();
        return _placeholderBrush!;
    }

    private void PaintGlobalSnapGuides(Graphics g)
    {
        if (!_snapGuideXVisible && !_snapGuideYVisible)
            return;

        g.SmoothingMode = SmoothingMode.None;
        int centerX = ClientSize.Width / 2;
        int centerY = ClientSize.Height / 2;
        var shadowPen = SnapGuideShadowPen;
        var guidePen = GetSnapGuideDashPen();

        if (_snapGuideXVisible)
        {
            g.DrawLine(shadowPen, centerX + 1, 0, centerX + 1, ClientSize.Height);
            g.DrawLine(guidePen, centerX, 0, centerX, ClientSize.Height);
        }

        if (_snapGuideYVisible)
        {
            g.DrawLine(shadowPen, 0, centerY + 1, ClientSize.Width, centerY + 1);
            g.DrawLine(guidePen, 0, centerY, ClientSize.Width, centerY);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
    }

    /// <summary>Text annotation: uses DrawString for correct kerning. Shadow and stroke via offset draws.</summary>
    private static void PaintExcalidrawText(Graphics g, Point pos, string text, float fontSize, Color color,
        bool bold = true, bool italic = false, bool stroke = true, bool shadow = true, bool background = false, string fontFamily = UiChrome.DefaultFontFamily)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;
        var font = GetAnnotationFont(fontFamily, fontSize, style);
        {
            if (background)
            {
                var bgRect = MeasureTextRect(pos, text, fontSize, fontFamily, bold, italic, background: true);
                using var bgPath = SketchRenderer.RoundedRect(bgRect, 8f);
                if (shadow)
                {
                    using var shadowPath = SketchRenderer.RoundedRect(new RectangleF(bgRect.X + 2, bgRect.Y + 2, bgRect.Width, bgRect.Height), 8f);
                    g.FillPath(TextBackgroundShadowBrush, shadowPath);
                }
                g.FillPath(SketchRenderer.GetToolColorBrush(color), bgPath);
                if (stroke)
                    g.DrawPath(TextBackgroundStrokeThickPen, bgPath);
                color = Color.White;
            }

            // Shadow: draw text offset in dark color at multiple offsets for soft effect
            if (shadow)
            {
                g.DrawString(text, font, TextShadowBrush1, pos.X + 2, pos.Y + 2);
                g.DrawString(text, font, TextShadowBrush2, pos.X + 3, pos.Y + 3);
            }

            // Stroke: draw text at small offsets in dark color to simulate outline
            if (stroke)
            {
                for (int ox = -1; ox <= 1; ox++)
                    for (int oy = -1; oy <= 1; oy++)
                        if (ox != 0 || oy != 0)
                            g.DrawString(text, font, TextStrokeBrush, pos.X + ox, pos.Y + oy);
            }

            // Main text
            g.DrawString(text, font, SketchRenderer.GetToolColorBrush(color), pos.X, pos.Y);
        }

        g.TextRenderingHint = TextRenderingHint.SystemDefault;
    }

    private static Font? _stepNumberFont;
    private static readonly SolidBrush StepNumberShadowBrush = new(Color.FromArgb(50, 0, 0, 0));
    private static readonly Pen StepNumberInnerEdgePen = new(Color.FromArgb(40, 255, 255, 255), 1f);
    private static readonly SolidBrush StepNumberDarkText = new(Color.FromArgb(20, 20, 20));
    private static readonly SolidBrush StepNumberLightText = new(Color.FromArgb(255, 255, 255));

    private static void PaintStepNumber(Graphics g, Point pos, int num, Color color, float opacity = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var font = _stepNumberFont ??= UiChrome.ChromeFont(11f, FontStyle.Bold);
        string text = num.ToString();
        var sz = g.MeasureString(text, font);

        // Size the badge to fit the number with padding
        float padX = 8f, padY = 4f;
        float w = Math.Max(sz.Width + padX * 2, sz.Height + padY * 2); // at least circular
        float h = sz.Height + padY * 2;
        float r = h / 2f; // fully rounded ends
        var rect = new RectangleF(pos.X - w / 2f, pos.Y - h / 2f, w, h);

        using var shadowPath = SketchRenderer.RoundedRect(
            new RectangleF(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), r);
        using var bgPath = SketchRenderer.RoundedRect(rect, r);
        int luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;

        if (opacity >= 1f)
        {
            g.FillPath(StepNumberShadowBrush, shadowPath);
            g.FillPath(SketchRenderer.GetToolColorBrush(color), bgPath);
            g.DrawPath(StepNumberInnerEdgePen, bgPath);
            var textBrush = luma > 140 ? StepNumberDarkText : StepNumberLightText;
            g.DrawString(text, font, textBrush, rect.X + (rect.Width - sz.Width) / 2f, rect.Y + (rect.Height - sz.Height) / 2f);
        }
        else
        {
            // Translucent ghost preview that follows the cursor before the click commits it.
            int a = (int)Math.Clamp(255 * opacity, 0, 255);
            using var shadow = new SolidBrush(Color.FromArgb((int)(50 * opacity), 0, 0, 0));
            using var bg = new SolidBrush(Color.FromArgb(a, color.R, color.G, color.B));
            using var edge = new Pen(Color.FromArgb((int)(40 * opacity), 255, 255, 255), 1f);
            g.FillPath(shadow, shadowPath);
            g.FillPath(bg, bgPath);
            g.DrawPath(edge, bgPath);
            var tc = luma > 140 ? Color.FromArgb(a, 20, 20, 20) : Color.FromArgb(a, 255, 255, 255);
            using var textBrush = new SolidBrush(tc);
            g.DrawString(text, font, textBrush, rect.X + (rect.Width - sz.Width) / 2f, rect.Y + (rect.Height - sz.Height) / 2f);
        }

        g.TextRenderingHint = TextRenderingHint.SystemDefault;
        g.SmoothingMode = SmoothingMode.Default;
    }

    private void PaintPlacedMagnifier(Graphics g, Point pos, Rectangle srcRect)
    {
        PaintMagnifierAt(g, pos, srcRect, 1f);
    }

    private Rectangle GetMagnifierVisualBounds(MagnifierAnnotation mg)
    {
        int zoom = 3;
        int dstSize = mg.SrcRect.Width * zoom;
        int px = mg.Pos.X + 20;
        int py = mg.Pos.Y + 20;
        if (px + dstSize + 6 > ClientSize.Width) px = mg.Pos.X - 20 - dstSize;
        if (py + dstSize + 6 > ClientSize.Height) py = mg.Pos.Y - 20 - dstSize;
        return new Rectangle(px - 6, py - 6, dstSize + 12, dstSize + 12);
    }

    private static Rectangle GetMagnifierPaintBounds(Point pos, Rectangle srcRect, Size clientSize)
    {
        int zoom = 3;
        int dstSize = srcRect.Width * zoom;

        int px = pos.X + 20;
        int py = pos.Y + 20;
        if (!clientSize.IsEmpty)
        {
            if (px + dstSize + 6 > clientSize.Width) px = pos.X - 20 - dstSize;
            if (py + dstSize + 6 > clientSize.Height) py = pos.Y - 20 - dstSize;
        }
        else
        {
            int spread = dstSize + 34;
            return new Rectangle(pos.X - spread, pos.Y - spread, spread * 2, spread * 2);
        }

        var bounds = new Rectangle(px - 4, py - 4, dstSize + 8, dstSize + 8);
        bounds.Inflate(6, 6);
        return bounds;
    }

    private void PaintMagnifierAt(Graphics g, Point pos, Rectangle srcRect, float opacity)
    {
        int zoom = 3;
        int dstSize = srcRect.Width * zoom;

        int px = pos.X + 20;
        int py = pos.Y + 20;
        if (px + dstSize + 6 > ClientSize.Width) px = pos.X - 20 - dstSize;
        if (py + dstSize + 6 > ClientSize.Height) py = pos.Y - 20 - dstSize;

        var dstRect = new Rectangle(px, py, dstSize, dstSize);

        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bgPath = new GraphicsPath())
            {
                bgPath.AddEllipse(new RectangleF(px - 2, py - 2, dstSize + 4, dstSize + 4));
                var bg = SketchRenderer.GetToolColorBrush(Color.FromArgb((int)(200 * opacity), UiChrome.SurfaceElevated.R, UiChrome.SurfaceElevated.G, UiChrome.SurfaceElevated.B));
                g.FillPath(bg, bgPath);
            }

            using var clipPath = new GraphicsPath();
            clipPath.AddEllipse(dstRect);
            g.SetClip(clipPath);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(_screenshot, dstRect, srcRect, GraphicsUnit.Pixel);

            int ccx = px + dstSize / 2, ccy = py + dstSize / 2;
            var crossPen = SketchRenderer.GetRoundCapPen(Color.FromArgb((int)(180 * opacity), UiChrome.SurfaceTextPrimary.R, UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B), 1f);
            g.DrawLine(crossPen, ccx - 8, ccy, ccx + 8, ccy);
            g.DrawLine(crossPen, ccx, ccy - 8, ccx, ccy + 8);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var borderPen = SketchRenderer.GetRoundCapPen(Color.FromArgb((int)(70 * opacity), UiChrome.SurfaceBorderStrong.R, UiChrome.SurfaceBorderStrong.G, UiChrome.SurfaceBorderStrong.B), 1f);
            g.DrawPath(borderPen, clipPath);
        }
        finally
        {
            g.Restore(state);
        }
    }

    [ThreadStatic] private static System.Drawing.Imaging.ImageAttributes? _emojiOpacityAttr;
    [ThreadStatic] private static System.Drawing.Imaging.ColorMatrix? _emojiOpacityMatrix;

    private void PaintEmojiAnnotation(Graphics g, Point pos, string emoji, float size, float opacity = 1f)
    {
        var emojiBmp = _emojiRenderer.GetEmoji(emoji, size);

        if (opacity < 1f)
        {
            // Reuse a thread-static ImageAttributes + ColorMatrix to avoid 5 jagged-array allocs per frame.
            _emojiOpacityAttr ??= new System.Drawing.Imaging.ImageAttributes();
            _emojiOpacityMatrix ??= new System.Drawing.Imaging.ColorMatrix();
            _emojiOpacityMatrix.Matrix00 = 1f;
            _emojiOpacityMatrix.Matrix11 = 1f;
            _emojiOpacityMatrix.Matrix22 = 1f;
            _emojiOpacityMatrix.Matrix33 = opacity;
            _emojiOpacityMatrix.Matrix44 = 1f;
            _emojiOpacityAttr.SetColorMatrix(_emojiOpacityMatrix);
            g.DrawImage(emojiBmp, new Rectangle(pos.X, pos.Y, emojiBmp.Width, emojiBmp.Height),
                0, 0, emojiBmp.Width, emojiBmp.Height, GraphicsUnit.Pixel, _emojiOpacityAttr);
        }
        else
        {
            g.DrawImage(emojiBmp, pos.X, pos.Y);
        }
    }

    // Pre-cached annotation fonts (allocated once, reused every frame)
    private static readonly Dictionary<(string, float, FontStyle), Font> _annotationFontCache = new();
    private static Font GetAnnotationFont(string family, float size, FontStyle style)
    {
        var key = (family, size, style);
        if (_annotationFontCache.TryGetValue(key, out var cached))
            return cached;
        Font font;
        try { font = new Font(family, size, style); }
        catch { font = UiChrome.ChromeFont(size, style); }
        _annotationFontCache[key] = font;
        return font;
    }

    private static readonly SolidBrush TextShadowBrush1 = new(Color.FromArgb(50, 0, 0, 0));
    private static readonly SolidBrush TextShadowBrush2 = new(Color.FromArgb(25, 0, 0, 0));
    private static readonly SolidBrush TextStrokeBrush = new(Color.FromArgb(60, 0, 0, 0));
    private static readonly SolidBrush TextBackgroundShadowBrush = new(Color.FromArgb(55, 0, 0, 0));
    private static readonly Pen TextBackgroundStrokeThickPen = new(Color.FromArgb(60, 0, 0, 0), 1.25f);
    private static readonly Pen _caretPen = new(Color.Black, 1.6f);
}
