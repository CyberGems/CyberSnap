using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CyberSnap.Services;

public static class CaptureFrameRenderer
{
    public const int MaxMargin = 256;

    public static bool TryGetSize(int width, int height, int outline, int margin, out Size size)
    {
        size = default;
        long padding = (long)outline + margin;
        long w = width + 2 * padding, h = height + 2 * padding;
        if (width <= 0 || height <= 0 || outline < 0 || outline > CaptureOutlineRenderer.MaxThickness
            || margin < 0 || margin > MaxMargin
            || w > ImageScaleService.MaxDimension || h > ImageScaleService.MaxDimension
            || w * h > ImageScaleService.MaxPixels) return false;
        size = new Size((int)w, (int)h);
        return true;
    }

    public static Bitmap Render(Bitmap source, int outline, Color outlineColor, int margin, Color marginColor)
    {
        if (!TryGetSize(source.Width, source.Height, outline, margin, out var size))
            throw new InvalidOperationException("The margin and outline would exceed the image size limit.");
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(Color.FromArgb(255, marginColor));
            if (outline > 0)
            {
                using var brush = new SolidBrush(Color.FromArgb(255, outlineColor));
                graphics.FillRectangle(brush, margin, margin, source.Width + 2 * outline, source.Height + 2 * outline);
            }
            // Keep the capture's pixels and alpha; only the added frame is opaque.
            graphics.DrawImageUnscaled(source, margin + outline, margin + outline);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
