using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CyberSnap.Helpers;

public static class WindowsMenuRenderer
{
    public const int DefaultWidth = 340;
    public const int RowHeight = 38;

    public static ContextMenuStrip Create(bool showImages = true, int minWidth = DefaultWidth)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = showImages,
            ShowCheckMargin = false,
            Padding = new Padding(5, 6, 5, 6),
            Font = UiChrome.ChromeFont(9.0f, FontStyle.Regular),
            DropShadowEnabled = false, // legacy CS_DROPSHADOW is faint and suppresses DWM's softer shadow
            MinimumSize = new Size(minWidth, 0),
        };
        ApplyTheme(menu, showImages);
        return menu;
    }

    /// <summary>Build a submenu parent item whose drop-down uses the same themed style
    /// (dark rounded surface, accent hover bar) as the top-level menu.</summary>
    public static ToolStripMenuItem Submenu(string text, bool showImages = false, bool active = false)
    {
        var item = Item(text, null, null, active: active);
        if (item.DropDown is ToolStripDropDownMenu dd)
        {
            dd.ShowImageMargin = showImages;
            dd.ShowCheckMargin = false;
            dd.Padding = new Padding(5, 6, 5, 6);
            dd.DropShadowEnabled = false;
            ApplyTheme(dd, showImages);
        }
        return item;
    }

    private static Color ToDrawingColor(System.Windows.Media.Color c)
    {
        return Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    private static void ApplyTheme(ToolStripDropDown dd, bool showImages)
    {
        CyberSnap.UI.Theme.Refresh();
        var bg = ToDrawingColor(CyberSnap.UI.Theme.BgCard);
        var fg = ToDrawingColor(CyberSnap.UI.Theme.TextPrimary);
        var accent = ToDrawingColor(CyberSnap.UI.Theme.Accent);
        var hover = ToDrawingColor(CyberSnap.UI.Theme.TabHoverBg);
        var active = ToDrawingColor(CyberSnap.UI.Theme.TabActiveBg);
        var muted = ToDrawingColor(CyberSnap.UI.Theme.TextMuted);
        var sep = ToDrawingColor(CyberSnap.UI.Theme.Separator);
        var border = ToDrawingColor(CyberSnap.UI.Theme.BorderSubtle);

        dd.BackColor = bg;
        dd.ForeColor = fg;
        dd.Font = UiChrome.ChromeFont(9.0f, FontStyle.Regular);
        dd.Renderer = new Renderer(bg, fg, hover, active, muted, sep, border, accent, showImages);

        dd.HandleCreated += (s, _) =>
        {
            try
            {
                var strip = (ToolStripDropDown)s!;
                var handle = strip.Handle;
                CyberSnap.Native.Dwm.TrySetWindowCornerPreference(handle, CyberSnap.Native.Dwm.DWMWCP_ROUND);
                CyberSnap.Native.Dwm.TrySetImmersiveDarkMode(handle, UiChrome.IsDark);
                // On Win11, DWM rounds the window and draws its native drop shadow. The manual
                // GDI region clip would round the corners but kill that shadow, so only fall back
                // to it on older Windows where DWM rounding isn't available.
                if (!IsWin11)
                    ApplyRoundedRegion(strip);
            }
            catch { }
        };
        if (!IsWin11)
            dd.SizeChanged += (_, _) => ApplyRoundedRegion(dd);
        dd.Disposed += (_, _) => dd.Region?.Dispose();
    }

    private static bool IsWin11 => Environment.OSVersion.Version.Build >= 22000;

    public static ToolStripMenuItem Item(
        string text,
        string? shortcut = null,
        string? iconId = null,
        bool active = false,
        bool danger = false,
        Color? customColor = null,
        int iconSize = 20)
    {
        text = CyberSnap.Services.LocalizationService.Translate(text);

        var color = customColor ?? (danger
            ? Color.FromArgb(239, 68, 68)
            : ToDrawingColor(CyberSnap.UI.Theme.TextPrimary));
        var textPrimary = ToDrawingColor(CyberSnap.UI.Theme.TextPrimary);
        var textSecondary = ToDrawingColor(CyberSnap.UI.Theme.TextSecondary);
        var imageColor = customColor ?? (danger
            ? color
            : active
                ? Color.FromArgb(255, textPrimary.R, textPrimary.G, textPrimary.B)
                : Color.FromArgb(215, textSecondary.R, textSecondary.G, textSecondary.B));

        return new ToolStripMenuItem(text)
        {
            AutoSize = false,
            Height = RowHeight,
            Width = DefaultWidth - 8,
            ForeColor = color,
            Image = iconId is null ? null : FluentIcons.RenderBitmap(iconId, imageColor, iconSize, active),
            ImageScaling = ToolStripItemImageScaling.None,
            ShortcutKeyDisplayString = shortcut ?? string.Empty,
            Tag = active
        };
    }

    public static int NormalizeItemWidths(ContextMenuStrip menu, int minWidth = DefaultWidth, int? itemHeight = null)
    {
        int width = minWidth;
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is not ToolStripMenuItem menuItem)
                continue;

            int text = TextRenderer.MeasureText(g, menuItem.Text, menuItem.Font).Width;
            int shortcut = string.IsNullOrWhiteSpace(menuItem.ShortcutKeyDisplayString)
                ? 0
                : TextRenderer.MeasureText(g, menuItem.ShortcutKeyDisplayString, menuItem.Font).Width;
            width = Math.Max(width, text + shortcut + (menu.ShowImageMargin ? 124 : 76));
        }

        SetMenuWidth(menu, width, itemHeight);
        return width;
    }

    /// <summary>Size a submenu's drop-down items uniformly, like NormalizeItemWidths does for a top-level menu.
    /// Returns the resulting drop-down width so callers can position it without querying layout.</summary>
    public static int NormalizeDropDownWidths(ToolStripMenuItem parent, int minWidth = DefaultWidth)
    {
        int width = minWidth;
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            if (item is not ToolStripMenuItem menuItem)
                continue;
            int text = TextRenderer.MeasureText(g, menuItem.Text, menuItem.Font).Width;
            width = Math.Max(width, text + 76);
        }

        width = Math.Max(120, width);
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.AutoSize = false;
                menuItem.Width = width - 8;
                menuItem.Height = RowHeight;
            }
        }
        return width;
    }

    public static void SetMenuWidth(ContextMenuStrip menu, int width, int? itemHeight = null)
    {
        width = Math.Max(120, width);
        menu.MinimumSize = new Size(width, 0);
        menu.Width = width;
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.AutoSize = false;
                menuItem.Width = width - 8;
                menuItem.Height = itemHeight ?? RowHeight;
            }
        }
    }

    private static void ApplyRoundedRegion(ToolStripDropDown menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0)
            return;

        using var path = Renderer.RoundedRect(new Rectangle(0, 0, menu.Width, menu.Height), 12);
        var previous = menu.Region;
        menu.Region = new Region(path);
        previous?.Dispose();
    }

    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        private readonly Color _bg;
        private readonly Color _fg;
        private readonly Color _hover;
        private readonly Color _active;
        private readonly Color _muted;
        private readonly Color _sep;
        private readonly Color _border;
        private readonly Color _accent;
        private readonly bool _showImages;

        public Renderer(Color bg, Color fg, Color hover, Color active, Color muted, Color sep, Color border, Color accent, bool showImages)
            : base(new ColorTable(bg))
        {
            _bg = bg;
            _fg = fg;
            _hover = hover;
            _active = active;
            _muted = muted;
            _sep = sep;
            _border = border;
            _accent = accent;
            _showImages = showImages;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_bg);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float penWidth = 1.8f;
            using var pen = new Pen(_border, penWidth);
            float inset = penWidth / 2f;
            var rect = new RectangleF(inset, inset, e.ToolStrip.Width - penWidth, e.ToolStrip.Height - penWidth);
            float radius = IsWin11 ? 8f : 12f;
            using var path = RoundedRectF(rect, radius);
            e.Graphics.DrawPath(pen, path);
        }

        public static GraphicsPath RoundedRectF(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled)
                return;

            bool active = e.Item.Tag is true;
            if (!e.Item.Selected && !active)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);
            using var brush = new SolidBrush(active ? _active : _hover);
            using var path = RoundedRect(rect, 6);
            e.Graphics.FillPath(brush, path);

            if (e.Item.Selected)
            {
                int barWidth = 3;
                int barHeight = e.Item.Height - 16;
                int barX = 7;
                int barY = (e.Item.Height - barHeight) / 2;
                using var accentBrush = new SolidBrush(_accent);
                using var barPath = RoundedRect(new Rectangle(barX, barY, barWidth, barHeight), 1);
                e.Graphics.FillPath(accentBrush, barPath);
            }
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image is null)
                return;

            int indent = e.Item.Padding.Left;
            int size = Math.Min(24, Math.Min(e.Item.Height - 9, e.Image.Width));
            int x = 10 + indent + (24 - size) / 2;
            int y = e.Item.ContentRectangle.Y + (e.Item.Height - size) / 2;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(e.Image, new Rectangle(x, y, size, size));
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;

            using (var pen = new Pen(_accent, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            })
            {
                e.Graphics.DrawLines(pen, new[]
                {
                    new Point(cx - 5, cy),
                    new Point(cx - 1, cy + 4),
                    new Point(cx + 5, cy - 4)
                });
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item)
            {
                base.OnRenderItemText(e);
                return;
            }

            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            string shortcut = item.ShortcutKeyDisplayString ?? string.Empty;
            int indent = item.Padding.Left;
            int left = (_showImages ? 43 : 14) + indent;
            int shortcutWidth = string.IsNullOrEmpty(shortcut)
                ? 0
                : TextRenderer.MeasureText(e.Graphics, shortcut, item.Font).Width + 18;

            var labelRect = new Rectangle(
                left,
                0,
                Math.Max(24, item.Width - left - shortcutWidth - 12),
                item.Height);
            
            var textColor = item.Enabled
                ? (item.ForeColor.IsEmpty ? _fg : item.ForeColor)
                : _muted;

            TextRenderer.DrawText(
                e.Graphics,
                item.Text,
                item.Font,
                labelRect,
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (shortcut.Length == 0)
                return;

            var shortcutRect = new Rectangle(item.Width - shortcutWidth - 12, 0, shortcutWidth, item.Height);
            TextRenderer.DrawText(
                e.Graphics,
                shortcut,
                item.Font,
                shortcutRect,
                _muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int left = _showImages ? 42 : 10;
            int y = e.Item.Height / 2;
            using var pen = new Pen(_sep);
            e.Graphics.DrawLine(pen, left, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            if (e.Item is null) return;

            // Default arrow renders dark-on-dark and is nearly invisible; draw a crisp chevron
            // in the menu foreground color instead.
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.Item.ContentRectangle;
            int cx = e.Item.Width - 16;
            int cy = r.Y + r.Height / 2;
            const int w = 4, h = 7;
            using var pen = new Pen(e.Item.Enabled ? _fg : _muted, 1.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            e.Graphics.DrawLines(pen, new[]
            {
                new Point(cx - w / 2, cy - h / 2),
                new Point(cx + w / 2, cy),
                new Point(cx - w / 2, cy + h / 2),
            });
        }

        public static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class ColorTable : ProfessionalColorTable
    {
        private readonly Color _bg;

        public ColorTable(Color bg)
        {
            _bg = bg;
        }

        public override Color MenuBorder => Color.Transparent;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
    }
}
