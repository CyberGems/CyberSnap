using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI.Editor;

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
        if (_preSpaceTool == null && IsDrawingOrMoveTool(_activeTool) && _moveHoverIndex >= 0 && _moveHoverIndex < _annotations.Count
            && _moveHoverIndex != _selectedAnnotationIndex && !_multiSelectedIndices.Contains(_moveHoverIndex))
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
                if (idx >= 0 && idx < _annotations.Count)
                {
                    var ann = _annotations[idx];
                    var bounds = GetAnnotationBounds(ann);
                    DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(ann));
                }
            }
        }
        // Single selection highlight (only when NOT part of an active multi-selection)
        else if (_preSpaceTool == null && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _annotations.Count)
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

    private static void RenderTextAnnotation(Graphics g, TextAnnotation ta)
    {
        var style = (ta.Bold ? FontStyle.Bold : 0) | (ta.Italic ? FontStyle.Italic : 0);
        using var font = new Font(ta.FontFamily, ta.FontSize, style, GraphicsUnit.Pixel);
        var pos = new Point(ta.Pos.X, ta.Pos.Y);
        var color = ta.Color;

        if (ta.Background)
        {
            var size = g.MeasureString(ta.Text, font);
            var bgRect = new RectangleF(
                pos.X - 8f,
                pos.Y - 6f,
                size.Width + 16,
                size.Height + 12);
            using var bgPath = SketchRenderer.RoundedRect(bgRect, 8f);
            if (ta.Shadow)
            {
                using var shadowPath = SketchRenderer.RoundedRect(new RectangleF(bgRect.X + 2, bgRect.Y + 2, bgRect.Width, bgRect.Height), 8f);
                using var shadowBrush = new SolidBrush(Color.FromArgb(55, 0, 0, 0));
                g.FillPath(shadowBrush, shadowPath);
            }
            using var fillBrush = new SolidBrush(color);
            g.FillPath(fillBrush, bgPath);
            if (ta.Stroke)
            {
                using var strokePen = new Pen(Color.FromArgb(60, 0, 0, 0), 1.25f);
                g.DrawPath(strokePen, bgPath);
            }
            color = Color.White;
        }

        if (ta.Shadow)
        {
            using var sb1 = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
            using var sb2 = new SolidBrush(Color.FromArgb(25, 0, 0, 0));
            g.DrawString(ta.Text, font, sb1, pos.X + 2, pos.Y + 2);
            g.DrawString(ta.Text, font, sb2, pos.X + 3, pos.Y + 3);
        }

        if (ta.Stroke)
        {
            using var strokeBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                    if (ox != 0 || oy != 0)
                        g.DrawString(ta.Text, font, strokeBrush, pos.X + ox, pos.Y + oy);
        }

        using var brush = new SolidBrush(color);
        g.DrawString(ta.Text, font, brush, pos.X, pos.Y);
    }

    /// <summary>Measures the visual bounding rect of a text annotation (including padding).
    /// Matches the logic used in the capture overlay.</summary>
    private static RectangleF MeasureInlineTextRect(Point pos, string text, float fontSize, string fontFamily, bool bold, bool italic, bool background = false)
    {
        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;
        // Pixel units to match how the text is actually drawn (RenderTextAnnotation /
        // RenderInlineTextPreview), so the selection box, background and toolbar anchor
        // line up with the glyphs instead of being measured ~33% too large.
        using var font = new Font(fontFamily, fontSize, style, GraphicsUnit.Pixel);
        string display = text.Length > 0 ? text : "Type here...";
        SizeF size;
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            size = g.MeasureString(display, font);
        }
        int padX = background ? 16 : 8;
        int padY = background ? 12 : 8;
        return new RectangleF(
            pos.X - (padX / 2f),
            pos.Y - (padY / 2f),
            size.Width + padX,
            size.Height + padY);
    }

    private static float MeasureInlineTextPrefixWidth(string text, int length, Font font)
    {
        if (length <= 0 || string.IsNullOrEmpty(text)) return 0f;
        length = Math.Min(length, text.Length);
        var size = TextRenderer.MeasureText(text[..length], font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return size.Width;
    }

    /// <summary>Renders the live inline text preview inside the zoom/pan transform.</summary>
    private void RenderInlineTextPreview(Graphics g)
    {
        if (_inlineTextBox is null) return;

        var style = (_textBold ? FontStyle.Bold : 0) | (_textItalic ? FontStyle.Italic : 0);
        // Pixel units (not the default Point) so the live preview matches the committed
        // annotation, which renders with GraphicsUnit.Pixel. Otherwise the text appears
        // ~33% larger while typing and visibly shrinks the moment it is confirmed.
        using var font = new Font(_textFontFamily, _textFontSize, style, GraphicsUnit.Pixel);
        var text = _inlineTextBox.Text.Length > 0 ? _inlineTextBox.Text : "Type here...";
        var color = ToolColor;
        var pos = new Point(_inlineTextOrigin.X, _inlineTextOrigin.Y);

        // Background (matches committed annotation style)
        if (_textBackground)
        {
            var bgRect = MeasureInlineTextRect(_inlineTextOrigin, _inlineTextBox.Text, _textFontSize, _textFontFamily, _textBold, _textItalic, true);
            using var bgPath = SketchRenderer.RoundedRect(bgRect, 8f);
            if (_textShadow)
            {
                using var shadowPath = SketchRenderer.RoundedRect(new RectangleF(bgRect.X + 2, bgRect.Y + 2, bgRect.Width, bgRect.Height), 8f);
                using var shadowBrush = new SolidBrush(Color.FromArgb(55, 0, 0, 0));
                g.FillPath(shadowBrush, shadowPath);
            }
            using var fillBrush = new SolidBrush(color);
            g.FillPath(fillBrush, bgPath);
            if (_textStroke)
            {
                using var strokePen = new Pen(Color.FromArgb(60, 0, 0, 0), 1.25f);
                g.DrawPath(strokePen, bgPath);
            }
            color = Color.White;
        }

        // Shadow
        if (_textShadow)
        {
            using var sb1 = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
            using var sb2 = new SolidBrush(Color.FromArgb(25, 0, 0, 0));
            g.DrawString(text, font, sb1, pos.X + 2, pos.Y + 2);
            g.DrawString(text, font, sb2, pos.X + 3, pos.Y + 3);
        }

        // Stroke
        if (_textStroke)
        {
            using var strokeBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                    if (ox != 0 || oy != 0)
                        g.DrawString(text, font, strokeBrush, pos.X + ox, pos.Y + oy);
        }

        // Main text / placeholder
        if (_inlineTextBox.Text.Length > 0)
        {
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, pos.X, pos.Y);
        }
        else
        {
            using var placeholderBrush = new SolidBrush(Color.FromArgb(120, 180, 180, 180));
            g.DrawString(text, font, placeholderBrush, pos.X, pos.Y);
        }

        // Dashed selection border
        var textRect = MeasureInlineTextRect(_inlineTextOrigin, _inlineTextBox.Text, _textFontSize, _textFontFamily, _textBold, _textItalic, _textBackground);
        using (var dashPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash })
        {
            g.DrawRectangle(dashPen, textRect.X, textRect.Y, textRect.Width, textRect.Height);
        }

        // Blinking caret
        float blinkAlpha = (float)(Math.Sin(Environment.TickCount64 / 400.0 * Math.PI) * 0.5 + 0.5);
        int alpha = (int)(blinkAlpha * 220);
        using (var caretPen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 1.6f))
        {
            float cursorX;
            if (_inlineTextBox.Text.Length > 0)
            {
                int caretIndex = _inlineTextBox.SelectionStart;
                cursorX = pos.X + MeasureInlineTextPrefixWidth(_inlineTextBox.Text, caretIndex, font) - 1;
            }
            else
            {
                cursorX = pos.X;
            }
            g.DrawLine(caretPen, cursorX, textRect.Y + 3, cursorX, textRect.Bottom - 3);
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
            TextAnnotation ta => Rectangle.Round(MeasureInlineTextRect(ta.Pos, ta.Text, ta.FontSize, ta.FontFamily, ta.Bold, ta.Italic, ta.Background)),
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

            using var font = new Font("Segoe UI Variable Display", 11f, FontStyle.Bold, GraphicsUnit.Point);
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

    private void DrawWelcomeIcon(Graphics g, float cx, float cy, float size, Color color)
    {
        float strokeW = Math.Max(2f, size * 0.03f);
        using var pen = new Pen(color, strokeW) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var bgBrush = new SolidBrush(Color.FromArgb(220, EditorColors.BgCard));

        // --- Layout: total icon spans from cy-size/2 to cy+size/2 ---
        float top = cy - size * 0.50f;

        // --- Pre-compute all layer positions ---
        float tabGap = size * 0.05f;       // vertical gap between tab tops
        float tabThick = size * 0.06f;      // how tall each tab strip is
        float cornerR = size * 0.055f;

        float tab2Y = top;
        float tab1Y = top + tabGap;
        float cardY = top + tabGap * 2;
        float cardW = size * 0.72f;
        float cardH = size * 0.55f;
        float cardX = cx - cardW / 2;

        // --- Back tab (narrowest, highest) — clip to only show above middle tab ---
        float tab2W = size * 0.50f;
        float tab2X = cx - tab2W / 2;
        using var tab2Path = RoundedRectPath(tab2X, tab2Y, tab2W, tabThick + cornerR, cornerR);
        var savedClip = g.Clip.Clone();
        g.SetClip(new RectangleF(tab2X - strokeW, tab2Y - strokeW, tab2W + strokeW * 2, (tab1Y - tab2Y) + strokeW * 2), CombineMode.Intersect);
        g.FillPath(bgBrush, tab2Path);
        g.DrawPath(pen, tab2Path);
        g.Clip = savedClip;

        // --- Middle tab (wider, slightly lower) — clip to only show above front card ---
        float tab1W = size * 0.60f;
        float tab1X = cx - tab1W / 2;
        using var tab1Path = RoundedRectPath(tab1X, tab1Y, tab1W, tabThick + cornerR, cornerR);
        var savedClip2 = g.Clip.Clone();
        g.SetClip(new RectangleF(tab1X - strokeW, tab1Y - strokeW, tab1W + strokeW * 2, (cardY - tab1Y) + strokeW * 2), CombineMode.Intersect);
        g.FillPath(bgBrush, tab1Path);
        g.DrawPath(pen, tab1Path);
        g.Clip = savedClip2;

        // --- Front card ---
        using var cardPath = RoundedRectPath(cardX, cardY, cardW, cardH, cornerR);
        g.FillPath(bgBrush, cardPath);
        g.DrawPath(pen, cardPath);

        // --- Large centered download arrow inside card ---
        float arrowCx = cx;
        float arrowCy = cardY + cardH * 0.48f;
        float shaftLen = cardH * 0.55f;
        float arrowTop = arrowCy - shaftLen * 0.35f;
        float arrowBot = arrowCy + shaftLen * 0.65f;
        float wingSpan = size * 0.14f;

        // Vertical shaft
        g.DrawLine(pen, arrowCx, arrowTop, arrowCx, arrowBot);

        // Chevron arrowhead
        g.DrawLine(pen, arrowCx - wingSpan, arrowBot - wingSpan, arrowCx, arrowBot);
        g.DrawLine(pen, arrowCx + wingSpan, arrowBot - wingSpan, arrowCx, arrowBot);
    }

    private void DrawCursorIcon(Graphics g, float x, float y, float size, Color color)
    {
        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, 1.0f) { LineJoin = LineJoin.Round };
        
        // Arrow cursor with visible handle/tail
        var path = new GraphicsPath();
        path.AddLine(x, y, x, y + size);                                         // left edge down
        path.AddLine(x, y + size, x + size * 0.24f, y + size * 0.74f);           // bottom-left diagonal
        path.AddLine(x + size * 0.24f, y + size * 0.74f, x + size * 0.46f, y + size * 1.10f); // handle left
        path.AddLine(x + size * 0.46f, y + size * 1.10f, x + size * 0.60f, y + size * 1.00f); // handle bottom
        path.AddLine(x + size * 0.60f, y + size * 1.00f, x + size * 0.38f, y + size * 0.64f); // handle right
        path.AddLine(x + size * 0.38f, y + size * 0.64f, x + size * 0.62f, y + size * 0.62f); // right wing
        path.CloseAllFigures();
        
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    private void RenderWelcomeText(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var titleFont = new Font("Segoe UI Variable Display", 15f, FontStyle.Bold, GraphicsUnit.Point);
        using var mediumFont = new Font("Segoe UI Variable Text", 11.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var smallFont = new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

        var titleText = LocalizationService.Translate("Annotations Editor");
        var mediumText = LocalizationService.Translate("Drag & drop a file");
        var smallTextTemplate = LocalizationService.Translate("Paste an image (Ctrl+V) or double-click {0} to browse");

        // Measure strings
        var titleSize = g.MeasureString(titleText, titleFont);
        var mediumSize = g.MeasureString(mediumText, mediumFont);

        // Measure small text (with spaces in place of {0})
        float cursorPlaceholderWidth = 16;
        string smallTextForMeasurement = string.Format(smallTextTemplate, "   "); // 3 spaces
        var smallSize = g.MeasureString(smallTextForMeasurement, smallFont);

        float paddingH = 32;
        float paddingV = 20;
        float spacing = 8;
        float iconSize = 72;

        float width = Math.Max(titleSize.Width, Math.Max(mediumSize.Width, smallSize.Width)) + paddingH * 2;
        float height = iconSize + titleSize.Height + mediumSize.Height + smallSize.Height + paddingV * 2 + spacing * 2 + 2;

        // Make sure the width is at least 340 to look premium
        width = Math.Max(width, 340);

        float x = (ClientSize.Width - width) / 2;
        float y = (ClientSize.Height - height) / 2;

        var rect = new Rectangle((int)x, (int)y, (int)width, (int)height);
        using var path = EditorPaint.RoundedRect(rect, 12);
        using var bgBrush = new SolidBrush(Color.FromArgb(220, EditorColors.BgCard));
        using var borderPen = new Pen(EditorColors.BorderSubtle, 1.5f);

        g.FillPath(bgBrush, path);
        g.DrawPath(borderPen, path);

        // Layout order: Title → Icon → Subheader → Small text
        float curY = y + paddingV;

        // 1. Draw Title (header first)
        Color titleColor = EditorColors.IsDark ? EditorColors.TextSecondary : Color.FromArgb(45, 65, 95);
        Color mediumColor = EditorColors.IsDark ? EditorColors.TextSecondary : Color.FromArgb(50, 75, 110);
        Color iconColor = EditorColors.IsDark ? EditorColors.TextMuted : Color.FromArgb(70, 110, 175);
        Color smallColor = EditorColors.IsDark ? EditorColors.TextMuted : Color.FromArgb(100, 120, 150);
        Color cursorColor = EditorColors.IsDark ? EditorColors.TextMuted : Color.FromArgb(100, 120, 150);

        using var titleBrush = new SolidBrush(titleColor);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var titleRect = new RectangleF(x, curY, width, titleSize.Height);
        g.DrawString(titleText, titleFont, titleBrush, titleRect, sf);
        curY += titleSize.Height + spacing;

        // 2. Draw Icon
        float iconCx = x + width / 2;
        float iconCy = curY + iconSize / 2 + iconSize * 0.09f;  // offset down to visually center (tabs shorter than arrow)
        DrawWelcomeIcon(g, iconCx, iconCy, iconSize, iconColor);
        curY += iconSize + 2;  // tight gap below icon

        // 3. Draw Subheader
        using var mediumBrush = new SolidBrush(mediumColor);
        var mediumRect = new RectangleF(x, curY, width, mediumSize.Height);
        g.DrawString(mediumText, mediumFont, mediumBrush, mediumRect, sf);
        curY += mediumSize.Height + spacing;

        // 4. Draw Small Text (with inline cursor)
        using var smallBrush = new SolidBrush(smallColor);
        var smallRect = new RectangleF(x, curY, width, smallSize.Height);

        string part1 = "";
        string part2 = "";
        if (smallTextTemplate.Contains("{0}"))
        {
            var idx = smallTextTemplate.IndexOf("{0}");
            part1 = smallTextTemplate.Substring(0, idx);
            part2 = smallTextTemplate.Substring(idx + 3);
        }
        else
        {
            part1 = smallTextTemplate;
        }

        if (string.IsNullOrEmpty(part2))
        {
            // Draw normally if no {0} template matches
            g.DrawString(smallTextTemplate, smallFont, smallBrush, smallRect, sf);
        }
        else
        {
            // Draw part1, cursor, and part2 inline.
            var sizePart1 = g.MeasureString(part1, smallFont);
            var sizePart2 = g.MeasureString(part2, smallFont);
            float totalTextW = sizePart1.Width + cursorPlaceholderWidth + sizePart2.Width;
            
            float startTextX = x + (width - totalTextW) / 2;
            float textY = smallRect.Y + (smallRect.Height - Math.Max(sizePart1.Height, sizePart2.Height)) / 2;

            g.DrawString(part1, smallFont, smallBrush, startTextX, textY);
            
            float cursorX = startTextX + sizePart1.Width + 2;
            float cursorY = textY + 2;
            DrawCursorIcon(g, cursorX, cursorY, 12, cursorColor);

            g.DrawString(part2, smallFont, smallBrush, cursorX + cursorPlaceholderWidth, textY);
        }
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
