using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Native;

namespace CyberSnap.UI;

/// <summary>A temporary desktop veil behind the Pin source chooser, in physical pixels.</summary>
internal sealed class PinSourceBackdrop : Form
{
    internal PinSourceBackdrop()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.20;
        Bounds = ScreenCapture.GetVirtualScreenBounds();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= User32.WS_EX_TOOLWINDOW | 0x08000000; // WS_EX_NOACTIVATE
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.TrySetWindowCornerPreference(Handle, Dwm.DWMWCP_DONOTROUND);
        CaptureWindowExclusion.Apply(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // WinForms can adopt a different DPI while creating the HWND. Restore exact
        // virtual-desktop coverage without scaling the veil or taking focus.
        var bounds = ScreenCapture.GetVirtualScreenBounds();
        User32.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        CaptureWindowExclusion.Unregister(Handle);
        base.OnHandleDestroyed(e);
    }
}
