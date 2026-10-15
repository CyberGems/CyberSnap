using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI.Editor;

internal readonly record struct EditorTabInfo(string Title, bool Dirty, bool HasSavedPath, bool Active);

/// <summary>
/// Compact document tabs above the horizontal ruler when more than one
/// document is open. Hidden with a single document so chrome stays unchanged.
/// Chevron buttons appear at the ends once the tabs no longer fit.
/// </summary>
internal sealed class EditorTabStrip : DoubleBufferedPanel
{
    public const int PreferredHeight = 34;
    private const int WmContextMenu = 0x007B;
    // 20px clickable button plus a 4px gutter from the strip edge.
    private const int ChevronWidth = 20;
    private const int ChevronMargin = 4;
    private const int ChevronZone = ChevronWidth + ChevronMargin;
    // Neutral X at rest; brighter on hover so it stays legible over the red
    // wash (same treatment as the preview tabs).
    private static Color CloseIconColor => Color.FromArgb(210, EditorColors.TextSecondary);
    private static Color CloseIconHoverColor => Color.FromArgb(240, EditorColors.TextPrimary);
    // Red wash disc on hover: the light X stays legible over it.
    private static Color CloseHoverWash => EditorColors.IsDark
        ? Color.FromArgb(64, 255, 68, 68)
        : Color.FromArgb(36, 196, 43, 28);

    private readonly List<EditorTabInfo> _tabs = new();
    private readonly List<Rectangle> _tabRects = new();
    private readonly List<Rectangle> _closeRects = new();
    private int _hoverIndex = -1;
    private int _hoverCloseIndex = -1;
    private int _hoverChevron = -1;
    private int _scrollX;
    private int _contentWidth;
    private bool _showChevrons;
    private Rectangle _leftChevronRect;
    private Rectangle _rightChevronRect;

    public event EventHandler<int>? TabSelected;
    public event EventHandler<int>? TabCloseRequested;
    public event EventHandler<int>? TabContextMenuRequested;
    public event EventHandler? BarContextMenuRequested;
    public event EventHandler? EmptyAreaDoubleClicked;

    public EditorTabStrip()
    {
        Dock = DockStyle.Fill;
        TabStop = false;
        SetStyle(ControlStyles.StandardDoubleClick | ControlStyles.UserMouse, true);
        BackColor = EditorColors.BgPrimary;
        Cursor = Cursors.Default;
    }

