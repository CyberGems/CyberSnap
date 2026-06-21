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
        if (_flyoutTools.Length > 0)
        {
            RectangleF tier2Rect;
            if (IsVerticalDock)
            {
                float dividerX = _toolbarRect.X + pad + buttonSize + buttonSpacing / 2f;
                tier2Rect = new RectangleF(dividerX, r.Y, r.Right - dividerX, r.Height);
            }
            else
            {
                float dividerY = _toolbarRect.Y + pad + buttonSize + buttonSpacing / 2f;
                tier2Rect = new RectangleF(r.X, dividerY, r.Width, r.Bottom - dividerY);
            }
            WindowsDockRenderer.PaintSurfaceBg(g, tier2Rect, UiChrome.SurfaceTier2, cr, IsVerticalDock);
        }

        // Render sleek CyberGems premium accent border outline around the panel
        using (var path = WindowsDockRenderer.RoundedRect(r, cr))
        using (var pen = new Pen(Color.FromArgb(UiChrome.IsDark ? 80 : 50, UiChrome.AccentColor), 1f))
            g.DrawPath(pen, path);

        // Render gorgeous glowing neon accent line along the docked screen edge of the bar
        using (var pen = new Pen(UiChrome.AccentColor, 2f))
        {
            var dock = ActiveDockSide;
            if (dock == CaptureDockSide.Top)
            {
                g.DrawLine(pen, r.X + cr, r.Y + 1f, r.Right - cr, r.Y + 1f);
            }
            else if (dock == CaptureDockSide.Bottom)
            {
                g.DrawLine(pen, r.X + cr, r.Bottom - 1f, r.Right - cr, r.Bottom - 1f);
            }
            else if (dock == CaptureDockSide.Left)
            {
                g.DrawLine(pen, r.X + 1f, r.Y + cr, r.X + 1f, r.Bottom - cr);
            }
            else if (dock == CaptureDockSide.Right)
            {
                g.DrawLine(pen, r.Right - 1f, r.Y + cr, r.Right - 1f, r.Bottom - cr);
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
        var cm = new ColorMatrix(new float[][]
        {
            new float[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
            new float[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
            new float[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
            new float[] { 0f,     0f,     0f,     opacity, 0f },
            new float[] { 0f,     0f,     0f,     0f, 1f }
        });

        int logoSz = UiChrome.ScaleInt(10); // Reduced by 20% as requested

        var oldHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; // Force ClearType subpixel LCD rendering for pristine crispness

        if (IsVerticalDock)
        {
            // Draw logo icon at the top of Column 1 (centered)
            float lx = _toolbarRect.X + pad + (buttonSize - logoSz) / 2f;
            float ly = _toolbarRect.Y + pad + UiChrome.ScaleInt(6);
            
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

            // Draw rotated CyberSnap label running vertically downwards
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
        else
        {
            int tier1Width = GetToolbarPrimarySpan(_mainBarTools.Length + 4, 2, buttonSize, buttonSpacing, 0);
            int tier2Width = GetToolbarPrimarySpan(_flyoutTools.Length, 2, buttonSize, buttonSpacing, 0);
            bool canShowText = (tier2Width - tier1Width >= UiChrome.ScaleInt(80)) || (_mainBarTools.Length >= 6);

            float availableBrandWidth = _toolbarButtons[0].X - _toolbarRect.X;
            int tempLogoSz = UiChrome.ScaleInt(10);
            float tempLx = _toolbarRect.X + pad + UiChrome.ScaleInt(6);
            float tempTextX = tempLx + tempLogoSz + UiChrome.ScaleInt(6);
            float tempTextW = _toolbarButtons[0].X - tempTextX - UiChrome.ScaleInt(6);
            
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
                lx = _toolbarRect.X + (availableBrandWidth - logoSz) / 2f;
                ly = _toolbarRect.Y + pad + (buttonSize - logoSz) / 2f - UiChrome.ScaleFloat(0.5f);
            }
            
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

        // 1. Divider line splitting Tier 1 from Tier 2
        if (_flyoutTools.Length > 0)
        {
            if (IsVerticalDock)
            {
                int dividerX = _toolbarRect.X + pad + buttonSize + buttonSpacing / 2;
                int inset = UiChrome.ScaleInt(12);
                WindowsDockRenderer.PaintDivider(g, new Point(dividerX, r.Y + inset), new Point(dividerX, r.Bottom - inset));
            }
            else
            {
                int dividerY = _toolbarRect.Y + pad + buttonSize + buttonSpacing / 2;
                int inset = UiChrome.ScaleInt(12);
                WindowsDockRenderer.PaintDivider(g, new Point(r.X + inset, dividerY), new Point(r.Right - inset, dividerY));
            }
        }

        // 2. Tier 1 Dividers: after scroll (3) and last capture tool
        int[] tier1SepIndices = { 3 };
        foreach (int idx in tier1SepIndices)
        {
            if (idx < 0 || idx >= _toolbarButtons.Length) continue;
            if (IsVerticalDock)
            {
                int sy = _toolbarButtons[idx].Bottom + (buttonSpacing + GroupGap) / 2;
                int sx1 = _toolbarButtons[idx].X + 4;
                int sx2 = _toolbarButtons[idx].Right - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx1, sy), new Point(sx2, sy));
            }
            else
            {
                int sx = _toolbarButtons[idx].Right + (buttonSpacing + GroupGap) / 2;
                int sy1 = _toolbarButtons[idx].Y + 4;
                int sy2 = _toolbarButtons[idx].Bottom - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx, sy1), new Point(sx, sy2));
            }
        }
        int lastCaptureIdx = _mainBarTools.Length - 1;
        if (lastCaptureIdx >= 0 && _toolbarButtons.Length > lastCaptureIdx)
        {
            if (IsVerticalDock)
            {
                int sy = _toolbarButtons[lastCaptureIdx].Bottom + (buttonSpacing + GroupGap) / 2;
                int sx1 = _toolbarButtons[lastCaptureIdx].X + 4;
                int sx2 = _toolbarButtons[lastCaptureIdx].Right - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx1, sy), new Point(sx2, sy));
            }
            else
            {
                int sx = _toolbarButtons[lastCaptureIdx].Right + (buttonSpacing + GroupGap) / 2;
                int sy1 = _toolbarButtons[lastCaptureIdx].Y + 4;
                int sy2 = _toolbarButtons[lastCaptureIdx].Bottom - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx, sy1), new Point(sx, sy2));
            }
        }

        // 3. Tier 2 Dividers: after indices offset by _mainBarTools.Length + 4
        int drawingStartIdx = _mainBarTools.Length + 4;
        int[] tier2Offsets = { 1, 8 };
        int[] tier2Seps = tier2Offsets.Select(offset => drawingStartIdx + offset).ToArray();
        foreach (int idx in tier2Seps)
        {
            if (idx < 0 || idx >= _toolbarButtons.Length) continue;
            if (IsVerticalDock)
            {
                int sy = _toolbarButtons[idx].Bottom + (buttonSpacing + GroupGap) / 2;
                int sx1 = _toolbarButtons[idx].X + 4;
                int sx2 = _toolbarButtons[idx].Right - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx1, sy), new Point(sx2, sy));
            }
            else
            {
                int sx = _toolbarButtons[idx].Right + (buttonSpacing + GroupGap) / 2;
                int sy1 = _toolbarButtons[idx].Y + 4;
                int sy2 = _toolbarButtons[idx].Bottom - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx, sy1), new Point(sx, sy2));
            }
        }

        // 4. Draw all buttons
        for (int i = 0; i < BtnCount; i++)
        {
            var btn = _toolbarButtons[i];
            bool active = _toolbarModes[i] is { } && string.Equals(_toolbarToolIds[i], _activeToolId, StringComparison.OrdinalIgnoreCase);
            bool hover = _hoveredButton == i;
            bool isTier2 = i >= drawingStartIdx;
            var tierAccent = isTier2 ? UiChrome.AccentTier2 : UiChrome.AccentColor;

            // Stroke width button (shows line thickness preview in current tool color)
            if (_toolbarIcons[i] == "strokeWidth")
            {
                WindowsDockRenderer.PaintButton(g, btn, active, hover, accent: tierAccent);
                float lineY = btn.Y + btn.Height / 2f;
                float margin = 6f;
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
                continue;
            }

            WindowsDockRenderer.PaintButton(g, btn, active, hover, accent: tierAccent);

            int ia = active ? 255 : hover ? 240 : (i == CloseButtonIndex || i == PositionButtonIndex) ? 130 : 200;
            var iconColor = active ? tierAccent : UiChrome.SurfaceTextPrimary;
            DrawIcon(g, _toolbarIcons[i], btn, Color.FromArgb(ia, iconColor.R, iconColor.G, iconColor.B), active);
        }

        // Draw elegant mini menu activator
        if (_hoveredMenuActivator)
        {
            using (var path = WindowsDockRenderer.RoundedRect(_menuActivatorRect, UiChrome.ScaleInt(3)))
            using (var brush = new SolidBrush(Color.FromArgb(30, UiChrome.AccentColor)))
                g.FillPath(brush, path);
        }

        int triW = UiChrome.ScaleInt(6);
        int triH = UiChrome.ScaleInt(4);
        float tcx = _menuActivatorRect.X + _menuActivatorRect.Width / 2f;
        float tcy = _menuActivatorRect.Y + _menuActivatorRect.Height / 2f;

        PointF[] points = new PointF[]
        {
            new PointF(tcx - triW / 2f, tcy - triH / 2f),
            new PointF(tcx + triW / 2f, tcy - triH / 2f),
            new PointF(tcx, tcy + triH / 2f)
        };

        Color arrowColor = _hoveredMenuActivator 
            ? UiChrome.AccentColor 
            : Color.FromArgb((int)((UiChrome.IsDark ? 0.35f : 0.40f) * 0.80f * 255), UiChrome.SurfaceTextPrimary);

        using (var brush = new SolidBrush(arrowColor))
        {
            g.FillPolygon(brush, points);
        }

        g.SmoothingMode = SmoothingMode.Default;
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    private static Color ScaleAlpha(Color color, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        return Color.FromArgb((int)Math.Round(color.A * factor), color.R, color.G, color.B);
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
        if (_mergedCaptureButtonIndex < 0 || _mergedCaptureButtonIndex >= _toolbarButtons.Length)
            return;

        int buttonSize = UiChrome.ScaledToolbarButtonSize;
        int containerPadding = UiChrome.ScaleInt(4);
        int containerSize = buttonSize + containerPadding * 2;
        var primaryBtn = _toolbarButtons[_mergedCaptureButtonIndex];
        int gap = UiChrome.ScaledToolbarInnerPadding;

        int x = primaryBtn.X + (primaryBtn.Width - containerSize) / 2;
        int y = primaryBtn.Y + (primaryBtn.Height - containerSize) / 2;

        var dock = ActiveDockSide;
        if (dock == CaptureDockSide.Bottom)
        {
            y = primaryBtn.Y - containerSize - gap;
        }
        else if (dock == CaptureDockSide.Top)
        {
            y = primaryBtn.Bottom + gap;
        }
        else if (dock == CaptureDockSide.Left)
        {
            x = primaryBtn.Right + gap;
        }
        else if (dock == CaptureDockSide.Right)
        {
            x = primaryBtn.X - containerSize - gap;
        }

        _altCaptureButtonRect = new Rectangle(x, y, containerSize, containerSize);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        float cr = UiChrome.ScaledToolbarCornerRadius;
        WindowsDockRenderer.PaintShadow(g, _altCaptureButtonRect, cr);

        using (var path = WindowsDockRenderer.RoundedRect(_altCaptureButtonRect, cr))
        using (var brush = new SolidBrush(UiChrome.SurfaceTier1))
            g.FillPath(brush, path);

        using (var path = WindowsDockRenderer.RoundedRect(_altCaptureButtonRect, cr))
        using (var pen = new Pen(Color.FromArgb(UiChrome.IsDark ? 80 : 50, UiChrome.AccentColor), 1f))
            g.DrawPath(pen, path);

        bool hover = _hoveredAltCaptureBtn;
        var btnRect = new Rectangle(
            _altCaptureButtonRect.X + containerPadding,
            _altCaptureButtonRect.Y + containerPadding,
            buttonSize,
            buttonSize);

        WindowsDockRenderer.PaintButton(g, btnRect, active: false, hovered: hover, accent: UiChrome.AccentColor);

        var settings = Services.SettingsService.LoadStatic();
        var defaultMode = settings?.DefaultCaptureMode ?? CaptureMode.Rectangle;
        var altToolId = (defaultMode == CaptureMode.Center) ? "rect" : "center";

        var altIcon = altToolId switch { "crop" => "rect", "rect" => "captureRect", var id => id };

        int ia = hover ? 240 : 200;
        var iconColor = UiChrome.SurfaceTextPrimary;
        DrawIcon(g, altIcon, btnRect, Color.FromArgb(ia, iconColor.R, iconColor.G, iconColor.B), active: false);
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

    private static void DrawIcon(Graphics g, string icon, Rectangle b, Color c, bool active = false)
    {
        if (icon == "color") return;

        var iconId = icon == "scroll" ? "scrollCapture" : icon;

        // Try Streamline icon first (line=inactive, solid=active)
        if (FluentIcons.HasIcon(iconId))
        {
            float inset = active ? 6f : 7f;
            FluentIcons.DrawIcon(g, iconId, b, c, inset, active);
            return;
        }

        return;
    }
}
