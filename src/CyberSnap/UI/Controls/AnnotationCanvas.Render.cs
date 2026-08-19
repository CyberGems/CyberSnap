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

    private TextureBrush? _checkerboardBrush;
    private bool _checkerboardBrushIsDark;

    private void PaintCheckerboardBackground(Graphics g, float x, float y, float width, float height)
    {
        bool isDark = EditorColors.IsDark;
        if (_checkerboardBrush == null || _checkerboardBrushIsDark != isDark)
        {
            _checkerboardBrush?.Dispose();

            var color1 = isDark ? Color.FromArgb(20, 22, 33) : Color.FromArgb(245, 246, 250);
            var color2 = isDark ? Color.FromArgb(28, 30, 43) : Color.FromArgb(233, 235, 243);

            int size = 16;
            using (var tempBmp = new Bitmap(size * 2, size * 2))
            {
                using (var tempG = Graphics.FromImage(tempBmp))
                {
                    tempG.Clear(color1);
                    using (var b = new SolidBrush(color2))
                    {
                        tempG.FillRectangle(b, 0, 0, size, size);
                        tempG.FillRectangle(b, size, size, size, size);
                    }
                }
                _checkerboardBrush = new TextureBrush(tempBmp);
            }
            _checkerboardBrushIsDark = isDark;
        }

        _checkerboardBrush.ResetTransform();
        _checkerboardBrush.ScaleTransform((float)_zoom, (float)_zoom);
        _checkerboardBrush.TranslateTransform(_pan.X / (float)_zoom, _pan.Y / (float)_zoom);
        g.FillRectangle(_checkerboardBrush, x, y, width, height);
    }

    /// <summary>Draws the base bitmap at the current zoom/pan, using the pre-scaled cache and visible-viewport clipping.</summary>
    private void DrawBaseImage(Graphics g)
    {
        int scaledW = Math.Max(1, (int)Math.Round(_baseBitmap.Width * _zoom));
        int scaledH = Math.Max(1, (int)Math.Round(_baseBitmap.Height * _zoom));

        int clientW = ClientSize.Width;
        int clientH = ClientSize.Height;
        if (clientW <= 0 || clientH <= 0) return;

        // Clip destination rectangle strictly to the client viewport to avoid huge off-screen coordinate calculations
        var destRect = Rectangle.Intersect(
            new Rectangle(0, 0, clientW, clientH),
            new Rectangle((int)_pan.X, (int)_pan.Y, scaledW, scaledH));

        if (destRect.Width <= 0 || destRect.Height <= 0)
            return;

        // Draw checkerboard background ONLY for the visible destination area
        PaintCheckerboardBackground(g, destRect.X, destRect.Y, destRect.Width, destRect.Height);

        // Compute visible source rectangle in base bitmap coordinates
        float srcX = (destRect.X - _pan.X) / (float)_zoom;
        float srcY = (destRect.Y - _pan.Y) / (float)_zoom;
        float srcW = destRect.Width / (float)_zoom;
        float srcH = destRect.Height / (float)_zoom;

        srcX = Math.Clamp(srcX, 0f, _baseBitmap.Width);
        srcY = Math.Clamp(srcY, 0f, _baseBitmap.Height);
        srcW = Math.Min(srcW, _baseBitmap.Width - srcX);
        srcH = Math.Min(srcH, _baseBitmap.Height - srcY);

        if (srcW <= 0 || srcH <= 0)
            return;

        // 1. Zoomed in (>= 1.0): nearest-neighbor visible-rect sampling (instant, 0 lag regardless of zoom level)
        if (_zoom >= 1.0)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_baseBitmap, destRect, srcX, srcY, srcW, srcH, GraphicsUnit.Pixel);
            return;
        }

        // 2. Zoomed out (< 1.0) during active zoom gesture: fast bilinear visible-rect sampling
        if (_zoomInteracting)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_baseBitmap, destRect, srcX, srcY, srcW, srcH, GraphicsUnit.Pixel);
            return;
        }

        // 3. Settled zoomed-out view (< 1.0): high-quality pre-scaled cache blitted 1:1
        EnsureScaledCache(scaledW, scaledH);
        if (_scaledCache is not null)
        {
            int cacheX = Math.Clamp(destRect.X - (int)_pan.X, 0, _scaledCache.Width);
            int cacheY = Math.Clamp(destRect.Y - (int)_pan.Y, 0, _scaledCache.Height);
            int cacheW = Math.Min(destRect.Width, _scaledCache.Width - cacheX);
            int cacheH = Math.Min(destRect.Height, _scaledCache.Height - cacheY);

            if (cacheW > 0 && cacheH > 0)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_scaledCache, new Rectangle(destRect.X, destRect.Y, cacheW, cacheH),
                    cacheX, cacheY, cacheW, cacheH, GraphicsUnit.Pixel);
            }
        }
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
        // Also skip the annotation currently being re-edited (live inline frame replaces it),
        // and while a new draw drag is in progress (hover must not compete with the preview).
        if (_preSpaceTool == null && IsDrawingOrMoveTool(_activeTool) && _moveHoverIndex >= 0 && _moveHoverIndex < _annotations.Count
            && _moveHoverIndex != _selectedAnnotationIndex
            && _moveHoverIndex != _renderSkipAnnotationIndex
            && !_multiSelectedIndices.Contains(_moveHoverIndex)
            && !(_isDragging && !IsManipulatingExistingAnnotation))
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

        float offset = 4f / z; // offset outside bounds

        var rect = new RectangleF(
            bounds.X - offset,
            bounds.Y - offset,
            bounds.Width + 2 * offset,
            bounds.Height + 2 * offset
        );

        // Theme-aware accent: cyan on dark, accent on light
        var accent = Theme.Accent;
        byte aR = accent.R, aG = accent.G, aB = accent.B;

        int accentAlpha = isSelected ? 255 : 120;
        int fillAlpha = isSelected ? 0 : 10;
        int dashAlpha = isSelected ? 200 : 75;

        var accentColor = Color.FromArgb(accentAlpha, aR, aG, aB);

        // Fill and dash
        if (fillAlpha > 0)
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(fillAlpha, aR, aG, aB));
            g.FillRectangle(fillBrush, rect);
        }

        using (var dashPen = new Pen(Color.FromArgb(dashAlpha, aR, aG, aB), 1.2f / z))
        {
            dashPen.DashStyle = DashStyle.Dash;
            dashPen.DashPattern = new[] { 4f, 3f };
            g.DrawRectangle(dashPen, rect.X, rect.Y, rect.Width, rect.Height);
        }

        if (moveOnly) return;

        // Figma-style handles: crisp white squares with 1.5px accent border and subtle drop shadow
        float screenW = bounds.Width * z;
        float screenH = bounds.Height * z;
        float hSize = ((screenW < 28 || screenH < 28) ? 6f : 8f) / z;
        float hHalf = hSize / 2f;

        using var shadowBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
        using var whiteBrush  = new SolidBrush(Color.White);
        using var handlePen   = new Pen(accentColor, 1.5f / z);

        void DrawHandle(float cx, float cy)
        {
            var hr = new RectangleF(cx - hHalf, cy - hHalf, hSize, hSize);
            g.FillRectangle(shadowBrush, hr.X + 0.8f / z, hr.Y + 1.2f / z, hr.Width, hr.Height);
            g.FillRectangle(whiteBrush, hr);
            g.DrawRectangle(handlePen, hr.X, hr.Y, hr.Width, hr.Height);
        }

        // 4 Corner handles
        DrawHandle(rect.Left,  rect.Top);
        DrawHandle(rect.Right, rect.Top);
        DrawHandle(rect.Left,  rect.Bottom);
        DrawHandle(rect.Right, rect.Bottom);

        // Mid-edge handles only on larger objects (>= 56px in screen space)
        bool showEdgeX = screenW >= 56;
        bool showEdgeY = screenH >= 56;
        float midX = rect.Left + rect.Width / 2f;
        float midY = rect.Top + rect.Height / 2f;

        if (showEdgeX)
        {
            DrawHandle(midX, rect.Top);
            DrawHandle(midX, rect.Bottom);
        }
        if (showEdgeY)
        {
            DrawHandle(rect.Left,  midY);
            DrawHandle(rect.Right, midY);
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

    /// <summary>Draws a modern, polished vector image/drop badge with dual-tone layers and smooth lighting.</summary>
    private void DrawWelcomeIcon(Graphics g, float cx, float cy, float size, Color accentColor, bool isDragOver, bool isHovered, bool isPressed)
    {
        // 1. Badge container circle with subtle glow and gradient tint
        float radius = size * 0.46f;
        var badgeRect = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);

        using (var shadowPath = new GraphicsPath())
        {
            shadowPath.AddEllipse(badgeRect.X, badgeRect.Y + 3, badgeRect.Width, badgeRect.Height);
            using var shadowBrush = new SolidBrush(Color.FromArgb(isHovered ? 60 : 45, 0, 0, 0));
            g.FillPath(shadowBrush, shadowPath);
        }

        // Ambient glow when hovered or dragging over
        if (isHovered || isDragOver)
        {
            using var glowPen = new Pen(Color.FromArgb(isDragOver ? 50 : 35, accentColor), 6f);
            g.DrawEllipse(glowPen, badgeRect.X - 1, badgeRect.Y - 1, badgeRect.Width + 2, badgeRect.Height + 2);
        }

        int bgAlpha = isPressed ? 75 : (isHovered ? 52 : (isDragOver ? 60 : (EditorColors.IsDark ? 32 : 24)));
        using (var bgBrush = new SolidBrush(Color.FromArgb(bgAlpha, accentColor)))
            g.FillEllipse(bgBrush, badgeRect);

        int borderAlpha = isPressed ? 255 : (isHovered || isDragOver ? 220 : (EditorColors.IsDark ? 110 : 90));
        using (var borderPen = new Pen(Color.FromArgb(borderAlpha, accentColor), (isHovered || isDragOver) ? 1.75f : 1.5f))
            g.DrawEllipse(borderPen, badgeRect);

        // 2. Picture Frame artwork
        float frameW = size * 0.44f;
        float frameH = size * 0.34f;
        float frameX = cx - frameW / 2f;
        float frameY = cy - frameH / 2f - size * 0.02f;

        using (var framePath = RoundedRectPath(frameX, frameY, frameW, frameH, 3.5f))
        {
            // Frame outline
            using var framePen = new Pen(Color.FromArgb(isDragOver ? 255 : 200, accentColor), 1.5f)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawPath(framePen, framePath);

            // Celestial sun / dot
            float sunR = size * 0.045f;
            float sunX = frameX + frameW * 0.72f;
            float sunY = frameY + frameH * 0.32f;
            using var sunBrush = new SolidBrush(Color.FromArgb(isDragOver ? 255 : 220, accentColor));
            g.FillEllipse(sunBrush, sunX - sunR, sunY - sunR, sunR * 2, sunR * 2);

            // Mountain peaks (clipped inside frame)
            var oldClip = g.Clip;
            g.SetClip(framePath, CombineMode.Intersect);
            try
            {
                // Back mountain peak (soft)
                PointF[] backPeak =
                {
                    new(frameX + frameW * 0.35f, frameY + frameH),
                    new(frameX + frameW * 0.65f, frameY + frameH * 0.42f),
                    new(frameX + frameW * 0.95f, frameY + frameH)
                };
                using (var backBrush = new SolidBrush(Color.FromArgb(70, accentColor)))
                    g.FillPolygon(backBrush, backPeak);
                using (var backPen = new Pen(Color.FromArgb(140, accentColor), 1.2f))
                    g.DrawLines(backPen, backPeak);

                // Front mountain peak (prominent)
                PointF[] frontPeak =
                {
                    new(frameX - 2, frameY + frameH + 2),
                    new(frameX + frameW * 0.38f, frameY + frameH * 0.32f),
                    new(frameX + frameW * 0.78f, frameY + frameH + 2)
                };
                using (var frontBrush = new SolidBrush(Color.FromArgb(110, accentColor)))
                    g.FillPolygon(frontBrush, frontPeak);
                using (var frontPen = new Pen(Color.FromArgb(230, accentColor), 1.4f))
                    g.DrawLines(frontPen, frontPeak);
            }
            finally
            {
                g.Clip = oldClip;
            }
        }

        // Small floating spark / indicator badge at bottom right of icon
        float sparkX = cx + radius * 0.62f;
        float sparkY = cy + radius * 0.60f;
        float sparkR = size * 0.12f;
        var sparkBadge = new RectangleF(sparkX - sparkR, sparkY - sparkR, sparkR * 2, sparkR * 2);
        using (var sbBg = new SolidBrush(EditorColors.BgCard))
            g.FillEllipse(sbBg, sparkBadge);
        using (var sbBorder = new Pen(Color.FromArgb(200, accentColor), 1.25f))
            g.DrawEllipse(sbBorder, sparkBadge);

        // Plus / drop arrow in mini badge
        using (var sbIconPen = new Pen(accentColor, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (isDragOver)
            {
                // Down arrow
                g.DrawLine(sbIconPen, sparkX, sparkY - 3f, sparkX, sparkY + 3f);
                g.DrawLine(sbIconPen, sparkX - 2.5f, sparkY + 0.5f, sparkX, sparkY + 3f);
                g.DrawLine(sbIconPen, sparkX + 2.5f, sparkY + 0.5f, sparkX, sparkY + 3f);
            }
            else
            {
                // Clean plus (+)
                g.DrawLine(sbIconPen, sparkX - 2.5f, sparkY, sparkX + 2.5f, sparkY);
                g.DrawLine(sbIconPen, sparkX, sparkY - 2.5f, sparkX, sparkY + 2.5f);
            }
        }
    }

    private void DrawWelcomeChip(
        Graphics g,
        RectangleF rect,
        string label,
        Font font,
        int chipType,
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
            ? Color.FromArgb(30, chipBg)
            : pressed
                ? Color.FromArgb(60, accent)
                : hovered
                    ? Color.FromArgb(40, accent)
                    : chipBg;
        Color border = !enabled
            ? Color.FromArgb(50, chipBorder)
            : hovered || pressed
                ? Color.FromArgb(220, accent)
                : chipBorder;
        Color fg = enabled ? (hovered ? Color.White : textColor) : mutedColor;
        Color iconCol = enabled ? (hovered ? accent : (EditorColors.IsDark ? Color.FromArgb(180, 200, 225) : Color.FromArgb(90, 115, 150))) : mutedColor;

        using var path = RoundedRectPath(rect.X, rect.Y, rect.Width, rect.Height, 8f);

        // Subtle drop shadow on hover
        if (hovered && enabled)
        {
            using var shPath = RoundedRectPath(rect.X, rect.Y + 1.5f, rect.Width, rect.Height, 8f);
            using var shBrush = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
            g.FillPath(shBrush, shPath);
        }

        using var bgBrush = new SolidBrush(bg);
        using var borderPen = new Pen(border, hovered ? 1.4f : 1.1f);
        g.FillPath(bgBrush, path);
        g.DrawPath(borderPen, path);

        // Vector icon + text
        float iconW = 14f;
        float gap = 6f;
        var textSz = TextRenderer.MeasureText(g, label, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        float totalContentW = iconW + gap + textSz.Width;
        float startX = rect.X + (rect.Width - totalContentW) / 2f;
        float iconCy = rect.Y + rect.Height / 2f;

        DrawChipIcon(g, chipType, startX + iconW / 2f, iconCy, iconCol);

        var textRect = new Rectangle((int)Math.Round(startX + iconW + gap), (int)Math.Round(rect.Y + (rect.Height - textSz.Height) / 2f), textSz.Width + 2, textSz.Height);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, label, font, textRect, fg, flags);
    }

    private static void DrawChipIcon(Graphics g, int chipType, float cx, float cy, Color color)
    {
        using var pen = new Pen(color, 1.25f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        switch (chipType)
        {
            case 0: // New Canvas (plus / canvas document)
            {
                float w = 11.5f, h = 12f;
                float x = cx - w / 2f, y = cy - h / 2f + 0.5f;
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w * 0.62f, y),
                    new PointF(x, y),
                    new PointF(x, y + h),
                    new PointF(x + w, y + h),
                    new PointF(x + w, y + w * 0.38f),
                    new PointF(x + w * 0.62f, y),
                    new PointF(x + w * 0.62f, y + w * 0.38f),
                    new PointF(x + w, y + w * 0.38f)
                });
                float px = x + w * 0.35f, py = y + h * 0.62f;
                g.DrawLine(pen, px - 2.2f, py, px + 2.2f, py);
                g.DrawLine(pen, px, py - 2.2f, px, py + 2.2f);
                break;
            }
            case 1: // Folder (Open)
            {
                float w = 13f, h = 9.5f;
                float x = cx - w / 2f, y = cy - h / 2f + 0.5f;
                g.DrawLines(pen, new[]
                {
                    new PointF(x, y + 2.5f),
                    new PointF(x, y + h),
                    new PointF(x + w, y + h),
                    new PointF(x + w, y + 2.5f),
                    new PointF(x + w * 0.55f, y + 2.5f),
                    new PointF(x + w * 0.42f, y),
                    new PointF(x, y),
                    new PointF(x, y + 2.5f),
                    new PointF(x + w, y + 2.5f)
                });
                break;
            }
            case 2: // Clipboard (Paste)
            {
                float w = 9.5f, h = 12f;
                float x = cx - w / 2f, y = cy - h / 2f + 0.5f;
                g.DrawLines(pen, new[]
                {
                    new PointF(x + 2f, y + 2f),
                    new PointF(x, y + 2f),
                    new PointF(x, y + h),
                    new PointF(x + w, y + h),
                    new PointF(x + w, y + 2f),
                    new PointF(x + w - 2f, y + 2f)
                });
                g.DrawRectangle(pen, x + 2.5f, y - 0.5f, w - 5f, 2.8f);
                break;
            }
            case 3: // Camera / Crop (Capture)
            {
                float w = 12.5f, h = 10f;
                float x = cx - w / 2f, y = cy - h / 2f + 1f;
                g.DrawLines(pen, new[]
                {
                    new PointF(x, y + 2.5f),
                    new PointF(x, y + h),
                    new PointF(x + w, y + h),
                    new PointF(x + w, y + 2.5f),
                    new PointF(x + w * 0.72f, y + 2.5f),
                    new PointF(x + w * 0.62f, y),
                    new PointF(x + w * 0.38f, y),
                    new PointF(x + w * 0.28f, y + 2.5f),
                    new PointF(x, y + 2.5f)
                });
                g.DrawEllipse(pen, cx - 2.2f, cy - 0.2f, 4.4f, 4.4f);
                break;
            }
        }
    }

    private void RenderWelcomeText(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.TextContrast = 12;

        // Slightly larger, high-contrast title; body uses ClearType via TextRenderer.
        using var titleFont = UiChrome.ChromeFont(15f, FontStyle.Bold);
        using var subFont = UiChrome.ChromeFont(9.5f, FontStyle.Regular);
        using var chipFont = UiChrome.ChromeFont(9.5f, FontStyle.Bold);

        var titleText = LocalizationService.Translate("Drop an image or project");
        var hintText = LocalizationService.Translate("Double-click · drag and drop");
        var newLabel = LocalizationService.Translate("New canvas");
        var openLabel = LocalizationService.Translate("Open");
        var pasteLabel = LocalizationService.Translate("Paste");
        var captureLabel = LocalizationService.Translate("Capture");

        var titleSize = TextRenderer.MeasureText(g, titleText, titleFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var hintSize = TextRenderer.MeasureText(g, hintText, subFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        float paddingH = 28;
        float paddingV = 24;
        float spacing = 10;
        float iconSize = 56;
        float chipH = 32;
        float chipGap = 8;
        float chipMinW = 84;

        float newW = Math.Max(chipMinW, TextRenderer.MeasureText(g, newLabel, chipFont).Width + 34);
        float openW = Math.Max(chipMinW, TextRenderer.MeasureText(g, openLabel, chipFont).Width + 34);
        float pasteW = Math.Max(chipMinW, TextRenderer.MeasureText(g, pasteLabel, chipFont).Width + 34);
        float captureW = Math.Max(chipMinW, TextRenderer.MeasureText(g, captureLabel, chipFont).Width + 34);
        float chipsRowW = newW + openW + pasteW + captureW + chipGap * 3;

        float contentW = Math.Max(titleSize.Width, Math.Max(hintSize.Width, chipsRowW));
        float width = Math.Max(contentW + paddingH * 2, 450);
        float height = paddingV * 2 + iconSize + spacing + titleSize.Height + spacing
            + hintSize.Height + spacing + 6 + chipH;

        float x = (ClientSize.Width - width) / 2f;
        float y = (ClientSize.Height - height) / 2f;
        _welcomeCardRect = new RectangleF(x, y, width, height);

        Color titleColor = EditorColors.IsDark
            ? Color.FromArgb(240, 245, 255)
            : Color.FromArgb(28, 42, 65);
        Color subColor = EditorColors.IsDark ? EditorColors.TextMuted : Color.FromArgb(100, 120, 150);
        Color accent = EditorColors.Accent;
        Color chipBg = EditorColors.IsDark
            ? Color.FromArgb(255, Math.Min(255, EditorColors.BgCard.R + 12), Math.Min(255, EditorColors.BgCard.G + 14), Math.Min(255, EditorColors.BgCard.B + 18))
            : Color.FromArgb(245, 248, 252);
        Color chipBorder = EditorColors.BorderSubtle;
        Color cardBorder = _welcomeDragOver
            ? Color.FromArgb(220, accent)
            : EditorColors.BorderSubtle;
        float borderW = _welcomeDragOver ? 2f : 1.25f;

        var rect = new Rectangle((int)x, (int)y, (int)width, (int)height);
        
        // Multi-layered card drop shadow for depth
        using (var shPath1 = RoundedRectPath(rect.X, rect.Y + 4, rect.Width, rect.Height, 16f))
        using (var shBrush1 = new SolidBrush(Color.FromArgb(45, 0, 0, 0)))
            g.FillPath(shBrush1, shPath1);

        using (var shPath2 = RoundedRectPath(rect.X, rect.Y + 8, rect.Width, rect.Height, 16f))
        using (var shBrush2 = new SolidBrush(Color.FromArgb(30, 0, 0, 0)))
            g.FillPath(shBrush2, shPath2);

        using var path = RoundedRectPath(rect.X, rect.Y, rect.Width, rect.Height, 16f);
        using var bgBrush = new SolidBrush(Color.FromArgb(_welcomeDragOver ? 245 : 230, EditorColors.BgCard));
        using var borderPen = new Pen(cardBorder, borderW);
        g.FillPath(bgBrush, path);
        g.DrawPath(borderPen, path);

        if (EditorColors.IsDark)
        {
            using var hlPen = new Pen(Color.FromArgb(30, 255, 255, 255), 1f);
            g.DrawLine(hlPen, rect.Left + 16, rect.Top + 1, rect.Right - 16, rect.Top + 1);
        }

        if (_welcomeDragOver)
        {
            using var glow = new Pen(Color.FromArgb(50, accent), 6f);
            g.DrawPath(glow, path);
        }

        float curY = y + paddingV;
        float iconCx = x + width / 2f;
        float iconCy = curY + iconSize / 2f;
        float iconRadius = iconSize * 0.46f;
        _welcomeIconRect = new RectangleF(iconCx - iconRadius, iconCy - iconRadius, iconRadius * 2, iconRadius * 2);
        DrawWelcomeIcon(g, iconCx, iconCy, iconSize, accent, _welcomeDragOver, _welcomeHoverIcon, _welcomePressedIcon);
        curY += iconSize + spacing;

        var titleFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        var titleRect = new Rectangle((int)x, (int)curY, (int)width, titleSize.Height + 2);
        TextRenderer.DrawText(g, titleText, titleFont, titleRect, titleColor, titleFlags);
        curY += titleSize.Height + spacing;

        // Plain hint
        var hintRect = new Rectangle((int)x, (int)curY, (int)width, hintSize.Height + 2);
        TextRenderer.DrawText(g, hintText, subFont, hintRect, subColor, titleFlags);
        curY += hintSize.Height + spacing + 6;

        // Action chips
        float chipsStartX = x + (width - chipsRowW) / 2f;
        _welcomeChipRects[0] = new RectangleF(chipsStartX, curY, newW, chipH);
        _welcomeChipRects[1] = new RectangleF(chipsStartX + newW + chipGap, curY, openW, chipH);
        _welcomeChipRects[2] = new RectangleF(chipsStartX + newW + chipGap + openW + chipGap, curY, pasteW, chipH);
        _welcomeChipRects[3] = new RectangleF(chipsStartX + newW + chipGap + openW + chipGap + pasteW + chipGap, curY, captureW, chipH);

        bool pasteEnabled = IsWelcomeChipEnabled(2);
        DrawWelcomeChip(g, _welcomeChipRects[0], newLabel, chipFont, 0, true,
            _welcomeHoverChip == 0, _welcomePressedChip == 0, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[1], openLabel, chipFont, 1, true,
            _welcomeHoverChip == 1, _welcomePressedChip == 1, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[2], pasteLabel, chipFont, 2, pasteEnabled,
            _welcomeHoverChip == 2, _welcomePressedChip == 2, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[3], captureLabel, chipFont, 3, true,
            _welcomeHoverChip == 3, _welcomePressedChip == 3, titleColor, subColor, accent, chipBg, chipBorder);
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
