using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace CyberSnap.UI;

public static class ThemedLogo
{
    private const string SquarePath = "pack://application:,,,/Assets/CyberSnap_square.png";

    public static ImageSource Square(int size) => Render(SquarePath, size, size, Theme.TextPrimary);

    public static ImageSource SquareGrayscale(int size) => RenderGrayscale(SquarePath, size, size);

    private static ImageSource Render(string resourcePath, int width, int height, MediaColor color)
    {
        var source = LoadBitmap(resourcePath);

        var scaled = new TransformedBitmap(
            source,
            new ScaleTransform(
                width / (double)source.PixelWidth,
                height / (double)source.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    private static ImageSource RenderGrayscale(string resourcePath, int width, int height)
    {
        var source = LoadBitmap(resourcePath);
        var pWidth = source.PixelWidth;
        var pHeight = source.PixelHeight;
        var stride = pWidth * 4;
        var pixels = new byte[pHeight * stride];

        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
        {
            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            formatted.CopyPixels(pixels, stride, 0);
        }
        else
        {
            source.CopyPixels(pixels, stride, 0);
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            byte a = pixels[i + 3];

            if (a > 0)
            {
                byte gray = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                pixels[i] = gray;
                pixels[i + 1] = gray;
                pixels[i + 2] = gray;
            }
        }

        var grayBitmap = BitmapSource.Create(pWidth, pHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        var scaled = new TransformedBitmap(
            grayBitmap,
            new ScaleTransform(
                width / (double)pWidth,
                height / (double)pHeight));
        scaled.Freeze();
        return scaled;
    }

    private static BitmapSource LoadBitmap(string resourcePath)
    {
        var info = Application.GetResourceStream(new Uri(resourcePath, UriKind.Absolute))
            ?? throw new InvalidOperationException($"Logo resource not found: {resourcePath}");
        var decoder = BitmapDecoder.Create(info.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
