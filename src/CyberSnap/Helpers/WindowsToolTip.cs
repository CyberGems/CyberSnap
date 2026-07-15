using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Native;

namespace CyberSnap.Helpers;

public sealed class WindowsToolTip : Form
{
    private const int MaxWidth = 360;
    private const int PadX = 10;
    private const int PadY = 6;
    private readonly Font _font = UiChrome.ChromeFont(8.5f);
    private string _text = "";

    public WindowsToolTip()
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
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Hide();
            return;
        }

        CyberSnap.UI.Theme.Refresh();
        _text = text;
        BackColor = UiChrome.SurfaceTooltip;
        ForeColor = UiChrome.SurfaceTextPrimary;

        var preferred = TextRenderer.MeasureText(
            text,
            _font,
            new Size(MaxWidth - PadX * 2, 0),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        int width = Math.Min(MaxWidth, Math.Max(1, preferred.Width + PadX * 2));
        int height = Math.Max(1, preferred.Height + PadY * 2);

        int x = anchorScreenBounds.Left + (anchorScreenBounds.Width - width) / 2;
        int y = above
            ? anchorScreenBounds.Top - height - 8
            : anchorScreenBounds.Bottom + 8;

        var screen = Screen.FromRectangle(anchorScreenBounds).WorkingArea;
        x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - width - 4));
        y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - height - 4));

        Bounds = new Rectangle(x, y, width, height);
        Region?.Dispose();
        using (var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, width, height), 7f))
            Region = new Region(path);

        if (!Visible)
            Show(owner);

        try
        {
            CyberSnap.Native.Dwm.TrySetWindowCornerPreference(Handle, CyberSnap.Native.Dwm.DWMWCP_ROUND);
            CyberSnap.Native.Dwm.TrySetImmersiveDarkMode(Handle, UiChrome.IsDark);
        }
        catch { }

        Invalidate();
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
        TextRenderer.DrawText(
            g,
            _text,
            _font,
            textRect,
            UiChrome.SurfaceTextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Region?.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
