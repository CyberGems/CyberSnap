using System.Drawing;
using CyberSnap.Helpers;
using Forms = System.Windows.Forms;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
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
        layout.Show();
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
            Arrange(_root, null);
        }

        private void Configure(Forms.ToolStripDropDown menu, Forms.ToolStripMenuItem? owner)
        {
            ApplyMetrics(menu);
            // WinForms can adopt a different native DPI when a popup first opens.
            // Reapply explicit metrics afterwards, then clamp its FINAL native bounds.
            menu.Opened += (_, _) => Arrange(menu, owner);
            menu.Layout += (_, _) =>
            {
                if (!_arranging && menu.Visible) Position(menu, owner);
            };
            foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
            {
                if (!item.HasDropDownItems) continue;
                Configure(item.DropDown, item);
                item.DropDownOpening += (_, _) =>
                {
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
