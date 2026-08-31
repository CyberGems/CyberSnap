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
/// </summary>
internal sealed class EditorTabStrip : DoubleBufferedPanel
{
    public const int PreferredHeight = 28;

    private readonly List<EditorTabInfo> _tabs = new();
    private readonly List<Rectangle> _tabRects = new();
    private readonly List<Rectangle> _closeRects = new();
    private int _hoverIndex = -1;
    private int _hoverCloseIndex = -1;
    private int _scrollX;
    private int _contentWidth;

    public event EventHandler<int>? TabSelected;
    public event EventHandler<int>? TabCloseRequested;
    public event EventHandler? EmptyAreaDoubleClicked;

    private readonly ToolTip _closeTip = new()
    {
        ShowAlways = true,
        AutoPopDelay = 4000,
        InitialDelay = 400,
        ReshowDelay = 200,
    };

    public EditorTabStrip()
    {
        Dock = DockStyle.Fill;
        TabStop = false;
        SetStyle(ControlStyles.StandardDoubleClick, true);
        BackColor = EditorColors.BgPrimary;
        Cursor = Cursors.Hand;
    }

    public void SetTabs(IReadOnlyList<EditorTabInfo> tabs)
    {
        _tabs.Clear();
        _tabs.AddRange(tabs);
        _hoverIndex = -1;
        _hoverCloseIndex = -1;
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
        if (tab != _hoverIndex || close != _hoverCloseIndex)
        {
            _hoverIndex = tab;
            _hoverCloseIndex = close;
            Invalidate();
        }
        Cursor = tab >= 0 ? Cursors.Hand : Cursors.Default;
        if (close >= 0)
            _closeTip.SetToolTip(this, LocalizationService.Translate("Close tab"));
        else
            _closeTip.SetToolTip(this, string.Empty);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1 || _hoverCloseIndex != -1)
        {
            _hoverIndex = -1;
            _hoverCloseIndex = -1;
            Invalidate();
        }
        _closeTip.SetToolTip(this, string.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _closeTip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int close = HitTestClose(e.Location);
        if (close >= 0)
        {
            TabCloseRequested?.Invoke(this, close);
            return;
        }

        int tab = HitTestTab(e.Location);
        if (tab >= 0)
            TabSelected?.Invoke(this, tab);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (HitTestTab(e.Location) >= 0 || HitTestClose(e.Location) >= 0)
            return;
        EmptyAreaDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_contentWidth <= Width) return;
        int max = Math.Max(0, _contentWidth - Width);
        _scrollX = Math.Clamp(_scrollX - Math.Sign(e.Delta) * 48, 0, max);
        RecalcLayout();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(EditorColors.BgPrimary);

        using (var edge = new Pen(EditorColors.BorderSubtle))
            g.DrawLine(edge, 0, Height - 1, Width, Height - 1);

        using var titleFont = UiChrome.ChromeFont(8f, FontStyle.Bold);
        using var closeFont = UiChrome.ChromeFont(7.5f, FontStyle.Bold);

        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var rect = _tabRects[i];
            if (rect.Width <= 0) continue;

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
                using var accent = new Pen(EditorColors.Accent, 2f);
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
            var closeColor = closeHover ? EditorColors.TextPrimary : EditorColors.TextMuted;
            TextRenderer.DrawText(
                g,
                "×",
                closeFont,
                close,
                closeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    private void RecalcLayout()
    {
        _tabRects.Clear();
        _closeRects.Clear();
        const int pad = 4;
        int tabH = Math.Max(20, Height - 4);
        int x = pad - _scrollX;
        int y = Math.Max(1, (Height - tabH) / 2);
        using var font = UiChrome.ChromeFont(8f, FontStyle.Bold);
        foreach (var tab in _tabs)
        {
            int titleW = TextRenderer.MeasureText(tab.Title, font, Size.Empty,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            int w = Math.Clamp(titleW + 14 + 32, 96, 210);
            var rect = new Rectangle(x, y, w, tabH);
            _tabRects.Add(rect);
            _closeRects.Add(new Rectangle(rect.Right - 20, rect.Top + 2, 16, rect.Height - 4));
            x += w + 3;
        }
        _contentWidth = x + pad + _scrollX;
        int maxScroll = Math.Max(0, _contentWidth - Width);
        if (_scrollX > maxScroll)
            _scrollX = maxScroll;
    }

    private int HitTestTab(Point p)
    {
        for (int i = 0; i < _tabRects.Count; i++)
        {
            if (_tabRects[i].Contains(p))
                return i;
        }
        return -1;
    }

    private int HitTestClose(Point p)
    {
        for (int i = 0; i < _closeRects.Count; i++)
        {
            if (_closeRects[i].Contains(p))
                return i;
        }
        return -1;
    }
}
