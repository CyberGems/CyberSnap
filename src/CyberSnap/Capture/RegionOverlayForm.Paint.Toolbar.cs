using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Linq;
using System.Globalization;
using CyberSnap.Helpers;
using CyberSnap.Models;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    private static Pen? _swatchSelectionPen;
    private static int _swatchSelectionPenKey;

    private static Pen GetSwatchSelectionPen()
    {
        int key = UiChrome.SurfaceTextPrimary.ToArgb();
        if (_swatchSelectionPen is null || _swatchSelectionPenKey != key)
        {
            _swatchSelectionPen?.Dispose();
            _swatchSelectionPen = new Pen(UiChrome.SurfaceTextPrimary, 2f);
            _swatchSelectionPenKey = key;
        }
        return _swatchSelectionPen;
    }

    private static Pen? _swatchOutlinePen;
    private static int _swatchOutlinePenKey;

    /// <summary>Subtle outline so dark swatches don't disappear against the background.</summary>
    private static Pen GetSwatchOutlinePen()
    {
        var color = Color.FromArgb(68, UiChrome.SurfaceTextPrimary);
        int key = color.ToArgb();
        if (_swatchOutlinePen is null || _swatchOutlinePenKey != key)
        {
            _swatchOutlinePen?.Dispose();
            _swatchOutlinePen = new Pen(color, 1f);
            _swatchOutlinePenKey = key;
        }
        return _swatchOutlinePen;
    }

    private void PaintToolbar(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var r = new Rectangle(_toolbarRect.X, _toolbarRect.Y,
            _toolbarRect.Width, _toolbarRect.Height);

        float cr = UiChrome.ScaledToolbarCornerRadius;
        int pad = UiChrome.ScaledToolbarInnerPadding;
        int buttonSize = UiChrome.ScaledToolbarButtonSize;
        int buttonSpacing = UiChrome.ScaledToolbarButtonSpacing;

        // Paint shadow once for the full toolbar, then two-tier black mica backgrounds
        WindowsDockRenderer.PaintShadow(g, r, cr);
        using (var path = WindowsDockRenderer.RoundedRect(r, cr))
        using (var brush = new SolidBrush(UiChrome.SurfaceTier1))
            g.FillPath(brush, path);
        // Capture phase is a single-row dock. Never paint the legacy tier-2 plate here —
        // enabled annotation tools only appear after confirm (ShowAnnotationChrome).

        // Render sleek CyberGems premium accent border outline around the panel
        using (var path = WindowsDockRenderer.RoundedRect(r, cr))
        using (var pen = new Pen(Color.FromArgb(UiChrome.IsDark ? 80 : 50, UiChrome.AccentColor), 1f))
            g.DrawPath(pen, path);

        // Render gorgeous glowing neon accent line along the docked screen edge of the bar.
        // Trace the rounded corners (not a flat line stopping short of them) so the accent hugs the
        // bar's radius and reaches into the corners, matching the system-toast accent style.
        using (var path = BuildDockedEdgePath(r, cr, ActiveDockSide))
        {
            if (path != null)
            {
                var dock = ActiveDockSide;
                bool horizontal = dock == CaptureDockSide.Top || dock == CaptureDockSide.Bottom;
                var accent = UiChrome.AccentColor;
                var fade = Color.FromArgb(0, accent);
                // Gradient runs along the bar's long axis, fading to transparent at both tips so the
                // neon line dissolves softly into the rounded corners instead of ending abruptly.
                PointF p0 = new PointF(r.X, r.Y);
                PointF p1 = horizontal ? new PointF(r.Right, r.Y) : new PointF(r.X, r.Bottom);
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
                        g.DrawPath(pen, path);
                }
            }
        }

        // Draw question mark icon (Quick Start / help)
        int closeIdx = CloseButtonIndex;

        // Opacity: 35-40% base, 70-80% on hover
        float baseOpacity = UiChrome.IsDark ? 0.35f : 0.40f;
        float opacity = _hoveredBrand ? (UiChrome.IsDark ? 0.70f : 0.80f) : baseOpacity;
        Color brandIconColor = Color.FromArgb((int)(opacity * 255), UiChrome.SurfaceTextPrimary);

        int logoSz = UiChrome.ScaleInt(14);

        var oldHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        float lx = _brandRect.X + (_brandRect.Width - logoSz) / 2f;
        float ly = _brandRect.Y + (_brandRect.Height - logoSz) / 2f;

        _logoRect = new Rectangle((int)Math.Round(lx), (int)Math.Round(ly), logoSz, logoSz);

        FluentIcons.DrawIcon(g, "question", new RectangleF(lx, ly, logoSz, logoSz), brandIconColor, 0f);

        g.TextRenderingHint = oldHint;

        // 1. Divider line splitting Tier 1 from Tier 2 — removed: capture is single-row;
        // confirm is a single annotation dock (no second plate).

        // Render sleek unified toolbar dividers (no duplicate lines)
        var dividerPositions = new List<int>();

        if (!ShowAnnotationChrome)
        {
            var tier1Group = new[] { "rect", "center", "scroll", "recordGif", "record" };
            int lastInGroup = -1;
            for (int i = 0; i < _mainBarTools.Length; i++)
            {
                if (tier1Group.Contains(_mainBarTools[i].Id))
                    lastInGroup = i;
            }
            if (lastInGroup >= 0 && lastInGroup < _mainBarTools.Length - 1 && _toolbarButtons[lastInGroup].Width > 0)
            {
                int p = IsVerticalDock
                    ? _toolbarButtons[lastInGroup].Bottom + (buttonSpacing + GroupGap) / 2
                    : _toolbarButtons[lastInGroup].Right + (buttonSpacing + GroupGap) / 2;
                dividerPositions.Add(p);
            }

            // Divider before Close button (after last main bar tool)
            if (_mainBarTools.Length > 0 && CloseButtonIndex < _toolbarButtons.Length)
            {
                int lastIdx = _mainBarTools.Length - 1;
                var lastBtn = _toolbarButtons[lastIdx];
                var closeBtn = _toolbarButtons[CloseButtonIndex];
                if (lastBtn.Width > 0 && closeBtn.Width > 0)
                {
                    int p = IsVerticalDock
                        ? (lastBtn.Bottom + closeBtn.Y) / 2
                        : (lastBtn.Right + closeBtn.X) / 2;
                    dividerPositions.Add(p);
                }
            }
        }
        else
        {
            // Annotation dock: sticky trigger/color/stroke/eraser/select + retractable strip.
            // Dividers only between buttons that are actual Y-neighbors (never flyout-index midpoints
            // across the sticky/retractable split — that put lines over tools like Flecha).
            void AddMidY(Rectangle above, Rectangle below)
            {
                if (above.Width <= 0 || below.Width <= 0) return;
                int gap = below.Y - above.Bottom;
                if (gap < 0 || gap > GroupGap + UiChrome.ScaleInt(4))
                    return; // not visually adjacent
                dividerPositions.Add((above.Bottom + below.Y) / 2);
            }

            int flyoutStartIdx = FlyoutStartIndex;

            // Single unified divider on the annotation dock: separates the drawing suite
            // (all tools + color + stroke) from the bottom utilities (undo / eraser / select).
            if (StrokeWidthButtonIndex < _toolbarButtons.Length
                && _toolbarButtons[StrokeWidthButtonIndex].Width > 0)
            {
                for (int i = 0; i < _flyoutTools.Length; i++)
                {
                    if (!string.Equals(_flyoutTools[i].Id, "undo", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(_flyoutTools[i].Id, "eraser", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(_flyoutTools[i].Id, "select", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (_toolbarButtons[flyoutStartIdx + i].Width > 0)
                    {
                        AddMidY(_toolbarButtons[StrokeWidthButtonIndex], _toolbarButtons[flyoutStartIdx + i]);
                        break;
                    }
                }
            }
        }

        foreach (int pos in dividerPositions.Distinct())
        {
            if (IsVerticalDock)
            {
                int sx1 = _toolbarRect.X + pad + 4;
                int sx2 = _toolbarRect.Right - pad - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx1, pos), new Point(sx2, pos));
            }
            else
            {
                int sy1 = _toolbarRect.Y + pad + 4;
                int sy2 = _toolbarRect.Bottom - pad - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(pos, sy1), new Point(pos, sy2));
            }
        }



        int drawingStartIdx = FlyoutStartIndex;

        // 4. Draw all buttons
        var previousClip = g.Clip;
        bool clipRetract = ShowAnnotationChrome && !_annotationRetractRevealRect.IsEmpty;
        bool clipHistory = ShowAnnotationChrome && !_annotationHistoryRevealRect.IsEmpty;
        for (int i = 0; i < BtnCount; i++)
        {
            var btn = _toolbarButtons[i];
            if (btn.Width <= 0 || btn.Height <= 0)
                continue;

            bool isHistoryUtility = ShowAnnotationChrome &&
                (string.Equals(_toolbarToolIds[i], "undo", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(_toolbarToolIds[i], "eraser", StringComparison.OrdinalIgnoreCase));

            if (isHistoryUtility)
            {
                if (_historyUtilitiesRevealAmt <= 0.001f || !_annotationHistoryRevealRect.IntersectsWith(btn))
                    continue;
                if (clipHistory && _historyUtilitiesRevealAmt < 0.999f)
                    g.SetClip(Rectangle.Intersect(_annotationHistoryRevealRect, Rectangle.Round(g.ClipBounds)), CombineMode.Replace);
                else
                    g.Clip = previousClip;
            }
            else if (IsRetractableAnnotationToolbarButton(i))
            {
                if (!clipRetract || !btn.IntersectsWith(_annotationRetractRevealRect))
                    continue;
                g.SetClip(Rectangle.Intersect(_annotationRetractRevealRect, Rectangle.Round(g.ClipBounds)), CombineMode.Replace);
            }
            else
            {
                g.Clip = previousClip;
            }

            bool active = _toolbarModes[i] is { } && string.Equals(_toolbarToolIds[i], _activeToolId, StringComparison.OrdinalIgnoreCase);
            bool hover = _hoveredButton == i;
            bool isTier2 = i >= drawingStartIdx;
            var tierAccent = isTier2 ? UiChrome.AccentTier2 : UiChrome.AccentColor;
            // Stroke width button (shows line thickness preview in current tool color)
            if (_toolbarIcons[i] == "strokeWidth")
            {
                WindowsDockRenderer.PaintButton(g, btn, active, hover, accent: tierAccent);
                float lineY = btn.Y + btn.Height / 2f;
                // Inset to roughly match the icon glyphs' footprint so the preview doesn't crowd the
                // group separator on its left.
                float margin = 9f;
                float lineX1 = btn.X + margin;
                float lineX2 = btn.Right - margin;
                int alpha = active ? 255 : hover ? 230 : 175;
                float width = _strokeWidth;
                var lineColor = Color.FromArgb(alpha, _toolColor.R, _toolColor.G, _toolColor.B);
                using (var pen = new Pen(lineColor, width))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, lineX1, lineY, lineX2, lineY);
                }
                PaintCaptureHoldHint(g, btn, UiChrome.SurfaceTextPrimary, buttonIndex: i);
                continue;
            }

            // Color dot button (shows active drawing color)
            if (_toolbarIcons[i] == "color")
            {
                WindowsDockRenderer.PaintButton(g, btn, active, hover, accent: tierAccent);
                int dotSize = 16;
                float dx = btn.X + (btn.Width - dotSize) / 2f;
                float dy = btn.Y + (btn.Height - dotSize) / 2f;
                int colorAlpha = active ? 255 : hover ? 230 : 175;
                var baseColor = Color.FromArgb(colorAlpha, _toolColor.R, _toolColor.G, _toolColor.B);
                var lightColor = Color.FromArgb(colorAlpha,
                    Math.Min(255, _toolColor.R + 80),
                    Math.Min(255, _toolColor.G + 80),
                    Math.Min(255, _toolColor.B + 80));
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(dx, dy, dotSize, dotSize);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = lightColor;
                        pgb.SurroundColors = new[] { baseColor };
                        pgb.CenterPoint = new PointF(dx + dotSize * 0.35f, dy + dotSize * 0.35f);
                        g.FillEllipse(pgb, dx, dy, dotSize, dotSize);
                    }
                }
                float hlW = dotSize * 0.35f;
                float hlH = dotSize * 0.25f;
                float hlX = dx + dotSize * 0.15f;
                float hlY = dy + dotSize * 0.12f;
                int glossAlpha = colorAlpha > 200 ? 100 : 70;
                using (var hlBrush = new SolidBrush(Color.FromArgb(glossAlpha, 255, 255, 255)))
                    g.FillEllipse(hlBrush, hlX, hlY, hlW, hlH);
                PaintCaptureHoldHint(g, btn, UiChrome.SurfaceTextPrimary, buttonIndex: i);
                continue;
            }

            // Cancel button: render in danger red (hover bg + icon tint) so it reads as a destructive
            // action, not just another tool sitting at the end of the row.
            if (i == CloseButtonIndex)
            {
                var danger = UiChrome.SurfaceDanger;
                WindowsDockRenderer.PaintButton(g, btn, active: false, hovered: hover, accent: danger);
                int ca = hover ? 255 : 165;
                DrawIcon(g, _toolbarIcons[i], btn, Color.FromArgb(ca, danger.R, danger.G, danger.B), active: false, flipHorizontal: true);
                continue;
            }

            // Undo action button: enabled only when there are edits in the undo stack
            if (string.Equals(_toolbarToolIds[i], "undo", StringComparison.OrdinalIgnoreCase))
            {
                bool canUndo = _editUndoStack.Count > 0;
                WindowsDockRenderer.PaintButton(g, btn, active: false, hovered: hover && canUndo, accent: tierAccent);
                int ca = canUndo ? (hover ? 255 : 200) : 90;
                var baseCol = UiChrome.SurfaceTextPrimary;
                DrawIcon(g, "undo", btn, Color.FromArgb(ca, baseCol.R, baseCol.G, baseCol.B), active: false);
                continue;
            }

            WindowsDockRenderer.PaintButton(g, btn, active, hover, accent: tierAccent);

            int ia = active ? 255 : hover ? 240 : 200;
            var iconColor = active ? tierAccent : UiChrome.SurfaceTextPrimary;
            var drawColor = Color.FromArgb(ia, iconColor.R, iconColor.G, iconColor.B);
            DrawIcon(g, _toolbarIcons[i], btn, drawColor, active);

            // Active-state cue lives in PaintButton's border/fill + icon tint — the floating accent
            // pill was extra chrome noise for little extra signal.

            // Hold-to-switch affordance on merged capture (rect ↔ center) and annotation groups.
            // Pass the icon's actual color so the chevron matches the glyph instead of the tier accent.
            if (IsMergedHoldButton(i))
                PaintCaptureHoldHint(g, btn, drawColor, buttonIndex: i);
        }

        g.Clip = previousClip;

        // Draw menu activator (⋮) — capture bar only. Confirm-phase overflow is the gear pill.
        if (!_menuActivatorRect.IsEmpty)
        {
            // Soft accent pulse while the quick-start guide is open.
            // Pulse phase 0→1→0 over ~1.1s; driven by StartMenuActivatorPulse → UpdateToolbarSurfaceOnly.
            float guidePulse = 0f;
            if (_highlightMenuActivatorForGuide)
            {
                double secs = (DateTime.UtcNow - _menuActivatorPulseStart).TotalSeconds;
                // 0..1..0 triangle-ish via absolute sine for a clear bright/dim beat.
                guidePulse = Math.Abs((float)Math.Sin(secs * Math.PI * 2.0 / 1.1));
            }

            bool activatorHot = _hoveredMenuActivator || _highlightMenuActivatorForGuide;
            if (activatorHot)
            {
                int fillA = _highlightMenuActivatorForGuide
                    ? (int)(50 + 90 * guidePulse)
                    : 30;
                var glowRect = _highlightMenuActivatorForGuide
                    ? Rectangle.Inflate(_menuActivatorRect, UiChrome.ScaleInt(3), UiChrome.ScaleInt(3))
                    : _menuActivatorRect;
                using (var path = WindowsDockRenderer.RoundedRect(glowRect, UiChrome.ScaleInt(4)))
                using (var brush = new SolidBrush(Color.FromArgb(fillA, UiChrome.AccentColor)))
                    g.FillPath(brush, path);

                if (_highlightMenuActivatorForGuide)
                {
                    int ringA = (int)(110 + 120 * guidePulse);
                    using var ring = new Pen(Color.FromArgb(ringA, UiChrome.AccentColor), 1.6f);
                    using var path = WindowsDockRenderer.RoundedRect(glowRect, UiChrome.ScaleInt(4));
                    g.DrawPath(ring, path);
                }
            }

            Color dotsColor;
            if (_highlightMenuActivatorForGuide)
            {
                int a = (int)(200 + 55 * guidePulse);
                dotsColor = Color.FromArgb(a, UiChrome.AccentColor);
            }
            else if (_hoveredMenuActivator)
            {
                dotsColor = UiChrome.AccentColor;
            }
            else
            {
                float baseAlpha = (UiChrome.IsDark ? 0.35f : 0.40f) * 0.80f;
                dotsColor = Color.FromArgb((int)(baseAlpha * 255), UiChrome.SurfaceTextPrimary);
            }

            // Kebab dots — orientation follows the activator's hit-target shape: capture
            // horizontal dock uses a narrow/tall target (vertical ⋮); vertical dock uses a
            // wide/short one (horizontal ⋯).
            float tcx = _menuActivatorRect.X + _menuActivatorRect.Width / 2f;
            float tcy = _menuActivatorRect.Y + _menuActivatorRect.Height / 2f;
            float dotR = UiChrome.ScaleFloat(_highlightMenuActivatorForGuide ? 1.7f : 1.45f);
            float gap = UiChrome.ScaleFloat(_highlightMenuActivatorForGuide ? 5.6f : 5.2f);
            bool horizontalDots = _menuActivatorRect.Width >= _menuActivatorRect.Height;
            using (var brush = new SolidBrush(dotsColor))
            {
                for (int i = -1; i <= 1; i++)
                {
                    float cx = horizontalDots ? tcx + i * gap : tcx;
                    float cy = horizontalDots ? tcy : tcy + i * gap;
                    g.FillEllipse(brush, cx - dotR, cy - dotR, dotR * 2f, dotR * 2f);
                }
            }
        }

        if (ShowAnnotationChrome && !_annotationGripRect.IsEmpty)
        {
            DrawToolbarGripDots(g, _annotationGripRect, UiChrome.AccentColor, isAnnotationBar: true);
        }
        else if (!ShowAnnotationChrome && !_captureGripRect.IsEmpty)
        {
            DrawToolbarGripDots(g, _captureGripRect, UiChrome.AccentColor, isAnnotationBar: false);
        }

        g.SmoothingMode = SmoothingMode.Default;
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    private static void DrawToolbarGripDots(Graphics g, Rectangle rect, Color accent, bool isAnnotationBar = false)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;
        float stepX = UiChrome.ScaleFloat(4.2f);
        float stepY = UiChrome.ScaleFloat(4.2f);
        float dotRadius = UiChrome.ScaleFloat(1.2f);
        
        float baseAlpha = UiChrome.IsDark ? 0.28f : 0.32f;
        if (isAnnotationBar)
        {
            baseAlpha = UiChrome.IsDark ? 0.22f : 0.26f;
        }
        int alpha = (int)(baseAlpha * 255);
        using var dotBrush = new SolidBrush(Color.FromArgb(alpha, UiChrome.SurfaceTextPrimary));

        bool horizontal = rect.Width >= rect.Height;
        if (horizontal)
        {
            for (int col = -1; col <= 1; col++)
            {
                for (int row = -1; row <= 0; row++)
                {
                    float dx = col * stepX;
                    float dy = (row + 0.5f) * stepY;
                    g.FillEllipse(dotBrush, cx + dx - dotRadius, cy + dy - dotRadius, dotRadius * 2f, dotRadius * 2f);
                }
            }
        }
        else
        {
            for (int row = -1; row <= 1; row++)
            {
                for (int col = -1; col <= 0; col++)
                {
                    float dx = (col + 0.5f) * stepX;
                    float dy = row * stepY;
                    g.FillEllipse(dotBrush, cx + dx - dotRadius, cy + dy - dotRadius, dotRadius * 2f, dotRadius * 2f);
                }
            }
        }
    }

    /// <summary>Path tracing the docked edge of the bar including its two corner arcs, so the neon
    /// accent line curves into the rounded corners instead of stopping short of them.</summary>
    private static GraphicsPath? BuildDockedEdgePath(Rectangle rect, float radius, CaptureDockSide dock)
    {
        float r = Math.Max(1f, radius - 1f);
        float d = r * 2f;
        // Sit just inside the panel border (1px) like the previous flat line did.
        var b = new RectangleF(rect.X + 1f, rect.Y + 1f, rect.Width - 2f, rect.Height - 2f);
        var path = new GraphicsPath();
        switch (dock)
        {
            case CaptureDockSide.Top:
                path.AddArc(b.X, b.Y, d, d, 180f, 90f);                 // top-left corner
                path.AddArc(b.Right - d, b.Y, d, d, 270f, 90f);         // top edge + top-right corner
                break;
            case CaptureDockSide.Bottom:
                path.AddArc(b.X, b.Bottom - d, d, d, 180f, -90f);       // bottom-left corner
                path.AddArc(b.Right - d, b.Bottom - d, d, d, 90f, -90f);// bottom edge + bottom-right corner
                break;
            case CaptureDockSide.Left:
                path.AddArc(b.X, b.Y, d, d, 270f, -90f);                // top-left corner
                path.AddArc(b.X, b.Bottom - d, d, d, 180f, -90f);       // left edge + bottom-left corner
                break;
            case CaptureDockSide.Right:
                path.AddArc(b.Right - d, b.Y, d, d, 270f, 90f);         // top-right corner
                path.AddArc(b.Right - d, b.Bottom - d, d, d, 0f, 90f);  // right edge + bottom-right corner
                break;
            default:
                path.Dispose();
                return null;
        }
        return path;
    }

    private static Color ScaleAlpha(Color color, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        return Color.FromArgb((int)Math.Round(color.A * factor), color.R, color.G, color.B);
    }

    /// <summary>
    /// Small chevron badge on a merged tool button so users discover the long-press alternate
    /// mode (Area ↔ From Center, shape/stroke groups, …). The chevron sits at the bottom-right
    /// and points outward along the button diagonal suggesting "opens to that corner". Its color
    /// matches the icon glyph it sits on (<paramref name="chevronColor"/>) with reduced alpha so
    /// it reads as a secondary affordance instead of competing with the icon itself.
    /// </summary>
    private void PaintCaptureHoldHint(Graphics g, Rectangle btn, Color chevronColor, int buttonIndex)
    {
        bool holdingThis = _isMouseDownOnCaptureBtn
            && (_mergedHoldButtonIndex == buttonIndex
                || (_mergedHoldButtonIndex < 0 && (buttonIndex == _mergedCaptureButtonIndex || buttonIndex == _mergedRecordButtonIndex)));

        bool popupForThis = (_altCapturePopupOpen && (_mergedHoldButtonIndex == buttonIndex || (_mergedHoldButtonIndex < 0 && (buttonIndex == _mergedCaptureButtonIndex || buttonIndex == _mergedRecordButtonIndex))))
            || (_colorPickerOpen && buttonIndex == ColorButtonIndex)
            || (_strokePickerOpen && buttonIndex == StrokeWidthButtonIndex);

        // Tiny bottom-right corner chevron pointing outward along the diagonal.
        // Stroke-based instead of fill-based so the open direction reads unambiguously.
        // Alpha is intentionally LOWER than the icon glyph so the L reads as a
        // secondary affordance instead of competing with the icon itself.
        float armLen = UiChrome.ScaleFloat(4.5f);
        float cx = btn.Right - UiChrome.ScaleFloat(7f);
        float cy = btn.Bottom - UiChrome.ScaleFloat(7f);
        int chevA = holdingThis || popupForThis
            ? 200
            : (int)Math.Round(chevronColor.A * 0.55);
        using (var pen = new Pen(Color.FromArgb(chevA, chevronColor), UiChrome.ScaleFloat(1.3f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        {
            g.DrawLine(pen, cx - armLen, cy, cx, cy);
            g.DrawLine(pen, cx, cy, cx, cy - armLen);
        }

        // Hold-progress ring REMOVED: the circle animation made the long-press feel slow.
        // The popup still appears at the 300ms threshold via the existing timer — we just
        // don't draw the pulse around the button anymore.
    }

    /// <summary>
    /// Called by the separate ToolbarForm to paint toolbar, tooltips, and popups.
    /// Graphics is already translated so overlay coordinates map correctly.
    /// </summary>
    public void PaintToolbarTo(Graphics g)
    {
        ApplyUiGraphics(g);
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var state = g.Save();
        PaintToolbar(g);
        if (_colorPickerOpen) PaintColorPicker(g);
        if (_strokePickerOpen) PaintStrokePicker(g);
        if (_emojiPickerOpen) PaintEmojiPicker(g);
        if (_fontPickerOpen) PaintFontPicker(g);
        if (_altCapturePopupOpen) PaintAltCaptureButton(g);
        g.Restore(state);
    }

    private void PaintAltCaptureButton(Graphics g)
    {
        EnsureAltPopupSlotsLaidOut();
        if (_altPopupSlots.Count == 0)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cr = UiChrome.ScaledToolbarCornerRadius;
        int buttonSize = UiChrome.ScaledToolbarButtonSize;
        int containerPadding = UiChrome.ScaleInt(4);
        var accent = ShowAnnotationChrome ? UiChrome.AccentTier2 : UiChrome.AccentColor;

        Rectangle union = Rectangle.Empty;
        for (int i = 0; i < _altPopupSlots.Count; i++)
        {
            var slot = _altPopupSlots[i];
            var container = slot.Container;
            union = union.IsEmpty ? container : Rectangle.Union(union, container);

            WindowsDockRenderer.PaintShadow(g, container, cr);

            using (var path = WindowsDockRenderer.RoundedRect(container, cr))
            using (var brush = new SolidBrush(UiChrome.SurfaceTier1))
                g.FillPath(brush, path);

            using (var path = WindowsDockRenderer.RoundedRect(container, cr))
            using (var pen = new Pen(Color.FromArgb(UiChrome.IsDark ? 80 : 50, accent), 1f))
                g.DrawPath(pen, path);

            bool hover = _hoveredAltSlotIndex == i;
            var btnRect = new Rectangle(
                container.X + containerPadding,
                container.Y + containerPadding,
                buttonSize,
                buttonSize);

            WindowsDockRenderer.PaintButton(g, btnRect, active: false, hovered: hover, accent: accent);

            int ia = hover ? 240 : 200;
            var iconColor = UiChrome.SurfaceTextPrimary;
            DrawIcon(g, slot.IconId, btnRect, Color.FromArgb(ia, iconColor.R, iconColor.G, iconColor.B), active: false);
        }

        _altCaptureButtonRect = union;
        _hoveredAltCaptureBtn = _hoveredAltSlotIndex >= 0;
    }

    /// <summary>
    /// Builds alt popup slots from the held merged button: one slot for capture Area/Center,
    /// one or more for annotation merge groups (e.g. Line + Curved Arrow under Arrow).
    /// </summary>
    private void EnsureAltPopupSlotsLaidOut()
    {
        _altPopupSlots.Clear();
        int primaryIdx = _mergedHoldButtonIndex;
        if (primaryIdx < 0 || primaryIdx >= _toolbarButtons.Length)
            primaryIdx = _mergedCaptureButtonIndex;
        if (primaryIdx < 0 || primaryIdx >= _toolbarButtons.Length)
            return;

        var primaryBtn = _toolbarButtons[primaryIdx];
        if (primaryBtn.Width <= 0 || primaryBtn.Height <= 0)
            return;

        var altIds = ResolveAltToolIdsForButton(primaryIdx);
        if (altIds.Count == 0)
            return;

        int buttonSize = UiChrome.ScaledToolbarButtonSize;
        int containerPadding = UiChrome.ScaleInt(4);
        int containerSize = buttonSize + containerPadding * 2;
        int gap = UiChrome.ScaleInt(AltCapturePopupGapPx); // snug — almost flush with the host button
        var dock = ActiveDockSide;

        int baseX = primaryBtn.X + (primaryBtn.Width - containerSize) / 2;
        int baseY = primaryBtn.Y + (primaryBtn.Height - containerSize) / 2;

        for (int i = 0; i < altIds.Count; i++)
        {
            int step = i * (containerSize + gap);
            int x = baseX;
            int y = baseY;

            if (ShowAnnotationChrome)
            {
                // Frame-anchored column: open alts *away* from the crop so they don't cover it.
                if (dock == CaptureDockSide.Right)
                    x = primaryBtn.Right + gap + step;
                else if (dock == CaptureDockSide.Left)
                    x = primaryBtn.X - containerSize - gap - step;
                else
                    y = primaryBtn.Bottom + gap + step;
            }
            else if (dock == CaptureDockSide.Bottom)
                y = primaryBtn.Y - containerSize - gap - step;
            else if (dock == CaptureDockSide.Top)
                y = primaryBtn.Bottom + gap + step;
            else if (dock == CaptureDockSide.Left)
                x = primaryBtn.Right + gap + step;
            else if (dock == CaptureDockSide.Right)
                x = primaryBtn.X - containerSize - gap - step;
            else
                y = primaryBtn.Bottom + gap + step;

            var toolId = altIds[i];
 // Resolve to a real Fluent icon id; non-ToolDef actions (repeat/fullscreen/active window)
            // must map to their glyph ids or DrawIcon renders nothing.
            var iconId = toolId switch
            {
                "crop" => "rect",
                "rect" => "captureRect",
                "_repeatLastArea" => "captureBack",
                "_fullscreen" => "fullscreen",
                "_activeWindow" => "activeWindow",
                var id => id,
            };
            _altPopupSlots.Add((new Rectangle(x, y, containerSize, containerSize), toolId, iconId));
        }

        if (_altPopupSlots.Count > 0)
        {
            var union = _altPopupSlots[0].Container;
            for (int i = 1; i < _altPopupSlots.Count; i++)
                union = Rectangle.Union(union, _altPopupSlots[i].Container);
            _altCaptureButtonRect = union;
        }
    }

    private List<string> ResolveAltToolIdsForButton(int buttonIndex)
    {
        var alts = new List<string>();
        if (buttonIndex == _mergedCaptureButtonIndex)
        {
            var settings = Services.SettingsService.LoadStatic();
            var defaultMode = settings?.DefaultCaptureMode ?? CaptureMode.Rectangle;

            // Capture-mode flyout (the screenshot asked: all capture modes live on the Area
            // button; only its siblings Area ↔ From Center stay merged-and-default-aware).
            // Order: other area mode first (Centro), then the rest of capture modes. Use the
            // underscore-prefixed ids for the self-contained actions so the immediate-capture
            // dispatch + tooltip lookup (which key off "_fullscreen"/"_activeWindow") match.
            alts.Add(defaultMode == CaptureMode.Center ? "rect" : "center");
            alts.Add("scroll");      // already on the bar; moving into the Area flyout per request
            alts.Add("_fullscreen");
            alts.Add("_activeWindow");
            alts.Add("_repeatLastArea");

            return alts;
        }

        if (buttonIndex == _mergedRecordButtonIndex)
        {
            alts.Add("recordGif");
            return alts;
        }

        if (_annotationMergeAltsByButton.TryGetValue(buttonIndex, out var annAlts))
        {
            // Keep stable group order from AnnotationMergeGroups.
            var primaryId = buttonIndex < _toolbarToolIds.Length ? _toolbarToolIds[buttonIndex] : null;
            var group = !string.IsNullOrEmpty(primaryId) ? FindAnnotationMergeGroup(primaryId) : null;
            if (group is not null)
            {
                foreach (var id in group)
                {
                    if (annAlts.Any(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase)))
                        alts.Add(id);
                }
            }
            else
            {
                alts.AddRange(annAlts);
            }
        }

        return alts;
    }

    private int GetAltPopupSlotAt(Point location)
    {
        if (!_altCapturePopupOpen)
            return -1;
        EnsureAltPopupSlotsLaidOut();
        for (int i = 0; i < _altPopupSlots.Count; i++)
        {
            if (_altPopupSlots[i].Container.Contains(location))
                return i;
        }
        return -1;
    }

    private void PaintColorPicker(Graphics g)
    {
        // Small popup grid of color swatches
        int pw = ColorPickerColumns * (ColorPickerSwatchSize + ColorPickerPadding) + ColorPickerPadding;
        int ph = ColorPickerRows * (ColorPickerSwatchSize + ColorPickerPadding) + ColorPickerPadding;

        // Position below the color button
        var colorBtn = _toolbarButtons[ColorButtonIndex];
        _colorPickerRect = PositionPopupFromAnchor(colorBtn, pw, ph);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        WindowsDockRenderer.PaintSurface(g, _colorPickerRect);

        for (int i = 0; i < ToolColors.Length && i < ColorPickerColumns * ColorPickerRows; i++)
        {
            var swatchRect = GetColorPickerSwatchRect(i);
            g.FillEllipse(SketchRenderer.GetToolColorBrush(ToolColors[i]), swatchRect);
            // Subtle outline so dark swatches remain visible against the background
            g.DrawEllipse(GetSwatchOutlinePen(), swatchRect);
            if (ToolColors[i] == _toolColor)
                g.DrawEllipse(GetSwatchSelectionPen(), swatchRect);
        }
        g.SmoothingMode = SmoothingMode.Default;
    }

    private void PaintStrokePicker(Graphics g)
    {
        int pad = UiChrome.ScaleInt(6);
        int itemH = UiChrome.ScaleInt(26);
        int pw = UiChrome.ScaleInt(130);
        int ph = pad * 2 + StrokeWidths.Length * itemH;

        var strokeBtn = _toolbarButtons[StrokeWidthButtonIndex];
        _strokePickerRect = PositionPopupFromAnchor(strokeBtn, pw, ph);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        WindowsDockRenderer.PaintSurface(g, _strokePickerRect);

        int px = _strokePickerRect.X;
        int py = _strokePickerRect.Y;

        var (uiFont, _, _, _) = GetTextToolbarFonts();
        EnsurePickerChrome();

        for (int i = 0; i < StrokeWidths.Length; i++)
        {
            float sw = StrokeWidths[i];
            int iy = py + pad + i * itemH;
            var itemRect = new Rectangle(px + pad, iy, pw - pad * 2, itemH);

            bool active = Math.Abs(_strokeWidth - sw) < 0.01f;
            bool hovered = _hoveredStrokePickerIndex == i;

            if (active || hovered)
            {
                using var itemPath = WindowsDockRenderer.RoundedRect(itemRect, 4f);
                int fillA = active ? 40 : 20;
                using var itemBg = new SolidBrush(Color.FromArgb(fillA, UiChrome.SurfaceHover.R, UiChrome.SurfaceHover.G, UiChrome.SurfaceHover.B));
                g.FillPath(itemBg, itemPath);
                if (active)
                {
                    using var activePen = new Pen(UiChrome.AccentColor, 1.2f);
                    g.DrawPath(activePen, itemPath);
                }
            }

            // Left side: preview line of thickness sw in _toolColor
            float lineY = iy + itemH / 2f;
            float lineX1 = itemRect.X + UiChrome.ScaleFloat(8f);
            float lineX2 = lineX1 + UiChrome.ScaleFloat(54f);
            using (var pen = new Pen(_toolColor, Math.Max(1f, sw)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, lineX1, lineY, lineX2, lineY);
            }

            // Right side: "{sw} px"
            string label = $"{sw} px";
            int textAlpha = active ? 255 : hovered ? 230 : 170;
            var textColor = active ? UiChrome.AccentColor : UiChrome.SurfaceTextPrimary;
            using var textBrush = new SolidBrush(Color.FromArgb(textAlpha, textColor.R, textColor.G, textColor.B));
            var textRect = new RectangleF(lineX2 + UiChrome.ScaleFloat(6f), iy, itemRect.Right - (lineX2 + UiChrome.ScaleFloat(6f)), itemH);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(label, uiFont, textBrush, textRect, sf);
        }

        g.SmoothingMode = SmoothingMode.Default;
    }

    // Fixed button glyphs (not in ToolDef)
    private static readonly Dictionary<string, char> FixedGlyphs = new()
    {
        ["gear"]  = '\0',
        ["close"] = '\0',
        ["more"]  = '\0',
    };

    private static readonly StringFormat _iconFmt = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoClip
    };

    // Cached lookup for icon id -> glyph char (avoids LINQ FirstOrDefault per paint)
    private static Dictionary<string, char>? _iconGlyphCache;
    private static Dictionary<string, char> GetIconGlyphMap()
    {
        if (_iconGlyphCache != null) return _iconGlyphCache;
        _iconGlyphCache = new Dictionary<string, char>(ToolDef.AllTools.Length + FixedGlyphs.Count);
        foreach (var t in ToolDef.AllTools)
            _iconGlyphCache[t.Id] = t.Icon;
        foreach (var kv in FixedGlyphs)
            _iconGlyphCache[kv.Key] = kv.Value;
        return _iconGlyphCache;
    }

    private static void DrawIcon(Graphics g, string icon, Rectangle b, Color c, bool active = false, bool flipHorizontal = false)
    {
        if (icon == "color") return;

        var iconId = icon == "scroll" ? "scrollCapture" : icon;

        // Try Streamline icon first (line=inactive, solid=active)
        if (FluentIcons.HasIcon(iconId))
        {
            float inset = active ? 6f : 7f;
            FluentIcons.DrawIcon(g, iconId, b, c, inset, active, flipHorizontal);
            return;
        }

        return;
    }
}
