using System.Drawing;
using System.Drawing.Imaging;
using CyberSnap.Native;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using Color = System.Drawing.Color;

namespace CyberSnap.Capture;

/// <summary>Uploads the frozen desktop once. Display and capture share the same Direct2D sampler.</summary>
internal sealed class ZoomRenderer : IDisposable
{
    private ID2D1Factory? _factory;
    private ID2D1HwndRenderTarget? _target;
    private ID2D1Bitmap? _image;
    private ID2D1GdiInteropRenderTarget? _interop;
    private ID2D1Bitmap? _helpImage;
    private Bitmap? _helpSource;
    private readonly System.Drawing.Size _size;

    public ZoomRenderer(IntPtr window, Bitmap original)
    {
        _size = original.Size;
        try
        {
            _factory = D2D1.D2D1CreateFactory<ID2D1Factory>();
            _target = _factory.CreateHwndRenderTarget(new RenderTargetProperties
            {
                Type = RenderTargetType.Default,
                PixelFormat = new D2DPixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                DpiX = 96, DpiY = 96,
                Usage = RenderTargetUsage.GdiCompatible
            }, new HwndRenderTargetProperties
            {
                Hwnd = window, PixelSize = new SizeI(_size.Width, _size.Height), PresentOptions = PresentOptions.Immediately
            });
            using var upload = original.Clone(new Rectangle(System.Drawing.Point.Empty, original.Size), DrawingPixelFormat.Format32bppPArgb);
            var bits = upload.LockBits(new Rectangle(System.Drawing.Point.Empty, upload.Size), ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppPArgb);
            try
            {
                _image = _target.CreateBitmap(new SizeI(_size.Width, _size.Height), bits.Scan0, (uint)bits.Stride,
                    new BitmapProperties(new D2DPixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
            }
            finally { upload.UnlockBits(bits); }
            _interop = _target.QueryInterface<ID2D1GdiInteropRenderTarget>();
        }
        catch { Dispose(); throw; }
    }

    public void Paint(ZoomViewport viewport, bool smooth, Action<ZoomRenderer>? chrome = null)
    {
        BeginScene(viewport, smooth);
        try { chrome?.Invoke(this); }
        finally { _target!.EndDraw().CheckError(); }
    }

    private void BeginScene(ZoomViewport viewport, bool smooth)
    {
        _target!.BeginDraw();
        var source = viewport.SourceBounds;
        _target.DrawBitmap(_image!, new Rect(0, 0, _size.Width, _size.Height), 1,
            smooth ? BitmapInterpolationMode.Linear : BitmapInterpolationMode.NearestNeighbor,
            new Rect(source.X, source.Y, source.Width, source.Height));
    }

    public Bitmap Snapshot(ZoomViewport viewport, bool smooth, Action<ZoomRenderer>? chrome = null)
    {
        Bitmap? result = null;
        BeginScene(viewport, smooth);
        try
        {
            chrome?.Invoke(this);
            result = new Bitmap(_size.Width, _size.Height, DrawingPixelFormat.Format24bppRgb);
            try
            {
                // Read the actual rendered surface before adding capture chrome. No second resampling pass.
                var sourceDc = _interop!.GetDC(DcInitializeMode.Copy);
                try
                {
                    using var graphics = Graphics.FromImage(result);
                    var destinationDc = graphics.GetHdc();
                    try
                    {
                        if (!Gdi32.BitBlt(destinationDc, 0, 0, _size.Width, _size.Height, sourceDc, 0, 0, 0x00CC0020))
                            throw new System.ComponentModel.Win32Exception();
                    }
                    finally { graphics.ReleaseHdc(destinationDc); }
                }
                finally { _interop.ReleaseDC(null); }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally
        {
            try { _target!.EndDraw().CheckError(); }
            catch { result?.Dispose(); throw; }
        }
    }

    public void DrawHelp(Bitmap surface, System.Drawing.Point location)
    {
        if (!ReferenceEquals(surface, _helpSource))
        {
            var bits = surface.LockBits(new Rectangle(System.Drawing.Point.Empty, surface.Size), ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppPArgb);
            try
            {
                var image = _target!.CreateBitmap(new SizeI(surface.Width, surface.Height), bits.Scan0, (uint)bits.Stride,
                    new BitmapProperties(new D2DPixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
                _helpImage?.Dispose();
                _helpImage = image;
                _helpSource = surface;
            }
            finally { surface.UnlockBits(bits); }
        }
        _target!.DrawBitmap(_helpImage!, new Rect(location.X, location.Y, surface.Width, surface.Height),
            1f, BitmapInterpolationMode.NearestNeighbor, new Rect(0, 0, surface.Width, surface.Height));
    }

    public void Dispose()
    {
        _helpImage?.Dispose(); _interop?.Dispose();
        _image?.Dispose(); _target?.Dispose(); _factory?.Dispose();
    }
}
