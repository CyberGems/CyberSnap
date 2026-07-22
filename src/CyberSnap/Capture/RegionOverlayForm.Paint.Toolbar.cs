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

        // Draw discrete elegant CyberSnap logo and brand name
        int closeIdx = CloseButtonIndex;

        if (_brandBitmap == null)
        {
            try
            {
                var logoUri = new Uri("pack://application:,,,/Assets/CyberSnap_square.png", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(logoUri);
                if (streamInfo != null)
                {
                    using (var s = streamInfo.Stream)
                    {
                        _brandBitmap = new Bitmap(s);
                    }
                }
            }
            catch { }
        }

        // Grayscale and opacity Matrix (40% opacity in dark mode, 35% in light mode)
        float baseOpacity = UiChrome.IsDark ? 0.35f : 0.40f;
        float opacity = _hoveredBrand ? (UiChrome.IsDark ? 0.70f : 0.80f) : baseOpacity;
        float textOpacity = opacity * 0.80f; // Slightly lower opacity than the solid logo to visually balance thin stroke vs solid block density
        float sat = _hoveredBrand ? 0.7f : 0f; // 0 = greyscale, 0.7 = 70% saturation on hover
        float isat = 1f - sat;
        var cm = new ColorMatrix(new float[][]
        {
            new float[] { isat * 0.299f + sat, isat * 0.299f,       isat * 0.299f,       0f, 0f },
            new float[] { isat * 0.587f,       isat * 0.587f + sat, isat * 0.587f,       0f, 0f },
            new float[] { isat * 0.114f,       isat * 0.114f,       isat * 0.114f + sat, 0f, 0f },
            new float[] { 0f,                  0f,                  0f,                  opacity, 0f },
            new float[] { 0f,                  0f,                  0f,                  0f, 1f }
        });

        int logoSz = UiChrome.ScaleInt(10); // Reduced by 20% as requested

        var oldHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; // Force ClearType subpixel LCD rendering for pristine crispness

        if (IsVerticalDock)
        {
            // Draw logo icon at the top of Column 1 (centered)
            float lx = _toolbarRect.X + pad + (buttonSize - logoSz) / 2f;
            float ly = _toolbarRect.Y + pad + UiChrome.ScaleInt(6);
            // Annotation frame dock: keep logo inside the compact brand strip (no rotated wordmark).
            if (ShowAnnotationChrome && !_brandRect.IsEmpty)
            {
                logoSz = Math.Min(logoSz + UiChrome.ScaleInt(2), Math.Max(8, _brandRect.Height - UiChrome.ScaleInt(4)));
                lx = _brandRect.X + (_brandRect.Width - logoSz) / 2f;
                ly = _brandRect.Y + (_brandRect.Height - logoSz) / 2f;
            }
            _logoRect = new Rectangle((int)lx, (int)ly, logoSz, logoSz);
            
            if (_brandBitmap != null)
            {
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(_brandBitmap, 
                        new Rectangle((int)lx, (int)ly, logoSz, logoSz), 
                        0, 0, _brandBitmap.Width, _brandBitmap.Height, 
                        GraphicsUnit.Pixel, 
                        ia);
                }
            }
            else
            {
                FluentIcons.DrawIcon(g, "scan", new RectangleF(lx, ly, logoSz, logoSz), Color.FromArgb((int)(opacity * 255), UiChrome.SurfaceTextPrimary), 0f);
            }

            // Rotated wordmark only on screen-edge vertical docks (capture phase) — annotation
            // column is too narrow/tall for it without covering tools.
            if (!ShowAnnotationChrome)
            {
                using (var brandFont = UiChrome.ChromeFont(5.2f, FontStyle.Bold))
                using (var textBrush = new SolidBrush(Color.FromArgb((int)(textOpacity * 255), UiChrome.SurfaceTextPrimary)))
                {
                    var state = g.Save();
                    g.TranslateTransform(_toolbarRect.X + pad + buttonSize / 2f, ly + logoSz + UiChrome.ScaleInt(8));
                    g.RotateTransform(90);

                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap
                    };

                    g.DrawString("CyberSnap", brandFont, textBrush, 0f, 0f, sf);
                    g.Restore(state);
                }
            }
        }
        else
        {
            int tier1Width = ShowAnnotationChrome
                ? GetToolbarPrimarySpan(_flyoutTools.Length + 2, GetAnnotationGroupSepFlyoutIndices().Count + 1, buttonSize, buttonSpacing, 0)
                : GetToolbarPrimarySpan(_mainBarTools.Length + 2, 1, buttonSize, buttonSpacing, 0);
            bool canShowText = ShowAnnotationChrome
                || _mainBarTools.Length >= 6;

            // Prefer the first laid-out tool (capture bar or annotation-only confirm dock).
            // Empty capture slots in confirm mode must not be used — they sit at (0,0) and
            // push the logo far away from the bar.
            int firstToolX = GetFirstVisibleToolbarButtonX();
            if (firstToolX < 0)
                firstToolX = _toolbarRect.X + pad + UiChrome.ScaleInt(28);

            float availableBrandWidth = Math.Max(0, firstToolX - _toolbarRect.X);
            int tempLogoSz = UiChrome.ScaleInt(10);
            float tempLx = _toolbarRect.X + pad + UiChrome.ScaleInt(6);
            float tempTextX = tempLx + tempLogoSz + UiChrome.ScaleInt(6);
            float tempTextW = firstToolX - tempTextX - UiChrome.ScaleInt(6);
            
            bool drawText = canShowText && (tempTextW >= UiChrome.ScaleInt(60));

            float lx;
            float ly;
            
            if (drawText)
            {
                logoSz = tempLogoSz;
                lx = tempLx;
                ly = _toolbarRect.Y + pad + (buttonSize - logoSz) / 2f - UiChrome.ScaleFloat(0.5f);
            }
            else
            {
                logoSz = UiChrome.ScaleInt(14); // Enlarged and centered when text is hidden
                // Keep the logo snug in the brand strip, just left of the first tool.
                float brandPad = UiChrome.ScaleFloat(6f);
                lx = _toolbarRect.X + Math.Max(brandPad, (availableBrandWidth - logoSz) / 2f);
                // Never let the logo drift more than a few px away from the first tool.
                float maxLx = firstToolX - logoSz - brandPad;
                if (maxLx >= _toolbarRect.X + brandPad)
                    lx = Math.Min(lx, maxLx);
                ly = _toolbarRect.Y + pad + (buttonSize - logoSz) / 2f - UiChrome.ScaleFloat(0.5f);
            }
            
            _logoRect = new Rectangle((int)lx, (int)ly, logoSz, logoSz);
            
            if (_brandBitmap != null)
            {
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(_brandBitmap, 
                        new Rectangle((int)lx, (int)ly, logoSz, logoSz), 
                        0, 0, _brandBitmap.Width, _brandBitmap.Height, 
                        GraphicsUnit.Pixel, 
                        ia);
                }
            }
            else
            {
                FluentIcons.DrawIcon(g, "scan", new RectangleF(lx, ly, logoSz, logoSz), Color.FromArgb((int)(opacity * 255), UiChrome.SurfaceTextPrimary), 0f);
            }

            if (drawText)
            {
                using (var brandFont = UiChrome.ChromeFont(5.8f, FontStyle.Bold))
                using (var textBrush = new SolidBrush(Color.FromArgb((int)(textOpacity * 255), UiChrome.SurfaceTextPrimary)))
                {
                    var textRect = new RectangleF(tempTextX, _toolbarRect.Y + pad - UiChrome.ScaleInt(1), tempTextW, buttonSize);
                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap,
                        Trimming = StringTrimming.EllipsisCharacter
                    };
                    g.DrawString("CyberSnap", brandFont, textBrush, textRect, sf);
                }
            }
        }

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

            // Divider before Position button (after last main bar tool)
            if (_mainBarTools.Length > 0 && PositionButtonIndex < _toolbarButtons.Length)
            {
                int lastIdx = _mainBarTools.Length - 1;
                var lastBtn = _toolbarButtons[lastIdx];
                var posBtn = _toolbarButtons[PositionButtonIndex];
                if (lastBtn.Width > 0 && posBtn.Width > 0)
                {
                    int p = IsVerticalDock
                        ? (lastBtn.Bottom + posBtn.Y) / 2
                        : (lastBtn.Right + posBtn.X) / 2;
                    dividerPositions.Add(p);
                }
            }
        }
        else
        {
            var tier2Seps = GetAnnotationGroupSepFlyoutIndices();
            int flyoutStartIdx = _mainBarTools.Length + 4;
            foreach (int flyoutIdx in tier2Seps)
            {
                int btnIdx = flyoutStartIdx + flyoutIdx;
                if (btnIdx < _toolbarButtons.Length && _toolbarButtons[btnIdx].Width > 0)
                {
                    int p = IsVerticalDock
                        ? _toolbarButtons[btnIdx].Bottom + (buttonSpacing + GroupGap) / 2
                        : _toolbarButtons[btnIdx].Right + (buttonSpacing + GroupGap) / 2;
                    dividerPositions.Add(p);
                }
            }

            // Divider before stroke/color (after last annotation tool). Position/Close are not on this dock.
            if (_flyoutTools.Length > 0
                && StrokeWidthButtonIndex < _toolbarButtons.Length
                && _toolbarButtons[StrokeWidthButtonIndex].Width > 0)
            {
                int lastFlyoutIdx = flyoutStartIdx + _flyoutTools.Length - 1;
                if (lastFlyoutIdx >= 0 && lastFlyoutIdx < _toolbarButtons.Length
                    && _toolbarButtons[lastFlyoutIdx].Width > 0)
                {
                    var lastFlyout = _toolbarButtons[lastFlyoutIdx];
                    var strokeBtn = _toolbarButtons[StrokeWidthButtonIndex];
                    int p = IsVerticalDock
                        ? (lastFlyout.Bottom + strokeBtn.Y) / 2
                        : (lastFlyout.Right + strokeBtn.X) / 2;
                    dividerPositions.Add(p);
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



        int drawingStartIdx = _mainBarTools.Length + 4;

        // 4. Draw all buttons
        for (int i = 0; i < BtnCount; i++)
        {
            var btn = _toolbarButtons[i];
            if (btn.Width <= 0 || btn.Height <= 0)
                continue;
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
                if (active)
                    WindowsDockRenderer.PaintActiveIndicator(g, btn, tierAccent);
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
                if (active)
                    WindowsDockRenderer.PaintActiveIndicator(g, btn, tierAccent);
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

            // Position (Move bar): slightly muted so it stays with Close as chrome.
            if (i == PositionButtonIndex)
            {
                WindowsDockRenderer.PaintButton(g, btn, active: false, hovered: hover, accent: tierAccent);
                int pa = hover ? 210 : 120;
                DrawIcon(g, _toolbarIcons[i], btn, Color.FromArgb(pa, UiChrome.SurfaceTextPrimary), active: false);
                continue;
            }

            WindowsDockRenderer.PaintButton(g, btn, active, hover, accent: tierAccent);

            int ia = active ? 255 : hover ? 240 : 200;
            var iconColor = active ? tierAccent : UiChrome.SurfaceTextPrimary;
            DrawIcon(g, _toolbarIcons[i], btn, Color.FromArgb(ia, iconColor.R, iconColor.G, iconColor.B), active);

            if (active)
                WindowsDockRenderer.PaintActiveIndicator(g, btn, tierAccent);

            // Hold-to-switch affordance on merged capture (rect ↔ center) and annotation groups.
            if (IsMergedHoldButton(i))
                PaintCaptureHoldHint(g, btn, tierAccent, buttonIndex: i);
        }

        // Draw menu activator (⋮). Soft accent pulse while the quick-start guide is open.
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
            dotsColor = Color.FromArgb((int)((UiChrome.IsDark ? 0.35f : 0.40f) * 0.80f * 255), UiChrome.SurfaceTextPrimary);
        }

        // Vertical kebab — compact glyph centered in a taller hit target (hover/click area
        // stays button-height; dots stay the classic tight ⋮ spacing).
        float tcx = _menuActivatorRect.X + _menuActivatorRect.Width / 2f;
        float tcy = _menuActivatorRect.Y + _menuActivatorRect.Height / 2f;
        float dotR = UiChrome.ScaleFloat(_highlightMenuActivatorForGuide ? 1.7f : 1.45f);
        float gap = UiChrome.ScaleFloat(_highlightMenuActivatorForGuide ? 5.6f : 5.2f);
        using (var brush = new SolidBrush(dotsColor))
        {
            for (int i = -1; i <= 1; i++)
            {
                float cy = tcy + i * gap;
                g.FillEllipse(brush, tcx - dotR, cy - dotR, dotR * 2f, dotR * 2f);
            }
        }

        g.SmoothingMode = SmoothingMode.Default;
        g.PixelOffsetMode = PixelOffsetMode.Default;
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
    /// Small chevron badge + hold-progress arc on a merged tool button so users
    /// discover the long-press alternate mode (Area ↔ From Center, shape/stroke groups, …).
    /// </summary>
    private void PaintCaptureHoldHint(Graphics g, Rectangle btn, Color accent, int buttonIndex)
    {
        bool holdingThis = _isMouseDownOnCaptureBtn
            && (_mergedHoldButtonIndex == buttonIndex
                || (_mergedHoldButtonIndex < 0 && buttonIndex == _mergedCaptureButtonIndex));

        bool popupForThis = _altCapturePopupOpen
            && (_mergedHoldButtonIndex == buttonIndex
                || (_mergedHoldButtonIndex < 0 && buttonIndex == _mergedCaptureButtonIndex));

        // Tiny corner chevron (always visible on merged slots).
        float s = UiChrome.ScaleFloat(3.2f);
        float cx = btn.Right - UiChrome.ScaleFloat(7f);
        float cy = btn.Bottom - UiChrome.ScaleFloat(7f);
        var chev = new[]
        {
            new PointF(cx - s, cy - s * 0.35f),
            new PointF(cx + s, cy - s * 0.35f),
            new PointF(cx, cy + s * 0.75f),
        };
        int chevA = holdingThis || popupForThis ? 230 : 120;
        using (var brush = new SolidBrush(Color.FromArgb(chevA, accent)))
            g.FillPolygon(brush, chev);

        // Progress ring while holding toward the 300ms threshold.
        if (holdingThis && _mouseDownStartTime != DateTime.MinValue)
        {
            float raw = (float)(DateTime.UtcNow - _mouseDownStartTime).TotalMilliseconds / 300f;
            float t = Math.Clamp(raw, 0f, 1f);
            if (t > 0.02f)
            {
                float pad = UiChrome.ScaleFloat(2.5f);
                var ring = RectangleF.Inflate(btn, -pad, -pad);
                using var pen = new Pen(Color.FromArgb((int)(80 + 140 * t), accent), UiChrome.ScaleFloat(1.6f))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                // Sweep from top, clockwise.
                g.DrawArc(pen, ring, -90f, 360f * t);
            }
        }
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
        int gap = UiChrome.ScaledToolbarInnerPadding;
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
            var iconId = toolId switch { "crop" => "rect", "rect" => "captureRect", var id => id };
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
            alts.Add(defaultMode == CaptureMode.Center ? "rect" : "center");
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
