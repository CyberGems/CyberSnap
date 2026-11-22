using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Native;

namespace CyberSnap.Helpers;

public enum ToolTipPlacement
{
    Above,
    Below,
    Left,
    Right
}

public sealed class WindowsToolTip : Form
{
    private const int MaxWidth = 360;
    private const int PadX = 10;
    private const int PadY = 6;
    private const int AnchorGap = 8;
    private readonly Font _font = UiChrome.ChromeFont(8.5f);
    private readonly Font _titleFont = UiChrome.ChromeFont(8.5f, FontStyle.Bold);
    private readonly Font _badgeFont = UiChrome.ChromeFont(7.5f, FontStyle.Bold);
    [System.ComponentModel.DefaultValue(false)]
    public bool EmphasizeTitle { get; set; }
    private string? _title;
    private string? _description;
    private int _titleHeight;
    private int _titleWidth;
    private string _text = "";
    private bool _singleLine;
    // Hotkey hint lifted out of the text and painted as a keycap badge, so capture-bar
    // tooltips use the same chip as the rest of the suite instead of a plain "(Esc)".
    private string? _shortcut;
    private int _badgeWidth;
    private int _badgeHeight;

    public WindowsToolTip() : this(managedTopMost: true) { }

    internal WindowsToolTip(bool managedTopMost)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // WinForms may focus a managed TopMost form on creation or show even with
        // ShowWithoutActivation. The Editor uses only the native, non-activating raise below.
        TopMost = managedTopMost;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = UiChrome.SurfaceTooltip;
        ForeColor = UiChrome.SurfaceTextPrimary;
    }

    protected override bool ShowWithoutActivation => true;

    // A tooltip must never absorb mouse input. Because it is a TopMost window painted
    // *above* its anchor, it can overlap the control in the row above (e.g. the Emoji
    // tool's hint sits over the Highlight button). Returning HTTRANSPARENT on hit-test
    // makes the whole window click-/hover-through, so the mouse reaches the button below.
    private const int WM_NCHITTEST = 0x0084;
    private static readonly IntPtr HTTRANSPARENT = new(-1);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = HTTRANSPARENT;
            return;
        }

        // The tooltip is a separate top-level window and can become the keyboard target
        // despite WS_EX_NOACTIVATE. Forward key-down messages to the owner synchronously
        // so hotkeys fire on the same keypress (PostMessage was one frame too late).
        if (m.Msg is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN)
        {
            var owner = Owner;
            Hide();
            if (owner is { IsDisposed: false } && owner.IsHandleCreated)
            {
                User32.SendMessage(owner.Handle, m.Msg, m.WParam, m.LParam);
            }
            return;
        }

        base.WndProc(ref m);
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureWindowExclusion.Register(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        CaptureWindowExclusion.Unregister(Handle);
        base.OnHandleDestroyed(e);
    }

    public void ShowNear(IWin32Window owner, string text, Rectangle anchorScreenBounds, bool above)
        => ShowNear(owner, text, anchorScreenBounds, above ? ToolTipPlacement.Above : ToolTipPlacement.Below);

    public void ShowNear(
        IWin32Window owner,
        string text,
        Rectangle anchorScreenBounds,
        ToolTipPlacement placement,
        bool singleLine = false,
        bool attachToOwner = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Hide();
            return;
        }

        CyberSnap.UI.Theme.Refresh();
        _text = text;
        _shortcut = ExtractShortcutHint(ref _text);
        _singleLine = singleLine;
        _badgeWidth = 0;
        _badgeHeight = 0;
        BackColor = UiChrome.SurfaceTooltip;
        ForeColor = UiChrome.SurfaceTextPrimary;

        // Single-line tooltips measure without word-wrap so the title + hint stay on one row.
        var measureSize = singleLine ? new Size(int.MaxValue, 0) : new Size(MaxWidth - PadX * 2, 0);
        var measureFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        if (!singleLine)
            measureFlags |= TextFormatFlags.WordBreak;

        // Create the handle ON the anchor's monitor before Show: otherwise the handle is
        // born when Show places the window, and landing on a monitor whose DPI differs
        // from the owner's fires WM_DPICHANGED — .NET 10 PMv2 rescales the form while the
        // text keeps the thread's fixed metrics, inflating/offsetting the box on the 150%
        // monitor (the confirm-dock tooltips clipped and the toolbar tooltips drifted
        // left, 150% only). Control.CreateControl skips invisible controls; the Handle
        // getter forces creation.
        if (!IsHandleCreated)
        {
            Bounds = new Rectangle(anchorScreenBounds.Left, anchorScreenBounds.Top, 1, 1);
            _ = Handle;
        }
        var preferred = TextRenderer.MeasureText(_text, _font, measureSize, measureFlags);
        _title = null;
        _description = null;
        _titleWidth = 0;
        if (_shortcut is not null)
        {
            var badgeText = TextRenderer.MeasureText(_shortcut, _badgeFont, new Size(int.MaxValue, 0), measureFlags);
            _badgeWidth = badgeText.Width + 10;
            _badgeHeight = badgeText.Height + 4;
        }
        if (EmphasizeTitle && !singleLine && _text.IndexOf('\n') is int split && split > 0)
        {
            _title = _text[..split];
            _description = _text[(split + 1)..];
            var titleSize = TextRenderer.MeasureText(_title, _titleFont, measureSize, measureFlags);
            var bodySize = TextRenderer.MeasureText(_description, _font, measureSize, measureFlags);
            _titleWidth = titleSize.Width;
            _titleHeight = Math.Max(titleSize.Height, _badgeHeight);
            int titleRowWidth = _badgeWidth > 0 ? titleSize.Width + 8 + _badgeWidth : titleSize.Width;
            preferred = new Size(Math.Max(titleRowWidth, bodySize.Width), _titleHeight + 4 + bodySize.Height);
        }
        else if (_badgeWidth > 0)
        {
            preferred = singleLine
                ? new Size(preferred.Width + 8 + _badgeWidth, Math.Max(preferred.Height, _badgeHeight))
                : new Size(Math.Max(preferred.Width, _badgeWidth), preferred.Height + 4 + _badgeHeight);
        }
        int width = singleLine
            ? Math.Max(1, preferred.Width + PadX * 2)
            : Math.Min(MaxWidth, Math.Max(1, preferred.Width + PadX * 2));
        int height = Math.Max(1, preferred.Height + PadY * 2);

        int x;
        int y;
        switch (placement)
        {
            case ToolTipPlacement.Left:
                x = anchorScreenBounds.Left - width - AnchorGap;
                y = anchorScreenBounds.Top + (anchorScreenBounds.Height - height) / 2;
                break;
            case ToolTipPlacement.Right:
                x = anchorScreenBounds.Right + AnchorGap;
                y = anchorScreenBounds.Top + (anchorScreenBounds.Height - height) / 2;
                break;
            case ToolTipPlacement.Below:
                x = anchorScreenBounds.Left + (anchorScreenBounds.Width - width) / 2;
                y = anchorScreenBounds.Bottom + AnchorGap;
                break;
            default: // Above
                x = anchorScreenBounds.Left + (anchorScreenBounds.Width - width) / 2;
                y = anchorScreenBounds.Top - height - AnchorGap;
                break;
        }

        var screen = Screen.FromRectangle(anchorScreenBounds).WorkingArea;
        x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - width - 4));
        y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - height - 4));

        // If a side placement was clamped back over the column, flip to the other side.
        if (placement is ToolTipPlacement.Left or ToolTipPlacement.Right)
        {
            var tip = new Rectangle(x, y, width, height);
            if (tip.IntersectsWith(Rectangle.Inflate(anchorScreenBounds, 2, 2)))
            {
                x = placement == ToolTipPlacement.Left
                    ? anchorScreenBounds.Right + AnchorGap
                    : anchorScreenBounds.Left - width - AnchorGap;
                x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - width - 4));
            }
        }

        var targetBounds = new Rectangle(x, y, width, height);
        Bounds = targetBounds;
        // Moving an existing Form to another monitor raises WM_DPICHANGED inside the
        // Bounds setter. WinForms then scales the size we already measured in screen
        // pixels. Set it again after the handle has adopted the destination DPI.
        if (Bounds != targetBounds)
            Bounds = targetBounds;
        Region?.Dispose();
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, width, height), 7f))
            Region = new Region(path);

        if (!Visible)
        {
            // Owning a TopMost tooltip to a fullscreen overlay raises that overlay
            // above sibling TopMost windows (the recording bar). Skip ownership when
            // the caller needs the overlay to stay underneath other chrome.
            if (attachToOwner)
                Show(owner);
            else
                Show();
        }

        // Windows demotes HWNDs inside the topmost band over time (other topmost chrome,
        // Hide/Show cycles); the widget and the recording bar re-assert natively for the
        // same reason. A demoted tooltip that overlaps its anchor renders partially behind
        // the dock's own paint, leaving only a textless sliver visible.
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);

        try
        {
            CyberSnap.Native.Dwm.TrySetWindowCornerPreference(Handle, CyberSnap.Native.Dwm.DWMWCP_ROUND);
            CyberSnap.Native.Dwm.TrySetImmersiveDarkMode(Handle, UiChrome.IsDark);
        }
        catch { }

        Invalidate();
        // A freshly shown tooltip composites an empty surface until its first WM_PAINT;
        // when the overlay thread stalls that empty box lingers on screen. Paint now.
        Update();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        WindowsDockRenderer.PaintShadow(g, new RectangleF(0, 0, Width, Height), 7f);
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, Width, Height), 7f))
        {
            using (var brush = new SolidBrush(UiChrome.SurfaceTooltip))
                g.FillPath(brush, path);
            using (var pen = new Pen(UiChrome.SurfaceBorderStrong, 1f))
                g.DrawPath(pen, path);
        }

        var textRect = new Rectangle(PadX, PadY, Width - PadX * 2, Height - PadY * 2);
        if (_title is not null && _description is not null)
        {
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, _title, _titleFont,
                new Rectangle(textRect.X, textRect.Y, _titleWidth, _titleHeight), UiChrome.SurfaceTextPrimary, flags);
            if (_shortcut is not null)
            {
                int badgeX = Math.Min(textRect.X + _titleWidth + 8, textRect.Right - _badgeWidth);
                PaintShortcutBadge(g, badgeX, textRect.Y, textRect.Y + _titleHeight);
            }
            TextRenderer.DrawText(g, _description, _font,
                new Rectangle(textRect.X, textRect.Y + _titleHeight + 4, textRect.Width, textRect.Height - _titleHeight - 4),
                UiChrome.SurfaceTextSecondary, flags);
            return;
        }
        var drawFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        if (_singleLine)
            drawFlags |= TextFormatFlags.SingleLine;
        else
            drawFlags |= TextFormatFlags.WordBreak;
        if (_shortcut is null)
        {
            TextRenderer.DrawText(g, _text, _font, textRect, UiChrome.SurfaceTextPrimary, drawFlags);
            return;
        }

        if (_singleLine)
        {
            var textWidth = Math.Max(1, textRect.Width - _badgeWidth - 8);
            TextRenderer.DrawText(g, _text, _font,
                new Rectangle(textRect.X, textRect.Y, textWidth, textRect.Height),
                UiChrome.SurfaceTextPrimary, drawFlags);
            PaintShortcutBadge(g, textRect.X + textWidth + 8, textRect.Y, textRect.Bottom);
            return;
        }

        var bodyHeight = Math.Max(1, textRect.Height - _badgeHeight - 4);
        TextRenderer.DrawText(g, _text, _font,
            new Rectangle(textRect.X, textRect.Y, textRect.Width, bodyHeight),
            UiChrome.SurfaceTextPrimary, drawFlags);
        PaintShortcutBadge(g, textRect.X, textRect.Y + bodyHeight + 4, textRect.Bottom);
    }

    /// <summary>
    /// Lifts a trailing hotkey hint off the first line. The capture bar composes hints as
    /// "Label  (Ctrl+Z)" / "Cancel capture  (Esc)\nDescription"; the renderer paints that
    /// hint as a keycap badge instead of plain text.
    /// </summary>
    private static string? ExtractShortcutHint(ref string text)
    {
        int lineEnd = text.IndexOf('\n');
        string firstLine = lineEnd < 0 ? text : text[..lineEnd];
        if (!firstLine.EndsWith(')'))
            return null;
        int open = firstLine.LastIndexOf("  (", StringComparison.Ordinal);
        if (open < 0)
            return null;
        string hint = firstLine[(open + 3)..^1];
        if (hint.Length == 0 || hint.Contains('(') || hint.Contains(')'))
            return null;
        string title = firstLine[..open];
        text = lineEnd < 0 ? title : title + text[lineEnd..];
        return hint;
    }

    /// <summary>Keycap at <paramref name="x"/>, vertically centered between top and bottom.</summary>
    private void PaintShortcutBadge(Graphics g, int x, int top, int bottom)
    {
        if (_shortcut is null || _badgeWidth <= 0) return;
        var bounds = new Rectangle(x, top + Math.Max(0, (bottom - top - _badgeHeight) / 2), _badgeWidth, _badgeHeight);
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), 4f))
        {
            using (var fill = new SolidBrush(UiChrome.KeycapBackground))
                g.FillPath(fill, path);
            using (var pen = new Pen(UiChrome.KeycapBorder, 1f))
                g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, _shortcut, _badgeFont, bounds, UiChrome.SurfaceTextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Region?.Dispose();
            _font.Dispose();
            _titleFont.Dispose();
            _badgeFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
