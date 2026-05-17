using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CyberSnap.Helpers;

internal static class BitmapPerf
{
    public static Bitmap LoadDetached(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }

    public static Bitmap Clone32bppArgb(Bitmap source)
    {
        if (source.PixelFormat == DrawingPixelFormat.Format32bppArgb)
            return new Bitmap(source);

        var clone = new Bitmap(source.Width, source.Height, DrawingPixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(clone);
        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        return clone;
    }

    public static BitmapSource ToBitmapSource(Bitmap source)
    {
        Bitmap? ownedClone = null;
        var bitmap = source;
        if (source.PixelFormat != DrawingPixelFormat.Format32bppArgb)
        {
            ownedClone = Clone32bppArgb(source);
            bitmap = ownedClone;
        }

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                data.Scan0,
                data.Stride * bitmap.Height,
                data.Stride);
            src.Freeze();
            return src;
        }
        finally
        {
            bitmap.UnlockBits(data);
            ownedClone?.Dispose();
        }
    }

    public static unsafe void BoostGrayscaleInPlace(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, DrawingPixelFormat.Format32bppArgb);
        try
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;
            int width = bitmap.Width;
            int height = bitmap.Height;

            // BT.601 luma weights as Q16 fixed-point: 0.299, 0.587, 0.114 â†’ 19595, 38470, 7471 (sum = 65536).
            // Contrast boost: lum' = clamp((lum - 128) * 2 + 128, 0, 255) â†’ lum'/255 = (lum*2 - 128) / 255.
            const int RWeight = 19595;
            const int GWeight = 38470;
            const int BWeight = 7471;

            for (int y = 0; y < height; y++)
            {
                byte* row = basePtr + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    byte* px = row + (x * 4);
                    int lum = (RWeight * px[2] + GWeight * px[1] + BWeight * px[0] + 32768) >> 16;
                    int boosted = (lum << 1) - 128;
                    if (boosted < 0) boosted = 0;
                    else if (boosted > 255) boosted = 255;
                    byte v = (byte)boosted;

                    px[0] = v;
                    px[1] = v;
                    px[2] = v;
                    px[3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public static unsafe Bitmap CleanupTransparentPixels(Bitmap source, byte alphaThreshold)
    {
        var cleaned = Clone32bppArgb(source);
        var rect = new Rectangle(0, 0, cleaned.Width, cleaned.Height);
        var data = cleaned.LockBits(rect, ImageLockMode.ReadWrite, DrawingPixelFormat.Format32bppArgb);

        try
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;

            for (int y = 0; y < cleaned.Height; y++)
            {
                byte* row = basePtr + (y * stride);
                for (int x = 0; x < cleaned.Width; x++)
                {
                    byte* px = row + (x * 4);
                    if (px[3] <= alphaThreshold)
                        px[0] = px[1] = px[2] = px[3] = 0;
                }
            }
        }
        finally
        {
            cleaned.UnlockBits(data);
        }

        return cleaned;
    }

    public static unsafe Bitmap TrimTransparentBounds(Bitmap source, byte alphaThreshold)
    {
        var normalized = Clone32bppArgb(source);
        var rect = new Rectangle(0, 0, normalized.Width, normalized.Height);
        var data = normalized.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        Rectangle? crop = null;
        bool isEmpty = false;

        try
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;
            int width = normalized.Width;
            int height = normalized.Height;

            // Walk inward from each edge â€” stops at the first row/column with any non-transparent pixel.
            int top = -1;
            for (int y = 0; y < height && top < 0; y++)
            {
                byte* row = basePtr + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    if (row[(x * 4) + 3] > alphaThreshold)
                    {
                        top = y;
                        break;
                    }
                }
            }

            if (top < 0)
            {
                isEmpty = true;
            }
            else
            {
                int bottom = top;
                for (int y = height - 1; y > top; y--)
                {
                    byte* row = basePtr + (y * stride);
                    bool found = false;
                    for (int x = 0; x < width; x++)
                    {
                        if (row[(x * 4) + 3] > alphaThreshold)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (found) { bottom = y; break; }
                }

                int left = width;
                int right = -1;
                for (int y = top; y <= bottom; y++)
                {
                    byte* row = basePtr + (y * stride);
                    for (int x = 0; x < left; x++)
                    {
                        if (row[(x * 4) + 3] > alphaThreshold)
                        {
                            left = x;
                            break;
                        }
                    }
                    for (int x = width - 1; x > right; x--)
                    {
                        if (row[(x * 4) + 3] > alphaThreshold)
                        {
                            right = x;
                            break;
                        }
                    }
                    if (left == 0 && right == width - 1)
                        break;
                }

                if (right < left)
                    isEmpty = true;
                else
                    crop = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
        }
        finally
        {
            normalized.UnlockBits(data);
        }

        if (isEmpty || crop is null)
            return normalized;

        return normalized.Clone(crop.Value, DrawingPixelFormat.Format32bppArgb);
    }
}
