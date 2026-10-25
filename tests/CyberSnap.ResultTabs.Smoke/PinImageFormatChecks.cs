using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using CyberSnap.Capture;
using CyberSnap.Services;

internal static partial class Program
{
    private static void CheckPinImageFormats(string temp)
    {
        var read = typeof(CyberSnap.App).GetMethod("ReadPinImageFile", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string, Bitmap>>();
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pin");
        string[] files = { "rgb24.bmp", "indexed8.bmp", "indexed.gif", "animated.gif",
            "rgb48.tiff", "rgba64.tiff", "cmyk.jpg", "cmyk.tiff" };
        foreach (string name in files)
        {
            // A non-image extension also checks that the decoder uses the file content.
            string path = Path.Combine(temp, name + ".image");
            File.Copy(Path.Combine(fixtures, name), path);
            using var image = read(path);
            File.Delete(path);
            Assert(!File.Exists(path) && image.Size == new Size(32, 32)
                && image.PixelFormat == PixelFormat.Format32bppArgb,
                "Pin owns detached 32-bit pixels and releases its file: " + name);
            if (name.StartsWith("cmyk", StringComparison.Ordinal))
            {
                // Untagged CMYK uses Windows' default color conversion. Check color
                // identity and channel ordering without hard-coding an installed profile.
                Color red = image.GetPixel(4, 4), green = image.GetPixel(28, 4);
                Color blue = image.GetPixel(4, 28), white = image.GetPixel(28, 28);
                Assert(red.A == 255 && red.R > 180 && red.G < 90 && red.B < 90
                    && green.A == 255 && green.G > 100 && green.G > green.R + 60 && green.G > green.B + 40
                    && blue.A == 255 && blue.B > 90 && blue.B > blue.R + 30 && blue.B > blue.G + 30
                    && white.A == 255 && white.R > 240 && white.G > 240 && white.B > 240,
                    "CMYK conversion preserves quadrant colors and opaque alpha: " + name);
            }
            else
            {
                bool valid = true;
                for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    Color expected = (x < 16, y < 16) switch
                    {
                        (true, true) => Color.FromArgb(name == "rgba64.tiff" ? 128 : 255, 255, 0, 0),
                        (false, true) => Color.Lime,
                        (true, false) => Color.Blue,
                        _ => Color.White
                    };
                    valid &= image.GetPixel(x, y).ToArgb() == expected.ToArgb();
                }
                Assert(valid, "every RGB pixel and alpha value survives decoding: " + name);
            }
            using var copy = PinImageForm.CopyPixels(image);
            Assert(EqualZoomBitmaps(image, copy), "Pin copying preserves the decoded pixels: " + name);
        }

        using (var stream = File.OpenRead(Path.Combine(fixtures, "animated.gif")))
        {
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert(decoder.Frames.Count == 2, "animated GIF coverage uses a real two-frame source");
            var second = new FormatConvertedBitmap(decoder.Frames[1], System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            second.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert(pixel.SequenceEqual(new byte[] { 0, 0, 0, 255 }),
                "the second GIF frame differs, so quadrant checks prove Pin selected the first frame");
        }
        foreach (string name in new[] { "rgb48.tiff", "rgba64.tiff" })
        {
            using var stream = File.OpenRead(Path.Combine(fixtures, name));
            var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert(decoder.Frames[0].Format.BitsPerPixel == (name == "rgb48.tiff" ? 48 : 64),
                "high-depth TIFF coverage contains real 16-bit channels: " + name);
        }
        foreach (string name in new[] { "cmyk.jpg", "cmyk.tiff", "indexed8.bmp", "indexed.gif" })
        {
            using var stream = File.OpenRead(Path.Combine(fixtures, name));
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert(name.StartsWith("cmyk", StringComparison.Ordinal)
                    ? decoder.Frames[0].Format == System.Windows.Media.PixelFormats.Cmyk32
                    : decoder.Frames[0].Format == System.Windows.Media.PixelFormats.Indexed8,
                "format coverage uses a real CMYK or indexed source: " + name);
        }

        foreach (string language in new[] { "en", "es" })
        {
            LocalizationService.ApplyCurrentCulture(language);
            foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[] { 1, 2, 3, 4 },
                File.ReadAllBytes(Path.Combine(fixtures, "rgba64.tiff")).Take(16).ToArray() })
            {
                string path = Path.Combine(temp, "damaged-image.tiff");
                File.WriteAllBytes(path, invalid);
                Exception? error = null;
                try { using var unexpected = read(path); }
                catch (Exception ex) { error = ex; }
                File.Delete(path);
                Assert(error is InvalidDataException { InnerException: not null }
                    && error.Message == LocalizationService.Translate(
                        "This image could not be decoded. It may be damaged or use an unsupported format."),
                    "damaged images release the file and return a clear localized error: " + language);
            }
        }
        LocalizationService.ApplyCurrentCulture("en");
    }
}
