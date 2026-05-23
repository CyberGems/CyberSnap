using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CyberSnap.Helpers;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using BitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace CyberSnap.Services;

public enum OcrWorkload
{
    Fast = 0,
    Full = 1
}

public static class OcrService
{
    public const string EngineId = "winocr-v1";
    private static readonly SemaphoreSlim RecognizeGate = new(1, 1);
    private static readonly object EngineCacheGate = new();
    private static readonly Dictionary<string, OcrEngine> EngineCache = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<string>? AvailableLanguageCache;
    internal readonly record struct OcrLineLayout(string Text, double Left, double Top, double Right, double Bottom)
    {
        public double Width => Math.Max(0, Right - Left);
        public double Height => Math.Max(0, Bottom - Top);
    }

    /// <summary>Windows OCR is always ready â€” no downloads needed.</summary>
    public static bool IsReady() => true;

    /// <summary>Dispose is a no-op for Windows OCR.</summary>
    public static void ClearEngines()
    {
        lock (EngineCacheGate)
        {
            EngineCache.Clear();
            AvailableLanguageCache = null;
        }
    }

    /// <summary>Returns BCP-47 language tags for all installed Windows OCR languages.</summary>
    public static IReadOnlyList<string> GetAvailableRecognizerLanguages(bool refresh = false)
    {
        lock (EngineCacheGate)
        {
            if (!refresh && AvailableLanguageCache is not null)
                return AvailableLanguageCache;
        }

        var languages = OcrEngine.AvailableRecognizerLanguages
            .Select(l => l.LanguageTag)
            .ToList();

        lock (EngineCacheGate)
            AvailableLanguageCache = languages;

        return languages;
    }

    public static async Task<string> RecognizeAsync(Bitmap bitmap, string? languageTag = null, OcrWorkload workload = OcrWorkload.Full)
    {
        await RecognizeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                var engine = CreateEngine(languageTag);
                if (engine == null)
                    return "";

                // 1. Try Simple Pipeline first (no heavy filters, upscale only if very small < 200, crop threshold 180.0)
                string simpleText = "";
                using (var croppedAndPaddedSimple = CropBordersAndPad(bitmap, 180.0))
                using (var preparedSimple = UpscaleForOcrSimple(croppedAndPaddedSimple))
                {
                    SoftwareBitmap softwareBitmapSimple;
                    try
                    {
                        softwareBitmapSimple = ConvertBitmapToSoftwareBitmapDirect(preparedSimple);
                    }
                    catch
                    {
                        softwareBitmapSimple = await ConvertBitmapToSoftwareBitmapFallbackAsync(preparedSimple);
                    }

                    using (softwareBitmapSimple)
                    {
                        var result = await engine.RecognizeAsync(softwareBitmapSimple);
                        if (result != null)
                        {
                            var lines = result.Lines
                                .Select(CreateLineLayout)
                                .Where(layout => !string.IsNullOrWhiteSpace(layout.Text))
                                .ToList();
                            simpleText = CorrectSpanishDiacritics(FormatRecognizedText(lines, result.Text));
                        }

                        // Check if the primary engine missed the clock time (contains am/pm/m. but no colon)
                        bool missedClockTime = !string.IsNullOrWhiteSpace(simpleText)
                            && (simpleText.Contains("m.") || simpleText.ToLowerInvariant().Contains("am") || simpleText.ToLowerInvariant().Contains("pm"))
                            && !simpleText.Contains(":");

                        if (missedClockTime)
                        {
                            var fallbackEngine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                                              ?? OcrEngine.TryCreateFromUserProfileLanguages();

                            if (fallbackEngine != null && fallbackEngine.RecognizerLanguage.LanguageTag != engine.RecognizerLanguage.LanguageTag)
                            {
                                var fallbackResult = await fallbackEngine.RecognizeAsync(softwareBitmapSimple);
                                if (fallbackResult != null)
                                {
                                    var fallbackLines = fallbackResult.Lines
                                        .Select(CreateLineLayout)
                                        .Where(layout => !string.IsNullOrWhiteSpace(layout.Text))
                                        .ToList();
                                    string fallbackText = CorrectSpanishDiacritics(FormatRecognizedText(fallbackLines, fallbackResult.Text));

                                    // If fallback recognized more/better text (e.g. contains colons), use it!
                                    if (!string.IsNullOrWhiteSpace(fallbackText) && fallbackText.Contains(":"))
                                    {
                                        simpleText = fallbackText;
                                    }
                                }
                            }
                        }
                    }
                }

                // If simple pipeline successfully recognized text (length >= 3), return it immediately!
                if (!string.IsNullOrWhiteSpace(simpleText) && simpleText.Length >= 3)
                {
                    return simpleText;
                }

                // 2. Fallback to Full Pipeline with all filters (gamma, sharpening, binarization, upscale to 800, crop threshold 50.0)
                using var croppedAndPaddedFull = CropBordersAndPad(bitmap, 50.0);
                using var preparedFull = UpscaleForOcrFull(croppedAndPaddedFull);

                ApplyGammaCorrection(preparedFull, gamma: 0.75);
                ApplySharpening(preparedFull, amount: 0.4);
                ApplyBinarization(preparedFull, threshold: 120.0);

                SoftwareBitmap softwareBitmapFull;
                try
                {
                    softwareBitmapFull = ConvertBitmapToSoftwareBitmapDirect(preparedFull);
                }
                catch
                {
                    softwareBitmapFull = await ConvertBitmapToSoftwareBitmapFallbackAsync(preparedFull);
                }

                using (softwareBitmapFull)
                {
                    var result = await engine.RecognizeAsync(softwareBitmapFull);
                    if (result == null)
                        return "";

                    var lines = result.Lines
                        .Select(CreateLineLayout)
                        .Where(layout => !string.IsNullOrWhiteSpace(layout.Text))
                        .ToList();

                    var rawText = FormatRecognizedText(lines, result.Text);
                    return CorrectSpanishDiacritics(rawText);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            RecognizeGate.Release();
        }
    }

