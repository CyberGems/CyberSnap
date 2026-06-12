using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Services;

using CyberSnap.UI.Controls;

namespace CyberSnap.UI.Editor;

/// <summary>
/// Borderless, themed popup for choosing an emoji for the editor's Emoji tool.
/// A search box filters the shared <see cref="EmojiCatalog"/>; the grid is a single
/// owner-drawn control so filtering never rebuilds hundreds of child controls.
/// </summary>
internal sealed class EmojiPickerPopup : Form
{
    private readonly EmojiRenderer _renderer = new();
    private readonly EmojiGrid _grid;
    private readonly TextBox _search;
    private readonly AnnotationCanvas _canvas;

    public event Action<string>? EmojiChosen;

    public EmojiPickerPopup(AnnotationCanvas canvas)
    {
        _canvas = canvas;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = EditorColors.BgSecondary;
        ClientSize = new Size(360, 384);
        KeyPreview = true;
        DoubleBuffered = true;

        var border = new Panel { Dock = DockStyle.Fill, BackColor = EditorColors.BgSecondary, Padding = new Padding(10) };
        border.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, border.Width - 1, border.Height - 1);
            using var path = EditorPaint.RoundedRect(r, 10);
            using var glow = new Pen(Color.FromArgb(34, EditorColors.Accent), 3f);
            using var pen = new Pen(EditorColors.Border);
            e.Graphics.DrawPath(glow, path);
            e.Graphics.DrawPath(pen, path);
        };

        _grid = new EmojiGrid(_renderer) { Dock = DockStyle.Fill };

        _search = new TextBox
        {
            Dock = DockStyle.Top,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = EditorColors.BgCard,
            ForeColor = EditorColors.TextPrimary,
            Font = new Font("Segoe UI Variable Text", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Height = 28,
        };
        _search.TextChanged += (_, _) => _grid.SetFilter(_search.Text);
        _search.KeyDown += (sender, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                if (!Bounds.Contains(Cursor.Position))
                {
                    e.SuppressKeyPress = true;
                    Close();
                    _canvas.Focus();
                }
            }
        };

        var searchHost = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = EditorColors.BgSecondary, Padding = new Padding(0, 0, 0, 10) };
        searchHost.Controls.Add(_search);

        _grid.EmojiChosen += emoji =>
        {
            EmojiChosen?.Invoke(emoji);
            Close();
        };

        border.Controls.Add(_grid);
        border.Controls.Add(searchHost);
        Controls.Add(border);

        Deactivate += (_, _) => Close();
        Shown += (_, _) => _search.Focus();
    }

    protected override bool ShowWithoutActivation => false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _renderer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Shows the picker anchored near <paramref name="anchorScreenRect"/> (screen pixels),
    /// kept within the work area of the monitor under that anchor.</summary>
    public void ShowNear(Rectangle anchorScreenRect)
    {
        var screen = Screen.FromRectangle(anchorScreenRect).WorkingArea;
        int x = anchorScreenRect.Right + 8;
        int y = anchorScreenRect.Top;
        if (x + Width > screen.Right) x = anchorScreenRect.Left - Width - 8;
        if (x < screen.Left) x = screen.Left + 8;
        if (y + Height > screen.Bottom) y = screen.Bottom - Height - 8;
        if (y < screen.Top) y = screen.Top + 8;
        Location = new Point(x, y);
        Show();
    }

    // ── Owner-drawn scrollable emoji grid ──────────────────────────────────
    private sealed class EmojiGrid : Control
    {
        private const int Columns = 8;
        private const int CellSize = 40;
        private const float GlyphSize = 24f;

        private readonly EmojiRenderer _renderer;
        private (string emoji, string name)[] _items = EmojiCatalog.Items;
        private int _scrollRow;
        private int _hover = -1;

        public event Action<string>? EmojiChosen;

        public EmojiGrid(EmojiRenderer renderer)
        {
            _renderer = renderer;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = EditorColors.BgSecondary;
        }

        public void SetFilter(string text)
        {
            text = text?.Trim() ?? "";
            _items = string.IsNullOrEmpty(text)
                ? EmojiCatalog.Items
                : EmojiCatalog.Items.Where(it => it.name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            _scrollRow = 0;
            _hover = -1;
            Invalidate();
        }

        private int VisibleRows => Math.Max(1, Height / CellSize);
        private int TotalRows => (_items.Length + Columns - 1) / Columns;
        private int MaxScrollRow => Math.Max(0, TotalRows - VisibleRows);

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int prev = _scrollRow;
            _scrollRow = Math.Clamp(_scrollRow + (e.Delta > 0 ? -1 : 1), 0, MaxScrollRow);
            if (_scrollRow != prev) { UpdateHover(e.Location); Invalidate(); }
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            UpdateHover(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover != -1) { _hover = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int idx = IndexAt(e.Location);
            if (idx >= 0 && idx < _items.Length)
                EmojiChosen?.Invoke(_items[idx].emoji);
            base.OnMouseDown(e);
        }

        private void UpdateHover(Point p)
        {
            int idx = IndexAt(p);
            if (idx != _hover) { _hover = idx; Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        private int IndexAt(Point p)
        {
            if (p.X < 0 || p.X >= Columns * CellSize) return -1;
            int col = p.X / CellSize;
            int row = p.Y / CellSize + _scrollRow;
            if (col < 0 || col >= Columns) return -1;
            int idx = row * Columns + col;
            return idx >= 0 && idx < _items.Length ? idx : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(EditorColors.BgSecondary);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int firstRow = _scrollRow;
            int lastRow = Math.Min(TotalRows, firstRow + VisibleRows + 1);

            for (int row = firstRow; row < lastRow; row++)
            {
                for (int col = 0; col < Columns; col++)
                {
                    int idx = row * Columns + col;
                    if (idx >= _items.Length) break;

                    int x = col * CellSize;
                    int y = (row - firstRow) * CellSize;
                    var cell = new Rectangle(x, y, CellSize, CellSize);

                    if (idx == _hover)
                    {
                        using var hl = new SolidBrush(EditorColors.BgHover);
                        using var path = EditorPaint.RoundedRect(Rectangle.Inflate(cell, -3, -3), 7);
                        g.FillPath(hl, path);
                        using var pen = new Pen(Color.FromArgb(120, EditorColors.Accent));
                        g.DrawPath(pen, path);
                    }

                    var bmp = _renderer.GetEmoji(_items[idx].emoji, GlyphSize);
                    int bx = x + (CellSize - bmp.Width) / 2;
                    int by = y + (CellSize - bmp.Height) / 2;
                    g.DrawImage(bmp, bx, by);
                }
            }

            // Scrollbar hint
            if (MaxScrollRow > 0)
            {
                float frac = VisibleRows / (float)TotalRows;
                float thumbH = Math.Max(24, Height * frac);
                float thumbY = (_scrollRow / (float)Math.Max(1, MaxScrollRow)) * (Height - thumbH);
                using var thumb = new SolidBrush(Color.FromArgb(90, EditorColors.Accent));
                g.FillRectangle(thumb, Width - 4, thumbY, 3, thumbH);
            }
        }
    }
}
