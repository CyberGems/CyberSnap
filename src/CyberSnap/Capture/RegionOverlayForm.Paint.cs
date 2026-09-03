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
using CyberSnap.Models.Commands;
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

        var clip = e.ClipRectangle;
        if (clip.Width <= 0 || clip.Height <= 0)
            clip = ClientRectangle;

        var committed = GetCommittedAnnotationsBitmap();
        bool isOcr = _mode == CaptureMode.Ocr;
        bool isScan = _mode == CaptureMode.Scan;
        bool isSelectionMode = _mode is CaptureMode.Rectangle or CaptureMode.Center or CaptureMode.Ocr or CaptureMode.Scan or CaptureMode.Sticker or CaptureMode.Upscale or CaptureMode.ScrollCapture;

        // Capture-selection tools: outside the hole is desaturated + lightly dimmed; the hole
        // stays full color. Annotation tools keep a clear full-color view for live previews.
        if (ShouldDimOutsideSelection())
        {
            EnsureDesaturatedScreenshot();
            var hole = GetSelectionDimHole();
            var desat = _desaturatedScreenshot;

            g.CompositingMode = CompositingMode.SourceCopy;

            // 1) Desaturated base for everything outside the hole (all monitors).
            if (desat is not null)
            {
                var stateDesat = g.Save();
                try
                {
                    if (hole.Width > 2 && hole.Height > 2)
                        g.ExcludeClip(hole);
                    g.DrawImage(desat, clip, clip, GraphicsUnit.Pixel);
                }
                finally
                {
                    g.Restore(stateDesat);
                }
            }
            else
            {
                // Fallback if grayscale bake failed: full color then heavier dim below.
                g.DrawImage(committed, clip, clip, GraphicsUnit.Pixel);
            }

            // 2) Full-color hole (screenshot + committed annotations).
            if (hole.Width > 2 && hole.Height > 2)
            {
                var holeClip = Rectangle.Intersect(hole, clip);
                if (holeClip.Width > 0 && holeClip.Height > 0)
                    g.DrawImage(committed, holeClip, holeClip, GraphicsUnit.Pixel);
            }

            // 3) Soft dark veil outside the hole so the desaturated area still recedes.
            g.CompositingMode = CompositingMode.SourceOver;
            var stateDim = g.Save();
            try
            {
                if (hole.Width > 2 && hole.Height > 2)
                    g.ExcludeClip(hole);
                using var dimBrush = new SolidBrush(SelectionDimColor);
                g.FillRectangle(dimBrush, clip);
            }
            finally
            {
                g.Restore(stateDim);
            }
        }
        else
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(committed, clip, clip, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // If confirmation mode is active, repaint the chrome cluster's opaque base BEFORE any
        // annotation previews / cursor tooltips / move handles are drawn. Historically this ran
        // later (right before the pill draw) which made it paint OVER any shape preview, move
        // handle frame, or cursor tooltip whose pixels fell inside the +36 px invalidate pad —
        // the "invisible mask" bug near the bottom of the selection. Running it here makes the
        // live-annotation / handle / cursor layers always composite ON TOP of the chrome base.
        //
        // BUT even with this ordering, the SourceCopy repaint still erases whatever the live
        // preview painted *this frame* if the preview's bounding box intersects the chrome pad.
        // (Dragging an arrow horizontally near the dock produces a flicker where the preview
        // appears "cut out" until mouse-up.) Skip the repaint entirely during any active drawing
        // drag: the commit re-invalidates on mouse-up and the wrapper repaints cleanly then.
        if (_isConfirmingSelection)
        {
            bool frameManipulating = _confirmDocksHiddenForFrameManip
                || _isConfirmDragging
                || _confirmHandleDragIndex >= 0;
            if (!frameManipulating && !IsDraggingAnyAnnotation())
            {
                LayoutConfirmChromeRects();
                EnsureConfirmChromeOpaqueBase(g, clip, committed);
            }
        }

        // Live tool previews (active drawing in progress)
        PaintAnnotations(g);

        RenderCursorToolPreview(g);

        // Move tool: hover highlight (skip annotation currently being re-edited)
        if (ShowsAnnotationHoverChrome(_mode) && !IsDraggingAnyAnnotation()
            && _moveHoverIndex >= 0 && _moveHoverIndex < _undoStack.Count
            && _moveHoverIndex != _selectedAnnotationIndex
            && _moveHoverIndex != _renderSkipIndex
            && !_multiSelectedIndices.Contains(_moveHoverIndex))
        {
            var hovered = _undoStack[_moveHoverIndex];
            var hoverBounds = GetAnnotationBounds(hovered);
            DrawMoveHandles(g, hoverBounds, isSelected: false, moveOnly: !IsResizable(hovered), hovered);
        }

        // Move or drawing tool: draw selection highlight and handles.
        bool showSelectionFrame = IsDrawingOrMoveMode(_mode);
        if (showSelectionFrame && _multiSelectedIndices.Count > 1)
        {
            foreach (int idx in _multiSelectedIndices)
            {
                if (idx == _renderSkipIndex) continue;
                if (idx >= 0 && idx < _undoStack.Count)
                {
                    var ann = _undoStack[idx];
                    var bounds = GetAnnotationBounds(ann);
                    DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(ann), ann);
                }
            }
        }
        else if (showSelectionFrame
            && _selectedAnnotationIndex >= 0
            && _selectedAnnotationIndex < _undoStack.Count
            && _selectedAnnotationIndex != _renderSkipIndex)
        {
            var selected = _selectPreviewAnnotation ?? _undoStack[_selectedAnnotationIndex];
            var bounds = GetAnnotationBounds(selected);
            DrawMoveHandles(g, bounds, isSelected: true, moveOnly: !IsResizable(selected), selected);
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
                    !_selectionMonitorClientBounds.IsEmpty ? _selectionMonitorClientBounds : ClientRectangle);
                _lastSelectionRect = _selectionRect;
                break;

        }

        if (!_hasSelection)
            _lastSelectionRect = Rectangle.Empty;

        // Draw confirmation-mode handles and buttons
        if (_isConfirmingSelection)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // During live frame drag/resize both docks are hidden (destination + annotation).
            bool frameManipulating = _confirmDocksHiddenForFrameManip
                || _isConfirmDragging
                || _confirmHandleDragIndex >= 0;

            if (!frameManipulating)
            {
                LayoutConfirmChromeRects();
                // NOTE: EnsureConfirmChromeOpaqueBase is intentionally NOT called here anymore.
                // It already ran BEFORE the live-preview / handle / cursor sections so those
                // layers always land on top of the chrome base. Calling it here again would
                // re-occlude them under the inflated chrome area.

                DrawConfirmChromeWrapper(g);
                using (var btnFont = CreateConfirmButtonFont())
                {
                    bool shineOn = _confirmShineTimer.Enabled && !UI.Motion.Disabled;
                    int primaryIdx = IndexOfPrimaryConfirmAction();

                    for (int i = 0; i < _confirmChromeKinds.Length && i < _confirmChromeRects.Length; i++)
                    {
                        var kind = _confirmChromeKinds[i];
                        var btn = _confirmChromeRects[i];
                        if (btn.Width <= 0) continue;

                        bool hover = _hoveredConfirmButton == i;
                        bool disabled = IsConfirmChromeDisabled(kind);
                        float press = (!disabled && _pressedConfirmButton == i) ? _confirmPressAmt : 0f;

                        if (kind == ConfirmChromeKind.TogglePreview)
                        {
                            DrawConfirmTogglePreview(g, btn, hover, press, 1f);
                            continue;
                        }

                        // Per-button shine only while that button is hovered (never group dim/shine).
                        float shine = !disabled && shineOn && hover && i < ConfirmShineSlots ? _shinePhase[i] : -1f;
                        float main = 1f;
                        float dup = (!disabled && hover && i < ConfirmShineSlots) ? _shineDup[i] : 0f;
                        float factor = 1f;
                        float opacity = 1f;

                        if (disabled)
                        {
                            factor = 0f;
                            opacity = hover ? 0.4f : 0.28f;
                            shine = -1f;
                            press = 0f;
                        }

                        bool isPrimaryDest = !disabled && i == primaryIdx;
                        // The retained modes trigger swaps identity with the destination of the
                        // tool that locked the region (OCR confirm → OCR trigger + Image in strip).
                        // Icon/label/accent AND the selection highlight all follow the DISPLAYED
                        // identity, so the pill that LOOKS like the source tool reads as selected.
                        var effectiveKind = DisplayConfirmChromeKind(kind);
                        Color activeColor = ConfirmChromeAccent(effectiveKind, isPrimary: false);
                        Color deactColor = UiChrome.IsDark ? Color.FromArgb(74, 80, 86) : Color.FromArgb(170, 178, 186);
                        Color deactTint = InterpolateColor(deactColor, activeColor, 0.25f);
                        Color color = InterpolateColor(deactTint, activeColor, factor);

                        int iconType = kind switch
                        {
                            ConfirmChromeKind.Retry => 1, // custom circular arc + arrowhead
                            ConfirmChromeKind.Cancel => 3, // use signOut fluent icon
                            _ => 3 // fluent icon path
                        };
                        string? fluentIcon = kind == ConfirmChromeKind.Retry ? null : ConfirmChromeFluentIcon(effectiveKind);
                        string label = ConfirmChromeDrawLabel(effectiveKind);

                        DrawConfirmActionPill(g, btn, color, label, btnFont, hover && !disabled, iconType, press, shine, main, dup, opacity,
                            hasShine: !disabled && hover, fluentIconId: fluentIcon, accent: activeColor, isPrimary: isPrimaryDest, kind: effectiveKind);
                    }

                    Color sep = UiChrome.IsDark
                        ? Color.FromArgb(90, 255, 255, 255)
                        : Color.FromArgb(90, 0, 0, 0);
                    using (var sepBrush = new SolidBrush(sep))
                    {
                        if (!_confirmChromeSeparatorRect1.IsEmpty)
                            g.FillRectangle(sepBrush, _confirmChromeSeparatorRect1);
                        if (!_confirmChromeSeparatorRect2.IsEmpty)
                            g.FillRectangle(sepBrush, _confirmChromeSeparatorRect2);
                    }
                }
            }

            // Draw selection frame and handles ON TOP of buttons
            SelectionFrameRenderer.DrawRectangle(g, _confirmRect, fill: false);
            SelectionFrameRenderer.DrawConfirmHandles(g, GetConfirmHandleRects());

            // Permanent size chip + gear (frame drag handle cluster).
            // Mid-drag: paint the cached cluster (offset with the frame) — never re-layout
            // with avoidRects=null, which desyncs size vs gear and leaves ghosts on both sides.
            if (!frameManipulating)
                RefreshConfirmSizeReadoutRect();

            if (!_confirmSizeReadoutChipRect.IsEmpty || !_confirmSizeReadoutGripRect.IsEmpty)
            {
                SelectionSizeReadout.DrawConfirmDragPillCached(
                    g,
                    _confirmSizeReadoutChipRect,
                    _confirmSizeReadoutGripRect,
                    _confirmRect.Width,
                    _confirmRect.Height,
                    _readoutFont,
                    hovered: _hoveredConfirmSizeReadout || _isConfirmDragging);
            }

            if (!_confirmOptionsPillRect.IsEmpty)
            {
                bool gearActive = _toolbarContextMenu?.Visible == true;
                SelectionSizeReadout.DrawConfirmOptions(
                    g,
                    _confirmOptionsPillRect,
                    hovered: _hoveredConfirmOptionsPill,
                    active: gearActive);
            }

            if (!HasConfirmAnnotations() && !_centerMoveGripRect.IsEmpty)
            {
                DrawCenterMoveGrip(g);
            }
        }

        g.SmoothingMode = SmoothingMode.Default;

        // First-time capture instruction banner (renders on top of everything)
        _banner?.Render(g);

        if (ShowCrosshairGuides && _isSelecting && _lastCursorPos != Point.Empty)
        {
            UpdateCrosshairGuides(_lastCursorPos);
        }
    }

    /// <summary>Accent used for pill face tint, outer glow, outline, and traveling shine.</summary>
    private static Color ConfirmChromeAccent(ConfirmChromeKind kind, bool isPrimary)
    {
        _ = isPrimary;
        if (CyberSnap.UI.Theme.IsGray && kind != ConfirmChromeKind.Cancel)
            return UiChrome.AccentColor;

        return kind switch
        {
            ConfirmChromeKind.Retry => Color.FromArgb(160, 160, 160),      // neutral gray
            ConfirmChromeKind.Cancel => UiChrome.SurfaceDanger,             // reddish (danger)
            ConfirmChromeKind.Done => Color.FromArgb(34, 197, 94),          // green
            ConfirmChromeKind.TogglePreview => Color.FromArgb(0, 162, 255), // accent blue
            ConfirmChromeKind.ModeImage => Color.FromArgb(0, 162, 255),     // accent blue
            ConfirmChromeKind.ModeOcr => Color.FromArgb(139, 92, 246),       // violet
            ConfirmChromeKind.ModeVideo => Color.FromArgb(239, 68, 68),     // red
            ConfirmChromeKind.ModeGif => Color.FromArgb(249, 115, 22),       // orange
            ConfirmChromeKind.ModeScroll => Color.FromArgb(6, 182, 212),     // cyan
            ConfirmChromeKind.ModeQr => Color.FromArgb(245, 158, 11),        // amber
            _ => Color.FromArgb(0, 162, 255)
        };
    }

    /// <summary>
    /// Repaint the screenshot + dim base under the confirm dock (and a pad for glow/shadow)
    /// whenever the paint clip touches that region. Prevents low-alpha glow trails.
    /// </summary>
    private void EnsureConfirmChromeOpaqueBase(Graphics g, Rectangle clip, Bitmap committed)
    {
        var union = UnionConfirmChromeRects();
        if (union.IsEmpty)
            return;

        // Always use the full pad — this paint now runs BEFORE any annotation preview, cursor
        // tooltip, or move handle, so it can safely claim its full invalidation area without
        // overpainting live content. The earlier "skip pad while dragging" trick became
        // unnecessary once we reordered this pass ahead of the preview layers.
        var area = InflateForRepaint(union, ConfirmChromeInvalidatePad);
        if (!area.IntersectsWith(clip))
            return;

        area.Intersect(ClientRectangle);
        if (area.Width <= 0 || area.Height <= 0)
            return;

        var state = g.Save();
        try
        {
            g.SetClip(area, CombineMode.Replace);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.SmoothingMode = SmoothingMode.None;

            var hole = GetSelectionDimHole();
            if (ShouldDimOutsideSelection())
            {
                EnsureDesaturatedScreenshot();
                var desat = _desaturatedScreenshot;
                if (desat is not null)
                    g.DrawImage(desat, area, area, GraphicsUnit.Pixel);
                else
                    g.DrawImage(committed, area, area, GraphicsUnit.Pixel);

                // Restore full-color hole where it overlaps the chrome area.
                if (hole.Width > 2 && hole.Height > 2)
                {
                    var holeClip = Rectangle.Intersect(hole, area);
                    if (holeClip.Width > 0 && holeClip.Height > 0)
                        g.DrawImage(committed, holeClip, holeClip, GraphicsUnit.Pixel);
                }

                g.CompositingMode = CompositingMode.SourceOver;
                if (hole.Width > 2 && hole.Height > 2)
                    g.ExcludeClip(hole);
                using var dimBrush = new SolidBrush(SelectionDimColor);
                g.FillRectangle(dimBrush, area);
            }
            else
            {
                g.DrawImage(committed, area, area, GraphicsUnit.Pixel);
            }
        }
        finally
        {
            g.Restore(state);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingMode = CompositingMode.SourceOver;
        }
    }

    /// <summary>Opaque dock behind the confirm pills — same chrome language as the capture toolbar.</summary>
    private void DrawConfirmChromeWrapper(Graphics g)
    {
        if (_confirmChromeWrapperRect.IsEmpty)
            return;

        float corner = UiChrome.ScaledToolbarCornerRadius;
        var bounds = _confirmChromeWrapperRect;
        var face = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var rect = Rectangle.Round(face);

        WindowsDockRenderer.PaintShadow(g, rect, corner);

        using var path = WindowsDockRenderer.RoundedRect(face, corner);

        // Fully opaque face so partial redraws never composite over old frames.
        using (var brush = new SolidBrush(UiChrome.SurfaceTier1))
            g.FillPath(brush, path);

        // Premium accent outline (matches PaintToolbar).
        using (var pen = new Pen(Color.FromArgb(UiChrome.IsDark ? 80 : 50, UiChrome.AccentColor), 1f))
            g.DrawPath(pen, path);

        // Neon accent along the bottom edge — same treatment as a bottom-docked capture bar.
        using (var edgePath = BuildDockedEdgePath(rect, corner, CaptureDockSide.Bottom))
        {
            if (edgePath != null)
            {
                var accent = UiChrome.AccentColor;
                var fade = Color.FromArgb(0, accent);
                PointF p0 = new PointF(rect.X, rect.Y);
                PointF p1 = new PointF(rect.Right, rect.Y);
                using (var brush = new LinearGradientBrush(p0, p1, accent, accent))
                {
                    brush.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { fade, accent, accent, fade },
                        Positions = new[] { 0f, 0.10f, 0.90f, 1f },
                    };
                    using (var pen = new Pen(brush, 2f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                        LineJoin = LineJoin.Round,
                    })
                        g.DrawPath(pen, edgePath);
                }
            }
        }

        DrawDockWrapperShine(g, face, corner, _dockShinePhase);

        if (!_confirmGripRect.IsEmpty)
        {
            DrawToolbarGripDots(g, _confirmGripRect, UiChrome.AccentColor);
        }
    }

    /// <summary>
    /// Draws a 3D rounded-rectangle confirm/cancel action button: a solid colored face
    /// with a vertical gradient sitting on a darker extruded "side" block, a white circular
    /// icon badge (check or cross), a white uppercase label, a soft drop shadow, and an
    /// outer glow that flares on hover. <paramref name="pressAmt"/> (0→1→0) sinks the face
    /// onto its base for the click "squash" animation.
    /// </summary>
    private void DrawConfirmActionPill(
        Graphics g, Rectangle rect, Color baseColor, string label, Font font,
        bool hover, int iconType, float pressAmt, float shinePhase, float shineMain, float shineDup,
        float opacity, bool hasShine, string? fluentIconId = null, Color? accent = null,
        bool isPrimary = false, ConfirmChromeKind kind = ConfirmChromeKind.Done)
    {
        float hoverCorner = UiChrome.ScaleFloat(5f); // match annotation toolbar corner radius
        Color accentColor = accent ?? baseColor;

        // Face sinks downward onto its fixed base while pressed.
        var face = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);

        // Background — reuse the exact same state visual as the capture/annotation toolbar:
        // hover gets a subtle flat fill; selected gets a stronger accent fill + inset accent ring.
        // Idle paints nothing.
        bool isSelectedMode = kind == SelectedConfirmModeKind();
        WindowsDockRenderer.PaintButton(g, face, active: isSelectedMode, hovered: hover, radius: hoverCorner, accent: accentColor);

        if (hasShine && shinePhase >= 0f && !UI.Motion.Disabled)
        {
            var shineCore = Color.FromArgb(
                Math.Min(255, accentColor.R + 80),
                Math.Min(255, accentColor.G + 80),
                Math.Min(255, accentColor.B + 80));
            DrawBorderShine(g, face, hoverCorner, shinePhase, accentColor, shineCore,
                intensity: shineMain * 0.45f, thicknessScale: 0.32f);
        }

        // ── Label + icon laid out as a single centered group ──
        bool useFluent = !string.IsNullOrEmpty(fluentIconId);
        // Done places its check to the RIGHT of the label and noticeably larger, so the
        // primary action glyph balances the Cancel X next to it (icon-then-label reads small).
        bool iconOnRight = kind == ConfirmChromeKind.Done && !string.IsNullOrEmpty(label);
        float iconSize;
        float bx, by;
        if (string.IsNullOrEmpty(label))
        {
            iconSize = face.Height * (useFluent ? 0.82f : 0.65f);
            bx = face.X + (face.Width - iconSize) / 2f;
            by = face.Y + (face.Height - iconSize) / 2f;
        }
        else
        {
            iconSize = face.Height * (iconOnRight ? DoneLabelIconFrac : 0.58f);
            float gap = face.Height * (iconOnRight ? DoneLabelGapFrac : 0.22f);

            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
                Trimming = StringTrimming.None
            };
            SizeF textSize = g.MeasureString(label, font, new SizeF(10000f, face.Height), sf);

            float vNudge;
            float textX;
            if (iconOnRight)
            {
                // Done: label + larger check as a single centered group, equal left/right padding.
                float padX = face.Height * DoneLabelPadXFrac;
                float groupW = textSize.Width + gap + iconSize;
                textX = face.X + (face.Width - groupW) / 2f;
                bx = textX + textSize.Width + gap;
                by = face.Y + (face.Height - iconSize) / 2f;

                var fam0 = font.FontFamily;
                float em0 = fam0.GetEmHeight(font.Style);
                float ascent0 = fam0.GetCellAscent(font.Style);
                float descent0 = fam0.GetCellDescent(font.Style);
                float capHeight0 = em0 * 0.72f; // Segoe UI cap height ≈ 0.72 em
                float emPx0 = font.SizeInPoints * g.DpiY / 72f;
                vNudge = ((ascent0 - capHeight0 - descent0) / em0) * emPx0 / 2f;
            }
            else
            {
                float iconVisualW = useFluent ? iconSize : iconSize * 0.84f;
                float groupW = iconVisualW + gap + textSize.Width;
                float startX = face.X + (face.Width - groupW) / 2f;

                var fam = font.FontFamily;
                float em = fam.GetEmHeight(font.Style);
                float ascent = fam.GetCellAscent(font.Style);
                float descent = fam.GetCellDescent(font.Style);
                float capHeight = em * 0.72f; // Segoe UI cap height ≈ 0.72 em
                float emPx = font.SizeInPoints * g.DpiY / 72f;
                vNudge = ((ascent - capHeight - descent) / em) * emPx / 2f;

                bx = startX;
                by = face.Y + (face.Height - iconSize) / 2f;
                textX = startX + iconVisualW + gap;
            }

            Color baseTextColor = (accentColor.ToArgb() == UiChrome.SurfaceDanger.ToArgb())
                ? UiChrome.SurfaceDanger
                : UiChrome.SurfaceTextPrimary;

            using (var textBrush = new SolidBrush(Color.FromArgb((int)(255 * (0.25f + 0.75f * opacity)), baseTextColor)))
            {
                var textRect = new RectangleF(textX, face.Y - vNudge, textSize.Width + 1f, face.Height);
                g.DrawString(label, font, textBrush, textRect, sf);
            }
        }

        float stroke = string.IsNullOrEmpty(label) ? UiChrome.ScaleFloat(1.5f) : UiChrome.ScaleFloat(2f);
        Color baseIconColor = (accentColor.ToArgb() == UiChrome.SurfaceDanger.ToArgb())
            ? UiChrome.SurfaceDanger
            : (kind == ConfirmChromeKind.Done && !CyberSnap.UI.Theme.IsGray
                ? Color.FromArgb(34, 197, 94)
                : UiChrome.SurfaceTextPrimary);

        float baseAlphaFactor = 0.25f + 0.75f * opacity;
        Color iconColor = Color.FromArgb((int)(255 * baseAlphaFactor), baseIconColor);
        // Selected: full-opacity so the accent tint isn't washed out by the shared baseAlpha.
        Color pillIconColor = isSelectedMode ? accentColor : iconColor;

        if (useFluent)
        {
            // Selected mode swaps the outline glyph for its filled variant and tints it with the
            // accent color — the same active cue the capture/annotation toolbar applies.
            FluentIcons.DrawIcon(g, fluentIconId!, new RectangleF(bx, by, iconSize, iconSize), pillIconColor, iconInset: 0f, active: isSelectedMode);
            return;
        }

        // Draw shadow first for contrast
        using (var shadowPen = new Pen(Color.FromArgb((int)(120 * opacity), 0, 0, 0), stroke + 1f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        })
        {
            if (iconType == 0) // Check mark
            {
                g.DrawLines(shadowPen, new[]
                {
                    new PointF(bx + iconSize * 0.2f, by + iconSize * 0.5f + 1f),
                    new PointF(bx + iconSize * 0.42f, by + iconSize * 0.72f + 1f),
                    new PointF(bx + iconSize * 0.8f, by + iconSize * 0.3f + 1f),
                });
            }
            else if (iconType == 1) // Retry arrow (circular arrow + filled arrowhead)
            {
                float margin = iconSize * 0.1f;
                var arcRect = new RectangleF(bx + margin, by + margin + 1f, iconSize - margin * 2f, iconSize - margin * 2f);
                g.DrawArc(shadowPen, arcRect, -40f, 280f);
                FillRetryArrowHead(g, bx, by + 1f, iconSize, Color.FromArgb((int)(120 * opacity), 0, 0, 0));
            }
            else // Close / X icon
            {
                float margin = iconSize * 0.15f;
                g.DrawLine(shadowPen, bx + margin, by + margin + 1f, bx + iconSize - margin, by + iconSize - margin + 1f);
                g.DrawLine(shadowPen, bx + iconSize - margin, by + margin + 1f, bx + margin, by + iconSize - margin + 1f);
            }
        }

        // Draw icon main stroke
        using (var iconPen = new Pen(iconColor, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        })
        {
            if (iconType == 0) // Check mark
            {
                g.DrawLines(iconPen, new[]
                {
                    new PointF(bx + iconSize * 0.2f, by + iconSize * 0.5f),
                    new PointF(bx + iconSize * 0.42f, by + iconSize * 0.72f),
                    new PointF(bx + iconSize * 0.8f, by + iconSize * 0.3f),
                });
            }
            else if (iconType == 1) // Retry arrow (circular arrow + filled arrowhead)
            {
                float margin = iconSize * 0.1f;
                var arcRect = new RectangleF(bx + margin, by + margin, iconSize - margin * 2f, iconSize - margin * 2f);
                g.DrawArc(iconPen, arcRect, -40f, 280f);
                FillRetryArrowHead(g, bx, by, iconSize, iconColor);
            }
            else // Close / X icon
            {
                float margin = iconSize * 0.15f;
                g.DrawLine(iconPen, bx + margin, by + margin, bx + iconSize - margin, by + iconSize - margin);
                g.DrawLine(iconPen, bx + iconSize - margin, by + margin, bx + margin, by + iconSize - margin);
            }
        }
    }

    /// <summary>
    /// Fills the solid triangular arrowhead of the "retry" circular arrow. The head sits at the
    /// open end of the arc (top-right) and points back against the sweep so it reads as a rotation.
    /// </summary>
    private static void FillRetryArrowHead(Graphics g, float bx, float by, float iconSize, Color color)
    {
        float margin = iconSize * 0.1f;
        float r = (iconSize - margin * 2f) / 2f;
        float cx = bx + iconSize / 2f;
        float cy = by + iconSize / 2f;

        const float headDeg = -40f; // matches the arc's start angle
        float rad = headDeg * (float)Math.PI / 180f;
        float ax = cx + r * MathF.Cos(rad);
        float ay = cy + r * MathF.Sin(rad);

        // Backward tangent (against the clockwise sweep) so the head points "up" into the rotation.
        float tx = MathF.Sin(rad);
        float ty = -MathF.Cos(rad);
        float len = iconSize * 0.24f;
        float wid = iconSize * 0.17f;

        var tip = new PointF(ax + tx * len, ay + ty * len);
        float backX = ax - tx * len * 0.25f;
        float backY = ay - ty * len * 0.25f;
        float nx = -ty, ny = tx; // normal
        var c1 = new PointF(backX + nx * wid, backY + ny * wid);
        var c2 = new PointF(backX - nx * wid, backY - ny * wid);

        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[] { tip, c1, c2 });
    }

    /// <summary>
    /// Traveling neon beam along a dock wrapper border: a short luminous segment
    /// that loops the rounded perimeter.
    /// </summary>
    private static void DrawDockWrapperShine(Graphics g, RectangleF face, float corner, float phase)
    {
        if (UI.Motion.Disabled)
            return;

        var accent = UiChrome.AccentColor;
        var core = Color.FromArgb(
            Math.Min(255, accent.R + 80),
            Math.Min(255, accent.G + 80),
            Math.Min(255, accent.B + 80));
        DrawBorderShine(g, face, corner, phase, accent, core, intensity: 1f);
    }

    /// <summary>
    /// Draws a soft glowing "border beam" (shine) that travels along a rounded-rectangle border.
    /// Delegates to <see cref="WindowsDockRenderer.PaintBorderShine"/> so capture, confirm, and
    /// scrolling-capture chrome share one perimeter sampler (no GDI+ flatten seams).
    /// </summary>
    private static void DrawBorderShine(Graphics g, RectangleF face, float corner, float phase, Color glowColor, Color coreColor, float intensity, float thicknessScale = 1f)
    {
        WindowsDockRenderer.PaintBorderShine(g, face, corner, phase, glowColor, coreColor, intensity, thicknessScale);
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
        // Dim tracks the selection hole; re-dim / un-dim both rects (incl. other monitors).
        if (ShouldDimOutsideSelection())
        {
            var dimDirty = Rectangle.Union(
                oldSelection.Width > 2 ? InflateForRepaint(oldSelection, 20) : Rectangle.Empty,
                newSelection.Width > 2 ? InflateForRepaint(newSelection, 20) : Rectangle.Empty);

            // Tiny→visible or visible→empty needs a broader pass so the dim veil stays coherent.
            if (oldSelection.Width <= 2 || newSelection.Width <= 2
                || oldSelection.IsEmpty || newSelection.IsEmpty)
            {
                Invalidate();
            }
            else if (!dimDirty.IsEmpty)
            {
                Invalidate(dimDirty);
            }
            else
            {
                Invalidate();
            }
        }
        else
        {
            InvalidateSelectionChromePart(oldSelection, oldCursor);
            InvalidateSelectionChromePart(newSelection, newCursor);
            return;
        }

        InvalidateSelectionReadout(oldCursor, oldSelection);
        InvalidateSelectionReadout(newCursor, newSelection);
    }

    private void InvalidateSelectionChromePart(Rectangle selection, Point cursor)
    {
        if (selection.Width <= 2 || selection.Height <= 2)
            return;

        var selectionDirty = selection;
        selectionDirty.Inflate(16, 16);
        Invalidate(selectionDirty);
        InvalidateSelectionReadout(cursor, selection);
    }

    private void InvalidateSelectionReadout(Point cursor, Rectangle selection)
    {
        if (selection.Width <= 2 || selection.Height <= 2)
            return;

        // Confirm mode ignores the live cursor — pills are anchored to the locked region.
        var anchor = _isConfirmingSelection ? GetReadoutCursorPoint() : cursor;
        var readoutBounds = SelectionSizeReadout.GetBounds(
            anchor,
            selection,
            _readoutFont,
            !_selectionMonitorClientBounds.IsEmpty ? _selectionMonitorClientBounds : ClientRectangle,
            avoidRects: GetConfirmReadoutAvoidRects());
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

    private Bitmap GetBlurPreviewBitmap(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        if (_blurPreviewBitmap == null || _blurPreviewSize != size)
        {
            _blurPreviewBitmap?.Dispose();
            _blurPreviewBitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            _blurPreviewSize = size;
        }

        return _blurPreviewBitmap;
    }

    private void PaintBlurRect(Graphics g, Rectangle rect)
    {
        if (rect.Width < 3 || rect.Height < 3) return;
        var clamped = Rectangle.Intersect(rect, new Rectangle(0, 0, _bmpW, _bmpH));
        if (clamped.Width < 1 || clamped.Height < 1) return;
        int blockSize = 10;
        int sw = Math.Max(2, clamped.Width / blockSize);
        int sh = Math.Max(2, clamped.Height / blockSize);
        var bmp = GetBlurPreviewBitmap(new Size(sw, sh));
        using (var small = Graphics.FromImage(bmp))
        {
            small.Clear(Color.Transparent);
            small.InterpolationMode = InterpolationMode.HighQualityBilinear;
            small.PixelOffsetMode = PixelOffsetMode.Half;
            small.DrawImage(_screenshot, new Rectangle(0, 0, sw, sh), clamped, GraphicsUnit.Pixel);
        }

        var state = g.Save();
        try
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, clamped);
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
    private void DrawMoveHandles(Graphics g, Rectangle bounds, bool isSelected, bool moveOnly = false, Annotation? source = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var accentColor = Color.FromArgb(
            isSelected ? 200 : 90,
            UiChrome.SelectionBracketColor.R,
            UiChrome.SelectionBracketColor.G,
            UiChrome.SelectionBracketColor.B);

        if (isSelected && _isRotateMode && source is not null && AnnotationTransforms.CanRotate(source)
            && _multiSelectedIndices.Count <= 1)
        {
            var corners = AnnotationTransforms.GetRotateHandleCorners(source, 0f);
            WindowsHandleRenderer.PaintRotateMode(g, corners, accentColor, 1f);
            return;
        }

        float offset = isSelected ? AnnotationSelectionHandleGapPx : 3f;
        var rect = new RectangleF(
            bounds.X - offset,
            bounds.Y - offset,
            bounds.Width  + 2 * offset,
            bounds.Height + 2 * offset
        );

        int fillAlpha   = isSelected ? 0   : 7;
        int dashAlpha   = isSelected ? 40 : 28;

        // Subtle accent fill (hover only)
        if (fillAlpha > 0)
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(fillAlpha, UiChrome.SelectionBracketColor));
            g.FillRectangle(fillBrush, rect);
        }

        // Dashed outline
        using (var dashPen = new Pen(Color.FromArgb(dashAlpha, UiChrome.SelectionBracketColor), isSelected ? 0.75f : 0.9f))
        {
            dashPen.DashStyle = DashStyle.Dash;
            dashPen.DashPattern = new[] { 4f, 3f };
            g.DrawRectangle(dashPen, rect.X, rect.Y, rect.Width, rect.Height);
        }

        float midX = rect.Left + rect.Width  / 2f;
        float midY = rect.Top  + rect.Height / 2f;

        if (isSelected && !moveOnly)
        {
            // Figma-style handles: crisp white squares with 1.5px accent border and subtle drop shadow
            float hSize = (bounds.Width < 28 || bounds.Height < 28) ? 6f : 8f;
            float hHalf = hSize / 2f;

            using var shadowBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
            using var whiteBrush  = new SolidBrush(Color.White);
            using var handlePen   = new Pen(accentColor, 1.5f);

            void DrawHandle(float cx, float cy)
            {
                var hr = new RectangleF(cx - hHalf, cy - hHalf, hSize, hSize);
                g.FillRectangle(shadowBrush, hr.X + 0.8f, hr.Y + 1.2f, hr.Width, hr.Height);
                g.FillRectangle(whiteBrush, hr);
                g.DrawRectangle(handlePen, hr.X, hr.Y, hr.Width, hr.Height);
            }

            DrawHandle(rect.Left,  rect.Top);
            DrawHandle(rect.Right, rect.Top);
            DrawHandle(rect.Left,  rect.Bottom);
            DrawHandle(rect.Right, rect.Bottom);

            if (bounds.Width >= 56)
            {
                DrawHandle(midX, rect.Top);
                DrawHandle(midX, rect.Bottom);
            }
            if (bounds.Height >= 56)
            {
                DrawHandle(rect.Left,  midY);
                DrawHandle(rect.Right, midY);
            }
        }

        if (isSelected && WindowsHandleRenderer.FitsCenterPlus(bounds.Width, bounds.Height))
            WindowsHandleRenderer.PaintCenterPlus(g, new PointF(midX, midY), accentColor, 1f);
    }

    private static Color InterpolateColor(Color c1, Color c2, float t)
    {
        int r = (int)(c1.R + (c2.R - c1.R) * t);
        int g = (int)(c1.G + (c2.G - c1.G) * t);
        int b = (int)(c1.B + (c2.B - c1.B) * t);
        return Color.FromArgb(r, g, b);
    }

    private void RenderCursorToolPreview(Graphics g)
    {
        // Gate on the RAW cursor (helper below): _lastCursorPos is clamped into _confirmRect, so it
        // always reads "inside" the region and would pin the chip to the border when the pointer is
        // outside. The chip must hide the moment the real cursor leaves the drawable area.
        if (!ShouldPaintCursorToolChip(_chipGateCursor)) return;

        bool hasStroke = ToolChipHasStroke(_mode);
        var color = _toolColor;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            const int glyphSize = 22;
            float glyphStroke = Math.Clamp(_strokeWidth * 0.5f, 1.8f, 4.5f);
            string label = hasStroke ? string.Format(LocalizationService.Translate("Thickness {0}"), (int)Math.Round(_strokeWidth)) : string.Empty;

            using var font = UiChrome.ChromeFont(8.5f, FontStyle.Regular);
            var (chipW, chipH, textSize) = MeasureCursorChipSize(label, font, glyphSize);

            const int padX = 7, gap = 6;
            const int off = 18;
            var chipRect = PlaceCursorChipRect(_lastCursorPos, chipW, chipH, off);
            if (chipRect.Width <= 0 || chipRect.Height <= 0)
                return;

            using (var shadowPath = RRect(new RectangleF(chipRect.X + 1, chipRect.Y + 2, chipRect.Width, chipRect.Height), 6))
            using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillPath(shadow, shadowPath);

            using (var path = RRect(chipRect, 6))
            using (var bg = new SolidBrush(Color.FromArgb(235, UiChrome.SurfaceTier2)))
            using (var border = new Pen(Color.FromArgb(120, UiChrome.AccentColor), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            var glyphBox = new RectangleF(chipRect.X + padX, chipRect.Y + (chipRect.Height - glyphSize) / 2f, glyphSize, glyphSize);
            DrawToolGlyph(g, _mode, glyphBox, color, glyphStroke);

            if (label.Length > 0)
            {
                float tx = glyphBox.Right + gap;
                float ty = chipRect.Y + (chipRect.Height - textSize.Height) / 2f;
                using var tb = new SolidBrush(UiChrome.SurfaceTextSecondary);
                g.DrawString(label, font, tb, tx, ty);
            }
        }
        finally
        {
            g.SmoothingMode = oldSmoothing;
        }
    }

    private bool ShouldPaintCursorToolChip(Point cursor)
    {
        if (_isSelecting || IsDraggingAnyAnnotation() || _isSelectDragging || _isSelectResizing || _isRotating) return false;
        if (_isTyping) return false;
        if (!ToolShowsCursorChip(_mode)) return false;
        if (cursor.IsEmpty || IsPointInOverlayUi(cursor)) return false;
        if (_moveHoverIndex >= 0 || _selectedAnnotationIndex >= 0 || _eraserHoverIndex >= 0) return false;

        if (_isConfirmingSelection)
        {
            if (!ToolDef.IsAnnotationTool(_mode)) return false;
            if (!_confirmRect.Contains(cursor)) return false;
            if (HitTestConfirmHandle(cursor) >= 0 || HitTestConfirmButton(cursor) >= 0)
                return false;
        }

        return true;
    }

    private static (int width, int height, SizeF textSize) MeasureCursorChipSize(string label, Font font, int glyphSize = 22)
    {
        const int padX = 7, padY = 5, gap = 6;
        SizeF textSize = SizeF.Empty;
        if (label.Length > 0)
        {
            var measured = TextRenderer.MeasureText(
                label,
                font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            textSize = measured;
        }

        float contentH = Math.Max(glyphSize, textSize.Height);
        int chipW = padX + glyphSize
            + (label.Length > 0 ? gap + (int)Math.Ceiling(textSize.Width) : 0) + padX;
        int chipH = padY + (int)Math.Ceiling(contentH) + padY;
        return (chipW, chipH, textSize);
    }

    private Rectangle GetCursorChipRect(Point cursor)
    {
        if (!ToolShowsCursorChip(_mode) || cursor.IsEmpty)
            return Rectangle.Empty;

        bool hasStroke = ToolChipHasStroke(_mode);
        string label = hasStroke ? string.Format(LocalizationService.Translate("Thickness {0}"), (int)Math.Round(_strokeWidth)) : string.Empty;

        using var font = UiChrome.ChromeFont(8.5f, FontStyle.Regular);
        var (chipW, chipH, _) = MeasureCursorChipSize(label, font);

        const int off = 18;
        var placed = PlaceCursorChipRect(cursor, chipW, chipH, off);
        if (placed.Width <= 0)
            return Rectangle.Empty;

        // Inflate covers shadow + anti-alias fringe so invalidation clears the previous chip fully.
        return Rectangle.Inflate(placed, 6, 6);
    }

    /// <summary>
    /// Positions the tool-cursor chip near the pointer, flipping/clamping so it stays on-screen
    /// and — in confirm mode — inside the locked selection (no spill into the dimmed outside).
    /// </summary>
    private Rectangle PlaceCursorChipRect(Point cursor, int chipW, int chipH, int off)
    {
        var clamp = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        if (_isConfirmingSelection && _confirmRect.Width > 2 && _confirmRect.Height > 2)
            clamp = Rectangle.Intersect(clamp, _confirmRect);

        if (clamp.Width <= 0 || clamp.Height <= 0)
            return Rectangle.Empty;

        int x = cursor.X + off;
        int y = cursor.Y + off;
        if (x + chipW > clamp.Right) x = cursor.X - off - chipW;
        if (y + chipH > clamp.Bottom) y = cursor.Y - off - chipH;

        x = Math.Clamp(x, clamp.Left, Math.Max(clamp.Left, clamp.Right - chipW));
        y = Math.Clamp(y, clamp.Top, Math.Max(clamp.Top, clamp.Bottom - chipH));
        return new Rectangle(x, y, chipW, chipH);
    }

    private static bool ToolShowsCursorChip(CaptureMode mode) => mode is
        CaptureMode.Draw or CaptureMode.Arrow or CaptureMode.CurvedArrow or
        CaptureMode.Line or CaptureMode.RectShape or CaptureMode.CircleShape or CaptureMode.Highlight;

    private static bool ToolChipHasStroke(CaptureMode mode) => mode is
        CaptureMode.Draw or CaptureMode.Arrow or CaptureMode.CurvedArrow or
        CaptureMode.Line or CaptureMode.RectShape or CaptureMode.CircleShape;

    private static void DrawToolGlyph(Graphics g, CaptureMode tool, RectangleF box, Color color, float stroke)
    {
        const float m = 3.5f;
        float l = box.Left + m, t = box.Top + m, r = box.Right - m, b = box.Bottom - m;

        using var pen = new Pen(color, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (tool)
        {
            case CaptureMode.Line:
                g.DrawLine(pen, l, b, r, t);
                break;

            case CaptureMode.Arrow:
                g.DrawLine(pen, l, b, r, t);
                DrawGlyphArrowhead(g, pen, new PointF(l, b), new PointF(r, t));
                break;

            case CaptureMode.CurvedArrow:
            {
                var p0 = new PointF(l, b);
                var p1 = new PointF(l + (r - l) * 0.1f, t + (b - t) * 0.35f);
                var p2 = new PointF(r, t);
                g.DrawCurve(pen, new[] { p0, p1, p2 }, 0.6f);
                DrawGlyphArrowhead(g, pen, p1, p2);
                break;
            }

            case CaptureMode.RectShape:
                using (var path = RRect(new RectangleF(l, t, r - l, b - t), 3))
                    g.DrawPath(pen, path);
                break;

            case CaptureMode.CircleShape:
                g.DrawEllipse(pen, l, t, r - l, b - t);
                break;

            case CaptureMode.Draw:
            {
                var pts = new[]
                {
                    new PointF(l, b - (b - t) * 0.15f),
                    new PointF(l + (r - l) * 0.34f, t),
                    new PointF(l + (r - l) * 0.66f, b),
                    new PointF(r, t + (b - t) * 0.15f),
                };
                g.DrawCurve(pen, pts, 0.6f);
                break;
            }

            case CaptureMode.Highlight:
            {
                using var fill = new SolidBrush(Color.FromArgb(150, color.R, color.G, color.B));
                float barTop = t + (b - t) * 0.18f;
                using var path = RRect(new RectangleF(l, barTop, r - l, (b - t) * 0.64f), 2);
                g.FillPath(fill, path);
                break;
            }
        }
    }

    private static void DrawGlyphArrowhead(Graphics g, Pen pen, PointF from, PointF to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.1f) return;

        float nx = dx / len, ny = dy / len;
        float head = Math.Max(5f, len * 0.4f);
        const float ang = 26f * MathF.PI / 180f;

        var basePt = new PointF(to.X - nx * head, to.Y - ny * head);
        var left = RotateAround(basePt, to, -ang);
        var right = RotateAround(basePt, to, ang);
        g.DrawLine(pen, left, to);
        g.DrawLine(pen, right, to);
    }

    private static PointF RotateAround(PointF p, PointF pivot, float radians)
    {
        float s = MathF.Sin(radians), c = MathF.Cos(radians);
        float dx = p.X - pivot.X, dy = p.Y - pivot.Y;
        return new PointF(
            pivot.X + dx * c - dy * s,
            pivot.Y + dx * s + dy * c);
    }

    private void DrawConfirmTogglePreview(Graphics g, Rectangle rect, bool hover, float pressAmt, float opacity)
    {
        var settings = Services.SettingsService.LoadStatic() ?? new AppSettings();
        bool showPreview = settings.ShowCapturePreview;

        float hoverCorner = UiChrome.ScaleFloat(5f);
        var face = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);

        if (hover)
        {
            using (var path = WindowsDockRenderer.RoundedRect(face, hoverCorner))
            using (var brush = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 26 : 20, UiChrome.AccentColor)))
                g.FillPath(brush, path);
        }

        int iconW = 0;                           // Eye icon removed — the toggle already
        int trackW = UiChrome.ScaleInt(34);      // communicates preview-on/off by itself.
        int trackH = UiChrome.ScaleInt(18);
        int gap = 0;

        float groupW = iconW + gap + trackW;
        float startX = face.X + (face.Width - groupW) / 2f;

        float trackX = startX + iconW + gap;
        float trackY = face.Y + (face.Height - trackH) / 2f;
        var trackRect = new RectangleF(trackX, trackY, trackW, trackH);
        float trackCorner = trackH * 0.5f;

        Color trackBgColor = showPreview 
            ? UiChrome.AccentColor 
            : (UiChrome.IsDark ? Color.FromArgb(42, 42, 42) : Color.FromArgb(200, 200, 200));
        Color trackBorderColor = showPreview
            ? UiChrome.AccentColor
            : (UiChrome.IsDark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(160, 160, 160));

        using (var path = WindowsDockRenderer.RoundedRect(trackRect, trackCorner))
        {
            using (var brush = new SolidBrush(Color.FromArgb((int)(255 * opacity), trackBgColor)))
                g.FillPath(brush, path);
            using (var pen = new Pen(Color.FromArgb((int)(255 * opacity), trackBorderColor), 1f))
                g.DrawPath(pen, path);
        }

        int thumbSz = UiChrome.ScaleInt(12);
        float thumbX = showPreview 
            ? (trackRect.Right - thumbSz - UiChrome.ScaleInt(3)) 
            : (trackRect.Left + UiChrome.ScaleInt(3));
        float thumbY = trackRect.Y + (trackRect.Height - thumbSz) / 2f;
        var thumbRect = new RectangleF(thumbX, thumbY, thumbSz, thumbSz);

        Color thumbColor = showPreview 
            ? Color.White 
            : (UiChrome.IsDark ? Color.FromArgb(136, 136, 136) : Color.FromArgb(100, 100, 100));

        using (var brush = new SolidBrush(Color.FromArgb((int)(255 * opacity), thumbColor)))
            g.FillEllipse(brush, thumbRect);
    }

    private void DrawCenterMoveGrip(Graphics g)
    {
        if (_centerMoveGripRect.IsEmpty)
            return;

        float opacity = _centerMoveGripOpacity;
        if (opacity <= 0f)
            return;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool hover = _hoveredCenterMoveGrip || _isConfirmDragging;

        // Scale badge up slightly while active — same feel as pill hover inflate.
        var baseRect = _centerMoveGripRect;
        float inflate = hover ? UiChrome.ScaleFloat(2f) : 0f;
        var drawRect = new RectangleF(
            baseRect.X - inflate,
            baseRect.Y - inflate,
            baseRect.Width  + inflate * 2f,
            baseRect.Height + inflate * 2f);

        float cx = drawRect.X + drawRect.Width  / 2f;
        float cy = drawRect.Y + drawRect.Height / 2f;

        var accent = UiChrome.AccentColor;

        // ── Drop shadow (same offset/alpha recipe as DrawDragGrip) ────────────
        int shadowAlpha = (int)((hover ? 100 : 70) * opacity);
        using (var shadowBrush = new SolidBrush(Color.FromArgb(shadowAlpha, 0, 0, 0)))
            g.FillEllipse(shadowBrush,
                drawRect.X, drawRect.Y + 1.5f, drawRect.Width, drawRect.Height);

        // ── Dark body (18, 18, 20) ─────────────────────────────────────────
        int bgA = (int)((hover ? 240 : 225) * opacity);
        using (var bgBrush = new SolidBrush(Color.FromArgb(bgA, 18, 18, 20)))
            g.FillEllipse(bgBrush, drawRect);

        // ── Accent border ─────────────────────────────────────────────────
        float borderW = hover ? 1.4f : 1f;
        int borderA = (int)((hover ? 220 : 150) * opacity);
        using (var borderPen = new Pen(Color.FromArgb(borderA, accent), borderW))
            g.DrawEllipse(borderPen,
                drawRect.X + 0.5f, drawRect.Y + 0.5f,
                drawRect.Width - 1f, drawRect.Height - 1f);

        // ── 4-directional move icon (accent-colored, same alpha as grip dots) ───
        float iconSz  = UiChrome.ScaleFloat(12f);  // arm half-extent
        float stemW   = UiChrome.ScaleFloat(1.8f); // stem thickness
        float arrowH  = UiChrome.ScaleFloat(4.8f); // arrowhead depth
        float arrowHW = UiChrome.ScaleFloat(3.2f); // arrowhead half-width
        float stemEnd = iconSz - arrowH;            // stem stops here

        int iconA = (int)((hover ? 240 : 190) * opacity);
        Color iconColor = Color.FromArgb(iconA, accent);

        using (var iconBrush = new SolidBrush(iconColor))
        using (var stemPen   = new Pen(iconColor, stemW)
               { StartCap = LineCap.Round, EndCap = LineCap.Flat })
        {
            g.DrawLine(stemPen, cx - stemEnd, cy, cx + stemEnd, cy);
            g.DrawLine(stemPen, cx, cy - stemEnd, cx, cy + stemEnd);

            g.FillPolygon(iconBrush, new PointF[]    // left
                { new(cx - iconSz, cy), new(cx - stemEnd, cy - arrowHW), new(cx - stemEnd, cy + arrowHW) });
            g.FillPolygon(iconBrush, new PointF[]    // right
                { new(cx + iconSz, cy), new(cx + stemEnd, cy - arrowHW), new(cx + stemEnd, cy + arrowHW) });
            g.FillPolygon(iconBrush, new PointF[]    // up
                { new(cx, cy - iconSz), new(cx - arrowHW, cy - stemEnd), new(cx + arrowHW, cy - stemEnd) });
            g.FillPolygon(iconBrush, new PointF[]    // down
                { new(cx, cy + iconSz), new(cx - arrowHW, cy + stemEnd), new(cx + arrowHW, cy + stemEnd) });
        }

        g.SmoothingMode = oldSmoothing;
    }

}
