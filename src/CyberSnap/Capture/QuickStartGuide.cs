using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed class QuickStartGuide : Form
{
    private const int MaxWidth = 480;
    private const int PadX = 22;
    private const int PadY = 22;
    private const int HeaderHeight = 36;
    private const int PrimaryRowHeight = 40;
    private const int ShortcutRowHeight = 32;
    private const int TipRowHeight = 30;
    private const int SectionGap = 14;
    private const int IconSize = 18;
    private const int IconTextGap = 12;
    private const int PillPadH = 14;
    private const int PillGap = 8;
    private const int PillKeyLabelGap = 10;
    private const float Corner = 10f;

    private readonly Font _headerFont = UiChrome.ChromeFont(10.5f, FontStyle.Bold);
    private readonly Font _sectionFont = UiChrome.ChromeFont(8.5f, FontStyle.Bold);
    private readonly Font _bodyFont = UiChrome.ChromeFont(9f);
    private readonly Font _keyFont = UiChrome.ChromeFont(8.25f, FontStyle.Bold);
    private readonly Font _footerFont = UiChrome.ChromeFont(7.5f);
    private readonly Font _brandFont = UiChrome.ChromeFont(6.5f);

    private record ShortcutDef(string Key, string Label);
    private record TipDef(string? IconId, string Text);

    private ShortcutDef[] _shortcuts = Array.Empty<ShortcutDef>();
    private TipDef[] _tips = Array.Empty<TipDef>();
    private string _title = "";
    private string _captureText = "";
    private string _shortcutsTitle = "";
    private string _footerText = "";
    private string _brandText = "";
    private int _totalHeight;
    private Rectangle _closeRect;

    public QuickStartGuide()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = UiChrome.SurfaceTooltip;
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
        Cursor = _closeRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
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
        _shortcuts = Array.Empty<ShortcutDef>();
        _tips = Array.Empty<TipDef>();

        bool isSpanish = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);

        _title = isSpanish ? "Guía rápida" : "Quick Start";
        _captureText = isSpanish
            ? "Arrastra para seleccionar el área de captura"
            : "Drag to select the capture area";
        _shortcutsTitle = isSpanish ? "Atajos de teclado" : "Keyboard shortcuts";
        _footerText = isSpanish ? "Click o Esc para cerrar" : "Click or Esc to close";
        _brandText = "CyberSnap";

        _shortcuts = isSpanish
            ? new[]
            {
                new ShortcutDef("Space", "Confirmar"),
                new ShortcutDef("Esc", "Cancelar"),
                new ShortcutDef("Ctrl+Z", "Deshacer"),
                new ShortcutDef("Ctrl+Y", "Rehacer"),
                new ShortcutDef("Ctrl+C", "Copiar"),
            }
            : new[]
            {
                new ShortcutDef("Space", "Confirm"),
                new ShortcutDef("Esc", "Cancel"),
                new ShortcutDef("Ctrl+Z", "Undo"),
                new ShortcutDef("Ctrl+Y", "Redo"),
                new ShortcutDef("Ctrl+C", "Copy"),
            };

        _tips = isSpanish
            ? new[]
            {
                new TipDef("captureRect", "Click derecho → ocultar tool del toolbar"),
                new TipDef("add", "Click ▼ → opciones del toolbar"),
                new TipDef("sticker", "Las anotaciones aparecen tras capturar"),
            }
            : new[]
            {
                new TipDef("captureRect", "Right-click → hide tool from toolbar"),
                new TipDef("add", "Click ▼ → toolbar options"),
                new TipDef("sticker", "Annotations appear after capture"),
            };

        // Measure width
        int maxTextWidth = 0;
        using (var g = CreateGraphics())
        {
            int titleW = TextRenderer.MeasureText(g, _title, _headerFont,
                new Size(MaxWidth - PadX * 2, 0),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            maxTextWidth = titleW;

            // Primary capture text
            int primaryW = IconSize + IconTextGap +
                TextRenderer.MeasureText(g, _captureText, _bodyFont,
                    new Size(MaxWidth - PadX * 2 - IconSize - IconTextGap, 0),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            maxTextWidth = Math.Max(maxTextWidth, primaryW);

            // Shortcut pills
            foreach (var sc in _shortcuts)
            {
                int kw = TextRenderer.MeasureText(g, sc.Key, _keyFont,
                    new Size(0, 0), TextFormatFlags.NoPadding).Width;
                int lw = TextRenderer.MeasureText(g, sc.Label, _bodyFont,
                    new Size(0, 0), TextFormatFlags.NoPadding).Width;
                int pillW = kw + lw + PillPadH * 2 + PillKeyLabelGap;
                maxTextWidth = Math.Max(maxTextWidth, pillW);
            }

            // Tips
            foreach (var tip in _tips)
            {
                int tw = TextRenderer.MeasureText(g, tip.Text, _bodyFont,
                    new Size(MaxWidth - PadX * 2 - IconSize - IconTextGap, 0),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Width;
                maxTextWidth = Math.Max(maxTextWidth, tw + IconSize + IconTextGap);
            }

            // Brand text
            int brandW = TextRenderer.MeasureText(g, _brandText, _brandFont,
                new Size(0, 0), TextFormatFlags.NoPadding).Width;
            maxTextWidth = Math.Max(maxTextWidth, brandW);
        }

        int width = Math.Min(MaxWidth, Math.Max(240, maxTextWidth + PadX * 2 + 4));

        // Calculate height
        int y = PadY;
        y += HeaderHeight + SectionGap;
        y += PrimaryRowHeight + SectionGap;
        y += 20 + SectionGap;
        int shortcutRows = (_shortcuts.Length + 1) / 2;
        y += shortcutRows * ShortcutRowHeight + SectionGap;
        y += 20;
        y += _tips.Length * TipRowHeight + 6;
        y += PadY + 16 + 18;
        _totalHeight = y;
        int height = _totalHeight;

        int x = anchorScreenBounds.Left + (anchorScreenBounds.Width - width) / 2;
        int gap = 12;
        int ay = above
            ? anchorScreenBounds.Top - height - gap
            : anchorScreenBounds.Bottom + gap;

        var screen = Screen.FromRectangle(anchorScreenBounds).WorkingArea;
        x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - width - 4));
        ay = Math.Clamp(ay, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - height - 4));

        Bounds = new Rectangle(x, ay, width, height);
        Region?.Dispose();
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, width, height), Corner))
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

        WindowsDockRenderer.PaintShadow(g, new RectangleF(0, 0, Width, Height), Corner);
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, Width, Height), Corner))
        {
            using var bgBrush = new SolidBrush(UiChrome.SurfaceTooltip);
            g.FillPath(bgBrush, path);
            using var borderPen = new Pen(UiChrome.SurfaceBorderStrong, 1f);
            g.DrawPath(borderPen, path);
        }

        int curY = PadY;
        int contentW = Width - PadX * 2;

        // ── Header ──
        const int closeBtnSize = 16;
        _closeRect = new Rectangle(Width - PadX - closeBtnSize - 4, PadY - 6,
                                    closeBtnSize + 8, closeBtnSize + 8);
        var headerRect = new Rectangle(PadX, curY, contentW - closeBtnSize - 12, HeaderHeight);
        TextRenderer.DrawText(g, _title, _headerFont, headerRect,
            UiChrome.AccentColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // Close button (X)
        FluentIcons.DrawIcon(g, "close",
            new RectangleF(_closeRect.X + 4, _closeRect.Y + 4, closeBtnSize, closeBtnSize),
            Color.FromArgb(150, UiChrome.SurfaceTextSecondary.R,
                               UiChrome.SurfaceTextSecondary.G,
                               UiChrome.SurfaceTextSecondary.B),
            iconInset: 0f);
        curY += HeaderHeight;

        // Separator
        int sepY = curY + SectionGap / 2;
        using var sepPen = new Pen(
            Color.FromArgb(35, UiChrome.AccentColor.R, UiChrome.AccentColor.G, UiChrome.AccentColor.B), 1f);
        g.DrawLine(sepPen, PadX, sepY, Width - PadX, sepY);
        curY += SectionGap;

        // ── Primary capture action ──
        int iconX = PadX + 2;
        var iconRect = new RectangleF(iconX, curY + (PrimaryRowHeight - IconSize) / 2f, IconSize, IconSize);
        FluentIcons.DrawIcon(g, "captureRect", iconRect,
            Color.FromArgb(200, UiChrome.AccentColor.R, UiChrome.AccentColor.G, UiChrome.AccentColor.B),
            iconInset: 0f);

        int textX = iconX + IconSize + IconTextGap;
        var textRect = new Rectangle(textX, curY, Width - textX - PadX, PrimaryRowHeight);
        TextRenderer.DrawText(g, _captureText, _bodyFont, textRect,
            UiChrome.SurfaceTextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        curY += PrimaryRowHeight + SectionGap;

        // ── Shortcuts section ──
        var secRect = new Rectangle(PadX, curY, contentW, 20);
        TextRenderer.DrawText(g, _shortcutsTitle, _sectionFont, secRect,
            UiChrome.AccentColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        curY += 20 + 8;

        int pillX = PadX;
        foreach (var sc in _shortcuts)
        {
            int keyW = TextRenderer.MeasureText(g, sc.Key, _keyFont,
                new Size(0, 0), TextFormatFlags.NoPadding).Width;
            int labelW = TextRenderer.MeasureText(g, sc.Label, _bodyFont,
                new Size(0, 0), TextFormatFlags.NoPadding).Width;
            int pillW = keyW + labelW + PillPadH * 2 + PillKeyLabelGap;
            int pillH = ShortcutRowHeight - 4;

            if (pillX + pillW > Width - PadX)
            {
                pillX = PadX;
                curY += ShortcutRowHeight;
            }

            var pillRect = new RectangleF(pillX, curY + 2, pillW, pillH);
            using (var pillPath = WindowsDockRenderer.RoundedRect(pillRect, 5f))
            {
                using var pillBg = new SolidBrush(UiChrome.SurfaceTier2);
                g.FillPath(pillBg, pillPath);
                using var pillBorder = new Pen(UiChrome.SurfaceBorderSubtle, 1f);
                g.DrawPath(pillBorder, pillPath);
            }

            TextRenderer.DrawText(g, sc.Key, _keyFont,
                new Rectangle((int)pillX + PillPadH, curY + 2, keyW + 4, pillH),
                UiChrome.AccentColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, sc.Label, _bodyFont,
                new Rectangle((int)pillX + keyW + PillPadH + PillKeyLabelGap, curY + 2, labelW + 4, pillH),
                UiChrome.SurfaceTextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            pillX += pillW + PillGap;
        }
        curY += ShortcutRowHeight + SectionGap + 2;

        // ── Tips section ──
        var tipSecRect = new Rectangle(PadX, curY, contentW, 20);
        string tipsTitle = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase)
            ? "Consejos" : "Tips";
        TextRenderer.DrawText(g, tipsTitle, _sectionFont, tipSecRect,
            UiChrome.AccentColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        curY += 20 + 4;

        int tipIconColor = Color.FromArgb(180, UiChrome.SurfaceTextSecondary.R,
            UiChrome.SurfaceTextSecondary.G, UiChrome.SurfaceTextSecondary.B).ToArgb();

        foreach (var tip in _tips)
        {
            if (tip.IconId != null)
            {
                var tipIconRect = new RectangleF(PadX + 2, curY + (TipRowHeight - 14) / 2f, 14, 14);
                FluentIcons.DrawIcon(g, tip.IconId, tipIconRect,
                    Color.FromArgb(tipIconColor), iconInset: 0f);
            }

            int tipTextX = PadX + (tip.IconId != null ? 14 + 8 : 0);
            var tipTextRect = new Rectangle(tipTextX, curY, Width - tipTextX - PadX, TipRowHeight);
            TextRenderer.DrawText(g, tip.Text, _bodyFont, tipTextRect,
                UiChrome.SurfaceTextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            curY += TipRowHeight;
        }

        curY += 8;

        // ── Footer ──
        var footerRect = new Rectangle(PadX, curY, contentW, 16);
        TextRenderer.DrawText(g, _footerText, _footerFont, footerRect,
            UiChrome.SurfaceTextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        curY += 16 + 4;

        // ── Brand line ──
        var brandIconRect = new RectangleF(
            Width / 2f - 40, curY + 2, 12, 12);
        FluentIcons.DrawIcon(g, "home", brandIconRect,
            Color.FromArgb(60, UiChrome.AccentColor.R, UiChrome.AccentColor.G, UiChrome.AccentColor.B),
            iconInset: 0f);

        var brandRect = new Rectangle((int)(Width / 2f - 24), curY, 80, 14);
        TextRenderer.DrawText(g, _brandText, _brandFont, brandRect,
            Color.FromArgb(50, UiChrome.SurfaceTextMuted.R, UiChrome.SurfaceTextMuted.G, UiChrome.SurfaceTextMuted.B),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Dispose();
    }
}
