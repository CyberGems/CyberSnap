using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Models.Commands;
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
            // DrawBaseImage leaves NearestNeighbor set; annotations (especially emoji
            // bitmaps) need a smooth resample when the view is not at 100%.
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height));
            _annotationViewScale = (float)_zoom;
            try
            {
                RenderAnnotations(g);
                RenderToolPreview(g);
                RenderInlineTextPreview(g);
            }
            finally
            {
                _annotationViewScale = 1f;
                g.Clip = oldClip;
            }
        }
        finally
        {
            g.Restore(state);
        }

        RenderResizeHandles(g);
        RenderCropOverlay(g);
        RenderCutOutOverlay(g);
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
        _checkerboardBrush.TranslateTransform(x / (float)_zoom, y / (float)_zoom);
        g.FillRectangle(_checkerboardBrush, x, y, width, height);
    }

    /// <summary>Draws the base bitmap at the current zoom/pan, using the pre-scaled cache.</summary>
    private void DrawBaseImage(Graphics g)
    {
        int scaledW = Math.Max(1, (int)Math.Round(_baseBitmap.Width * _zoom));
        int scaledH = Math.Max(1, (int)Math.Round(_baseBitmap.Height * _zoom));

        PaintCheckerboardBackground(g, _pan.X, _pan.Y, scaledW, scaledH);

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
        // Also skip the annotation currently being re-edited (live inline frame replaces it),
        // and while a new draw drag is in progress (hover must not compete with the preview).
        if (_preSpaceTool == null && ShowsAnnotationHoverChrome(_activeTool) && _moveHoverIndex >= 0 && _moveHoverIndex < _annotations.Count
            && _moveHoverIndex != _selectedAnnotationIndex
            && _moveHoverIndex != _renderSkipAnnotationIndex
            && !_multiSelectedIndices.Contains(_moveHoverIndex)
            && !(_isDragging && !IsManipulatingExistingAnnotation))
        {
            var hovered = _annotations[_moveHoverIndex];
            var bounds = GetAnnotationVisualBounds(hovered);
            DrawMoveHandles(g, bounds, isSelected: false, moveOnly: !IsResizable(hovered), hovered);
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
                    var bounds = GetAnnotationVisualBounds(ann);
                    DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(ann), ann);
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
            var bounds = GetAnnotationVisualBounds(selected);
            DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(selected), selected);
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
                    var bounds = GetAnnotationVisualBounds(ann);
                    DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(ann), ann);
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
            var bounds = GetAnnotationVisualBounds(selected);
            DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(selected), selected);
        }
    }

    private void DrawMoveHandles(Graphics g, RectangleF bounds, bool isSelected, bool moveOnly = false, Annotation? source = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        float z = (float)_zoom;
        if (z <= 0.01f) z = 1.0f;

        var accent = Theme.Accent;
        var accentColor = Color.FromArgb(isSelected ? 200 : 90, accent.R, accent.G, accent.B);

        if (isSelected && _isRotateMode && source is not null && AnnotationTransforms.CanRotate(source)
            && _multiSelectedIndices.Count <= 1)
        {
            var corners = GetRotateModeCorners(source, 0f);
            WindowsHandleRenderer.PaintRotateMode(g, corners, accentColor, 1f / z);
            return;
        }

        float offset = (isSelected ? SelectionHandleGapPx : 3f) / z;
        var rect = new RectangleF(
            bounds.X - offset,
            bounds.Y - offset,
            bounds.Width + 2 * offset,
            bounds.Height + 2 * offset
        );

        int fillAlpha = isSelected ? 0 : 6;
        int dashAlpha = isSelected ? 40 : 28;
        byte aR = accent.R, aG = accent.G, aB = accent.B;

        // Fill and dash
        if (fillAlpha > 0)
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(fillAlpha, aR, aG, aB));
            g.FillRectangle(fillBrush, rect);
        }

        using (var dashPen = new Pen(Color.FromArgb(dashAlpha, aR, aG, aB), isSelected ? 0.75f / z : 0.9f / z))
        {
            dashPen.DashStyle = DashStyle.Dash;
            dashPen.DashPattern = new[] { 4f, 3f };
            g.DrawRectangle(dashPen, rect.X, rect.Y, rect.Width, rect.Height);
        }

        float screenW = bounds.Width * z;
        float screenH = bounds.Height * z;
        float midX = rect.Left + rect.Width / 2f;
        float midY = rect.Top + rect.Height / 2f;

        if (isSelected && !moveOnly)
        {
            // Figma-style handles: crisp white squares with 1.5px accent border and subtle drop shadow
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

            DrawHandle(rect.Left,  rect.Top);
            DrawHandle(rect.Right, rect.Top);
            DrawHandle(rect.Left,  rect.Bottom);
            DrawHandle(rect.Right, rect.Bottom);

            if (screenW >= 56)
            {
                DrawHandle(midX, rect.Top);
                DrawHandle(midX, rect.Bottom);
            }
            if (screenH >= 56)
            {
                DrawHandle(rect.Left,  midY);
                DrawHandle(rect.Right, midY);
            }
        }

        if (isSelected && WindowsHandleRenderer.FitsCenterPlus((int)screenW, (int)screenH))
            WindowsHandleRenderer.PaintCenterPlus(g, new PointF(midX, midY), accentColor, 1f / z);
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
                AnnotationTransforms.DrawWithRectRotation(g, rs.Rect, rs.Rotation,
                    () => SketchRenderer.DrawRectShape(g, rs.Rect, rs.Color, AnnotationStrokeShadow, GetScaledStrokeWidth(rs.StrokeWidth)));
                break;
            case CircleShapeAnnotation cs:
                AnnotationTransforms.DrawWithRectRotation(g, cs.Rect, cs.Rotation,
                    () => SketchRenderer.DrawCircleShape(g, cs.Rect, cs.Color, AnnotationStrokeShadow, GetScaledStrokeWidth(cs.StrokeWidth)));
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

    private PointF ImageToScreenF(PointF p) =>
        new(_pan.X + (float)(p.X * _zoom), _pan.Y + (float)(p.Y * _zoom));

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
            RectShapeAnnotation rs => AnnotationTransforms.GetAxisAlignedBounds(rs.Rect, rs.Rotation),
            CircleShapeAnnotation cs => AnnotationTransforms.GetAxisAlignedBounds(cs.Rect, cs.Rotation),
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
            RecalcBannerLayout();
            var rect = _bannerPaintRect;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            int alphaBg = (int)(200 * _bannerOpacity);
            int alphaBorder = (int)(150 * _bannerOpacity);
            int alphaGlow = (int)(40 * _bannerOpacity);
            int alphaText = (int)(255 * _bannerOpacity);

            var bgCol = EditorColors.BgCard;
            var accentCol = EditorColors.Accent;

            using var path = EditorPaint.RoundedRect(rect, 8);
            using var bgBrush = new SolidBrush(Color.FromArgb(alphaBg, bgCol.R, bgCol.G, bgCol.B));
            using var glowPen = new Pen(Color.FromArgb(alphaGlow, accentCol.R, accentCol.G, accentCol.B), 3f);
            using var borderPen = new Pen(Color.FromArgb(alphaBorder, accentCol.R, accentCol.G, accentCol.B), 1.2f);
            using var font = UiChrome.ChromeFont(11f, FontStyle.Bold);

            g.FillPath(bgBrush, path);
            g.DrawPath(glowPen, path);
            g.DrawPath(borderPen, path);

            TextRenderer.DrawText(
                g,
                _bannerText,
                font,
                Rectangle.Inflate(rect, -16, -10),
                Color.FromArgb(alphaText, accentCol),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
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

    /// <summary>Draws a dashed rounded rectangle box with a centered plus sign matching the welcome mockup.</summary>
    private void DrawWelcomeIcon(Graphics g, float cx, float cy, float boxW, float boxH, Color accentColor, bool isDragOver, bool isHovered, bool isPressed)
    {
        Color neutralBorder = EditorColors.IsDark ? Color.FromArgb(175, 182, 192) : Color.FromArgb(120, 135, 155);
        Color brightBorder = EditorColors.IsDark ? Color.FromArgb(240, 246, 255) : Color.FromArgb(40, 55, 75);
        Color borderColor = isPressed ? brightBorder : (isDragOver ? accentColor : (isHovered ? brightBorder : neutralBorder));

        Color neutralPlus = EditorColors.IsDark ? Color.FromArgb(250, 252, 255) : Color.FromArgb(30, 40, 55);
        Color brightPlus = EditorColors.IsDark ? Color.White : Color.FromArgb(10, 15, 25);
        Color plusColor = (isHovered || isDragOver || isPressed) ? brightPlus : neutralPlus;

        float zoom = isPressed ? 0.97f : ((isHovered || isDragOver) ? 1.03f : 1.0f);
        var gfxState = g.Save();
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(zoom, zoom);
        g.TranslateTransform(-cx, -cy);
        try
        {
            float r = 14f;
            float boxX = cx - boxW / 2f;
            float boxY = cy - boxH / 2f;

            using var path = RoundedRectPath(boxX, boxY, boxW, boxH, r);

            // Subtle interactive fill on hover/drag
            if (isHovered || isDragOver || isPressed)
            {
                Color fillColor = isPressed
                    ? Color.FromArgb(32, accentColor)
                    : isDragOver
                        ? Color.FromArgb(24, accentColor)
                        : Color.FromArgb(14, accentColor);
                using var fillBrush = new SolidBrush(fillColor);
                g.FillPath(fillBrush, path);
            }

            // Dashed border matching mockup
            using (var pen = new Pen(borderColor, isDragOver ? 1.8f : 1.5f)
            {
                DashPattern = new float[] { 5.0f, 5.0f },
                DashCap = DashCap.Flat,
                LineJoin = LineJoin.Round
            })
            {
                g.DrawPath(pen, path);
            }

            // Plus symbol in center
            float arm = 12.5f; // 25px total span, matching mockup
            using var plusPen = new Pen(plusColor, 2.8f)
            {
                StartCap = LineCap.Flat,
                EndCap = LineCap.Flat
            };
            g.DrawLine(plusPen, cx - arm, cy, cx + arm, cy);
            g.DrawLine(plusPen, cx, cy - arm, cx, cy + arm);
        }
        finally
        {
            g.Restore(gfxState);
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
        float iconW = 16f;
        float gap = 7f;
        var textSz = g.MeasureString(label, font);
        float totalContentW = iconW + gap + textSz.Width;
        float startX = rect.X + (rect.Width - totalContentW) / 2f;
        float iconCy = rect.Y + rect.Height / 2f;

        DrawChipIcon(g, chipType, startX + iconW / 2f, iconCy, iconCol);

        using var textBrush = new SolidBrush(fg);
        g.DrawString(label, font, textBrush, startX + iconW + gap, rect.Y + (rect.Height - textSz.Height) / 2f);
    }

    private static void DrawChipIcon(Graphics g, int chipType, float cx, float cy, Color color)
    {
        using var pen = new Pen(color, 1.45f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        switch (chipType)
        {
            case 0: // New Canvas (document with fold and plus)
            {
                float w = 13.5f, h = 15.5f;
                float x = cx - w / 2f, y = cy - h / 2f + 0.5f;
                float fold = 4.5f;

                // Outer page outline with folded top-right corner
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w - fold, y),
                    new PointF(x, y),
                    new PointF(x, y + h),
                    new PointF(x + w, y + h),
                    new PointF(x + w, y + fold),
                    new PointF(x + w - fold, y),
                    new PointF(x + w - fold, y + fold),
                    new PointF(x + w, y + fold)
                });

                // Centered plus (+)
                float px = cx, py = cy + 2.0f;
                float pSize = 2.7f;
                g.DrawLine(pen, px - pSize, py, px + pSize, py);
                g.DrawLine(pen, px, py - pSize, px, py + pSize);
                break;
            }
            case 1: // Folder (Open)
            {
                float w = 15.5f, h = 12.5f;
                float x = cx - w / 2f, y = cy - h / 2f + 0.5f;

                // Back folder tab + body
                g.DrawLines(pen, new[]
                {
                    new PointF(x, y + 3f),
                    new PointF(x, y + h),
                    new PointF(x + w, y + h),
                    new PointF(x + w, y + 3f),
                    new PointF(x + w * 0.58f, y + 3f),
                    new PointF(x + w * 0.44f, y),
                    new PointF(x, y),
                    new PointF(x, y + 3f),
                    new PointF(x + w, y + 3f)
                });
                break;
            }
            case 2: // Clipboard (Paste)
            {
                float w = 13f, h = 15.5f;
                float x = cx - w / 2f, y = cy - h / 2f + 0.5f;

                // Board
                g.DrawLines(pen, new[]
                {
                    new PointF(x + 2.8f, y + 2.5f),
                    new PointF(x, y + 2.5f),
                    new PointF(x, y + h),
                    new PointF(x + w, y + h),
                    new PointF(x + w, y + 2.5f),
                    new PointF(x + w - 2.8f, y + 2.5f)
                });

                // Top clip
                using (var clipPath = RoundedRectPath(x + 3.2f, y - 0.5f, w - 6.4f, 3.2f, 1f))
                {
                    g.DrawPath(pen, clipPath);
                }

                // Lines on clipboard
                using var thin = new Pen(color, 1.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(thin, cx - 3.5f, y + 6.8f, cx + 3.5f, y + 6.8f);
                g.DrawLine(thin, cx - 3.5f, y + 10.2f, cx + 1.8f, y + 10.2f);
                break;
            }
            case 3: // Camera (Capture)
            {
                float w = 15.5f, h = 12f;
                float x = cx - w / 2f, y = cy - h / 2f + 1f;

                // Camera body
                g.DrawLines(pen, new[]
                {
                    new PointF(x, y + 3f),
                    new PointF(x, y + h),
                    new PointF(x + w, y + h),
                    new PointF(x + w, y + 3f),
                    new PointF(x + w * 0.72f, y + 3f),
                    new PointF(x + w * 0.62f, y),
                    new PointF(x + w * 0.38f, y),
                    new PointF(x + w * 0.28f, y + 3f),
                    new PointF(x, y + 3f)
                });

                // Lens circle
                float lr = 2.7f;
                g.DrawEllipse(pen, cx - lr, cy + 1.3f - lr, lr * 2, lr * 2);
                break;
            }
            case 4: // Question mark in circle (Quick Start)
            {
                float r = 7.5f;
                // Outer circle
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);

                // Vector question mark hook
                using var qPen = new Pen(color, 1.45f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };

                var qPath = new GraphicsPath();
                qPath.AddArc(cx - 2.5f, cy - 4.6f, 5.0f, 4.2f, 195, 170);
                qPath.AddLine(cx + 2.4f, cy - 2.5f, cx, cy - 0.4f);
                qPath.AddLine(cx, cy - 0.4f, cx, cy + 1.1f);
                g.DrawPath(qPen, qPath);

                // Perfectly centered dot
                using var dotBrush = new SolidBrush(color);
                g.FillEllipse(dotBrush, cx - 1.0f, cy + 3.2f, 2.0f, 2.0f);
                break;
            }
        }
    }

    private void RenderWelcomeText(Graphics target)
    {
        using var titleFont = UiChrome.ChromeFont(15f, FontStyle.Bold);
        using var subFont = UiChrome.ChromeFont(9.5f, FontStyle.Regular);
        using var chipFont = UiChrome.ChromeFont(9.5f, FontStyle.Bold);

        var titleText = LocalizationService.Translate("Drop an image or project");
        var hintText = LocalizationService.Translate("Double-click · drag and drop");
        var newLabel = LocalizationService.Translate("New canvas");
        var openLabel = LocalizationService.Translate("Open");
        var pasteLabel = LocalizationService.Translate("Paste");
        var captureLabel = LocalizationService.Translate("Capture");
        var guideLabel = LocalizationService.Translate("Quick Start");

        var measureFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var titleSize = TextRenderer.MeasureText(titleText, titleFont, new Size(int.MaxValue, int.MaxValue), measureFlags);
        var hintSize = TextRenderer.MeasureText(hintText, subFont, new Size(int.MaxValue, int.MaxValue), measureFlags);

        float paddingH = 28;
        float paddingV = 24;
        float spacing = 10;
        float iconSize = 74;
        float chipH = 32;
        float chipGap = 8;
        float chipMinW = 84;

        float newW = Math.Max(chipMinW, TextRenderer.MeasureText(newLabel, chipFont).Width + 38);
        float openW = Math.Max(chipMinW, TextRenderer.MeasureText(openLabel, chipFont).Width + 38);
        float pasteW = Math.Max(chipMinW, TextRenderer.MeasureText(pasteLabel, chipFont).Width + 38);
        float captureW = Math.Max(chipMinW, TextRenderer.MeasureText(captureLabel, chipFont).Width + 38);
        float guideW = Math.Max(chipMinW, TextRenderer.MeasureText(guideLabel, chipFont).Width + 38);
        float chipsRowW = newW + openW + pasteW + captureW + guideW + chipGap * 4;

        float contentW = Math.Max(titleSize.Width, Math.Max(hintSize.Width, chipsRowW));
        float width = Math.Max(contentW + paddingH * 2, 450);
        float height = paddingV * 2 + iconSize + spacing + 4 + titleSize.Height + spacing
            + hintSize.Height + spacing + 6 + chipH;

        float destX = (ClientSize.Width - width) / 2f;
        float destY = (ClientSize.Height - height) / 2f;

        // TextRenderer is GDI and bypasses WS_EX_COMPOSITED, so maximize/restore
        // ghosted this overlay. Paint it off-screen, then blit with GDI+.
        const int shadowPad = 12;
        int bmpW = Math.Max(1, (int)Math.Ceiling(width) + shadowPad * 2);
        int bmpH = Math.Max(1, (int)Math.Ceiling(height) + shadowPad * 2);
        float x = shadowPad;
        float y = shadowPad;

        using var bmp = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            PaintWelcomeCard(
                g, x, y, width, height, paddingV, spacing, iconSize, chipH, chipGap,
                newW, openW, pasteW, captureW, guideW, chipsRowW,
                titleText, hintText, newLabel, openLabel, pasteLabel, captureLabel, guideLabel,
                titleFont, subFont, chipFont, titleSize, hintSize);
        }

        var oldInterp = target.InterpolationMode;
        var oldOffset = target.PixelOffsetMode;
        target.InterpolationMode = InterpolationMode.NearestNeighbor;
        target.PixelOffsetMode = PixelOffsetMode.Half;
        target.DrawImageUnscaled(bmp, (int)Math.Round(destX) - shadowPad, (int)Math.Round(destY) - shadowPad);
        target.InterpolationMode = oldInterp;
        target.PixelOffsetMode = oldOffset;

        float hitDx = destX - x;
        float hitDy = destY - y;
        _welcomeCardRect.Offset(hitDx, hitDy);
        _welcomeIconRect.Offset(hitDx, hitDy);
        for (int i = 0; i < _welcomeChipRects.Length; i++)
            _welcomeChipRects[i].Offset(hitDx, hitDy);
    }

    private void PaintWelcomeCard(
        Graphics g,
        float x,
        float y,
        float width,
        float height,
        float paddingV,
        float spacing,
        float iconSize,
        float chipH,
        float chipGap,
        float newW,
        float openW,
        float pasteW,
        float captureW,
        float guideW,
        float chipsRowW,
        string titleText,
        string hintText,
        string newLabel,
        string openLabel,
        string pasteLabel,
        string captureLabel,
        string guideLabel,
        Font titleFont,
        Font subFont,
        Font chipFont,
        Size titleSize,
        Size hintSize)
    {
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
        float boxW = 74f;
        float boxH = 72f;
        _welcomeIconRect = new RectangleF(iconCx - boxW / 2f, iconCy - boxH / 2f, boxW, boxH);
        DrawWelcomeIcon(g, iconCx, iconCy, boxW, boxH, accent, _welcomeDragOver, _welcomeHoverIcon, _welcomePressedIcon);
        curY += iconSize + spacing + 4;

        using var titleBrush = new SolidBrush(titleColor);
        using var hintBrush = new SolidBrush(subColor);
        using var titleFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };
        var titleRect = new RectangleF(x, curY, width, titleSize.Height + 2);
        g.DrawString(titleText, titleFont, titleBrush, titleRect, titleFormat);
        curY += titleSize.Height + spacing;

        var hintRect = new RectangleF(x, curY, width, hintSize.Height + 2);
        g.DrawString(hintText, subFont, hintBrush, hintRect, titleFormat);
        curY += hintSize.Height + spacing + 6;

        // Action chips
        float chipsStartX = x + (width - chipsRowW) / 2f;
        _welcomeChipRects[0] = new RectangleF(chipsStartX, curY, newW, chipH);
        _welcomeChipRects[1] = new RectangleF(chipsStartX + newW + chipGap, curY, openW, chipH);
        _welcomeChipRects[2] = new RectangleF(chipsStartX + newW + chipGap + openW + chipGap, curY, pasteW, chipH);
        _welcomeChipRects[3] = new RectangleF(chipsStartX + newW + chipGap + openW + chipGap + pasteW + chipGap, curY, captureW, chipH);
        _welcomeChipRects[4] = new RectangleF(chipsStartX + newW + chipGap + openW + chipGap + pasteW + chipGap + captureW + chipGap, curY, guideW, chipH);

        bool pasteEnabled = IsWelcomeChipEnabled(2);
        DrawWelcomeChip(g, _welcomeChipRects[0], newLabel, chipFont, 0, true,
            _welcomeHoverChip == 0, _welcomePressedChip == 0, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[1], openLabel, chipFont, 1, true,
            _welcomeHoverChip == 1, _welcomePressedChip == 1, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[2], pasteLabel, chipFont, 2, pasteEnabled,
            _welcomeHoverChip == 2, _welcomePressedChip == 2, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[3], captureLabel, chipFont, 3, true,
            _welcomeHoverChip == 3, _welcomePressedChip == 3, titleColor, subColor, accent, chipBg, chipBorder);
        DrawWelcomeChip(g, _welcomeChipRects[4], guideLabel, chipFont, 4, true,
            _welcomeHoverChip == 4, _welcomePressedChip == 4, titleColor, subColor, accent, chipBg, chipBorder);
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
