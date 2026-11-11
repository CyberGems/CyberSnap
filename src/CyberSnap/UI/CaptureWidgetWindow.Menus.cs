using System.Drawing;
using CyberSnap.Helpers;
using Forms = System.Windows.Forms;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private static Forms.ToolStripMenuItem BuildLazyWidgetSubmenu(string label, string iconId,
        Action<Forms.ToolStripMenuItem> populate)
    {
        var item = WindowsMenuRenderer.Submenu(label, showImages: true, iconId: iconId);
        // Keep the submenu arrow available before its contents have been built.
        item.DropDownItems.Add(new Forms.ToolStripSeparator());
        bool populated = false;
        item.DropDownOpening += (_, _) =>
        {
            if (populated) return;
            item.DropDown.SuspendLayout();
            try
            {
                var previousItems = item.DropDownItems.Cast<Forms.ToolStripItem>().ToArray();
                item.DropDownItems.Clear();
                foreach (var previous in previousItems) previous.Dispose();
                populate(item);
                populated = true;
            }
            finally { item.DropDown.ResumeLayout(true); }
        };
        return item;
    }

    private void ShowMenuOnWidgetMonitor(Forms.ContextMenuStrip menu, bool fromMoreButton)
    {
        var screen = Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var work = GetPhysicalWorkingArea(screen);
        var dpi = PopupWindowHelper.GetScaleForPoint(new Point(work.X + work.Width / 2, work.Y + work.Height / 2));
        var contextAnchor = Forms.Control.MousePosition;
        var layout = new WidgetMenuLayout(menu, work, dpi.X * UiScale.Current, () =>
        {
            if (!fromMoreButton) return new Rectangle(contextAnchor, Size.Empty);
            var start = WidgetMoreButton.PointToScreen(new System.Windows.Point());
            var end = WidgetMoreButton.PointToScreen(new System.Windows.Point(WidgetMoreButton.ActualWidth, WidgetMoreButton.ActualHeight));
            return Rectangle.FromLTRB((int)Math.Floor(start.X), (int)Math.Floor(start.Y), (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
        });
        // While this menu is open the topmost watchdog stays quiet; without the flag the
        // next five-second tick would push the panel over the menu.
        _widgetMenuOpen = true;
        menu.Closed += (_, _) => _widgetMenuOpen = false;
        layout.Show();
        // The watchdog (or any other topmost window) may have taken the band top right
        // before the menu appeared: pin the menu back above the panel once it is open.
        try
        {
            Native.User32.SetWindowPos(menu.Handle, Native.User32.HWND_TOPMOST, 0, 0, 0, 0,
                Native.User32.SWP_NOMOVE | Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE);
        }
        catch { /* best-effort z-order pin */ }
    }

    private sealed class WidgetMenuLayout
    {
        private readonly Forms.ContextMenuStrip _root;
        private readonly Rectangle _work;
        private readonly double _scale;
        private readonly Font _font;
        private readonly Func<Rectangle> _anchor;
        private bool _arranging;

        public WidgetMenuLayout(Forms.ContextMenuStrip root, Rectangle work, double scale, Func<Rectangle> anchor)
        {
            _root = root;
            _work = work;
            _scale = scale;
            _anchor = anchor;
            // Pixel fonts make the measured text independent of the primary monitor's
            // GDI DPI. All metrics use this popup's explicit target monitor instead.
            _font = new Font(UiChrome.FontFamily, (float)(12 * scale), System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            Configure(root, null);
            root.Disposed += (_, _) => _font.Dispose();
        }

        private int P(int value) => Math.Max(1, (int)Math.Round(value * _scale));

        public void Show()
        {
            ApplyMetrics(_root);
            _root.Show(RootLocation(_root.PreferredSize));
            // The root can be rescaled by WinForms until Show returns.
            Arrange(_root, null);
        }

        private void Configure(Forms.ToolStripDropDown menu, Forms.ToolStripMenuItem? owner)
        {
            // WinForms can adopt a different native DPI when a popup first opens.
            // Reapply explicit metrics afterwards, then clamp its FINAL native bounds.
            if (owner is not null)
                menu.Opened += (_, _) => Arrange(menu, owner);
            menu.Layout += (_, _) =>
            {
                if (!_arranging && menu.Visible) Position(menu, owner);
            };
            foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
            {
                if (!item.HasDropDownItems) continue;
                bool configured = false;
                item.DropDownOpening += (_, _) =>
                {
                    // The population handler runs first, including for nested lazy menus.
                    if (!configured)
                    {
                        Configure(item.DropDown, item);
                        configured = true;
                    }
                    ApplyMetrics(item.DropDown);
                    item.DropDownDirection = CanOpenRight(item.Owner!.Bounds, item.DropDown.PreferredSize.Width)
                        ? Forms.ToolStripDropDownDirection.Right : Forms.ToolStripDropDownDirection.Left;
                };
            }
        }

        private void ApplyMetrics(Forms.ToolStripDropDown menu)
        {
            bool previous = _arranging;
            _arranging = true;
            menu.SuspendLayout();
            try
            {
                WindowsMenuRenderer.SetPhysicalScale(menu, _scale);
                menu.Font = _font;
                menu.Padding = new Forms.Padding(P(5), P(6), P(5), P(6));
                menu.ImageScalingSize = new Size(P(20), P(20));
                bool images = menu is Forms.ToolStripDropDownMenu dd && dd.ShowImageMargin;
                int width = P(menu == _root ? 220 : 150);
                foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
                {
                    item.Font = _font;
                    int text = Forms.TextRenderer.MeasureText(item.Text, _font, Size.Empty,
                        Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix).Width;
                    int shortcut = string.IsNullOrEmpty(item.ShortcutKeyDisplayString) ? 0
                        : Forms.TextRenderer.MeasureText(item.ShortcutKeyDisplayString, _font).Width + P(18);
                    width = Math.Max(width, text + shortcut + P((images ? 43 : 14) + (item.HasDropDownItems ? 32 : 12) + 10));
                }
                width = Math.Min(width, _work.Width);
                menu.MinimumSize = new Size(width, 0);
                menu.MaximumSize = _work.Size;
                foreach (Forms.ToolStripItem item in menu.Items)
                {
                    item.AutoSize = false;
                    item.Margin = Forms.Padding.Empty;
                    item.Width = width - menu.Padding.Horizontal;
                    item.Height = P(item is Forms.ToolStripSeparator ? 8 : WindowsMenuRenderer.RowHeight);
                }
            }
            finally
            {
                menu.ResumeLayout(true);
                _arranging = previous;
            }
        }

        private void Arrange(Forms.ToolStripDropDown menu, Forms.ToolStripMenuItem? owner)
        {
            ApplyMetrics(menu);
            Position(menu, owner);
        }

        private bool CanOpenRight(Rectangle parent, int width)
            => parent.Right + width <= _work.Right || parent.Left - width < _work.Left;

        private void Position(Forms.ToolStripDropDown menu, Forms.ToolStripMenuItem? owner)
        {
            if (_arranging || menu.IsDisposed) return;
            _arranging = true;
            try
            {
                Point point;
                if (owner?.Owner is { } parent)
                {
                    int x = CanOpenRight(parent.Bounds, menu.Width) ? parent.Right - P(2) : parent.Left - menu.Width + P(2);
                    point = new Point(x, parent.PointToScreen(owner.Bounds.Location).Y - menu.Padding.Top);
                }
                else point = RootLocation(menu.Size);
                menu.Location = Clamp(point, menu.Size);
            }
            finally { _arranging = false; }
        }

        private Point RootLocation(Size size)
        {
            Rectangle anchor = _anchor();
            Point[] candidates = [new(anchor.Left, anchor.Bottom + P(2)), new(anchor.Left, anchor.Top - size.Height - P(2)),
                new(anchor.Right + P(2), anchor.Top), new(anchor.Left - size.Width - P(2), anchor.Top)];
            foreach (var candidate in candidates)
            {
                var point = Clamp(candidate, size);
                if (anchor.IsEmpty || !new Rectangle(point, size).IntersectsWith(anchor)) return point;
            }
            return Clamp(candidates[0], size);
        }

        private Point Clamp(Point point, Size size) => new(
            Math.Clamp(point.X, _work.Left, Math.Max(_work.Left, _work.Right - size.Width)),
            Math.Clamp(point.Y, _work.Top, Math.Max(_work.Top, _work.Bottom - size.Height)));
    }
}
