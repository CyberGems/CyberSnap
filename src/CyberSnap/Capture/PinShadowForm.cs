using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Native;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>A click-through, per-pixel shadow immediately beneath its Pin. Owns its cached surface.</summary>
internal sealed class PinShadowForm : Form
{
    private Bitmap? _surface;
    private Rectangle _imageInSurface;
    private Rectangle _presentedBounds;
    private Rectangle _ownerBounds;
    private double _dpi;
    private byte _alpha;
    private bool _includeInCaptures;

    internal PinShadowForm(bool includeInCaptures)
    {
        _includeInCaptures = includeInCaptures;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | User32.WS_EX_TOOLWINDOW;
            // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
        if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        base.WndProc(ref message);
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { e.Cancel = true; base.OnDpiChanged(e); }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.TrySetTransitionsForcedDisabled(Handle, true);
        Dwm.TrySetWindowCornerPreference(Handle, Dwm.DWMWCP_DONOTROUND);
        SetCaptureAffinity();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        CaptureWindowExclusion.Unregister(Handle);
        base.OnHandleDestroyed(e);
    }

    internal void ApplyCapturePreference(bool include)
    {
        if (_includeInCaptures == include) return;
        _includeInCaptures = include;
        if (IsHandleCreated) SetCaptureAffinity();
    }
    private void SetCaptureAffinity()
    {
        if (!_includeInCaptures) CaptureWindowExclusion.Apply(this);
        else
        {
            CaptureWindowExclusion.Unregister(Handle);
            if (!User32.SetWindowDisplayAffinity(Handle, 0))
                AppDiagnostics.LogWarning("pin.shadow-affinity", "Could not enable capture of the Pin shadow.");
        }
    }

    internal void UpdateFor(PinImageForm owner, bool positionChanged)
    {
        var image = owner.Bounds;
        byte alpha = (byte)Math.Clamp((int)Math.Round(owner.Opacity * 255), 0, 255);
        if (!positionChanged && Visible && _ownerBounds == image && _alpha == alpha && TopMost == owner.TopMost) return;
        _ownerBounds = image;
        double dpi = PopupWindowHelper.GetScaleForPoint(new Point(image.Left + image.Width / 2, image.Top + image.Height / 2)).X;
        int margin = (int)Math.Ceiling(24 * dpi);
        var bounds = Rectangle.Intersect(Rectangle.Inflate(image, margin, margin), ScreenCapture.GetVirtualScreenBounds());
        // Oversized references may cover the desktop completely; no visible shadow needs a surface.
        if (bounds.IsEmpty || image.Contains(bounds)) { Hide(); return; }
        var localImage = new Rectangle(image.Left - bounds.Left, image.Top - bounds.Top, image.Width, image.Height);
        bool redraw = _surface is null || _surface.Size != bounds.Size || _imageInSurface != localImage || _dpi != dpi;
        if (redraw)
        {
            var surface = RenderShadow(bounds.Size, localImage, dpi);
            _surface?.Dispose();
            _surface = surface;
            _imageInSurface = localImage; _dpi = dpi;
        }
        bool topmostChanged = TopMost != owner.TopMost;
        if (topmostChanged) TopMost = owner.TopMost;
        if (redraw || alpha != _alpha || !IsHandleCreated)
        {
            Bounds = bounds;
            Present(bounds, alpha);
            _alpha = alpha;
        }
        bool hidden = !Visible;
        if (hidden) Show();
        if (positionChanged || redraw || hidden || topmostChanged || bounds != _presentedBounds)
        {
            if (!User32.SetWindowPos(Handle, owner.Handle, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the Pin shadow.");
            _presentedBounds = bounds;
        }
    }

    private static unsafe Bitmap RenderShadow(Size size, Rectangle image, double dpi)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        double sigma = 6 * dpi, offset = 3 * dpi;
        var horizontal = new double[size.Width];
        for (int x = 0; x < size.Width; x++)
            horizontal[x] = NormalCdf((x + .5 - image.Left) / sigma) - NormalCdf((x + .5 - image.Right) / sigma);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < size.Height; y++)
            {
                double vertical = NormalCdf((y + .5 - image.Top - offset) / sigma)
                    - NormalCdf((y + .5 - image.Bottom - offset) / sigma);
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                new Span<byte>(row, size.Width * 4).Clear();
                for (int x = 0; x < size.Width; x++)
                {
                    // Punch out the image area so transparent Pins never reveal a black rectangle beneath them.
                    if (y >= image.Top && y < image.Bottom && x >= image.Left && x < image.Right) continue;
                    row[x * 4 + 3] = (byte)Math.Clamp((int)Math.Round(72 * horizontal[x] * vertical), 0, 255);
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static double NormalCdf(double value)
    {
        double x = Math.Abs(value) / Math.Sqrt(2), t = 1 / (1 + .3275911 * x);
        double erf = 1 - (((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - .284496736) * t + .254829592) * t) * Math.Exp(-x * x);
        return .5 * (1 + Math.CopySign(erf, value));
    }

    private void Present(Rectangle bounds, byte alpha)
    {
        var point = new User32.POINT(bounds.X, bounds.Y);
        var size = new User32.SIZE { cx = bounds.Width, cy = bounds.Height };
        var source = new User32.POINT();
        var blend = new User32.BLENDFUNCTION { SourceConstantAlpha = alpha, AlphaFormat = 1 };
        IntPtr screen = User32.GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = User32.CreateCompatibleDC(screen);
            bitmap = _surface!.GetHbitmap(Color.FromArgb(0));
            previous = User32.SelectObject(memory, bitmap);
            if (!User32.UpdateLayeredWindow(Handle, screen, ref point, ref size, memory, ref source, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not render the Pin shadow.");
        }
        finally
        {
            if (memory != IntPtr.Zero && previous != IntPtr.Zero) User32.SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) User32.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) User32.DeleteDC(memory);
            User32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _surface?.Dispose(); _surface = null; }
        base.Dispose(disposing);
    }
}
