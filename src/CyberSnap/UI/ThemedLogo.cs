using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace CyberSnap.UI;

public static class ThemedLogo
{
    private const string SquarePath = "pack://application:,,,/Assets/CyberSnap_square.png";

    public static ImageSource Square(int size) => Render(SquarePath, size, size, Theme.TextPrimary);

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