    public void SetTabs(IReadOnlyList<EditorTabInfo> tabs)
    {
        _tabs.Clear();
        _tabs.AddRange(tabs);
        _hoverIndex = -1;
        _hoverCloseIndex = -1;
        _hoverChevron = -1;
        RecalcLayout();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalcLayout();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int tab = HitTestTab(e.Location);
        int close = HitTestClose(e.Location);
        int chevron = HitTestChevron(e.Location);
        if (tab != _hoverIndex || close != _hoverCloseIndex || chevron != _hoverChevron)
        {
            _hoverIndex = tab;
            _hoverCloseIndex = close;
            _hoverChevron = chevron;
            Invalidate();
        }
        Cursor = tab >= 0 || chevron >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1 || _hoverCloseIndex != -1 || _hoverChevron != -1)
        {
            _hoverIndex = -1;
            _hoverCloseIndex = -1;
            _hoverChevron = -1;
            Invalidate();
        }
    }

    /// <summary>
    /// Themed tooltip target under the cursor: close button, or tab body with
    /// filename plus save-state (same wording as the title-bar LED).
    /// </summary>
    public bool TryGetHoverTooltip(out string text, out Rectangle screenAnchor)
    {
        text = "";
        screenAnchor = Rectangle.Empty;
        if (!IsHandleCreated)
            return false;

        var client = PointToClient(Cursor.Position);
        int close = HitTestClose(client);
        if (close >= 0 && close < _closeRects.Count)
        {
            text = LocalizationService.Translate("Close tab");
            screenAnchor = PinTooltipAnchor(_closeRects[close], client);
            return true;
        }

        int tab = HitTestTab(client);
        if (tab >= 0 && tab < _tabs.Count && tab < _tabRects.Count)
        {
            text = FormatTabStatusTooltip(_tabs[tab]);
            screenAnchor = PinTooltipAnchor(_tabRects[tab], client);
            return true;
        }

        return false;
    }

    private Rectangle PinTooltipAnchor(Rectangle localRect, Point client)
    {
        const int pinWidth = 24;
        const int snap = 16;
        int width = Math.Min(pinWidth, Math.Max(8, localRect.Width));
        int maxX = Math.Max(localRect.Left, localRect.Right - width);
        int x = Math.Clamp(client.X - width / 2, localRect.Left, maxX);
        x = localRect.Left + ((x - localRect.Left) / snap) * snap;
        return RectangleToScreen(new Rectangle(x, localRect.Top, width, localRect.Height));
    }

    private static string FormatTabStatusTooltip(EditorTabInfo tab)
    {
        string status;
        if (tab.Dirty)
            status = LocalizationService.Translate("Unsaved changes");
        else if (!tab.HasSavedPath)
            status = LocalizationService.Translate("New canvas");
        else
            status = LocalizationService.Translate("Saved document");
        return $"{tab.Title} — {status}";
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmContextMenu)
        {
            Point client;
            long lp = m.LParam.ToInt64();
            if (lp == -1)
                client = PointToClient(Cursor.Position);
            else
            {
                int sx = unchecked((short)(lp & 0xFFFF));
                int sy = unchecked((short)((lp >> 16) & 0xFFFF));
                client = PointToClient(new Point(sx, sy));
            }
            RaiseContextMenu(client);
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    private void RaiseContextMenu(Point client)
    {
        int tab = HitTestTab(client);
        if (tab >= 0)
        {
            TabSelected?.Invoke(this, tab);
            TabContextMenuRequested?.Invoke(this, tab);
        }
        else
        {
            BarContextMenuRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int chevron = HitTestChevron(e.Location);
        if (chevron >= 0)
        {
            ScrollByChevron(chevron == 0 ? -1 : 1);
            return;
        }

        int close = HitTestClose(e.Location);
        if (close >= 0)
        {
            TabCloseRequested?.Invoke(this, close);
            return;
        }

        int hit = HitTestTab(e.Location);
        if (hit >= 0)
            TabSelected?.Invoke(this, hit);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (HitTestTab(e.Location) >= 0 || HitTestClose(e.Location) >= 0 || HitTestChevron(e.Location) >= 0)
            return;
        EmptyAreaDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_contentWidth <= ViewportWidth) return;
        _scrollX = Math.Clamp(_scrollX - Math.Sign(e.Delta) * 48, 0, MaxScroll);
        RecalcLayout();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(EditorColors.BgPrimary);

        using var titleFont = UiChrome.ChromeFont(9f, FontStyle.Bold);

        // Tabs are clipped to the region between the chevrons so a partially
        // scrolled tab never paints under the buttons.
        var viewport = new Rectangle(
            _showChevrons ? ChevronZone : 0, 0,
            ViewportWidth, Height);
        var saved = g.Save();
        g.SetClip(viewport);

        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var rect = _tabRects[i];
            if (rect.Width <= 0) continue;
            if (rect.Right < viewport.Left || rect.Left > viewport.Right) continue;

            bool hover = i == _hoverIndex;
            var fill = tab.Active
                ? EditorColors.BgCard
                : hover
                    ? EditorColors.BgHover
                    : Color.Transparent;
            if (fill.A > 0)
            {
                using var path = EditorPaint.RoundedRect(rect, 4);
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
            }

            if (tab.Active)
            {
                // Spans the whole tab (under the close circle too) so the
                // accent line reads as a full underline, not a cut segment.
                using var accent = new Pen(EditorColors.Accent, 2f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawLine(accent, rect.Left + 8, rect.Bottom - 2, rect.Right - 8, rect.Bottom - 2);
            }

            int textLeft = rect.Left + 8;
            float ledCy = rect.Top + rect.Height / 2f;
            EditorColors.DrawStatusLed(g, textLeft, ledCy, tab.Dirty, tab.HasSavedPath, coreSize: 6f, auraSize: 10f);
            textLeft += 14;

            var close = _closeRects[i];
            var textRect = new Rectangle(textLeft, rect.Top, Math.Max(0, close.Left - textLeft - 4), rect.Height);
            TextRenderer.DrawText(
                g,
                tab.Title,
                titleFont,
                textRect,
                tab.Active ? EditorColors.TextPrimary : EditorColors.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            bool closeHover = i == _hoverCloseIndex;
            if (closeHover)
            {
                // Browser-style circle target around the glyph.
                using var bg = new SolidBrush(CloseHoverWash);
                g.FillEllipse(bg, close);
            }
            var closeColor = closeHover ? CloseIconHoverColor : CloseIconColor;
            // Render at 2x and downscale into an explicitly centered box: tiny
            // direct rasterizations can snap the strokes off-center by a pixel.
            using var glyph = FluentIcons.RenderBitmap("close", closeColor, 24, false);
            if (glyph is not null)
            {
                const float s = 12f;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(glyph,
                    close.X + (close.Width - s) / 2f,
                    close.Y + (close.Height - s) / 2f, s, s);
            }
        }

        g.Restore(saved);

        if (_showChevrons)
        {
            // Opaque zones mask any stray glyph bleed, then the buttons paint on top.
            using (var zone = new SolidBrush(EditorColors.BgPrimary))
            {
                g.FillRectangle(zone, new Rectangle(0, 0, ChevronZone, Height));
                g.FillRectangle(zone, new Rectangle(Width - ChevronZone, 0, ChevronZone, Height));
            }
            PaintChevron(g, _leftChevronRect, "chevronLeft", CanScrollLeft, hovered: _hoverChevron == 0);
            PaintChevron(g, _rightChevronRect, "chevronRight", CanScrollRight, hovered: _hoverChevron == 1);
        }

        using (var edge = new Pen(EditorColors.BorderSubtle))
            g.DrawLine(edge, 0, Height - 1, Width, Height - 1);
    }

    private void PaintChevron(Graphics g, Rectangle rect, string iconId, bool enabled, bool hovered)
    {
        if (hovered && enabled)
        {
            using var path = EditorPaint.RoundedRect(rect, 4);
            using var brush = new SolidBrush(EditorColors.BgHover);
            g.FillPath(brush, path);
        }

        var color = enabled ? CloseIconColor : Color.FromArgb(90, EditorColors.TextSecondary);
        using var glyph = FluentIcons.RenderBitmap(iconId, color, 32, false);
        if (glyph is not null)
        {
            const float s = 16f;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(glyph,
                rect.X + (rect.Width - s) / 2f,
                rect.Y + (rect.Height - s) / 2f, s, s);
        }
    }

    private int ViewportWidth => Math.Max(1, Width - (_showChevrons ? ChevronZone * 2 : 0));
    private int MaxScroll => Math.Max(0, _contentWidth - ViewportWidth);
    private bool CanScrollLeft => _showChevrons && _scrollX > 0;
    private bool CanScrollRight => _showChevrons && _scrollX < MaxScroll;

    private void ScrollByChevron(int direction)
    {
        // One viewport-ish step per click, matching the sibling apps.
        int step = Math.Max((int)(ViewportWidth * 0.65), 160);
        _scrollX = Math.Clamp(_scrollX + direction * step, 0, MaxScroll);
        RecalcLayout();
        Invalidate();
    }

    private void RecalcLayout()
    {
        _tabRects.Clear();
        _closeRects.Clear();
        const int pad = 4;
        int tabH = Math.Max(20, Height - 4);
        using var font = UiChrome.ChromeFont(9f, FontStyle.Bold);

        // Measure first: whether the chevrons appear decides the viewport, and
        // the viewport decides the scroll range.
        var widths = new int[_tabs.Count];
        int content = pad * 2;
        for (int i = 0; i < _tabs.Count; i++)
        {
            int titleW = TextRenderer.MeasureText(_tabs[i].Title, font, Size.Empty,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            widths[i] = Math.Clamp(titleW + 14 + 34, 112, 240);
            content += widths[i] + 3;
        }
        _contentWidth = content;

        _showChevrons = _contentWidth > Width;
        _scrollX = Math.Clamp(_scrollX, 0, MaxScroll);

        int x = (_showChevrons ? ChevronZone : 0) + pad - _scrollX;
        int y = Math.Max(1, (Height - tabH) / 2);
        for (int i = 0; i < _tabs.Count; i++)
        {
            var rect = new Rectangle(x, y, widths[i], tabH);
            _tabRects.Add(rect);
            _closeRects.Add(new Rectangle(rect.Right - 26, rect.Top + (rect.Height - 22) / 2, 22, 22));
            x += widths[i] + 3;
        }

        const int chevronH = 24;
        int cy = (Height - chevronH) / 2;
        _leftChevronRect = new Rectangle(ChevronMargin, cy, ChevronWidth, chevronH);
        _rightChevronRect = new Rectangle(Width - ChevronMargin - ChevronWidth, cy, ChevronWidth, chevronH);
    }

    private int HitTestTab(Point p)
    {
        // The chevron zones own their clicks; tabs never extend into them.
        if (_showChevrons && (p.X < ChevronZone || p.X > Width - ChevronZone))
            return -1;

        for (int i = 0; i < _tabRects.Count; i++)
        {
            if (_tabRects[i].Contains(p))
                return i;
        }
        return -1;
    }

    private int HitTestClose(Point p)
    {
        // A close button scrolled under a chevron zone must not capture clicks
        // or hover, since the chevron owns that space.
        if (_showChevrons && (p.X < ChevronZone || p.X > Width - ChevronZone))
            return -1;

        for (int i = 0; i < _closeRects.Count; i++)
        {
            if (_closeRects[i].Contains(p))
                return i;
        }
        return -1;
    }

    private int HitTestChevron(Point p)
    {
        if (!_showChevrons)
            return -1;
        if (CanScrollLeft && _leftChevronRect.Contains(p))
            return 0;
        if (CanScrollRight && _rightChevronRect.Contains(p))
            return 1;
        return -1;
    }
}