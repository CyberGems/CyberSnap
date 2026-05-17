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

    private void PaintToolbar(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var r = new Rectangle(_toolbarRect.X, _toolbarRect.Y,
            _toolbarRect.Width, _toolbarRect.Height);

        float cr = UiChrome.ScaledToolbarCornerRadius;

        WindowsDockRenderer.PaintSurface(g, r, cr);

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

        int pad = UiChrome.ScaledToolbarInnerPadding;
        int buttonSize = UiChrome.ScaledToolbarButtonSize;
        int buttonSpacing = UiChrome.ScaledToolbarButtonSpacing;
        int closeIdx = _mainBarTools.Length + 1;

        // 1. Divider line splitting Tier 1 from Tier 2
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

        // 2. Tier 1 Dividers: after index 4 (between Capture tools and System tools)
        if (_toolbarButtons.Length > 4)
        {
            if (IsVerticalDock)
            {
                int sy = _toolbarButtons[4].Bottom + (buttonSpacing + GroupGap) / 2;
                int sx1 = _toolbarButtons[4].X + 4;
                int sx2 = _toolbarButtons[4].Right - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx1, sy), new Point(sx2, sy));
            }
            else
            {
                int sx = _toolbarButtons[4].Right + (buttonSpacing + GroupGap) / 2;
                int sy1 = _toolbarButtons[4].Y + 4;
                int sy2 = _toolbarButtons[4].Bottom - 4;
                WindowsDockRenderer.PaintDivider(g, new Point(sx, sy1), new Point(sx, sy2));
            }
        }

        // 3. Tier 2 Dividers: after indices 7, 9, 13, 15, 18, 20
        int[] tier2Seps = { 7, 9, 13, 15, 18, 20 };
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

            // Color dot button
            if (_toolbarIcons[i] == "color")
            {
                WindowsDockRenderer.PaintButton(g, btn, active, hover);
                int dotSize = 16;
                float dx = btn.X + (btn.Width - dotSize) / 2f;
                float dy = btn.Y + (btn.Height - dotSize) / 2f;
                int colorAlpha = active ? 255 : hover ? 230 : 175;
                g.FillEllipse(SketchRenderer.GetToolColorBrush(Color.FromArgb(colorAlpha, _toolColor.R, _toolColor.G, _toolColor.B)), dx, dy, dotSize, dotSize);
                continue;
            }

            WindowsDockRenderer.PaintButton(g, btn, active, hover);

            int ia = active ? 255 : hover ? 240 : i == closeIdx ? 130 : 200;
            var iconColor = active ? UiChrome.AccentColor : UiChrome.SurfaceTextPrimary;
            DrawIcon(g, _toolbarIcons[i], btn, Color.FromArgb(ia, iconColor.R, iconColor.G, iconColor.B), active);
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
        g.Restore(state);
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

        // Try Streamline icon first (line=inactive, solid=active)
        if (FluentIcons.HasIcon(icon))
        {
            float inset = active ? 6f : 7f;
            FluentIcons.DrawIcon(g, icon, b, c, inset, active);
            return;
        }

        return;
    }
}
