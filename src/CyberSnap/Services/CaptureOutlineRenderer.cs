using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CyberSnap.Services;

public static class CaptureOutlineRenderer
{
    public const int MaxThickness = 32;

    public static bool TryGetSize(int width, int height, int thickness, out Size size)
    {
        size = default;
        long w = (long)width + 2L * thickness, h = (long)height + 2L * thickness;
        if (width <= 0 || height <= 0 || thickness < 1 || thickness > MaxThickness
            || w > ImageScaleService.MaxDimension || h > ImageScaleService.MaxDimension
            || w * h > ImageScaleService.MaxPixels) return false;
        size = new Size((int)w, (int)h);
        return true;
    }

    public static Bitmap Render(Bitmap source, int thickness, Color color)
    {
        if (!TryGetSize(source.Width, source.Height, thickness, out var size))
            throw new InvalidOperationException("The outline would exceed the image size limit.");
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(Color.FromArgb(255, color.R, color.G, color.B));
            graphics.DrawImageUnscaled(source, thickness, thickness);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
