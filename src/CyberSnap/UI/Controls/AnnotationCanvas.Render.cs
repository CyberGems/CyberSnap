using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI.Editor;
using CyberSnap.Helpers;

namespace CyberSnap.UI.Controls;

public sealed partial class AnnotationCanvas
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        // Draw the base image from a pre-scaled cache so repaints don't re-run an
        // expensive full-resolution rescale every frame. See DrawBaseImage.
        DrawBaseImage(g);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Apply zoom/pan as a single transform so annotations stored in image-space
        // render to screen-space without further math per draw call.
        var state = g.Save();
        try
        {
            g.TranslateTransform(_pan.X, _pan.Y);
            g.ScaleTransform((float)_zoom, (float)_zoom);

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height));
            try
            {
                RenderAnnotations(g);
                RenderToolPreview(g);
                RenderInlineTextPreview(g);
            }
            finally
            {
                g.Clip = oldClip;
            }
        }
        finally
        {
            g.Restore(state);
        }

        RenderResizeHandles(g);
        RenderCropOverlay(g);
        RenderCheckerboardFrame(g);
        RenderGuides(g);
        RenderToolBanner(g);
        RenderCursorToolPreview(g);

        if (IsDefaultBlank && !_welcomeDismissed && ShowWelcomeBanner)
            RenderWelcomeText(g);

        if (_inlineTextBox is not null)
            RenderInlineTextToolbar(g);

        RenderScrollbars(g);
    }

    // ── Base-image draw cache ──────────────────────────────────────────────
    // Scaling the full-resolution base bitmap on every OnPaint (especially with
    // HighQualityBicubic when zoomed out) is the dominant cost for large images.
    // We render the scaled image once per (zoom, size) into _scaledCache and blit
    // it 1:1 on subsequent repaints (banner fades, caret blink, hover, pan, etc.).
    private Bitmap? _scaledCache;
    private int _scaledCacheW = -1;
    private int _scaledCacheH = -1;

    /// <summary>Draws the base bitmap at the current zoom/pan, using the pre-scaled cache.</summary>
    private void DrawBaseImage(Graphics g)
    {
        int scaledW = Math.Max(1, (int)Math.Round(_baseBitmap.Width * _zoom));
        int scaledH = Math.Max(1, (int)Math.Round(_baseBitmap.Height * _zoom));

        // Zoomed in (>= 1): NearestNeighbor straight from the source is already cheap —
        // GDI+ clips rasterization to the visible region, so cost scales with on-screen
        // pixels, not the (potentially huge) destination size. Caching here would mean
        // allocating a larger-than-screen bitmap (e.g. 28000×16000 at 8×), so we don't.
        // This also preserves the crisp pixel-peeping look of the original code.
        if (_zoom >= 1.0)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_baseBitmap, _pan.X, _pan.Y, scaledW, scaledH);
            return;
        }

        // Zoomed out (< 1): the expensive bicubic-downscale case.
        // During an active zoom gesture, stretch the last settled cache (≈ screen-sized,
        // far fewer source pixels to sample than the full-res bitmap) for a cheap draft;
        // the settle timer then rebuilds the crisp cache once. Only the full source as a
        // fallback when no cache exists yet.
        if (_zoomInteracting)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage((Image?)_scaledCache ?? _baseBitmap, _pan.X, _pan.Y, scaledW, scaledH);
            return;
        }

        EnsureScaledCache(scaledW, scaledH);

        // Blit the cache 1:1 (dest size == cache size) — NearestNeighbor here is a copy,
        // not a resample, so there's no quality loss versus the cached HQ render.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(_scaledCache!, _pan.X, _pan.Y, scaledW, scaledH);
    }

    /// <summary>Rebuilds _scaledCache when the requested on-screen size changes.</summary>
    private void EnsureScaledCache(int scaledW, int scaledH)
    {
        if (_scaledCache is not null && _scaledCacheW == scaledW && _scaledCacheH == scaledH)
            return;

        _scaledCache?.Dispose();
        _scaledCache = null;

        var cache = new Bitmap(scaledW, scaledH, PixelFormat.Format32bppPArgb);
        using (var cg = Graphics.FromImage(cache))
        {
            // Match the original quality rule: crisp pixels when zoomed in, smooth
            // bicubic when zoomed out. This runs once per zoom level, not per frame.
            cg.InterpolationMode = _zoom >= 1.0
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            cg.CompositingMode = CompositingMode.SourceCopy;
            cg.DrawImage(_baseBitmap, new Rectangle(0, 0, scaledW, scaledH));
        }

        _scaledCache = cache;
        _scaledCacheW = scaledW;
        _scaledCacheH = scaledH;
    }

    /// <summary>Discards the pre-scaled cache; call whenever the base bitmap content changes.</summary>
    private void InvalidateScaledCache()
    {
        _scaledCache?.Dispose();
        _scaledCache = null;
        _scaledCacheW = -1;
        _scaledCacheH = -1;
    }

    /// <summary>Renders committed annotations. Called inside the zoom/pan transform.</summary>
    private void RenderAnnotations(Graphics g)
    {
        for (int i = 0; i < _annotations.Count; i++)
        {
            // Skip the annotation currently being re-edited (live preview replaces it)
            if (i == _renderSkipAnnotationIndex) continue;

            RenderAnnotation(g, _annotations[i]);

            // Eraser hover highlight
            if (i == _eraserHoverIndex)
            {
                var bounds = GetAnnotationBounds(_annotations[i]);
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    using var overlay = new SolidBrush(Color.FromArgb(50, 220, 50, 50));
                    g.FillRectangle(overlay, bounds);

                    using var pen = new Pen(Color.FromArgb(200, 220, 40, 40), 2f)
                    {
                        DashStyle = DashStyle.Dash,
                        DashPattern = new[] { 5f, 3f }
                    };
                    g.DrawRectangle(pen, bounds.X - 3, bounds.Y - 3, bounds.Width + 6, bounds.Height + 6);
                }
            }
        }

        // Move hover highlight (skip if item is part of multi-selection — it already has handles)
        // Also skip the annotation currently being re-edited (live inline frame replaces it).
        if (_preSpaceTool == null && IsDrawingOrMoveTool(_activeTool) && _moveHoverIndex >= 0 && _moveHoverIndex < _annotations.Count
            && _moveHoverIndex != _selectedAnnotationIndex
            && _moveHoverIndex != _renderSkipAnnotationIndex
            && !_multiSelectedIndices.Contains(_moveHoverIndex))
        {
            var hovered = _annotations[_moveHoverIndex];
            var bounds = GetAnnotationBounds(hovered);
            DrawMoveHandles(g, bounds, isSelected: false, moveOnly: !IsResizable(hovered));
        }

        // Multi-selection highlights
        if (_preSpaceTool == null && _multiSelectedIndices.Count > 1)
        {
            foreach (int idx in _multiSelectedIndices)
            {
                if (idx == _renderSkipAnnotationIndex) continue;
                if (idx >= 0 && idx < _annotations.Count)
                {
                    var ann = _annotations[idx];
                    var bounds = GetAnnotationBounds(ann);
                    DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(ann));
                }
            }
        }
        // Single selection highlight (only when NOT part of an active multi-selection)
        else if (_preSpaceTool == null
            && _selectedAnnotationIndex >= 0
            && _selectedAnnotationIndex < _annotations.Count
            && _selectedAnnotationIndex != _renderSkipAnnotationIndex)
        {
            var selected = _annotations[_selectedAnnotationIndex];
            var bounds = GetAnnotationBounds(selected);
            DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(selected));
        }
    }

    private void DrawMoveHandles(Graphics g, Rectangle bounds, bool isSelected, bool moveOnly = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        float z = (float)_zoom;
        if (z <= 0.01f) z = 1.0f;

        float penWidthThick = 3.5f / z;
        float penWidthShadow = 5.5f / z;
        float len = 12f / z;
        float barLen = 14f / z;
        float offset = 4f / z; // offset outside bounds

        var rect = new RectangleF(
            bounds.X - offset,
            bounds.Y - offset,
            bounds.Width + 2 * offset,
            bounds.Height + 2 * offset
        );

        // Theme-aware accent: cyan reads great on the dark canvas but washes out on the
        // light one, so on light we use the app's blue accent (Theme.Accent). All selection
        // chrome (box, handles, move knob) shares this single color.
        var accent = Theme.Accent;
        byte aR = accent.R, aG = accent.G, aB = accent.B;

        // Alpha values
        int accentAlpha = isSelected ? 255 : 95;
        int shadowAlpha = isSelected ? 100 : 35;
        int fillAlpha = isSelected ? 0 : 6; // subtle accent tint
        int dashAlpha = isSelected ? 180 : 60;

        var accentColor = Color.FromArgb(accentAlpha, aR, aG, aB);
        var shadowColor = Color.FromArgb(shadowAlpha, 0, 0, 0);

        // Fill and dash
        if (fillAlpha > 0)
        {
            using (var fillBrush = new SolidBrush(Color.FromArgb(fillAlpha, aR, aG, aB)))
            {
                g.FillRectangle(fillBrush, rect);
            }
        }

        using (var dashPen = new Pen(Color.FromArgb(dashAlpha, aR, aG, aB), 1.5f / z))
        {
            dashPen.DashStyle = DashStyle.Dash;
            dashPen.DashPattern = new[] { 4f, 3f };
            g.DrawRectangle(dashPen, rect.X, rect.Y, rect.Width, rect.Height);
        }

        // Resize handles (L-corners + mid-edge bars). Skipped for move-only items (fixed-size
        // badges like step numbers), which keep just the dashed box and the center move knob.
        float midX = rect.Left + rect.Width / 2f;
        float midY = rect.Top + rect.Height / 2f;
        if (!moveOnly)
        {
            using var thickPen = new Pen(accentColor, penWidthThick) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var shadowPen = new Pen(shadowColor, penWidthShadow) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            // 1. Draw Corners (L-shapes)
            // Top-Left
            DrawL(g, shadowPen, rect.Left, rect.Top, len, len);
            DrawL(g, thickPen, rect.Left, rect.Top, len, len);

            // Top-Right
            DrawL(g, shadowPen, rect.Right, rect.Top, -len, len);
            DrawL(g, thickPen, rect.Right, rect.Top, -len, len);

            // Bottom-Left
            DrawL(g, shadowPen, rect.Left, rect.Bottom, len, -len);
            DrawL(g, thickPen, rect.Left, rect.Bottom, len, -len);

            // Bottom-Right
            DrawL(g, shadowPen, rect.Right, rect.Bottom, -len, -len);
            DrawL(g, thickPen, rect.Right, rect.Bottom, -len, -len);

            // 2. Draw Mid-edges (Pills/bars)
            // Top edge
            g.DrawLine(shadowPen, midX - barLen / 2f, rect.Top, midX + barLen / 2f, rect.Top);
            g.DrawLine(thickPen, midX - barLen / 2f, rect.Top, midX + barLen / 2f, rect.Top);

            // Bottom edge
            g.DrawLine(shadowPen, midX - barLen / 2f, rect.Bottom, midX + barLen / 2f, rect.Bottom);
            g.DrawLine(thickPen, midX - barLen / 2f, rect.Bottom, midX + barLen / 2f, rect.Bottom);

            // Left edge
            g.DrawLine(shadowPen, rect.Left, midY - barLen / 2f, rect.Left, midY + barLen / 2f);
            g.DrawLine(thickPen, rect.Left, midY - barLen / 2f, rect.Left, midY + barLen / 2f);

            // Right edge
            g.DrawLine(shadowPen, rect.Right, midY - barLen / 2f, rect.Right, midY + barLen / 2f);
            g.DrawLine(thickPen, rect.Right, midY - barLen / 2f, rect.Right, midY + barLen / 2f);
        }

        // 3. Center move handle — a free-standing 4-way move arrow (✥), rendered on both
        // hover and selection. There is NO enclosing ring (the arrows ARE the handle, so it
        // reads as a move cursor, not a circle/target), and it's drawn in SCREEN space — i.e.
        // outside the zoom transform — so it stays pixel-crisp and a constant size at any zoom
        // (drawing it inside the scaled transform softened it badly when zoomed in).
        {
            // Image-space center → screen space, then step out of the zoom/pan transform.
            float scx = (float)(_pan.X + midX * _zoom);
            float scy = (float)(_pan.Y + midY * _zoom);

            var gstate = g.Save();
            g.ResetTransform();

            const float armLen   = 11f;   // reach from center to each arrowhead tip
            const float gap      = 3.5f;  // central empty gap so the four arrows read as distinct
            const float headBack = 4.5f;  // how far the arrowhead chevron runs back along the stem
            const float headW    = 3.4f;  // arrowhead half-width (perpendicular spread)

            // State-aware alphas — dimmer on hover, full on selection (mirrors the box/handles).
            int glyphA = isSelected ? 255 : 175;
            // Contrast halo follows the glyph shape (NOT a disc): dark on the dark theme,
            // light on the light theme, so the arrows pop without painting a circle.
            int haloA  = isSelected ? 150 : 90;
            var haloColor  = Theme.IsDark ? Color.FromArgb(haloA, 0, 0, 0)
                                          : Color.FromArgb(haloA, 255, 255, 255);
            var glyphColor = Color.FromArgb(glyphA, aR, aG, aB);

            void DrawMoveArrows(Pen p)
            {
                // Stems (with a central gap so the four arrows read as distinct)
                g.DrawLine(p, scx - armLen, scy, scx - gap, scy);
                g.DrawLine(p, scx + gap,    scy, scx + armLen, scy);
                g.DrawLine(p, scx, scy - armLen, scx, scy - gap);
                g.DrawLine(p, scx, scy + gap,    scx, scy + armLen);
                // Arrowheads (chevrons) at each tip, pointing outward
                g.DrawLine(p, scx + armLen, scy, scx + armLen - headBack, scy - headW);
                g.DrawLine(p, scx + armLen, scy, scx + armLen - headBack, scy + headW);
                g.DrawLine(p, scx - armLen, scy, scx - armLen + headBack, scy - headW);
                g.DrawLine(p, scx - armLen, scy, scx - armLen + headBack, scy + headW);
                g.DrawLine(p, scx, scy - armLen, scx - headW, scy - armLen + headBack);
                g.DrawLine(p, scx, scy - armLen, scx + headW, scy - armLen + headBack);
                g.DrawLine(p, scx, scy + armLen, scx - headW, scy + armLen - headBack);
                g.DrawLine(p, scx, scy + armLen, scx + headW, scy + armLen - headBack);
            }

            using (var halo = new Pen(haloColor, 3.6f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            })
                DrawMoveArrows(halo);

            using (var glyphPen = new Pen(glyphColor, 1.9f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            })
                DrawMoveArrows(glyphPen);

            g.Restore(gstate);
        }
    }

    private void RenderAnnotation(Graphics g, Annotation a)
    {
        switch (a)
        {
            case DrawStroke ds:
                SketchRenderer.DrawFreehandStroke(g, ds.Points, ds.Color, GetScaledStrokeWidth(ds.StrokeWidth), AnnotationStrokeShadow);
                break;
            case ArrowAnnotation arr:
                SketchRenderer.DrawArrow(g, arr.From, arr.To, arr.Color, arr.From.GetHashCode(),
                    strokeShadow: AnnotationStrokeShadow, strokeWidth: GetScaledStrokeWidth(arr.StrokeWidth));
                break;
            case CurvedArrowAnnotation ca:
                SketchRenderer.DrawCurvedArrow(g, ca.Points, ca.Color, ca.Points.Count * 7919, AnnotationStrokeShadow, GetScaledStrokeWidth(ca.StrokeWidth));
                break;
            case LineAnnotation ln:
                SketchRenderer.DrawLine(g, ln.From, ln.To, ln.Color, ln.From.GetHashCode(), AnnotationStrokeShadow, GetScaledStrokeWidth(ln.StrokeWidth));
                break;
            case RectShapeAnnotation rs:
                SketchRenderer.DrawRectShape(g, rs.Rect, rs.Color, AnnotationStrokeShadow, GetScaledStrokeWidth(rs.StrokeWidth));
                break;
            case CircleShapeAnnotation cs:
                SketchRenderer.DrawCircleShape(g, cs.Rect, cs.Color, AnnotationStrokeShadow, GetScaledStrokeWidth(cs.StrokeWidth));
                break;
            case HighlightAnnotation hl:
                using (var path = SketchRenderer.RoundedRect(hl.Rect, 5))
                using (var brush = new SolidBrush(Color.FromArgb(92, hl.Color.R, hl.Color.G, hl.Color.B)))
                    g.FillPath(brush, path);
                break;
            case TextAnnotation ta:
                RenderTextAnnotation(g, ta);
                break;
            case BlurRect br:
                PaintBlurRect(g, br.Rect);
                break;
            case StepNumberAnnotation sn:
                PaintStepNumber(g, sn.Pos, sn.Number, sn.Color);
                break;
            case MagnifierAnnotation mg:
                PaintMagnifier(g, mg.Pos, mg.SrcRect);
                break;
            case EmojiAnnotation em:
                PaintEmoji(g, em.Pos, em.Emoji, em.Size);
                break;

        }
    }

    private static void RenderTextAnnotation(Graphics g, TextAnnotation ta) =>
        TextAnnotationPainter.Paint(g, ta);

    /// <summary>Measures the visual bounding rect of a text annotation (including padding).
    /// Matches the logic used in the capture overlay.</summary>
    private static RectangleF MeasureInlineTextRect(
        Point pos, string text, float fontSize, string fontFamily,
        bool bold, bool italic, bool background = false,
        float maxWidth = 0, TextHAlign align = TextHAlign.Left) =>
        TextAnnotationPainter.Measure(pos, text, fontSize, fontFamily, bold, italic, background, maxWidth, align);

    /// <summary>Renders the live inline text preview inside the zoom/pan transform.</summary>
    private void RenderInlineTextPreview(Graphics g)
    {
        if (_inlineTextBox is null) return;

        var pos = new Point(_inlineTextOrigin.X, _inlineTextOrigin.Y);
        string raw = _inlineTextBox.Text;
        int selStart = _inlineTextBox.SelectionStart;
        int selLen = _inlineTextBox.SelectionLength;

        var textRect = MeasureInlineTextRect(
            _inlineTextOrigin, raw, _textFontSize, _textFontFamily,
            _textBold, _textItalic, _textBackground, _textMaxWidth, _textAlign);

        // Selection highlight under glyphs (bright accent — must be obvious)
        if (raw.Length > 0 && selLen > 0)
        {
            PaintEditorTextSelection(g, pos, raw, selStart, selLen, textRect);
        }

        if (raw.Length > 0)
        {
            TextAnnotationPainter.Paint(g, pos, raw, _textFontSize, ToolColor,
                _textBold, _textItalic, _textStroke, _textShadow, _textBackground, _textFontFamily,
                _textMaxWidth, _textAlign);
        }
        else
        {
            TextAnnotationPainter.Paint(g, pos, "", _textFontSize, ToolColor,
                _textBold, _textItalic, _textStroke, _textShadow, _textBackground, _textFontFamily,
                _textMaxWidth, _textAlign, isPlaceholder: true);
        }

        // Dashed selection border + resize handles
        using (var dashPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(dashPen, textRect.X, textRect.Y, textRect.Width, textRect.Height);

        DrawInlineTextHandles(g, textRect);

        // Blinking caret (hidden while a range is selected)
        if (selLen == 0)
        {
            int caretIndex = selStart;
            var caret = TextAnnotationPainter.GetCaretPoint(
                pos, raw, caretIndex, _textFontSize, _textFontFamily,
                _textBold, _textItalic, _textMaxWidth, _textAlign);
            float lineH = TextAnnotationPainter.GetFont(_textFontFamily, _textFontSize, _textBold, _textItalic).GetHeight(g);
            float blinkAlpha = (float)(Math.Sin(Environment.TickCount64 / 400.0 * Math.PI) * 0.5 + 0.5);
            int alpha = (int)(blinkAlpha * 220);
            using var caretPen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 1.6f);
            g.DrawLine(caretPen, caret.X, caret.Y + 1, caret.X, caret.Y + lineH - 1);
        }
    }

    private void PaintEditorTextSelection(Graphics g, Point pos, string text, int start, int length, RectangleF textRect)
    {
        if (length <= 0 || string.IsNullOrEmpty(text)) return;
        start = Math.Clamp(start, 0, text.Length);
        int end = Math.Clamp(start + length, 0, text.Length);
        if (end <= start) return;

        var a = TextAnnotationPainter.GetCaretPoint(pos, text, start, _textFontSize, _textFontFamily,
            _textBold, _textItalic, _textMaxWidth, _textAlign);
        var b = TextAnnotationPainter.GetCaretPoint(pos, text, end, _textFontSize, _textFontFamily,
            _textBold, _textItalic, _textMaxWidth, _textAlign);
        float lineH = Math.Max(14f,
            TextAnnotationPainter.GetFont(_textFontFamily, _textFontSize, _textBold, _textItalic).GetHeight(g));

        using var brush = new SolidBrush(Color.FromArgb(150, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B));
        if (Math.Abs(a.Y - b.Y) < 1.5f)
        {
            float x0 = Math.Min(a.X, b.X);
            float x1 = Math.Max(a.X, b.X);
            g.FillRectangle(brush, x0 - 1, a.Y, Math.Max(3f, x1 - x0 + 2), lineH);
        }
        else
        {
            float topY = Math.Min(a.Y, b.Y);
            float botY = Math.Max(a.Y, b.Y);
            PointF topPt = a.Y <= b.Y ? a : b;
            PointF botPt = a.Y <= b.Y ? b : a;
            float contentLeft = textRect.X + 2;
            float contentRight = textRect.Right - 2;
            float contentW = Math.Max(4f, contentRight - contentLeft);
            g.FillRectangle(brush, topPt.X - 1, topY, Math.Max(3f, contentRight - topPt.X + 1), lineH);
            for (float y = topY + lineH; y < botY - 0.5f; y += lineH)
                g.FillRectangle(brush, contentLeft, y, contentW, lineH);
            g.FillRectangle(brush, contentLeft, botY, Math.Max(3f, botPt.X - contentLeft + 2), lineH);
        }
    }

    private static void DrawInlineTextHandles(Graphics g, RectangleF textRect)
    {
        PointF[] pts =
        {
            new(textRect.X, textRect.Y),
            new(textRect.Right, textRect.Y),
            new(textRect.X, textRect.Bottom),
            new(textRect.Right, textRect.Bottom),
        };
        foreach (var p in pts)
        {
            var h = new RectangleF(p.X - 4, p.Y - 4, 8, 8);
            using var fill = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            using var border = new Pen(Color.FromArgb(200, 0, 200, 255), 1f);
            g.FillRectangle(fill, h);
            g.DrawRectangle(border, h.X, h.Y, h.Width, h.Height);
        }
    }

    /// <summary>Subtle border around the image so very pale captures still have edges.</summary>
    private void RenderCheckerboardFrame(Graphics g)
    {
        if (_baseBitmap is null || !ShowCaptureFrame) return;
        var rect = ImageToScreenRect(new RectangleF(0, 0, _baseBitmap.Width, _baseBitmap.Height));
        using var shadow = new Pen(Color.FromArgb(110, 0, 0, 0), 3f);
        using var pen = new Pen(Color.FromArgb(115, 0, 255, 255), 1f);
        g.DrawRectangle(shadow, rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2);
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private RectangleF ImageToScreenRect(RectangleF r) =>
        new(_pan.X + (float)(r.X * _zoom),
            _pan.Y + (float)(r.Y * _zoom),
            (float)(r.Width * _zoom),
            (float)(r.Height * _zoom));

    private Point ScreenToImage(Point p)
    {
        if (_zoom <= 0) return Point.Empty;
        var x = (p.X - _pan.X) / _zoom;
        var y = (p.Y - _pan.Y) / _zoom;
        return new Point((int)Math.Round(x), (int)Math.Round(y));
    }

    /// <summary>Public wrapper around the screen→image transform for hosting forms.</summary>
    public Point PointFromScreenToImage(Point client) => ScreenToImage(client);

    private PointF ScreenToImageF(PointF p)
    {
        if (_zoom <= 0) return PointF.Empty;
        return new PointF(
            (float)((p.X - _pan.X) / _zoom),
            (float)((p.Y - _pan.Y) / _zoom));
    }

    private static Rectangle GetAnnotationBounds(Annotation a)
    {
        return a switch
        {
            BlurRect br => br.Rect,
            HighlightAnnotation hl => hl.Rect,
            RectShapeAnnotation rs => rs.Rect,
            CircleShapeAnnotation cs => cs.Rect,
            EraserFill ef => ef.Rect,
            ArrowAnnotation ar => RectangleFromPoints(ar.From, ar.To),
            LineAnnotation ln => RectangleFromPoints(ln.From, ln.To),
            RulerAnnotation ru => RectangleFromPoints(ru.From, ru.To),
            CurvedArrowAnnotation ca => ca.Points.Count > 0 ? BoundingBox(ca.Points) : Rectangle.Empty,
            DrawStroke ds => ds.Points.Count > 0 ? BoundingBox(ds.Points) : Rectangle.Empty,
            TextAnnotation ta => Rectangle.Round(TextAnnotationPainter.Measure(ta)),
            StepNumberAnnotation sn => new Rectangle(sn.Pos.X - 20, sn.Pos.Y - 20, 40, 40),
            EmojiAnnotation em => new Rectangle(em.Pos.X, em.Pos.Y, (int)(em.Size * 1.4f) + 4, (int)(em.Size * 1.4f) + 4),
            MagnifierAnnotation mg => new Rectangle(mg.Pos.X - 30, mg.Pos.Y - 30, 60, 60),
            _ => Rectangle.Empty,
        };
    }

    private static Rectangle RectangleFromPoints(Point a, Point b)
    {
        int minX = Math.Min(a.X, b.X);
        int minY = Math.Min(a.Y, b.Y);
        int maxX = Math.Max(a.X, b.X);
        int maxY = Math.Max(a.Y, b.Y);
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    private static Rectangle BoundingBox(IReadOnlyList<Point> pts)
    {
        int minX = pts[0].X, minY = pts[0].Y, maxX = pts[0].X, maxY = pts[0].Y;
        for (int i = 1; i < pts.Count; i++)
        {
            minX = Math.Min(minX, pts[i].X);
            minY = Math.Min(minY, pts[i].Y);
            maxX = Math.Max(maxX, pts[i].X);
            maxY = Math.Max(maxY, pts[i].Y);
        }
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    private void RenderToolBanner(Graphics g)
    {
        if (_bannerOpacity <= 0f || string.IsNullOrEmpty(_bannerText)) return;

        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float x = 18;
            float y = 18;

            using var font = UiChrome.ChromeFont(11f, FontStyle.Bold);
            var size = g.MeasureString(_bannerText, font);
            
            int paddingH = 16;
            int paddingV = 10;
            
            float width = size.Width + paddingH * 2;
            float height = size.Height + paddingV * 2;
            
            int alphaBg = (int)(200 * _bannerOpacity);
            int alphaBorder = (int)(150 * _bannerOpacity);
            int alphaGlow = (int)(40 * _bannerOpacity);
            int alphaText = (int)(255 * _bannerOpacity);

            var bgCol = EditorColors.BgCard;
            var accentCol = EditorColors.Accent;

            using var path = EditorPaint.RoundedRect(new Rectangle((int)x, (int)y, (int)width, (int)height), 8);
            using var bgBrush = new SolidBrush(Color.FromArgb(alphaBg, bgCol.R, bgCol.G, bgCol.B));
            using var glowPen = new Pen(Color.FromArgb(alphaGlow, accentCol.R, accentCol.G, accentCol.B), 3f);
            using var borderPen = new Pen(Color.FromArgb(alphaBorder, accentCol.R, accentCol.G, accentCol.B), 1.2f);
            using var textBrush = new SolidBrush(Color.FromArgb(alphaText, accentCol.R, accentCol.G, accentCol.B));

            g.FillPath(bgBrush, path);
            g.DrawPath(glowPen, path);
            g.DrawPath(borderPen, path);

            var textRect = new RectangleF(x + paddingH, y + paddingV, size.Width, size.Height);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(_bannerText, font, textBrush, textRect, sf);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static GraphicsPath RoundedRectPath(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Simple drop/image glyph — single rounded frame + down arrow (no stacked tabs).</summary>
    private void DrawWelcomeIcon(Graphics g, float cx, float cy, float size, Color color)
    {
        float strokeW = Math.Max(1.75f, size * 0.045f);
        using var pen = new Pen(color, strokeW)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        float frameW = size * 0.62f;
        float frameH = size * 0.50f;
        float frameX = cx - frameW / 2f;
        float frameY = cy - frameH / 2f - size * 0.04f;
        float corner = size * 0.08f;

        using (var frame = RoundedRectPath(frameX, frameY, frameW, frameH, corner))
            g.DrawPath(pen, frame);

        // Down arrow into the frame (drop affordance)
        float arrowCx = cx;
        float arrowTop = frameY + frameH * 0.22f;
        float arrowBot = frameY + frameH * 0.72f;
        float wing = size * 0.11f;
        g.DrawLine(pen, arrowCx, arrowTop, arrowCx, arrowBot);
        g.DrawLine(pen, arrowCx - wing, arrowBot - wing, arrowCx, arrowBot);
        g.DrawLine(pen, arrowCx + wing, arrowBot - wing, arrowCx, arrowBot);
    }

    private void DrawWelcomeChip(
        Graphics g,
        RectangleF rect,
        string label,
        Font font,
        bool enabled,
        bool hovered,
        bool pressed,
        Color textColor,
        Color mutedColor,
        Color accent,
        Color chipBg,
        Color chipBorder)
    {
        Color bg = !enabled
            ? Color.FromArgb(40, chipBg)
            : pressed
                ? Color.FromArgb(60, accent)
                : hovered
                    ? Color.FromArgb(45, accent)
                    : chipBg;
        Color border = !enabled
            ? Color.FromArgb(60, chipBorder)
            : hovered || pressed
                ? Color.FromArgb(200, accent)
                : chipBorder;
        Color fg = enabled ? textColor : mutedColor;

        using var path = RoundedRectPath(rect.X, rect.Y, rect.Width, rect.Height, 8f);
        using var bgBrush = new SolidBrush(bg);
        using var borderPen = new Pen(border, 1.25f);
        g.FillPath(bgBrush, path);
        g.DrawPath(borderPen, path);

        // TextRenderer is sharper than GDI+ DrawString for UI labels (ClearType on Windows).
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, label, font, Rectangle.Round(rect), fg, flags);
    }

    private void RenderWelcomeText(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.TextContrast = 12;

        // Slightly larger, high-contrast title; body uses ClearType via TextRenderer.
        using var titleFont = UiChrome.ChromeFont(15.5f, FontStyle.Bold);
        using var subFont = UiChrome.ChromeFont(9.75f, FontStyle.Regular);
        using var chipFont = UiChrome.ChromeFont(9.5f, FontStyle.Bold);

        var titleText = LocalizationService.Translate("Drop an image or project");
        var hintText = LocalizationService.Translate("Double-click · drag and drop");
        var openLabel = LocalizationService.Translate("Open");
        var pasteLabel = LocalizationService.Translate("Paste");
        var captureLabel = LocalizationService.Translate("Capture");

        var titleSize = TextRenderer.MeasureText(g, titleText, titleFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var hintSize = TextRenderer.MeasureText(g, hintText, subFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        float paddingH = 28;
        float paddingV = 22;
        float spacing = 10;
        float iconSize = 52;
        float chipH = 30;
        float chipGap = 8;
        float chipMinW = 84;

        float openW = Math.Max(chipMinW, TextRenderer.MeasureText(g, openLabel, chipFont).Width + 28);
        float pasteW = Math.Max(chipMinW, TextRenderer.MeasureText(g, pasteLabel, chipFont).Width + 28);
        float captureW = Math.Max(chipMinW, TextRenderer.MeasureText(g, captureLabel, chipFont).Width + 28);
        float chipsRowW = openW + pasteW + captureW + chipGap * 2;

        float contentW = Math.Max(titleSize.Width, Math.Max(hintSize.Width, chipsRowW));
        float width = Math.Max(contentW + paddingH * 2, 360);
        float height = paddingV * 2 + iconSize + spacing + titleSize.Height + spacing
            + hintSize.Height + spacing + 4 + chipH;

        float x = (ClientSize.Width - width) / 2f;
        float y = (ClientSize.Height - height) / 2f;
        _welcomeCardRect = new RectangleF(x, y, width, height);

        Color titleColor = EditorColors.IsDark
            ? Color.FromArgb(235, 240, 248)
            : Color.FromArgb(32, 48, 72);
        Color subColor = EditorColors.IsDark ? EditorColors.TextMuted : Color.FromArgb(100, 120, 150);
        Color iconColor = EditorColors.IsDark ? EditorColors.TextMuted : Color.FromArgb(70, 110, 175);
        Color accent = EditorColors.Accent;
        Color chipBg = EditorColors.IsDark
            ? Color.FromArgb(255, Math.Min(255, EditorColors.BgCard.R + 12), Math.Min(255, EditorColors.BgCard.G + 14), Math.Min(255, EditorColors.BgCard.B + 18))
            : Color.FromArgb(245, 248, 252);
        Color chipBorder = EditorColors.BorderSubtle;
        bool emphasize = _welcomeDragOver || _welcomeHoverCard;
        Color cardBorder = _welcomeDragOver
            ? Color.FromArgb(220, accent)
            : emphasize
                ? Color.FromArgb(160, accent)
                : EditorColors.BorderSubtle;
        float borderW = _welcomeDragOver ? 2f : 1.5f;

        var rect = new Rectangle((int)x, (int)y, (int)width, (int)height);
        using var path = EditorPaint.RoundedRect(rect, 14);
        using var bgBrush = new SolidBrush(Color.FromArgb(_welcomeDragOver ? 235 : 220, EditorColors.BgCard));
        using var borderPen = new Pen(cardBorder, borderW);
        g.FillPath(bgBrush, path);
        g.DrawPath(borderPen, path);

        if (_welcomeDragOver)
        {
            using var glow = new Pen(Color.FromArgb(50, accent), 6f);
            g.DrawPath(glow, path);
        }

        float curY = y + paddingV;
        float iconCx = x + width / 2f;
        float iconCy = curY + iconSize / 2f;
        DrawWelcomeIcon(g, iconCx, iconCy, iconSize, _welcomeDragOver ? accent : iconColor);
        curY += iconSize + spacing;

        var titleFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        var titleRect = new Rectangle((int)x, (int)curY, (int)width, titleSize.Height + 2);
        TextRenderer.DrawText(g, titleText, titleFont, titleRect, titleColor, titleFlags);
        curY += titleSize.Height + spacing;

        // Plain hint (no key-pill — Paste chip already covers Ctrl+V).
        var hintRect = new Rectangle((int)x, (int)curY, (int)width, hintSize.Height + 2);
        TextRenderer.DrawText(g, hintText, subFont, hintRect, subColor, titleFlags);
        curY += hintSize.Height + spacing + 4;

        // Action chips
        float chipsStartX = x + (width - chipsRowW) / 2f;
        _welcomeChipRects[0] = new RectangleF(chipsStartX, curY, openW, chipH);
        _welcomeChipRects[1] = new RectangleF(chipsStartX + openW + chipGap, curY, pasteW, chipH);
        _welcomeChipRects[2] = new RectangleF(chipsStartX + openW + chipGap + pasteW + chipGap, curY, captureW, chipH);

        bool pasteEnabled = IsWelcomeChipEnabled(1);
        DrawWelcomeChip(g, _welcomeChipRects[0], openLabel, chipFont, true,
            _welcomeHoverChip == 0, _welcomePressedChip == 0, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[1], pasteLabel, chipFont, pasteEnabled,
            _welcomeHoverChip == 1, _welcomePressedChip == 1, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[2], captureLabel, chipFont, true,
            _welcomeHoverChip == 2, _welcomePressedChip == 2, titleColor, subColor, accent, chipBg, chipBorder);
    }

    private void RenderGuides(Graphics g)
    {
        var settings = Services.SettingsService.LoadStatic();
        if (settings != null && !settings.EditorShowRulers) return;

        using var normalPen = new Pen(Color.FromArgb(160, 0, 255, 255), 1f) { DashPattern = new float[] { 4, 4 } };
        using var hoverPen = new Pen(Color.FromArgb(255, 0, 255, 255), 1.5f);
        using var shadowPen = new Pen(Color.FromArgb(80, 0, 0, 0), 1f);

        // Draw horizontal guides
        for (int i = 0; i < _horizontalGuides.Count; i++)
        {
            float y = (float)(_horizontalGuides[i] * _zoom + _pan.Y);
            if (y >= 0 && y <= ClientSize.Height)
            {
                bool isHovered = (i == _hoveredHorizontalGuideIndex || i == _activeDraggedHorizontalGuideIndex);
                var pen = isHovered ? hoverPen : normalPen;
                g.DrawLine(shadowPen, 0, y + 1, ClientSize.Width, y + 1);
                g.DrawLine(pen, 0, y, ClientSize.Width, y);
            }
        }

        // Draw vertical guides
        for (int i = 0; i < _verticalGuides.Count; i++)
        {
            float x = (float)(_verticalGuides[i] * _zoom + _pan.X);
            if (x >= 0 && x <= ClientSize.Width)
            {
                bool isHovered = (i == _hoveredVerticalGuideIndex || i == _activeDraggedVerticalGuideIndex);
                var pen = isHovered ? hoverPen : normalPen;
                g.DrawLine(shadowPen, x + 1, 0, x + 1, ClientSize.Height);
                g.DrawLine(pen, x, 0, x, ClientSize.Height);
            }
        }

        // Draw temporary horizontal guide currently being dragged from ruler
        if (DraggingTempHorizontalGuide.HasValue)
        {
            float y = (float)(DraggingTempHorizontalGuide.Value * _zoom + _pan.Y);
            g.DrawLine(shadowPen, 0, y + 1, ClientSize.Width, y + 1);
            g.DrawLine(hoverPen, 0, y, ClientSize.Width, y);
        }

        // Draw temporary vertical guide currently being dragged from ruler
        if (DraggingTempVerticalGuide.HasValue)
        {
            float x = (float)(DraggingTempVerticalGuide.Value * _zoom + _pan.X);
            g.DrawLine(shadowPen, x + 1, 0, x + 1, ClientSize.Height);
            g.DrawLine(hoverPen, x, 0, x, ClientSize.Height);
        }
    }
}