    /// <summary>
    /// Applies gamma correction to all color channels (Red, Green, Blue) of a 32bppArgb bitmap.
    /// Gamma &lt; 1 brightens mid-tones smoothly, helping preserve fine diacritics
    /// and details without crushing colors.
    /// </summary>
    private static unsafe void ApplyGammaCorrection(Bitmap bitmap, double gamma)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;
            int width = bitmap.Width;
            int height = bitmap.Height;

            // Precompute gamma lookup table for speed
            byte[] lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double normalized = i / 255.0;
                double corrected = Math.Pow(normalized, gamma);
                lut[i] = (byte)Math.Clamp(corrected * 255.0, 0.0, 255.0);
            }

            for (int y = 0; y < height; y++)
            {
                byte* row = basePtr + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    byte* px = row + (x * 4);
                    px[0] = lut[px[0]]; // Blue
                    px[1] = lut[px[1]]; // Green
                    px[2] = lut[px[2]]; // Red
                    px[3] = 255;        // Ensure opaque alpha
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Applies a light unsharp-mask sharpening to restore edge crispness
    /// across all color channels independently.
    /// </summary>
    private static unsafe void ApplySharpening(Bitmap bitmap, double amount)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var clone = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(clone))
        {
            g.DrawImage(bitmap, 0, 0);
        }

        var srcRect = new Rectangle(0, 0, width, height);
        var dstRect = new Rectangle(0, 0, width, height);
        var srcData = clone.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = bitmap.LockBits(dstRect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* srcBase = (byte*)srcData.Scan0;
            byte* dstBase = (byte*)dstData.Scan0;
            int srcStride = srcData.Stride;
            int dstStride = dstData.Stride;

            double centerWeight = 1.0 + amount * 4.0;
            double neighborWeight = -amount;

            for (int y = 1; y < height - 1; y++)
            {
                byte* dstRow = dstBase + (y * dstStride);
                for (int x = 1; x < width - 1; x++)
                {
                    byte* px = dstRow + (x * 4);
                    // Process R, G, B channels independently
                    for (int c = 0; c < 3; c++)
                    {
                        double val = (srcBase + (y * srcStride) + (x * 4))[c] * centerWeight;
                        val += (srcBase + ((y - 1) * srcStride) + (x * 4))[c] * neighborWeight;
                        val += (srcBase + ((y + 1) * srcStride) + (x * 4))[c] * neighborWeight;
                        val += (srcBase + (y * srcStride) + ((x - 1) * 4))[c] * neighborWeight;
                        val += (srcBase + (y * srcStride) + ((x + 1) * 4))[c] * neighborWeight;

                        px[c] = (byte)Math.Clamp(val, 0.0, 255.0);
                    }
                    px[3] = 255;
                }
            }
        }
        finally
        {
            clone.UnlockBits(srcData);
            bitmap.UnlockBits(dstData);
            clone.Dispose();
        }
    }

    /// <summary>
    /// Converts the color image to a high-contrast binary (black & white) image
    /// using a true Luma threshold calculation.
    /// </summary>
    private static unsafe void ApplyBinarization(Bitmap bitmap, double threshold)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;
            int width = bitmap.Width;
            int height = bitmap.Height;

            for (int y = 0; y < height; y++)
            {
                byte* row = basePtr + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    byte* px = row + (x * 4);
                    // Calculate true Luma: 0.299 * Red + 0.587 * Green + 0.114 * Blue
                    double luma = 0.299 * px[2] + 0.587 * px[1] + 0.114 * px[0];
                    byte b = (byte)(luma < threshold ? 0 : 255);
                    px[0] = b;
                    px[1] = b;
                    px[2] = b;
                    px[3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Post-OCR linguistic correction for Spanish/Portuguese diacritics.
    /// Windows OCR on upscaled small text frequently misreads acute accents
    /// as diaereses or rings (e.g. á→å, ó→ö, í→ì). This maps the most
    /// common false positives back to their likely intended characters.
    /// </summary>
    private static string CorrectSpanishDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Common misreadings observed with small-text OCR after upscaling
        var corrections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["å"] = "á",
            ["ö"] = "ó",
            ["Ö"] = "Ó",
            ["ì"] = "í",
            ["Ì"] = "Í",
            ["è"] = "é",
            ["È"] = "É",
            ["ù"] = "ú",
            ["Ù"] = "Ú",
            ["ñ"] = "ñ",
            ["Ñ"] = "Ñ",
            ["ä"] = "á",
            ["Ä"] = "Á",
            ["ü"] = "ú",
            ["Ü"] = "Ú",
        };

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '6' && i > 0 && i < text.Length - 1)
            {
                char prev = text[i - 1];
                char next = text[i + 1];
                if (char.IsLetter(prev) && char.IsLetter(next))
                {
                    sb.Append('ó');
                    continue;
                }
            }

            if (corrections.TryGetValue(c.ToString(), out string? replacement))
                sb.Append(replacement);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Scales small captures up to a minimum longest-edge using sharp NearestNeighbor interpolation.
    /// Only scales if the longest edge is less than 200.
    /// Always returns a new mutable 32bppArgb bitmap.
    /// </summary>
    private static Bitmap UpscaleForOcrSimple(Bitmap source)
    {
        const int MinLongEdge = 200;
        int longest = Math.Max(source.Width, source.Height);
        if (longest >= MinLongEdge)
        {
            return (Bitmap)source.Clone();
        }

        double scale = MinLongEdge / (double)longest;
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        result.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    /// <summary>
    /// Scales small captures up to a minimum longest-edge using Bicubic interpolation.
    /// Scales to 800px.
    /// Always returns a new mutable 32bppArgb bitmap.
    /// </summary>
    private static Bitmap UpscaleForOcrFull(Bitmap source)
    {
        const int MinLongEdge = 800;
        int longest = Math.Max(source.Width, source.Height);
        double scale = longest < MinLongEdge ? (MinLongEdge / (double)longest) : 1.0;

        int width = scale > 1.0
            ? Math.Max(1, (int)Math.Round(source.Width * scale))
            : source.Width;
        int height = scale > 1.0
            ? Math.Max(1, (int)Math.Round(source.Height * scale))
            : source.Height;

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        result.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    /// <summary>
    /// Detects the content bounding box to crop out dark/black borders,
    /// and pads the image with the average color of its corners to avoid clipping.
    /// Uses the provided luma threshold.
    /// </summary>
    private static unsafe Bitmap CropBordersAndPad(Bitmap source, double threshold)
    {
        int w = source.Width;
        int h = source.Height;
        if (w == 0 || h == 0) return (Bitmap)source.Clone();

        int minX = w;
        int maxX = 0;
        int minY = h;
        int maxY = 0;
        bool foundContent = false;

        var rect = new Rectangle(0, 0, w, h);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;

            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + (y * stride);
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + (x * 4);
                    // B: px[0], G: px[1], R: px[2]
                    double luma = 0.299 * px[2] + 0.587 * px[1] + 0.114 * px[0];
                    if (luma > threshold)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        foundContent = true;
                    }
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
        }

        if (!foundContent || minX > maxX || minY > maxY)
        {
            return (Bitmap)source.Clone();
        }

        int croppedW = maxX - minX + 1;
        int croppedH = maxY - minY + 1;

        var cropped = new Bitmap(croppedW, croppedH, PixelFormat.Format32bppArgb);
        cropped.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using (var g = Graphics.FromImage(cropped))
        {
            g.DrawImage(source, new Rectangle(0, 0, croppedW, croppedH), new Rectangle(minX, minY, croppedW, croppedH), GraphicsUnit.Pixel);
        }

        Color c1 = cropped.GetPixel(0, 0);
        Color c2 = cropped.GetPixel(croppedW - 1, 0);
        Color c3 = cropped.GetPixel(0, croppedH - 1);
        Color c4 = cropped.GetPixel(croppedW - 1, croppedH - 1);

        int avgR = (c1.R + c2.R + c3.R + c4.R) / 4;
        int avgG = (c1.G + c2.G + c3.G + c4.G) / 4;
        int avgB = (c1.B + c2.B + c3.B + c4.B) / 4;
        Color bgColor = Color.FromArgb(255, avgR, avgG, avgB);

        const int pad = 20;
        int newW = croppedW + pad * 2;
        int newH = croppedH + pad * 2;

        var padded = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
        padded.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using (var g = Graphics.FromImage(padded))
        {
            using (var brush = new SolidBrush(bgColor))
            {
                g.FillRectangle(brush, 0, 0, newW, newH);
            }
            g.DrawImage(cropped, pad, pad);
        }

        cropped.Dispose();
        return padded;
    }

    /// <summary>
    /// Fast, lossless conversion from GDI+ Bitmap to WinRT SoftwareBitmap
    /// by copying raw locked pixels via COM interop (IMemoryBufferByteAccess).
    /// </summary>
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    private static unsafe SoftwareBitmap ConvertBitmapToSoftwareBitmapDirect(Bitmap bitmap)
    {
        if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
            throw new InvalidOperationException("Bitmap must be Format32bppArgb for direct conversion.");

        int width = bitmap.Width;
        int height = bitmap.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Straight);
            using var buffer = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Write);
            var reference = buffer.CreateReference();

            var memoryBuffer = (IMemoryBufferByteAccess)reference;
            memoryBuffer.GetBuffer(out byte* destPtr, out uint capacity);

            byte* srcPtr = (byte*)data.Scan0;
            int srcStride = data.Stride;
            var plane = buffer.GetPlaneDescription(0);
            int destStride = plane.Stride;

            uint required = (uint)(destStride * height);
            if (capacity < required)
                throw new InvalidOperationException($"SoftwareBitmap buffer capacity {capacity} < required {required}.");

            for (int y = 0; y < height; y++)
            {
                System.Buffer.MemoryCopy(
                    srcPtr + (y * srcStride),
                    destPtr + (y * destStride),
                    width * 4,
                    width * 4);
            }

            return softwareBitmap;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Safe fallback that writes an uncompressed BMP to a stream and decodes it
    /// back into a SoftwareBitmap. No compression blur and negligible overhead.
    /// </summary>
    private static async Task<SoftwareBitmap> ConvertBitmapToSoftwareBitmapFallbackAsync(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        using var stream = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
    }

    internal static string FormatRecognizedText(IReadOnlyList<OcrLineLayout> lines, string? fallbackText = null)
    {
        if (lines.Count == 0)
            return fallbackText?.Trim() ?? "";

        var ordered = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Top)
            .ThenBy(line => line.Left)
            .ToList();

        if (ordered.Count == 0)
            return fallbackText?.Trim() ?? "";

        double medianHeight = Median(ordered.Select(line => line.Height).Where(value => value > 0));
        if (medianHeight <= 0)
            medianHeight = 16;

        double medianCharWidth = Median(ordered
            .Select(line =>
            {
                var length = line.Text.Trim().Length;
                return length == 0 || line.Width <= 0 ? 0 : line.Width / length;
            })
            .Where(value => value > 0));
        if (medianCharWidth <= 0)
            medianCharWidth = Math.Max(6, medianHeight * 0.45);

        double minLeft = ordered.Min(line => line.Left);
        double baselineWindow = Math.Max(medianCharWidth * 2, 8);
        var baselineCandidates = ordered
            .Select(line => line.Left)
            .Where(left => left - minLeft <= baselineWindow)
            .ToList();
        double baselineLeft = baselineCandidates.Count > 0 ? baselineCandidates.Average() : minLeft;

        var builder = new StringBuilder();
        OcrLineLayout? previous = null;

        foreach (var line in ordered)
        {
            var text = line.Text.Trim();
            if (text.Length == 0)
                continue;

            bool paragraphBreak = previous is OcrLineLayout prior && (line.Top - prior.Bottom) > Math.Max(medianHeight * 0.85, 8);
            int indentSpaces = ComputeIndentSpaces(line.Left - baselineLeft, medianCharWidth);
            int previousIndent = previous is OcrLineLayout previousLine
                ? ComputeIndentSpaces(previousLine.Left - baselineLeft, medianCharWidth)
                : 0;

            bool paragraphStart = previous == null || paragraphBreak || indentSpaces >= previousIndent + 2;

            if (builder.Length > 0)
                builder.Append(paragraphStart ? Environment.NewLine + Environment.NewLine : Environment.NewLine);

            if (paragraphStart && indentSpaces >= 2)
                builder.Append(' ', Math.Clamp(indentSpaces, 2, 8));

            builder.Append(text);
            previous = line;
        }

        return builder.ToString().Trim();
    }

    private static OcrLineLayout CreateLineLayout(OcrLine line)
    {
        if (line.Words == null || line.Words.Count == 0)
            return new OcrLineLayout(line.Text ?? "", 0, 0, 0, 0);

        double left = double.MaxValue;
        double top = double.MaxValue;
        double right = double.MinValue;
        double bottom = double.MinValue;

        foreach (var word in line.Words)
        {
            var rect = word.BoundingRect;
            left = Math.Min(left, rect.X);
            top = Math.Min(top, rect.Y);
            right = Math.Max(right, rect.X + rect.Width);
            bottom = Math.Max(bottom, rect.Y + rect.Height);
        }

        if (left == double.MaxValue || top == double.MaxValue || right == double.MinValue || bottom == double.MinValue)
            return new OcrLineLayout(line.Text ?? "", 0, 0, 0, 0);

        return new OcrLineLayout(line.Text ?? "", left, top, right, bottom);
    }

    private static int ComputeIndentSpaces(double indentPixels, double medianCharWidth)
    {
        if (indentPixels <= 0 || medianCharWidth <= 0)
            return 0;

        return (int)Math.Round(indentPixels / medianCharWidth, MidpointRounding.AwayFromZero);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return 0;

        int mid = ordered.Length / 2;
        if ((ordered.Length & 1) == 1)
            return ordered[mid];

        return (ordered[mid - 1] + ordered[mid]) / 2d;
    }

    private static OcrEngine? CreateEngine(string? languageTag)
    {
        var cacheKey = GetEngineCacheKey(languageTag);
        lock (EngineCacheGate)
        {
            if (EngineCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var engine = CreateEngineUncached(languageTag);
        if (engine is not null)
        {
            lock (EngineCacheGate)
                EngineCache[cacheKey] = engine;
        }

        return engine;
    }

    private static string GetEngineCacheKey(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag) && languageTag != "auto")
            return languageTag.Trim().ToLowerInvariant();

        try
        {
            return "auto:" + LocalizationService.ResolveContentLanguageCode();
        }
        catch
        {
            return "auto";
        }
    }

    private static OcrEngine? CreateEngineUncached(string? languageTag)
    {
        // If specific language requested, try it
        if (!string.IsNullOrWhiteSpace(languageTag) && languageTag != "auto")
        {
            try
            {
                var lang = new Windows.Globalization.Language(languageTag);
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine != null) return engine;
            }
            catch { }
        }

        // Auto: prefer the active app/system UI language when installed, then user profile languages.
        try
        {
            var uiLanguage = LocalizationService.ResolveContentLanguageCode();
            if (!string.IsNullOrWhiteSpace(uiLanguage))
            {
                var lang = new Windows.Globalization.Language(uiLanguage);
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine != null) return engine;
            }
        }
        catch { }

        var userEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (userEngine != null) return userEngine;

        // Last resort: first available language
        var available = OcrEngine.AvailableRecognizerLanguages;
        if (available.Count > 0)
            return OcrEngine.TryCreateFromLanguage(available[0]);

        return null;
    }
}
