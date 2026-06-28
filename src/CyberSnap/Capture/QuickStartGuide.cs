using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed class QuickStartGuide : Form
{
    private const int MaxWidth = 480;
    private const int MinWidth = 336;
    private const int WidthSlack = 10;
    private const int TipMeasureSlack = 6;
    private const int PadX = 18;
    private const int PadY = 16;
    private const int FooterBottomPad = 14;
    private const int HeaderHeight = 32;
    private const int HeroMinHeight = 44;
    private const int HeroPad = 12;
    private const int ShortcutRowHeight = 30;
    private const int TipRowMinHeight = 28;
    private const int TipRowGap = 3;
    private const int TipTextPad = 6;
    private const int SectionGap = 16;
    private const int SectionLabelHeight = 18;
    private const int IconColWidth = 20;
    private const int IconSize = 18;
    private const int IconTextGap = 10;
    private const int KbdPadH = 8;
    private const int KbdPadV = 3;
    private const int KbdLabelGap = 8;
    private const int ShortcutColGap = 12;
    private const int TipIconSize = 14;
    private const float Corner = 10f;

    private readonly Font _headerFont = UiChrome.ChromeFont(11f, FontStyle.Bold);
    private readonly Font _sectionFont = UiChrome.ChromeFont(7.5f, FontStyle.Bold);
    private readonly Font _bodyFont = UiChrome.ChromeFont(9f);
    private readonly Font _bodyMediumFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
    private readonly Font _keyFont = UiChrome.ChromeFont(8f, FontStyle.Bold);
    private readonly Font _footerFont = UiChrome.ChromeFont(7.5f);
    private readonly Font _brandFont = UiChrome.ChromeFont(7f, FontStyle.Bold);

    private record ShortcutDef(string Key, string Label);
    private record TipDef(string? IconId, string Text);

    private ShortcutDef[] _shortcuts = Array.Empty<ShortcutDef>();
    private TipDef[] _tips = Array.Empty<TipDef>();
    private string _title = "";
    private string _captureText = "";
    private string _shortcutsTitle = "";
    private string _tipsTitle = "";
    private string _footerText = "";
    private string _brandText = "";
    private int _totalHeight;
    private int _contentWidth;
    private int _heroHeight = HeroMinHeight;
    private int _shortcutColWidth;
    private int[] _tipHeights = Array.Empty<int>();
    private Rectangle _closeRect;
    private bool _closeHovered;

    public QuickStartGuide()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = UiChrome.SurfaceTier1;
        ForeColor = UiChrome.SurfaceTextPrimary;
    }

    protected override bool ShowWithoutActivation => true;

    private const int WM_NCHITTEST = 0x0084;
    private static readonly IntPtr HTCLIENT = new(1);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = HTCLIENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_closeRect.Contains(e.Location))
        {
            Close();
            return;
        }
        Close();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hovered = _closeRect.Contains(e.Location);
        if (hovered != _closeHovered)
        {
            _closeHovered = hovered;
            Invalidate(_closeRect);
        }
        Cursor = hovered ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_closeHovered)
        {
            _closeHovered = false;
            Invalidate(_closeRect);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Close();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;
            cp.ExStyle |= 0x08000000;
            return cp;
        }
    }

    public void ShowNear(IWin32Window owner, Rectangle anchorScreenBounds, bool above)
    {
        CyberSnap.UI.Theme.Refresh();
        BackColor = UiChrome.SurfaceTier1;
        ForeColor = UiChrome.SurfaceTextPrimary;

        bool isSpanish = string.Equals(
            SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);

        _title = isSpanish ? "Guía rápida" : "Quick Start";
        _captureText = isSpanish
            ? "Arrastra para seleccionar el área de captura"
            : "Drag to select the capture area";
        _shortcutsTitle = isSpanish ? "ATAJOS DE TECLADO" : "KEYBOARD SHORTCUTS";
        _tipsTitle = isSpanish ? "SOBRE LA BARRA" : "ABOUT THE TOOLBAR";
        _footerText = isSpanish ? "Click o Esc para cerrar" : "Click or Esc to close";
        _brandText = "CyberSnap";

        _shortcuts = isSpanish
            ? new[]
            {
                new ShortcutDef("Enter", "Capturar"),
                new ShortcutDef("Esc", "Cancelar"),
                new ShortcutDef("Ctrl+Z", "Deshacer"),
                new ShortcutDef("Ctrl+Y", "Rehacer"),
            }
            : new[]
            {
                new ShortcutDef("Enter", "Capture"),
                new ShortcutDef("Esc", "Cancel"),
                new ShortcutDef("Ctrl+Z", "Undo"),
                new ShortcutDef("Ctrl+Y", "Redo"),
            };

        _tips = isSpanish
            ? new[]
            {
                new TipDef("menu", "▼ abre tools ocultas y preferencias"),
                new TipDef("menu", "También: aviso al cancelar y ayudas"),
                new TipDef("sticker", "Anotar solo después de capturar"),
                new TipDef("captureRect", "Clic derecho en tool → ocultarla"),
            }
            : new[]
            {
                new TipDef("menu", "▼ opens hidden tools and preferences"),
                new TipDef("menu", "Also: cancel prompt and help tips"),
                new TipDef("sticker", "Annotate only after capture"),
                new TipDef("captureRect", "Right-click tool → hide from bar"),
            };

        using var g = CreateGraphics();

        int maxShortcutCellW = 0;
        foreach (var sc in _shortcuts)
        {
            int cellW = MeasureShortcutCell(g, sc);
            maxShortcutCellW = Math.Max(maxShortcutCellW, cellW);
        }

        _contentWidth = Math.Max(
            ComputeReferenceContentWidth(g),
            maxShortcutCellW * 2 + ShortcutColGap);
        int width = Math.Min(MaxWidth, Math.Max(MinWidth, _contentWidth + PadX * 2));
        _contentWidth = width - PadX * 2;
        _shortcutColWidth = maxShortcutCellW;

        int tipTextWidth = _contentWidth - IconColWidth - IconTextGap;
        _tipHeights = new int[_tips.Length];
        int tipsBlockHeight = 0;
        for (int i = 0; i < _tips.Length; i++)
        {
            _tipHeights[i] = MeasureTipRowHeight(g, _tips[i].Text, tipTextWidth);
            tipsBlockHeight += _tipHeights[i];
            if (i < _tips.Length - 1)
                tipsBlockHeight += TipRowGap;
        }

        int heroTextWidth = _contentWidth - HeroPad * 2 - IconSize - IconTextGap;
        var heroTextSize = TextRenderer.MeasureText(g, _captureText, _bodyMediumFont,
            new Size(heroTextWidth, 0),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        _heroHeight = Math.Max(HeroMinHeight, heroTextSize.Height + HeroPad * 2);

        int y = PadY;
        y += HeaderHeight + SectionGap;
        y += _heroHeight + SectionGap;
        y += SectionLabelHeight + 8;
        y += tipsBlockHeight + 6;
        y += SectionLabelHeight + 10;
        int shortcutRows = (_shortcuts.Length + 1) / 2;
        y += shortcutRows * ShortcutRowHeight + SectionGap;
        y += SectionGap + 1 + 14 + 16 + FooterBottomPad + PadY;
        _totalHeight = y;

        int x = anchorScreenBounds.Left + (anchorScreenBounds.Width - width) / 2;
        int gap = 12;
        // When above the anchor, grow upward so the bottom edge stays fixed.
        int ay = above
            ? anchorScreenBounds.Top - _totalHeight - gap
            : anchorScreenBounds.Bottom + gap;

        var screen = Screen.FromRectangle(anchorScreenBounds).WorkingArea;
        x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - width - 4));
        ay = Math.Clamp(ay, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - _totalHeight - 4));

        Bounds = new Rectangle(x, ay, width, _totalHeight);
        Region?.Dispose();
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, width, _totalHeight), Corner))
            Region = new Region(path);

        Show(owner);

        try
        {
            Native.Dwm.TrySetWindowCornerPreference(Handle, Native.Dwm.DWMWCP_ROUND);
            Native.Dwm.TrySetImmersiveDarkMode(Handle, UiChrome.IsDark);
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var accent = UiChrome.AccentColor;
        var bounds = new RectangleF(0, 0, Width, Height);

        WindowsDockRenderer.PaintShadow(g, bounds, Corner);
        using (var path = WindowsDockRenderer.RoundedRect(bounds, Corner))
        {
            using var bgBrush = new SolidBrush(UiChrome.SurfaceTier1);
            g.FillPath(bgBrush, path);
            using var borderPen = new Pen(Color.FromArgb(UiChrome.IsDark ? 80 : 50, accent), 1f);
            g.DrawPath(borderPen, path);
        }

        PaintTopAccentLine(g, bounds, accent);

        int curY = PadY;

        // Header
        const int closeBtnSize = 14;
        _closeRect = new Rectangle(Width - PadX - closeBtnSize - 6, PadY - 2,
            closeBtnSize + 10, closeBtnSize + 10);

        if (_closeHovered)
        {
            using var hoverPath = WindowsDockRenderer.RoundedRect(_closeRect, 5f);
            using var hoverBrush = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 28 : 20, accent));
            g.FillPath(hoverBrush, hoverPath);
        }

        var titleIconRect = new RectangleF(PadX, curY + 6, 16, 16);
        FluentIcons.DrawIcon(g, "info", titleIconRect, accent, iconInset: 1f);

        var headerRect = new Rectangle(PadX + 22, curY, _contentWidth - closeBtnSize - 28, HeaderHeight);
        TextRenderer.DrawText(g, _title, _headerFont, headerRect,
            UiChrome.SurfaceTextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var closeColor = _closeHovered
            ? UiChrome.SurfaceTextPrimary
            : Color.FromArgb(140, UiChrome.SurfaceTextSecondary);
        FluentIcons.DrawIcon(g, "close",
            new RectangleF(_closeRect.X + 5, _closeRect.Y + 5, closeBtnSize, closeBtnSize),
            closeColor, iconInset: 0f);
        curY += HeaderHeight + SectionGap;

        // Hero call-to-action card
        var heroRect = new RectangleF(PadX, curY, _contentWidth, _heroHeight);
        using (var heroPath = WindowsDockRenderer.RoundedRect(heroRect, 7f))
        {
            using var heroBg = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 32 : 24, accent));
            g.FillPath(heroBg, heroPath);
            using var heroBorder = new Pen(Color.FromArgb(UiChrome.IsDark ? 90 : 70, accent), 1f);
            g.DrawPath(heroBorder, heroPath);
        }

        var heroIconRect = new RectangleF(PadX + HeroPad, curY + (_heroHeight - IconSize) / 2f, IconSize, IconSize);
        FluentIcons.DrawIcon(g, "captureRect", heroIconRect, accent, iconInset: 0f);

        var heroTextRect = new Rectangle(
            PadX + HeroPad + IconSize + IconTextGap,
            (int)curY + HeroPad,
            _contentWidth - HeroPad * 2 - IconSize - IconTextGap,
            _heroHeight - HeroPad * 2);
        TextRenderer.DrawText(g, _captureText, _bodyMediumFont, heroTextRect,
            UiChrome.SurfaceTextPrimary,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        curY += _heroHeight + SectionGap;

        // Tips (before shortcuts — context first for new users)
        curY = PaintSectionLabel(g, _tipsTitle, curY) + 8;
        curY = PaintTips(g, curY) + SectionGap;

        // Shortcuts
        curY = PaintSectionLabel(g, _shortcutsTitle, curY) + 10;
        curY = PaintShortcutGrid(g, curY);

        // Footer divider + text (inset from rounded bottom corners)
        const int footerBlockHeight = 34;
        int footerY = Height - FooterBottomPad - PadY - footerBlockHeight;
        using (var footerSep = new Pen(UiChrome.SurfaceBorderSubtle, 1f))
            g.DrawLine(footerSep, PadX, footerY, Width - PadX, footerY);

        var footerRect = new Rectangle(PadX, footerY + 6, _contentWidth, 14);
        TextRenderer.DrawText(g, _footerText, _footerFont, footerRect,
            UiChrome.SurfaceTextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int brandH = TextRenderer.MeasureText(g, _brandText, _brandFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Height;
        int brandW = TextRenderer.MeasureText(g, _brandText, _brandFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;
        var brandRect = new Rectangle((Width - brandW) / 2, footerY + 22, brandW + 4, brandH + 2);
        TextRenderer.DrawText(g, _brandText, _brandFont, brandRect,
            Color.FromArgb(UiChrome.IsDark ? 100 : 80, accent),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static void PaintTopAccentLine(Graphics g, RectangleF bounds, Color accent)
    {
        float inset = 1f;
        float y = bounds.Y + inset + 0.5f;
        float x0 = bounds.X + Corner;
        float x1 = bounds.Right - Corner;
        var fade = Color.FromArgb(0, accent);
        using var brush = new LinearGradientBrush(
            new PointF(x0, y), new PointF(x1, y), accent, accent)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[] { fade, accent, accent, fade },
                Positions = new[] { 0f, 0.12f, 0.88f, 1f },
            },
        };
        using var pen = new Pen(brush, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, x0, y, x1, y);
    }

    private int PaintSectionLabel(Graphics g, string text, int y)
    {
        var rect = new Rectangle(PadX, y, _contentWidth, SectionLabelHeight);
        TextRenderer.DrawText(g, text, _sectionFont, rect,
            UiChrome.SurfaceTextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return y + SectionLabelHeight;
    }

    private int PaintShortcutGrid(Graphics g, int startY)
    {
        var accent = UiChrome.AccentColor;
        int curY = startY;

        for (int i = 0; i < _shortcuts.Length; i++)
        {
            int col = i % 2;
            int row = i / 2;
            if (col == 0 && i > 0)
                curY = startY + row * ShortcutRowHeight;

            int cellX = PadX + col * (_shortcutColWidth + ShortcutColGap);
            int cellY = startY + row * ShortcutRowHeight;
            PaintShortcutCell(g, _shortcuts[i], cellX, cellY, accent);
        }

        int rows = (_shortcuts.Length + 1) / 2;
        return startY + rows * ShortcutRowHeight;
    }

    private void PaintShortcutCell(Graphics g, ShortcutDef sc, int x, int y, Color accent)
    {
        int keyW = TextRenderer.MeasureText(g, sc.Key, _keyFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;
        int labelW = TextRenderer.MeasureText(g, sc.Label, _bodyFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;

        int kbdW = keyW + KbdPadH * 2;
        int kbdH = ShortcutRowHeight - 8;
        var kbdRect = new RectangleF(x, y + 4, kbdW, kbdH);

        using (var kbdPath = WindowsDockRenderer.RoundedRect(kbdRect, 4f))
        {
            using var kbdBg = new SolidBrush(UiChrome.SurfaceTier2);
            g.FillPath(kbdBg, kbdPath);
            using var kbdBorder = new Pen(Color.FromArgb(UiChrome.IsDark ? 70 : 55, accent), 1f);
            g.DrawPath(kbdBorder, kbdPath);
        }

        TextRenderer.DrawText(g, sc.Key, _keyFont,
            new Rectangle(x + KbdPadH, y + 4, keyW + 2, kbdH),
            accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, sc.Label, _bodyFont,
            new Rectangle(x + kbdW + KbdLabelGap, y + 4, labelW + 4, kbdH),
            UiChrome.SurfaceTextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private int MeasureShortcutCell(Graphics g, ShortcutDef sc)
    {
        int keyW = TextRenderer.MeasureText(g, sc.Key, _keyFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;
        int labelW = TextRenderer.MeasureText(g, sc.Label, _bodyFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;
        return keyW + KbdPadH * 2 + KbdLabelGap + labelW;
    }

    private int PaintTips(Graphics g, int startY)
    {
        int curY = startY;
        var iconColor = Color.FromArgb(170, UiChrome.SurfaceTextSecondary);
        int tipTextWidth = Width - PadX - IconColWidth - IconTextGap - PadX;
        using var textBrush = new SolidBrush(UiChrome.SurfaceTextSecondary);
        using var tipFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
        };

        for (int i = 0; i < _tips.Length; i++)
        {
            var tip = _tips[i];
            int rowH = i < _tipHeights.Length ? _tipHeights[i] : TipRowMinHeight;
            var rowRect = new Rectangle(PadX, curY, Width - PadX * 2, rowH);

            var saved = g.Save();
            g.SetClip(rowRect);

            float iconY = curY + (rowH - TipIconSize) / 2f;
            var iconRect = new RectangleF(PadX + 2, iconY, TipIconSize, TipIconSize);

            if (tip.IconId != null && FluentIcons.HasIcon(tip.IconId))
            {
                FluentIcons.DrawIcon(g, tip.IconId, iconRect, iconColor, iconInset: 1f);
            }
            else
            {
                float dotX = PadX + IconColWidth / 2f;
                float dotY = curY + rowH / 2f;
                using var dotBrush = new SolidBrush(Color.FromArgb(100, UiChrome.SurfaceTextMuted));
                g.FillEllipse(dotBrush, dotX - 2f, dotY - 2f, 4f, 4f);
            }

            int tipTextX = PadX + IconColWidth + IconTextGap;
            var tipTextRect = new RectangleF(tipTextX, curY, tipTextWidth, rowH);
            g.DrawString(tip.Text, _bodyFont, textBrush, tipTextRect, tipFormat);

            g.Restore(saved);
            curY += rowH;
            if (i < _tips.Length - 1)
                curY += TipRowGap;
        }

        return curY;
    }

    private int ComputeReferenceContentWidth(Graphics g)
    {
        const string refTitle = "Quick Start";
        const string refCapture = "Drag to select the capture area";
        var refShortcuts = new[]
        {
            new ShortcutDef("Enter", "Capture"),
            new ShortcutDef("Esc", "Cancel"),
            new ShortcutDef("Ctrl+Z", "Undo"),
            new ShortcutDef("Ctrl+Y", "Redo"),
        };
        var refTips = new[]
        {
            "▼ opens hidden tools and preferences",
            "Also: cancel prompt and help tips",
            "Annotate only after capture",
            "Right-click tool → hide from bar",
        };

        int maxTextWidth = TextRenderer.MeasureText(g, refTitle, _headerFont, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

        int maxShortcutCellW = 0;
        foreach (var sc in refShortcuts)
            maxShortcutCellW = Math.Max(maxShortcutCellW, MeasureShortcutCell(g, sc));
        maxTextWidth = Math.Max(maxTextWidth, maxShortcutCellW * 2 + ShortcutColGap);

        int heroLineW = HeroPad * 2 + IconSize + IconTextGap +
            TextRenderer.MeasureText(g, refCapture, _bodyMediumFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        maxTextWidth = Math.Max(maxTextWidth, heroLineW);

        foreach (var tip in refTips)
        {
            int tw = MeasureTipLineWidth(g, tip) + TipMeasureSlack;
            maxTextWidth = Math.Max(maxTextWidth, tw + IconColWidth + IconTextGap);
        }

        return Math.Max(MinWidth - PadX * 2, maxTextWidth + WidthSlack);
    }

    private static int MeasureTipLineWidth(Graphics g, string text, Font font)
    {
        var size = g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);
        return (int)Math.Ceiling(size.Width);
    }

    private int MeasureTipLineWidth(Graphics g, string text)
        => MeasureTipLineWidth(g, text, _bodyFont);

    private int MeasureTipRowHeight(Graphics g, string text, int tipTextWidth)
    {
        using var format = StringFormat.GenericTypographic;
        var size = g.MeasureString(text, _bodyFont, tipTextWidth, format);
        return Math.Max(TipRowMinHeight, (int)Math.Ceiling(size.Height) + TipTextPad);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Region?.Dispose();
            _headerFont.Dispose();
            _sectionFont.Dispose();
            _bodyFont.Dispose();
            _bodyMediumFont.Dispose();
            _keyFont.Dispose();
            _footerFont.Dispose();
            _brandFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
