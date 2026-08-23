using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CyberSnap.Services;

public static class ImageScaleService
{
    public const int MaxDimension = 8192;
    public const long MaxPixels = 50_000_000L;
    public const int MaxFactor = 4;

    public static bool TryGetScaledSize(int srcW, int srcH, int factor, out int dstW, out int dstH, out string? errorKey)
    {
        dstW = 0; dstH = 0; errorKey = null;
        if (factor < 1 || factor > MaxFactor)
        {
            errorKey = "Scale factor out of range";
            return false;
        }
        if (srcW <= 0 || srcH <= 0)
        {
            errorKey = "Invalid source dimensions";
            return false;
        }
        long w = (long)srcW * factor;
        long h = (long)srcH * factor;
        if (w > MaxDimension || h > MaxDimension)
        {
            errorKey = "Scale exceeds max dimension";
            return false;
        }
        if (w * h > MaxPixels)
        {
            errorKey = "Scale exceeds max pixels";
            return false;
        }
        if (w > int.MaxValue || h > int.MaxValue)
        {
            errorKey = "Scale exceeds integer limit";
            return false;
        }
        dstW = (int)w;
        dstH = (int)h;
        return true;
    }

    public static bool IsFactorAvailable(int srcW, int srcH, int factor)
        => TryGetScaledSize(srcW, srcH, factor, out _, out _, out _);

    public static Bitmap Scale(Bitmap source, int factor)
    {
        if (factor == 1)
            return new Bitmap(source);
        if (!TryGetScaledSize(source.Width, source.Height, factor, out int dstW, out int dstH, out string? err))
            throw new InvalidOperationException(err ?? "Scale not available");

        var dest = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
        try
        {
            dest.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        }
        catch { }
        using var g = Graphics.FromImage(dest);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, dstW, dstH), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        return dest;
    }
}
