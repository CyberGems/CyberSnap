using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.Capture;

/// <summary>
/// A rich, icon-enhanced tooltip shown when the user clicks the CyberSnap logo
/// in the capture toolbar. Dismisses on click or Escape.
/// </summary>
public sealed class QuickStartGuide : Form
{
    private const int MaxWidth = 380;
    private const int PadX = 14;
    private const int PadY = 12;
    private const int RowHeight = 24;
    private const int IconSize = 16;
    private const int IconTextGap = 10;
    private const int SectionGap = 6;

    private readonly Font _titleFont = UiChrome.ChromeFont(10f, System.Drawing.FontStyle.Bold);
    private readonly Font _bodyFont = UiChrome.ChromeFont(8.5f);
    private readonly Font _keyFont = UiChrome.ChromeFont(8.5f);

    private record TipLine(string? IconId, string Text, string? KeyHint = null);

    private TipLine[] _tips = Array.Empty<TipLine>();
    private int _totalHeight;

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
            // Allow clicks to land on this window (so we can dismiss it)
            m.Result = HTCLIENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Close();
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
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    public void ShowNear(IWin32Window owner, Rectangle anchorScreenBounds, bool above)
    {
        // Allow GC of previous content bitmaps
        _tips = Array.Empty<TipLine>();

        bool isSpanish = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);

        string title = isSpanish ? "Guía rápida" : "Quick Start";
        _tips = isSpanish
            ? new TipLine[]
            {
                new("select",      "Arrastra para seleccionar el área de captura"),
                new(null,          "", "Space = Confirmar  •  Esc = Cancelar"),
                new(null,          "", "Ctrl+C = Copiar  •  Ctrl+S = Guardar"),
                new(null,          "", "Click derecho sobre una tool → ocultar"),
                new("add",         "Click en ••• para restaurar tools ocultas"),
                new("sticker",     "Las anotaciones aparecen tras capturar"),
            }
            : new TipLine[]
            {
                new("select",      "Drag to select the capture area"),
                new(null,          "", "Space = Confirm  •  Esc = Cancel"),
                new(null,          "", "Ctrl+C = Copy  •  Ctrl+S = Save"),
                new(null,          "", "Right-click a tool button to hide it"),
                new("add",         "Click ••• to restore hidden tools"),
                new("sticker",     "Annotations appear after capture"),
            };

        // Measure
        int maxTextWidth = 0;
        using (var g = CreateGraphics())
        {
            int titleW = TextRenderer.MeasureText(g, title, _titleFont,
                new Size(MaxWidth - PadX * 2, 0),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            maxTextWidth = titleW;

            foreach (var tip in _tips)
            {
                int iconSpace = tip.IconId != null ? IconSize + IconTextGap : 0;
                int avail = MaxWidth - PadX * 2 - iconSpace;
                int tw = TextRenderer.MeasureText(g, tip.Text, _bodyFont,
                    new Size(avail, 0),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Width;
                maxTextWidth = Math.Max(maxTextWidth, tw + iconSpace);
            }
        }

        int width = Math.Min(MaxWidth, Math.Max(120, maxTextWidth + PadX * 2));
        int titleH = 28;
        int bodyY = PadY + titleH + SectionGap;
        int bodyH = _tips.Length * RowHeight;

        _totalHeight = bodyY + bodyH + PadY;
        int height = _totalHeight;

        int x = anchorScreenBounds.Left + (anchorScreenBounds.Width - width) / 2;
        int y = above
            ? anchorScreenBounds.Top - height - 8
            : anchorScreenBounds.Bottom + 8;

        var screen = Screen.FromRectangle(anchorScreenBounds).WorkingArea;
        x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - width - 4));
        y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - height - 4));

        Bounds = new Rectangle(x, y, width, height);
        Region?.Dispose();
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, width, height), 8f))
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

        // Shadow + background
        WindowsDockRenderer.PaintShadow(g, new RectangleF(0, 0, Width, Height), 8f);
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, Width, Height), 8f))
        {
            using var bgBrush = new SolidBrush(UiChrome.SurfaceTooltip);
            g.FillPath(bgBrush, path);

            using var borderPen = new Pen(UiChrome.SurfaceBorderStrong, 1f);
            g.DrawPath(borderPen, path);
        }

        bool isSpanish = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);

        string title = isSpanish ? "Guía rápida" : "Quick Start";

        // Title
        var titleRect = new Rectangle(PadX, PadY, Width - PadX * 2, 28);
        TextRenderer.DrawText(g, title, _titleFont, titleRect,
            UiChrome.AccentColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // Separator line under title
        int sepY = PadY + 28 + SectionGap / 2;
        using var sepPen = new Pen(
            Color.FromArgb(40, UiChrome.SurfaceTextMuted.R, UiChrome.SurfaceTextMuted.G, UiChrome.SurfaceTextMuted.B), 1f);
        g.DrawLine(sepPen, PadX, sepY, Width - PadX, sepY);

        // Tip lines
        int y = PadY + 28 + SectionGap;
        int textColor = UiChrome.SurfaceTextPrimary.ToArgb();
        int mutedColor = UiChrome.SurfaceTextMuted.ToArgb();
        int iconC = Color.FromArgb(200, UiChrome.SurfaceTextSecondary.R,
            UiChrome.SurfaceTextSecondary.G, UiChrome.SurfaceTextSecondary.B).ToArgb();

        foreach (var tip in _tips)
        {
            int iconX = PadX + 2;
            int textX = PadX;

            if (tip.IconId != null)
            {
                var iconRect = new RectangleF(iconX, y + (RowHeight - IconSize) / 2f, IconSize, IconSize);
                FluentIcons.DrawIcon(g, tip.IconId, iconRect,
                    Color.FromArgb(iconC), iconInset: 0f);
                textX = iconX + IconSize + IconTextGap;
            }

            var textRect = new Rectangle(textX, y, Width - textX - PadX, RowHeight);
            Color lineColor;
            if (!string.IsNullOrEmpty(tip.Text))
            {
                lineColor = Color.FromArgb(textColor);
                TextRenderer.DrawText(g, tip.Text, _bodyFont, textRect, lineColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            if (!string.IsNullOrEmpty(tip.KeyHint))
            {
                var keyRect = new Rectangle(Width - PadX - 180, y, 170, RowHeight);
                TextRenderer.DrawText(g, tip.KeyHint, _keyFont, keyRect,
                    Color.FromArgb(mutedColor),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            y += RowHeight;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Dispose();
    }
}
