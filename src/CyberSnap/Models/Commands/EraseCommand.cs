using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CyberSnap.Models.Commands;

public sealed class EraseCommand : IEditCommand
{
    private readonly Rectangle _eraseRect;
    private byte[]? _savedPixels;
    private bool _disposed;

    public EraseCommand(Rectangle eraseRect)
    {
        _eraseRect = eraseRect;
    }

    public string Description => "Erase";

    public void Apply(IEditorContext ctx)
    {
        if (_disposed) return;

        var bmp = ctx.BaseBitmap;
        var rect = Rectangle.Intersect(_eraseRect, new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        if (_savedPixels is null)
        {
            bmp = EnsureAlphaFormat(ctx);
            rect = Rectangle.Intersect(_eraseRect, new Rectangle(0, 0, bmp.Width, bmp.Height));
            _savedPixels = CopyPixels(bmp, rect);
        }

        ClearPixels(bmp, rect);
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        if (_disposed || _savedPixels is null) return;

        var bmp = ctx.BaseBitmap;
        var rect = Rectangle.Intersect(_eraseRect, new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        RestorePixels(bmp, rect, _savedPixels);
        ctx.Invalidate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _savedPixels = null;
    }

    private static Bitmap EnsureAlphaFormat(IEditorContext ctx)
    {
        var bmp = ctx.BaseBitmap;
        var fmt = bmp.PixelFormat;

        if (fmt == PixelFormat.Format32bppPArgb || fmt == PixelFormat.Format32bppArgb)
            return bmp;

        var newBmp = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(newBmp);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(bmp, 0, 0);
        ctx.BaseBitmap = newBmp;
        bmp.Dispose();
        return newBmp;
    }

    private static byte[] CopyPixels(Bitmap bmp, Rectangle rect)
    {
        int stride = rect.Width * 4;
        int size = stride * rect.Height;
        var data = new byte[size];

        var bits = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < rect.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(bits.Scan0, y * bits.Stride),
                    data, y * stride, stride);
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }

        return data;
    }

    private static void ClearPixels(Bitmap bmp, Rectangle rect)
    {
        var bits = bmp.LockBits(rect, ImageLockMode.ReadWrite, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < rect.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    new byte[rect.Width * 4],
                    0,
                    IntPtr.Add(bits.Scan0, y * bits.Stride),
                    rect.Width * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }
    }

    private static void RestorePixels(Bitmap bmp, Rectangle rect, byte[] pixels)
    {
        int stride = rect.Width * 4;
        var bits = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < rect.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    pixels, y * stride,
                    IntPtr.Add(bits.Scan0, y * bits.Stride),
                    stride);
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }
    }
}
