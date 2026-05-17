using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CyberSnap.Helpers;

public static class WindowsMenuRenderer
{
    public const int DefaultWidth = 260;
    public const int RowHeight = 38;

    public static ContextMenuStrip Create(bool showImages = true, int minWidth = DefaultWidth)
    {
        CyberSnap.UI.Theme.Refresh();
        var bg = UiChrome.IsDark ? Color.FromArgb(20, 20, 20) : Color.FromArgb(250, 250, 250);
        var fg = UiChrome.SurfaceTextPrimary;
        var accent = UiChrome.AccentColor;
        var hover = Color.FromArgb(22, accent.R, accent.G, accent.B);
        var active = Color.FromArgb(36, accent.R, accent.G, accent.B);
        var muted = UiChrome.SurfaceTextMuted;
        var sep = UiChrome.IsDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);

        var menu = new ContextMenuStrip
        {
            BackColor = bg,
            ForeColor = fg,
            ShowImageMargin = showImages,
            ShowCheckMargin = false,
            Padding = new Padding(5, 6, 5, 6),
            Font = UiChrome.ChromeFont(9.0f),
            DropShadowEnabled = true,
            MinimumSize = new Size(minWidth, 0),
            Renderer = new Renderer(bg, fg, hover, active, muted, sep, showImages)
        };

        menu.HandleCreated += (s, _) =>
        {
            try
            {
                var strip = (ContextMenuStrip)s!;
                var handle = strip.Handle;
                CyberSnap.Native.Dwm.TrySetWindowCornerPreference(handle, CyberSnap.Native.Dwm.DWMWCP_ROUND);
                CyberSnap.Native.Dwm.TrySetImmersiveDarkMode(handle, UiChrome.IsDark);
                ApplyRoundedRegion(strip);
            }
            catch { }
        };
        menu.SizeChanged += (_, _) => ApplyRoundedRegion(menu);
        menu.Disposed += (_, _) => menu.Region?.Dispose();

        return menu;
    }

    public static ToolStripMenuItem Item(
        string text,
        string? shortcut = null,
        string? iconId = null,
        bool active = false,
        bool danger = false)
    {
        text = CyberSnap.Services.LocalizationService.Translate(text);

        var color = danger
            ? Color.FromArgb(239, 68, 68)
            : UiChrome.SurfaceTextPrimary;
        var imageColor = danger
            ? color
            : active
                ? Color.FromArgb(255, UiChrome.SurfaceTextPrimary.R, UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B)
                : Color.FromArgb(215, UiChrome.SurfaceTextSecondary.R, UiChrome.SurfaceTextSecondary.G, UiChrome.SurfaceTextSecondary.B);

        return new ToolStripMenuItem(text)
        {
            AutoSize = false,
            Height = RowHeight,
            Width = DefaultWidth - 8,
            ForeColor = color,
            Image = iconId is null ? null : FluentIcons.RenderBitmap(iconId, imageColor, 20, active),
            ImageScaling = ToolStripItemImageScaling.None,
            ShortcutKeyDisplayString = shortcut ?? string.Empty,
            Tag = active
        };
    }

    public static int NormalizeItemWidths(ContextMenuStrip menu, int minWidth = DefaultWidth)
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

        SetMenuWidth(menu, width);
        return width;
    }

    public static void SetMenuWidth(ContextMenuStrip menu, int width)
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
                menuItem.Height = RowHeight;
            }
        }
    }

    private static void ApplyRoundedRegion(ContextMenuStrip menu)
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
        private readonly bool _showImages;

        public Renderer(Color bg, Color fg, Color hover, Color active, Color muted, Color sep, bool showImages)
            : base(new ColorTable(bg))
        {
            _bg = bg;
            _fg = fg;
            _hover = hover;
            _active = active;
            _muted = muted;
            _sep = sep;
            _showImages = showImages;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_bg);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_sep);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
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
                using var accentBrush = new SolidBrush(UiChrome.AccentColor);
                using var barPath = RoundedRect(new Rectangle(barX, barY, barWidth, barHeight), 1);
                e.Graphics.FillPath(accentBrush, barPath);
            }
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image is null)
                return;

            int size = Math.Min(16, Math.Min(e.Item.Height - 9, e.Image.Width));
            int x = 14 + (20 - size) / 2;
            int y = e.Item.ContentRectangle.Y + (e.Item.Height - size) / 2;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(e.Image, new Rectangle(x, y, size, size));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item)
            {
                base.OnRenderItemText(e);
                return;
            }

            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            string shortcut = item.ShortcutKeyDisplayString ?? string.Empty;
            int left = _showImages ? 43 : 14;
            int shortcutWidth = string.IsNullOrEmpty(shortcut)
                ? 0
                : TextRenderer.MeasureText(e.Graphics, shortcut, item.Font).Width + 18;

            var labelRect = new Rectangle(
                left,
                0,
                Math.Max(24, item.Width - left - shortcutWidth - 12),
                item.Height);
            TextRenderer.DrawText(
                e.Graphics,
                item.Text,
                item.Font,
                labelRect,
                item.ForeColor.IsEmpty ? _fg : item.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            if (shortcut.Length == 0)
                return;

            var shortcutRect = new Rectangle(item.Width - shortcutWidth - 12, 0, shortcutWidth, item.Height);
            TextRenderer.DrawText(
                e.Graphics,
                shortcut,
                item.Font,
                shortcutRect,
                _muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int left = _showImages ? 42 : 10;
            int y = e.Item.Height / 2;
            using var pen = new Pen(_sep);
            e.Graphics.DrawLine(pen, left, y, e.Item.Width - 10, y);
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
