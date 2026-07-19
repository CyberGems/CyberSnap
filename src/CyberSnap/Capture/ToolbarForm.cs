using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Services;

namespace CyberSnap.Capture;

/// <summary>
/// Separate borderless Form that renders the toolbar, tooltips, and popups.
/// Uses UpdateLayeredWindow for per-pixel alpha -- no TransparencyKey needed.
/// Owned by RegionOverlayForm and positioned over it.
/// Having its own HWND means DWM composites it independently -- no tearing.
/// </summary>
public sealed class ToolbarForm : Form
{
    private readonly RegionOverlayForm _owner;
    private Bitmap? _surface;
    private Graphics? _surfaceGraphics;
    private int _lastRenderVersion = int.MinValue;
    private Size _lastRenderSize;
    private Point _lastRenderLocation;
    private byte _surfaceAlpha = 255;

    /// <summary>0–255 overall opacity for the layered toolbar surface (used by dock enter anim).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public byte SurfaceAlpha
    {
        get => _surfaceAlpha;
        set
        {
            byte v = value;
            if (_surfaceAlpha == v) return;
            _surfaceAlpha = v;
            // Force a present even if the bitmap content is unchanged.
            _lastRenderVersion = int.MinValue;
        }
    }

    public ToolbarForm(RegionOverlayForm owner)
    {
        _owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = owner.TopMost;
        StartPosition = FormStartPosition.Manual;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (click-through)
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureWindowExclusion.Apply(this);
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        base.SetBoundsCore(x, y, width, height, specified);
        _lastRenderVersion = int.MinValue;
    }

    public void UpdateSurface(Rectangle? newBounds = null)
    {
        var targetBounds = newBounds ?? Bounds;
        var sz = targetBounds.Size;
        if (sz.Width <= 0 || sz.Height <= 0) return;

        var location = targetBounds.Location;
        var renderVersion = _owner.ToolbarRenderVersion;
        if (_surface is not null &&
            _lastRenderVersion == renderVersion &&
            _lastRenderSize == sz &&
            _lastRenderLocation == location &&
            Bounds == targetBounds)
        {
            return;
        }

        if (_surface == null || _surface.Width != sz.Width || _surface.Height != sz.Height)
        {
            _surfaceGraphics?.Dispose();
            _surface?.Dispose();
            _surface = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppPArgb);
            _surfaceGraphics = Graphics.FromImage(_surface);
        }

        // _owner paints using overlay-client coordinates (e.g. _toolbarRect).
        // This form is positioned at screen coords; the overlay's screen origin
        // is _owner.Left, _owner.Top. So translate = overlayScreenOrigin - targetScreenOrigin.
        int dx = _owner.Left - targetBounds.X;
        int dy = _owner.Top - targetBounds.Y;

        var g = _surfaceGraphics!;
        try
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.TranslateTransform(dx, dy);
            _owner.PaintToolbarTo(g);
            g.ResetTransform();
            g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        }
        catch (Exception ex)
        {
            Services.AppDiagnostics.LogError("toolbar.paint-failed", ex);
            g.Clear(Color.Transparent);
            g.ResetTransform();
        }

        var screenPt = new Native.User32.POINT { X = targetBounds.X, Y = targetBounds.Y };
        var size = new Native.User32.SIZE { cx = sz.Width, cy = sz.Height };
        var srcPt = new Native.User32.POINT { X = 0, Y = 0 };
        var blend = new Native.User32.BLENDFUNCTION
        {
            BlendOp = 0, // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = _surfaceAlpha,
            AlphaFormat = 1  // AC_SRC_ALPHA
        };

        IntPtr hdcScreen = Native.User32.GetDC(IntPtr.Zero);
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBmp = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;

        try
        {
            hdcMem = Native.User32.CreateCompatibleDC(hdcScreen);
            hBmp = _surface!.GetHbitmap(Color.FromArgb(0));
            hOld = Native.User32.SelectObject(hdcMem, hBmp);
            Native.User32.UpdateLayeredWindow(Handle, hdcScreen, ref screenPt, ref size,
                hdcMem, ref srcPt, 0, ref blend, 2 /* ULW_ALPHA */);
            _lastRenderVersion = renderVersion;
            _lastRenderSize = sz;
            _lastRenderLocation = location;
            if (Bounds != targetBounds)
            {
                SetBoundsCore(targetBounds.X, targetBounds.Y, targetBounds.Width, targetBounds.Height, BoundsSpecified.All);
            }
        }
        finally
        {
            if (hdcMem != IntPtr.Zero && hOld != IntPtr.Zero)
                Native.User32.SelectObject(hdcMem, hOld);
            if (hBmp != IntPtr.Zero)
                Native.User32.DeleteObject(hBmp);
            if (hdcMem != IntPtr.Zero)
                Native.User32.DeleteDC(hdcMem);
            Native.User32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            _owner.CancelFromShortcut();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _surfaceGraphics?.Dispose();
            _surface?.Dispose();
        }
        base.Dispose(disposing);
    }
}
