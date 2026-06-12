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
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    private static void ApplyUiGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.None;

        // Blit the cached screenshot + committed annotations layer.
        var clip = e.ClipRectangle;
        var committed = GetCommittedAnnotationsBitmap();
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(committed, clip, clip, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;

        bool isOcr = _mode == CaptureMode.Ocr;
        bool isScan = _mode == CaptureMode.Scan;
        bool isSelectionMode = _mode is CaptureMode.Rectangle or CaptureMode.Center or CaptureMode.Ocr or CaptureMode.Scan or CaptureMode.Sticker or CaptureMode.Upscale or CaptureMode.ScrollCapture;

        // Dynamic dimming/accent overlay
        if (isSelectionMode)
        {
            var accent = UiChrome.AccentColor;
            var overlayColor = Color.FromArgb(4, accent.R, accent.G, accent.B);

            if (_isSelecting || _isConfirmingSelection)
            {
                var activeSelectionRect = _isConfirmingSelection ? _confirmRect : _selectionRect;
                var state = g.Save();
                try
                {
                    if (activeSelectionRect.Width > 0 && activeSelectionRect.Height > 0)
                    {
                        g.ExcludeClip(activeSelectionRect);
                    }
                    using (var dimBrush = new SolidBrush(overlayColor))
                    {
                        g.FillRectangle(dimBrush, ClientRectangle);
                    }
                }
                finally
                {
                    g.Restore(state);
                }
            }
            else if (!_hasSelection && !_autoDetectActive)
            {
                using (var dimBrush = new SolidBrush(overlayColor))
                {
                    g.FillRectangle(dimBrush, ClientRectangle);
                }
            }
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Live tool previews (active drawing in progress)
        PaintAnnotations(g);

        // Move tool: hover highlight
        if (IsDrawingOrMoveMode(_mode) && !IsDraggingAnyAnnotation() && _moveHoverIndex >= 0 && _moveHoverIndex < _undoStack.Count && _moveHoverIndex != _selectedAnnotationIndex)
        {
            var hoverBounds = GetAnnotationBounds(_undoStack[_moveHoverIndex]);
            DrawMoveHandles(g, hoverBounds, isSelected: false);
        }

        // Move tool: draw selection highlight and handles
        if (IsDrawingOrMoveMode(_mode) && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _undoStack.Count)
        {
            var selected = _selectPreviewAnnotation ?? _undoStack[_selectedAnnotationIndex];
            var bounds = GetAnnotationBounds(selected);
            DrawMoveHandles(g, bounds, isSelected: true);
        }

        // Eraser hover highlight
        if (_mode == CaptureMode.Eraser && _eraserHoverIndex >= 0 && _eraserHoverIndex < _undoStack.Count)
        {
            var bounds = GetAnnotationBounds(_undoStack[_eraserHoverIndex]);
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                using var overlay = new SolidBrush(Color.FromArgb(50, 220, 50, 50));
                g.FillRectangle(overlay, bounds);
                using var pen = new Pen(Color.FromArgb(200, 220, 40, 40), 2f)
                {
                    DashStyle = DashStyle.Dash,
                    DashPattern = new[] { 5f, 3f }
                };
                g.DrawRectangle(pen, Rectangle.Inflate(bounds, 3, 3));
            }
        }

        if (_mode == CaptureMode.ColorPicker)
            return; // magnifier is its own layered window, overlay stays static

        if (isSelectionMode && !_isSelecting && !_hasSelection && _autoDetectActive && _autoDetectRect.Width > 0)
        {
            // Clamp the rect so dashes stay within the visible client area
            var drawRect = ClampRectToClient(_autoDetectRect);
            if (drawRect.Width > 0 && drawRect.Height > 0)
            {
                SelectionFrameRenderer.DrawAutoDetectRectangle(g, drawRect);
            }
            _lastAutoDetectRect = _autoDetectRect;
        }
        else if (isSelectionMode && !_hasSelection && !_isSelecting)
        {
            _lastAutoDetectRect = Rectangle.Empty;
        }

        // Selection borders (on top of everything)
        switch (_mode)
        {
            case CaptureMode.Rectangle when _isSelecting && _hasSelection:
            case CaptureMode.ScrollCapture when _isSelecting && _hasSelection:
            case CaptureMode.Center when _isSelecting && _hasSelection:
            case CaptureMode.Ocr when _isSelecting && _hasSelection:
            case CaptureMode.Scan when _isSelecting && _hasSelection:
            case CaptureMode.Sticker when _isSelecting && _hasSelection:
            case CaptureMode.Upscale when _isSelecting && _hasSelection:
            case CaptureMode.Rectangle when _hasSelection && !_isSelecting:
            case CaptureMode.ScrollCapture when _hasSelection && !_isSelecting:
            case CaptureMode.Center when _hasSelection && !_isSelecting:
            case CaptureMode.Ocr when _hasSelection && !_isSelecting:
            case CaptureMode.Scan when _hasSelection && !_isSelecting:
            case CaptureMode.Sticker when _hasSelection && !_isSelecting:
            case CaptureMode.Upscale when _hasSelection && !_isSelecting:
                SelectionFrameRenderer.DrawRectangle(g, _selectionRect);
                SelectionSizeReadout.Draw(
                    g,
                    GetReadoutCursorPoint(),
                    _selectionRect,
                    _readoutFont,
                    ClientRectangle);
                _lastSelectionRect = _selectionRect;
                break;

        }

        if (!_hasSelection)
            _lastSelectionRect = Rectangle.Empty;

        // Draw confirmation-mode handles and buttons
        if (_isConfirmingSelection)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Draw buttons FIRST (underneath the frame so any overlap is covered)
            var (confirmBtn, cancelBtn) = GetConfirmButtonRects();
            using (var btnFont = UiChrome.ChromeFont(11f, FontStyle.Bold))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                float corner = UiChrome.ScaledToolbarCornerRadius;
                bool confirmHover = _hoveredConfirmButton == 0;
                bool cancelHover = _hoveredConfirmButton == 1;

                // ── Confirm button (primary action) ──
                using (var confirmPath = WindowsDockRenderer.RoundedRect(confirmBtn, corner))
                {
                    // Solid base to completely block background letters
                    int baseVal = UiChrome.IsDark ? 16 : 240;
                    using var baseFill = new SolidBrush(Color.FromArgb(255, baseVal, baseVal, baseVal));
                    g.FillPath(baseFill, confirmPath);

                    // Solid premium accent fill
                    using var confirmFill = new SolidBrush(Color.FromArgb(confirmHover ? 255 : 215, UiChrome.AccentColor));
                    g.FillPath(confirmFill, confirmPath);
                }
                using (var confirmGlowPen = new Pen(
                    confirmHover ? Color.FromArgb(90, UiChrome.AccentColor) : Color.FromArgb(50, UiChrome.AccentColor),
                    confirmHover ? 6f : 5f))
                using (var confirmGlowPath = WindowsDockRenderer.RoundedRect(
                    RectangleF.Inflate(confirmBtn, 2, 2), corner))
                    g.DrawPath(confirmGlowPen, confirmGlowPath);
                using (var confirmBorderPen = new Pen(
                    confirmHover ? Color.FromArgb(255, UiChrome.AccentColor) : Color.FromArgb(210, UiChrome.AccentColor), 1.5f))
                using (var confirmBorderPath = WindowsDockRenderer.RoundedRect(confirmBtn, corner))
                    g.DrawPath(confirmBorderPen, confirmBorderPath);
                
                // Clear high-contrast text
                using (var confirmTextBrush = new SolidBrush(Color.White))
                    g.DrawString(LocalizationService.Translate("Confirm"), btnFont, confirmTextBrush, confirmBtn, sf);

                // ── Cancel button (secondary action) ──
                using (var cancelPath = WindowsDockRenderer.RoundedRect(cancelBtn, corner))
                {
                    // Solid base fill with dark red in dark mode, light red in light mode
                    var cancelBgColor = UiChrome.IsDark
                        ? Color.FromArgb(255, 45, 12, 12)
                        : Color.FromArgb(255, 250, 230, 230);
                    using var cancelFill = new SolidBrush(cancelBgColor);
                    g.FillPath(cancelFill, cancelPath);
                    if (cancelHover)
                    {
                        using var cancelHoverBrush = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 30 : 20, 239, 68, 68));
                        g.FillPath(cancelHoverBrush, cancelPath);
                    }
                }
                var neonRedColor = Color.FromArgb(239, 68, 68);
                using (var cancelGlowPen = new Pen(
                    cancelHover ? Color.FromArgb(40, neonRedColor) : Color.FromArgb(12, neonRedColor), 4f))
                using (var cancelGlowPath = WindowsDockRenderer.RoundedRect(
                    RectangleF.Inflate(cancelBtn, 2, 2), corner))
                    g.DrawPath(cancelGlowPen, cancelGlowPath);
                using (var cancelBorderPen = new Pen(
                    cancelHover ? neonRedColor : Color.FromArgb(UiChrome.IsDark ? 100 : 70, neonRedColor), 1.2f))
                using (var cancelBorderPath = WindowsDockRenderer.RoundedRect(cancelBtn, corner))
                    g.DrawPath(cancelBorderPen, cancelBorderPath);

                using (var cancelTextBrush = new SolidBrush(cancelHover ? neonRedColor : UiChrome.SurfaceTextPrimary))
                    g.DrawString(LocalizationService.Translate("Cancel"), btnFont, cancelTextBrush, cancelBtn, sf);
            }

            // Draw selection frame and handles ON TOP of buttons
            SelectionFrameRenderer.DrawRectangle(g, _confirmRect, fill: false);
            var handles = GetConfirmHandleRects();
            foreach (var h in handles)
                WindowsHandleRenderer.Paint(g, h);
        }

        g.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>Clamp a rectangle so it stays 2px inside the client area (prevents dashes from being cut off at screen edges).</summary>
    private Rectangle ClampRectToClient(Rectangle rect)
    {
        const int pad = 2;
        int x = Math.Max(pad, rect.X);
        int y = Math.Max(pad, rect.Y);
        int right = Math.Min(ClientSize.Width - pad - 1, rect.Right);
        int bottom = Math.Min(ClientSize.Height - pad - 1, rect.Bottom);
        return new Rectangle(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private void InvalidateSelectionChrome(Rectangle oldSelection, Point oldCursor, Rectangle newSelection, Point newCursor)
    {
        InvalidateSelectionChromePart(oldSelection, oldCursor);
        InvalidateSelectionChromePart(newSelection, newCursor);
    }

    private void InvalidateSelectionChromePart(Rectangle selection, Point cursor)
    {
        if (selection.Width <= 2 || selection.Height <= 2)
            return;

        var selectionDirty = selection;
        selectionDirty.Inflate(16, 16);
        Invalidate(selectionDirty);

        var readoutBounds = SelectionSizeReadout.GetBounds(
            cursor,
            selection,
            _readoutFont,
            ClientRectangle);
        if (!readoutBounds.IsEmpty)
            Invalidate(InflateForRepaint(readoutBounds, 10));
    }


    private static void PaintShadow(Graphics g, RectangleF rect, float radius, int alpha = 52, float yOffset = 1f)
    {
        var oldSmooth = g.SmoothingMode;
        var oldComp = g.CompositingMode;
        var oldCompQual = g.CompositingQuality;
        var oldPix = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Fluent 2-layer shadow: ambient (soft, wide) + directional (tighter, more Y-offset)
        var ambient = rect;
        ambient.Inflate(8f, 8f);
        ambient.Offset(0, yOffset + 1f);
        int ambientAlpha = Math.Clamp((int)(alpha * 0.10f), 1, 255);
        using (var path = RRect(ambient, radius + 8f))
            g.FillPath(SketchRenderer.GetToolColorBrush(Color.FromArgb(ambientAlpha, 0, 0, 0)), path);

        var directional = rect;
        directional.Inflate(3f, 3f);
        directional.Offset(0, yOffset + 4f);
        int dirAlpha = Math.Clamp((int)(alpha * 0.22f), 1, 255);
        using (var path = RRect(directional, radius + 3f))
            g.FillPath(SketchRenderer.GetToolColorBrush(Color.FromArgb(dirAlpha, 0, 0, 0)), path);

        g.SmoothingMode = oldSmooth;
        g.CompositingMode = oldComp;
        g.CompositingQuality = oldCompQual;
        g.PixelOffsetMode = oldPix;
    }

    private static readonly Pen RulerShadowPen = new(Color.FromArgb(70, 0, 0, 0), 3f)
        { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
    private static Pen? _rulerLinePen;
    private static Pen? _rulerTickPen;
    private static SolidBrush? _rulerBgBrush;
    private static Pen? _rulerBorderPen;
    private static SolidBrush? _rulerFgBrush;
    private static Font? _rulerFont;
    private static int _rulerThemeKey;

    private static void EnsureRulerChrome()
    {
        var text = UiChrome.SurfaceTextPrimary;
        var pill = UiChrome.IsDark
            ? System.Drawing.Color.FromArgb(26, 27, 31)
            : System.Drawing.Color.FromArgb(252, 252, 252);
        var border = UiChrome.IsDark
            ? System.Drawing.Color.FromArgb(160, 0, 200, 215)
            : System.Drawing.Color.FromArgb(160, 0, 110, 205);
        int key = HashCode.Combine(text.ToArgb(), pill.ToArgb(), border.ToArgb());
        if (_rulerLinePen != null && _rulerThemeKey == key) return;

        _rulerLinePen?.Dispose();
        _rulerTickPen?.Dispose();
        _rulerBgBrush?.Dispose();
        _rulerBorderPen?.Dispose();
        _rulerFgBrush?.Dispose();

        _rulerLinePen = new Pen(text, 1.8f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        _rulerTickPen = new Pen(text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        _rulerBgBrush = new SolidBrush(pill);
        _rulerBorderPen = new Pen(border, 1.0f);
        _rulerFgBrush = new SolidBrush(text);
        _rulerFont ??= UiChrome.ChromeFont(9.5f);
        _rulerThemeKey = key;
    }

    private void PaintRuler(Graphics g, Point from, Point to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        float nx = 0, ny = 0;
        if (dist > 1) { nx = -dy / dist; ny = dx / dist; }
        const float tickHalf = 6f;

        EnsureRulerChrome();

        g.DrawLine(RulerShadowPen, from.X + 1, from.Y + 1, to.X + 1, to.Y + 1);
        g.DrawLine(_rulerLinePen!, from, to);
        g.DrawLine(_rulerTickPen!, from.X - nx * tickHalf, from.Y - ny * tickHalf,
                            from.X + nx * tickHalf, from.Y + ny * tickHalf);
        g.DrawLine(_rulerTickPen!, to.X - nx * tickHalf, to.Y - ny * tickHalf,
                            to.X + nx * tickHalf, to.Y + ny * tickHalf);

        string text = $"{(int)dist}px  \u00b7  {Math.Abs(dx):0} \u00d7 {Math.Abs(dy):0}  \u00b7  {angle:0.0}\u00b0";
        var sz = g.MeasureString(text, _rulerFont!);
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

        float w = sz.Width + 20;
        float h = sz.Height + 10;
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
                altLabel.Right <= ClientRectangle.Width - 4 &&
                altLabel.Bottom <= ClientRectangle.Height - 4)
            {
                label = altLabel;
            }
        }

        // Clamp to client bounds to ensure it never goes off-screen
        float lx = Math.Clamp(label.X, 4f, ClientRectangle.Width - w - 4f);
        float ly = Math.Clamp(label.Y, 4f, ClientRectangle.Height - h - 4f);
        label = new RectangleF(lx, ly, w, h);

        PaintShadow(g, label, 8f, 48, 1f);
        using var path = RRect(label, 8f);
        g.FillPath(_rulerBgBrush!, path);
        g.DrawPath(_rulerBorderPen!, path);
        g.DrawString(text, _rulerFont!, _rulerFgBrush!, label.X + 10, label.Y + 5);

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
        g.SmoothingMode = SmoothingMode.Default;
    }

    private Graphics GetBlurPreviewGraphics(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        if (_blurPreviewBitmap == null || _blurPreviewSize != size)
        {
            _blurPreviewGraphics?.Dispose();
            _blurPreviewBitmap?.Dispose();
            _blurPreviewBitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            _blurPreviewGraphics = Graphics.FromImage(_blurPreviewBitmap);
            _blurPreviewSize = size;
        }

        return _blurPreviewGraphics!;
    }

    private void PaintBlurRect(Graphics g, Rectangle rect)
    {
        if (rect.Width < 3 || rect.Height < 3) return;
        var clamped = Rectangle.Intersect(rect, new Rectangle(0, 0, _bmpW, _bmpH));
        if (clamped.Width < 1 || clamped.Height < 1) return;
        int blockSize = Math.Clamp(Math.Min(clamped.Width, clamped.Height) / 16, 3, 14);
        int sw = Math.Max(1, clamped.Width / blockSize);
        int sh = Math.Max(1, clamped.Height / blockSize);
        var small = GetBlurPreviewGraphics(new Size(sw, sh));
        small.Clear(Color.Transparent);
        small.InterpolationMode = InterpolationMode.HighQualityBilinear;
        small.PixelOffsetMode = PixelOffsetMode.Half;
        small.DrawImage(_screenshot, new Rectangle(0, 0, sw, sh), clamped, GraphicsUnit.Pixel);

        var state = g.Save();
        try
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_blurPreviewBitmap!, clamped);
        }
        finally
        {
            g.Restore(state);
        }
    }

    /// <summary>
    /// Draws premium crop-style L-corner handles and mid-edge bars for the Move tool.
    /// Mirrors <c>AnnotationCanvas.DrawMoveHandles</c> but operates in screen-space (no zoom).
    /// </summary>
    private static void DrawMoveHandles(Graphics g, Rectangle bounds, bool isSelected)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        const float penWidthThick  = 3.5f;
        const float penWidthShadow = 5.5f;
        const float len    = 12f;
        const float barLen = 14f;
        const float offset = 4f; // offset outside bounds

        var rect = new RectangleF(
            bounds.X - offset,
            bounds.Y - offset,
            bounds.Width  + 2 * offset,
            bounds.Height + 2 * offset
        );

        int accentAlpha = isSelected ? 255 : 120;
        int shadowAlpha = isSelected ? 100 : 50;
        int fillAlpha   = isSelected ? 15  : 10;
        int dashAlpha   = isSelected ? 180 : 80;

        var accentColor = Color.FromArgb(accentAlpha, 0, 255, 255);
        var shadowColor = Color.FromArgb(shadowAlpha, 0, 0, 0);

        // Subtle cyan fill
        using (var fillBrush = new SolidBrush(Color.FromArgb(fillAlpha, 0, 255, 255)))
            g.FillRectangle(fillBrush, rect);

        // Dashed outline
        using (var dashPen = new Pen(Color.FromArgb(dashAlpha, 0, 255, 255), 1.5f))
        {
            dashPen.DashStyle = DashStyle.Dash;
            dashPen.DashPattern = new[] { 4f, 3f };
            g.DrawRectangle(dashPen, rect.X, rect.Y, rect.Width, rect.Height);
        }

        using var thickPen  = new Pen(accentColor, penWidthThick)  { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var shadowPen = new Pen(shadowColor, penWidthShadow) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // L-shaped corners
        DrawL(g, shadowPen, rect.Left,  rect.Top,     len,  len);
        DrawL(g, thickPen,  rect.Left,  rect.Top,     len,  len);

        DrawL(g, shadowPen, rect.Right, rect.Top,    -len,  len);
        DrawL(g, thickPen,  rect.Right, rect.Top,    -len,  len);

        DrawL(g, shadowPen, rect.Left,  rect.Bottom,  len, -len);
        DrawL(g, thickPen,  rect.Left,  rect.Bottom,  len, -len);

        DrawL(g, shadowPen, rect.Right, rect.Bottom, -len, -len);
        DrawL(g, thickPen,  rect.Right, rect.Bottom, -len, -len);

        // Mid-edge bars
        float midX = rect.Left + rect.Width  / 2f;
        float midY = rect.Top  + rect.Height / 2f;

        g.DrawLine(shadowPen, midX - barLen / 2f, rect.Top,    midX + barLen / 2f, rect.Top);
        g.DrawLine(thickPen,  midX - barLen / 2f, rect.Top,    midX + barLen / 2f, rect.Top);

        g.DrawLine(shadowPen, midX - barLen / 2f, rect.Bottom, midX + barLen / 2f, rect.Bottom);
        g.DrawLine(thickPen,  midX - barLen / 2f, rect.Bottom, midX + barLen / 2f, rect.Bottom);

        g.DrawLine(shadowPen, rect.Left,  midY - barLen / 2f, rect.Left,  midY + barLen / 2f);
        g.DrawLine(thickPen,  rect.Left,  midY - barLen / 2f, rect.Left,  midY + barLen / 2f);

        g.DrawLine(shadowPen, rect.Right, midY - barLen / 2f, rect.Right, midY + barLen / 2f);
        g.DrawLine(thickPen,  rect.Right, midY - barLen / 2f, rect.Right, midY + barLen / 2f);
    }

    private static void DrawL(Graphics g, Pen pen, float x, float y, float dx, float dy)
    {
        g.DrawLine(pen, x, y, x + dx, y);
        g.DrawLine(pen, x, y, x, y + dy);
    }

}
