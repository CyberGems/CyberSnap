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

        // Dimming overlay — shown ONLY while dragging out / confirming a capture selection,
        // with the selected region excluded so it stands out against the dimmed surroundings.
        // Outside selection (idle, annotation tools like the ruler) there is NO dim: a full-screen
        // alpha blend every frame is what made live tool previews stutter. Tune alpha to taste.
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
                using (var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                {
                    g.FillRectangle(dimBrush, ClientRectangle);
                }
            }
            finally
            {
                g.Restore(state);
            }
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Live tool previews (active drawing in progress)
        PaintAnnotations(g);

        // Move tool: hover highlight
        if (IsDrawingOrMoveMode(_mode) && !IsDraggingAnyAnnotation() && _moveHoverIndex >= 0 && _moveHoverIndex < _undoStack.Count && _moveHoverIndex != _selectedAnnotationIndex && !_multiSelectedIndices.Contains(_moveHoverIndex))
        {
            var hovered = _undoStack[_moveHoverIndex];
            var hoverBounds = GetAnnotationBounds(hovered);
            DrawMoveHandles(g, hoverBounds, isSelected: false, moveOnly: !IsResizable(hovered));
        }

        // Move or drawing tool: draw selection highlight and handles.
        bool showSelectionFrame = IsDrawingOrMoveMode(_mode);
        if (showSelectionFrame && _multiSelectedIndices.Count > 1)
        {
            foreach (int idx in _multiSelectedIndices)
            {
                if (idx >= 0 && idx < _undoStack.Count)
                {
                    var ann = _undoStack[idx];
                    var bounds = GetAnnotationBounds(ann);
                    DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(ann));
                }
            }
        }
        else if (showSelectionFrame && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _undoStack.Count)
        {
            var selected = _selectPreviewAnnotation ?? _undoStack[_selectedAnnotationIndex];
            var bounds = GetAnnotationBounds(selected);
            DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(selected));
        }

        // Marquee selection box
        if (_isMarqueeSelecting)
        {
            var marqueeRect = NormRect(_marqueeStart, _marqueeEnd);
            if (marqueeRect.Width > 0 && marqueeRect.Height > 0)
            {
                using (var fillBrush = new SolidBrush(Color.FromArgb(30, 0, 120, 215)))
                using (var borderPen = new Pen(Color.FromArgb(180, 0, 120, 215), 1.5f))
                {
                    borderPen.DashStyle = DashStyle.Dash;
                    g.FillRectangle(fillBrush, marqueeRect);
                    g.DrawRectangle(borderPen, marqueeRect);
                }
            }
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
        {
            // The picker magnifier is its own layered window, so the overlay stays static — but the
            // instruction banner still lives on this form and must be painted before we bail out,
            // otherwise the Color Picker is the only tool with no banner.
            _banner?.Render(g);
            return;
        }

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
                SelectionFrameRenderer.DrawRectangle(g, _selectionRect, fill: false);
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
                bool confirmHover = _hoveredConfirmButton == 0;
                bool cancelHover = _hoveredConfirmButton == 1;
                float confirmPress = _pressedConfirmButton == 0 ? _confirmPressAmt : 0f;
                float cancelPress = _pressedConfirmButton == 1 ? _confirmPressAmt : 0f;
                bool shineOn = _confirmShineTimer.Enabled && !UI.Motion.Disabled;
                float confirmShine = shineOn ? _shinePhase[0] : -1f;
                float cancelShine = shineOn ? _shinePhase[1] : -1f;

                float confirmFactor = UI.Motion.Disabled
                    ? (_hoveredConfirmButton == 1 ? 0f : 1f)
                    : _shineMain[0];
                float cancelFactor = UI.Motion.Disabled
                    ? (_hoveredConfirmButton == 0 ? 0f : 1f)
                    : _shineMain[1];

                float confirmOpacity = UI.Motion.Disabled
                    ? (_hoveredConfirmButton == 1 ? 0.35f : 1.0f)
                    : (0.35f + 0.65f * _shineMain[0]);
                float cancelOpacity = UI.Motion.Disabled
                    ? (_hoveredConfirmButton == 0 ? 0.35f : 1.0f)
                    : (0.35f + 0.65f * _shineMain[1]);

                // Deactivated slate colors (solid cool gray, matches dark/light mode background style)
                Color deactColor = UiChrome.IsDark ? Color.FromArgb(74, 80, 86) : Color.FromArgb(170, 178, 186);

                var activeConfirmColor = Color.FromArgb(34, 197, 94);  // green-500
                var activeCancelColor = Color.FromArgb(239, 68, 68);   // red-500

                // Blend in 25% of the original active colors to keep a subtle, elegant hint of the green/red tone
                Color deactConfirmColor = InterpolateColor(deactColor, activeConfirmColor, 0.25f);
                Color deactCancelColor = InterpolateColor(deactColor, activeCancelColor, 0.25f);

                Color confirmColor = InterpolateColor(deactConfirmColor, activeConfirmColor, confirmFactor);
                Color cancelColor = InterpolateColor(deactCancelColor, activeCancelColor, cancelFactor);

                // 3D action buttons: green "Confirm" / red "Cancel", each with a white icon
                // badge, a hover-igniting glow, a click squash, and a glint traveling the
                // border so they stay readable over any busy capture background.
                DrawConfirmActionPill(g, confirmBtn, confirmColor,
                    LocalizationService.Translate("Confirm").ToUpperInvariant(), btnFont, confirmHover, isCheck: true, confirmPress, confirmShine, _shineMain[0], _shineDup[0], confirmOpacity);
                DrawConfirmActionPill(g, cancelBtn, cancelColor,
                    LocalizationService.Translate("Cancel").ToUpperInvariant(), btnFont, cancelHover, isCheck: false, cancelPress, cancelShine, _shineMain[1], _shineDup[1], cancelOpacity);
            }

            // Draw selection frame and handles ON TOP of buttons
            SelectionFrameRenderer.DrawRectangle(g, _confirmRect, fill: false);
            SelectionFrameRenderer.DrawConfirmHandles(g, GetConfirmHandleRects());
        }

        g.SmoothingMode = SmoothingMode.Default;

        // First-time capture instruction banner (renders on top of everything)
        _banner?.Render(g);

        if (ShowCrosshairGuides && _isSelecting && _lastCursorPos != Point.Empty)
        {
            UpdateCrosshairGuides(_lastCursorPos);
        }
    }

    /// <summary>
    /// Draws a 3D rounded-rectangle confirm/cancel action button: a solid colored face
    /// with a vertical gradient sitting on a darker extruded "side" block, a white circular
    /// icon badge (check or cross), a white uppercase label, a soft drop shadow, and an
    /// outer glow that flares on hover. <paramref name="pressAmt"/> (0→1→0) sinks the face
    /// onto its base for the click "squash" animation.
    /// </summary>
    private static void DrawConfirmActionPill(
        Graphics g, Rectangle rect, Color baseColor, string label, Font font,
        bool hover, bool isCheck, float pressAmt, float shinePhase, float shineMain, float shineDup,
        float opacity)
    {
        static Color Lighten(Color c, int amt) => Color.FromArgb(
            Math.Min(255, c.R + amt), Math.Min(255, c.G + amt), Math.Min(255, c.B + amt));
        static Color Darken(Color c, float f) => Color.FromArgb(
            (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));

        float corner = Math.Min(UiChrome.ScaleFloat(14f), rect.Height * 0.48f);
        float depth = UiChrome.ScaleFloat(5f);   // 3D extrusion thickness
        float press = depth * pressAmt;           // how far the face sinks while pressed

        // Face sinks downward onto its fixed base while pressed.
        var face = new RectangleF(rect.X, rect.Y + press, rect.Width, rect.Height);
        var baseRect = new RectangleF(rect.X, rect.Y + depth, rect.Width, rect.Height);

        Color fillTop = hover ? Lighten(baseColor, 42) : Lighten(baseColor, 20);
        Color fillBottom = hover ? baseColor : Darken(baseColor, 0.88f);
        Color sideColor = Darken(baseColor, 0.55f);   // the darker extruded side

        // ── Soft diffused outer glow — concentric low-alpha rings fade outward so it reads
        //    as a halo, not a hard border. Brightest at the edge, flares on hover. ──
        var glowBounds = RectangleF.FromLTRB(rect.X, face.Y, rect.Right, baseRect.Bottom);
        float glowSpread = UiChrome.ScaleFloat(hover ? 6f : 3f);
        int glowPeak = hover ? 60 : 18;
        const int glowSteps = 7;
        for (int i = glowSteps; i >= 1; i--)
        {
            float frac = i / (float)glowSteps;          // 1 (outermost) … ~0.14 (innermost)
            float inflate = glowSpread * frac;
            float falloff = 1f - frac;                  // stronger nearer the button edge
            int a = (int)(glowPeak * falloff * falloff * opacity);
            if (a <= 0) continue;
            using var glowPen = new Pen(Color.FromArgb(a, baseColor), glowSpread / glowSteps * 2.4f)
            {
                LineJoin = LineJoin.Round
            };
            using var glowPath = WindowsDockRenderer.RoundedRect(
                RectangleF.Inflate(glowBounds, inflate, inflate), corner + inflate);
            g.DrawPath(glowPen, glowPath);
        }

        // ── Soft drop shadow under the base ──
        using (var shadowPath = WindowsDockRenderer.RoundedRect(
            new RectangleF(rect.X, rect.Y + depth + 3f, rect.Width, rect.Height), corner))
        using (var shadowBrush = new SolidBrush(Color.FromArgb((int)((UiChrome.IsDark ? 110 : 55) * opacity), 0, 0, 0)))
            g.FillPath(shadowBrush, shadowPath);

        // ── 3D side: darker extruded block beneath the face ──
        using (var sidePath = WindowsDockRenderer.RoundedRect(baseRect, corner))
        using (var sideBrush = new SolidBrush(Color.FromArgb(255, sideColor)))
            g.FillPath(sideBrush, sidePath);

        // ── Face body (vertical gradient, fully opaque to block background) ──
        using (var facePath = WindowsDockRenderer.RoundedRect(face, corner))
        {
            using (var fill = new LinearGradientBrush(
                new RectangleF(face.X, face.Y - 1, face.Width, face.Height + 2),
                Color.FromArgb(255, fillTop),
                Color.FromArgb(255, fillBottom),
                LinearGradientMode.Vertical))
                g.FillPath(fill, facePath);

            // Glossy top highlight (fades while pressed)
            int glossA = (int)((hover ? 60 : 40) * (1f - 0.5f * pressAmt) * opacity);
            var glossRect = new RectangleF(face.X + 2, face.Y + 1.5f, face.Width - 4, face.Height * 0.46f);
            using (var glossPath = WindowsDockRenderer.RoundedRect(glossRect, Math.Min(corner, glossRect.Height / 2f)))
            using (var glossBrush = new SolidBrush(Color.FromArgb(glossA, 255, 255, 255)))
                g.FillPath(glossBrush, glossPath);
        }

        // ── Traveling glint(s) along the border (off when shinePhase < 0). The hovered
        //    button shows a second comet half a lap behind; the other button fades out. ──
        if (shinePhase >= 0f)
        {
            if (shineMain > 0.01f)
                DrawBorderShine(g, face, corner, shinePhase, Color.White, shineMain * opacity);
            if (shineDup > 0.01f)
                DrawBorderShine(g, face, corner, (shinePhase + 0.5f) % 1f, Color.White, shineDup * opacity);
        }

        // ── Label — white, uppercase, centered in the area right of the badge ──
        using (var sf = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None
        })
        using (var textBrush = new SolidBrush(Color.FromArgb((int)(255 * (0.4f + 0.6f * opacity)), Color.White)))
        {
            var textRect = RectangleF.FromLTRB(
                face.X + face.Height, face.Y, face.Right - face.Height * 0.18f, face.Bottom);
            g.DrawString(label, font, textBrush, textRect, sf);
        }

        // ── White circular icon badge on the left ──
        float pad = face.Height * 0.13f;
        float badgeD = face.Height - pad * 2f;
        float bx = face.X + (face.Height - badgeD) / 2f;
        float by = face.Y + (face.Height - badgeD) / 2f;

        using (var badgeShadow = new SolidBrush(Color.FromArgb((int)((UiChrome.IsDark ? 90 : 55) * opacity), 0, 0, 0)))
            g.FillEllipse(badgeShadow, bx, by + 1.3f, badgeD, badgeD);
        using (var badgeFill = new SolidBrush(Color.FromArgb((int)(255 * (0.4f + 0.6f * opacity)), Color.White)))
            g.FillEllipse(badgeFill, bx, by, badgeD, badgeD);

        // Icon (check or cross) painted in the pill color
        float stroke = Math.Max(2f, badgeD * 0.12f);
        using (var iconPen = new Pen(Color.FromArgb((int)(255 * (0.4f + 0.6f * opacity)), baseColor), stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        })
        {
            if (isCheck)
            {
                g.DrawLines(iconPen, new[]
                {
                    new PointF(bx + badgeD * 0.27f, by + badgeD * 0.53f),
                    new PointF(bx + badgeD * 0.43f, by + badgeD * 0.69f),
                    new PointF(bx + badgeD * 0.75f, by + badgeD * 0.33f),
                });
            }
            else
            {
                float m = badgeD * 0.31f;
                g.DrawLine(iconPen, bx + m, by + m, bx + badgeD - m, by + badgeD - m);
                g.DrawLine(iconPen, bx + badgeD - m, by + m, bx + m, by + badgeD - m);
            }
        }
    }

    /// <summary>
    /// Draws a soft "comet" glint that travels along a rounded-rectangle border. The border
    /// is flattened to a polyline; a bright head with a fading tail is positioned at
    /// <paramref name="phase"/> (0..1) around the perimeter. Cheap: ~18 short segments.
    /// </summary>
    private static void DrawBorderShine(Graphics g, RectangleF face, float corner, float phase, Color tint, float intensity)
    {
        using var path = WindowsDockRenderer.RoundedRect(face, corner);
        path.Flatten();
        var pts = path.PathPoints;
        int n = pts.Length;
        if (n < 2) return;

        var seg = new float[n];
        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            float d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            seg[i] = d;
            total += d;
        }
        if (total <= 0f) return;

        PointF PointAt(float dist)
        {
            dist = ((dist % total) + total) % total;
            for (int i = 0; i < n; i++)
            {
                if (dist <= seg[i] || i == n - 1)
                {
                    float t = seg[i] > 0 ? dist / seg[i] : 0f;
                    var a = pts[i];
                    var b = pts[(i + 1) % n];
                    return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                }
                dist -= seg[i];
            }
            return pts[0];
        }

        float head = phase * total;
        float tailLen = total * 0.16f;   // comet length as a fraction of the perimeter
        const int segments = 18;
        using var pen = new Pen(tint, UiChrome.ScaleFloat(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var prev = PointAt(head);
        for (int k = 1; k <= segments; k++)
        {
            float p01 = k / (float)segments;
            var cur = PointAt(head - tailLen * p01);
            int a = (int)(170 * intensity * (1f - p01) * (1f - p01)); // bright head → fading tail
            if (a > 0)
            {
                pen.Color = Color.FromArgb(a, tint);
                g.DrawLine(pen, prev, cur);
            }
            prev = cur;
        }
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

    private void InvalidateSelectionChromeThrottled(Rectangle oldSelection, Point oldCursor, Rectangle newSelection, Point newCursor)
    {
        // Always invalidate dirty areas — Invalidate is cheap (just marks regions).
        // Throttle only the synchronous Update() to avoid flooding the message pump
        // with forced repaints during fast drag.
        InvalidateSelectionChrome(oldSelection, oldCursor, newSelection, newCursor);

        if (_selectionPaintStopwatch.ElapsedMilliseconds < UiChrome.FrameIntervalMs)
        {
            _selectionPaintQueued = true;
            if (!_selectionPaintTimer.Enabled)
                _selectionPaintTimer.Start();
            return;
        }

        _selectionPaintStopwatch.Restart();
        _selectionPaintQueued = false;
        _selectionPaintTimer.Stop();
        Update();
    }

    private void FlushSelectionPaint()
    {
        if (!_selectionPaintQueued)
        {
            _selectionPaintTimer.Stop();
            return;
        }

        if (_selectionPaintStopwatch.ElapsedMilliseconds < UiChrome.FrameIntervalMs)
            return;

        _selectionPaintQueued = false;
        _selectionPaintTimer.Stop();
        _selectionPaintStopwatch.Restart();
        Update();
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

    private void PaintRuler(Graphics g, Point from, Point to)
    {
        RulerRenderer.Paint(g, from, to, ClientRectangle, UiChrome.IsDark);
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
    private static void DrawMoveHandles(Graphics g, Rectangle bounds, bool isSelected, bool moveOnly = false)
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

        // Move-only items (fixed-size badges) show just the dashed box — no resize handles,
        // which would otherwise imply a resize the annotation can't actually do.
        if (moveOnly) return;

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

    private static Color InterpolateColor(Color c1, Color c2, float t)
    {
        int r = (int)(c1.R + (c2.R - c1.R) * t);
        int g = (int)(c1.G + (c2.G - c1.G) * t);
        int b = (int)(c1.B + (c2.B - c1.B) * t);
        return Color.FromArgb(r, g, b);
    }

}
