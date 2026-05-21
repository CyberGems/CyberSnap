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

                // 1. Upscale small captures so Windows OCR can resolve tiny text reliably
                using var prepared = UpscaleForOcr(bitmap);

                // 2. Apply gamma correction to lighten mid-tones and preserve fine diacritics
                ApplyGammaCorrection(prepared, gamma: 0.75);

                // 3. Light sharpening to restore edge definition softened by bicubic upscale
                ApplySharpening(prepared, amount: 0.4);

                // 4. Convert directly to SoftwareBitmap without lossy PNG roundtrip
                SoftwareBitmap softwareBitmap;
                try
                {
                    softwareBitmap = ConvertBitmapToSoftwareBitmapDirect(prepared);
                }
                catch
                {
                    softwareBitmap = await ConvertBitmapToSoftwareBitmapFallbackAsync(prepared);
                }

                using (softwareBitmap)
                {
                    var result = await engine.RecognizeAsync(softwareBitmap);
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
    /// Applies gamma correction to a grayscale 32bppArgb bitmap.
    /// Gamma &lt; 1 brightens mid-tones smoothly without the harsh clipping
    /// of contrast stretching, which helps preserve fine diacritics (tildes,
    /// accents) after bicubic upscaling.
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
                    byte lum = px[0]; // already grayscale, all channels equal

                    byte boosted = lut[lum];
                    px[0] = boosted;
                    px[1] = boosted;
                    px[2] = boosted;
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
    /// Applies a very light unsharp-mask sharpening to restore edge crispness
    /// that bicubic interpolation softens. Amount is intentionally low (0.3-0.5)
    /// to avoid halo artifacts around already-thin diacritics.
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
                    // Read luminance from source (grayscale, all channels equal)
                    byte* srcPx = srcBase + (y * srcStride) + (x * 4);
                    double val = srcPx[0] * centerWeight;

                    // Top
                    val += (srcBase + ((y - 1) * srcStride) + (x * 4))[0] * neighborWeight;
                    // Bottom
                    val += (srcBase + ((y + 1) * srcStride) + (x * 4))[0] * neighborWeight;
                    // Left
                    val += (srcBase + (y * srcStride) + ((x - 1) * 4))[0] * neighborWeight;
                    // Right
                    val += (srcBase + (y * srcStride) + ((x + 1) * 4))[0] * neighborWeight;

                    byte v = (byte)Math.Clamp(val, 0.0, 255.0);
                    px[0] = v;
                    px[1] = v;
                    px[2] = v;
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
            ["ñ"] = "ñ", // guard – already correct
            ["Ñ"] = "Ñ", // guard – already correct
            ["ä"] = "á", // occasional misread of á
            ["Ä"] = "Á",
            ["ü"] = "ú", // occasional misread of ú
            ["Ü"] = "Ú",
        };

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (corrections.TryGetValue(c.ToString(), out string? replacement))
                sb.Append(replacement);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Scales small captures up to a minimum longest-edge so that tiny text
    /// is large enough for the Windows OCR engine to read reliably.
    /// Always returns a new mutable 32bppArgb bitmap.
    /// </summary>
    private static Bitmap UpscaleForOcr(Bitmap source)
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
