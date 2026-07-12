using System;
using System.Drawing;
using System.IO;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>Where an image is coming from when opened in the Editor.</summary>
public enum ImageOpenSource
{
    /// <summary>User Open dialog or drag-and-drop of an arbitrary file.</summary>
    UserImport,

    /// <summary>Post-capture auto-open or toast → Editor (CyberSnap-produced bitmap).</summary>
    Capture,

    /// <summary>Gallery / history card → Editor.</summary>
    History,

    /// <summary>Paste or "New from clipboard".</summary>
    Clipboard,

    /// <summary>CLI, recent files, or other path-based open of a known file.</summary>
    FilePath,
}

/// <summary>Outcome of <see cref="ImageOpenPolicy"/> evaluation.</summary>
public enum ImageOpenDecision
{
    Allow,
    RejectFileSize,
    RejectDimensions,
}

/// <summary>Result of checking whether an image may be opened in the Editor.</summary>
public sealed class ImageOpenEvaluation
{
    public ImageOpenDecision Decision { get; init; }
    public bool ShouldWarn { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long FileSizeBytes { get; init; }
    public int MaxLongestSide { get; init; }
    public long MaxFileBytes { get; init; }

    public bool IsAllowed => Decision == ImageOpenDecision.Allow;

    public string ErrorTitle =>
        LocalizationService.Translate("Error importing image");

    public string FormatErrorMessage()
    {
        return Decision switch
        {
            ImageOpenDecision.RejectFileSize => string.Format(
                LocalizationService.Translate("The file is too large ({0:F1} MB). Maximum allowed is {1:F0} MB."),
                FileSizeBytes / (1024.0 * 1024.0),
                MaxFileBytes / (1024.0 * 1024.0)),

            ImageOpenDecision.RejectDimensions => string.Format(
                LocalizationService.Translate("The image is too large ({0}x{1}). Maximum allowed is {2} pixels on the longest side."),
                Width, Height, MaxLongestSide),

            _ => string.Empty,
        };
    }

    public string FormatPerformanceBanner()
    {
        return string.Format(
            LocalizationService.Translate("Very large image ({0}×{1}). Editing may use more memory and feel slower."),
            Width, Height);
    }
}

/// <summary>
/// Single source of truth for Editor image size/weight limits.
/// Import (user files) is stricter; captures, history, clipboard, and path opens
/// from CyberSnap allow much taller images (scroll captures) with a soft warning.
/// </summary>
public static class ImageOpenPolicy
{
    /// <summary>Max encoded file size for Open / drag-drop import of arbitrary files.</summary>
    public const long MaxImportFileBytes = 25L * 1024 * 1024; // 25 MB

    /// <summary>Max encoded file size when loading a path we already trust (history, CLI, recent).</summary>
    public const long MaxTrustedFileBytes = 150L * 1024 * 1024; // 150 MB

    /// <summary>
    /// Hard ceiling on longest side for every Editor entry path (import, capture, history, paste).
    /// High enough for tall scroll stitches; still a GDI+ safety limit.
    /// Former import limit was 4096 (labeled 4K).
    /// </summary>
    public const int MaxLongestSide = 32768;

    /// <summary>Alias kept for call sites that document “trusted” vs import ceilings.</summary>
    public const int MaxTrustedLongestSide = MaxLongestSide;

    /// <summary>Soft-warning threshold: longest side above this shows a performance banner.</summary>
    public const int SoftWarningLongestSide = 4096;

    /// <summary>Soft-warning threshold by total pixels (~16 MP).</summary>
    public const long SoftWarningPixelCount = 16_000_000L;

    public static bool IsTrustedSource(ImageOpenSource source) =>
        source is ImageOpenSource.Capture
            or ImageOpenSource.History
            or ImageOpenSource.Clipboard
            or ImageOpenSource.FilePath;

    public static long MaxFileBytesFor(ImageOpenSource source) =>
        IsTrustedSource(source) ? MaxTrustedFileBytes : MaxImportFileBytes;

    public static int MaxLongestSideFor(ImageOpenSource source) => MaxLongestSide;

    /// <summary>Checks file length before decoding. Dimensions must be checked after load.</summary>
    public static ImageOpenEvaluation EvaluateFileSize(string filePath, ImageOpenSource source)
    {
        var info = new FileInfo(filePath);
        long maxBytes = MaxFileBytesFor(source);
        if (!info.Exists)
        {
            return new ImageOpenEvaluation
            {
                Decision = ImageOpenDecision.RejectFileSize,
                FileSizeBytes = 0,
                MaxFileBytes = maxBytes,
                MaxLongestSide = MaxLongestSideFor(source),
            };
        }

        if (info.Length > maxBytes)
        {
            return new ImageOpenEvaluation
            {
                Decision = ImageOpenDecision.RejectFileSize,
                FileSizeBytes = info.Length,
                MaxFileBytes = maxBytes,
                MaxLongestSide = MaxLongestSideFor(source),
            };
        }

        return new ImageOpenEvaluation
        {
            Decision = ImageOpenDecision.Allow,
            FileSizeBytes = info.Length,
            MaxFileBytes = maxBytes,
            MaxLongestSide = MaxLongestSideFor(source),
        };
    }

    public static ImageOpenEvaluation EvaluateDimensions(int width, int height, ImageOpenSource source, long fileSizeBytes = 0)
    {
        int maxSide = MaxLongestSideFor(source);
        long maxBytes = MaxFileBytesFor(source);

        if (width <= 0 || height <= 0)
        {
            return new ImageOpenEvaluation
            {
                Decision = ImageOpenDecision.RejectDimensions,
                Width = width,
                Height = height,
                FileSizeBytes = fileSizeBytes,
                MaxLongestSide = maxSide,
                MaxFileBytes = maxBytes,
            };
        }

        int longest = Math.Max(width, height);
        if (longest > maxSide)
        {
            return new ImageOpenEvaluation
            {
                Decision = ImageOpenDecision.RejectDimensions,
                Width = width,
                Height = height,
                FileSizeBytes = fileSizeBytes,
                MaxLongestSide = maxSide,
                MaxFileBytes = maxBytes,
            };
        }

        long pixels = (long)width * height;
        bool warn = longest > SoftWarningLongestSide || pixels > SoftWarningPixelCount;

        return new ImageOpenEvaluation
        {
            Decision = ImageOpenDecision.Allow,
            ShouldWarn = warn,
            Width = width,
            Height = height,
            FileSizeBytes = fileSizeBytes,
            MaxLongestSide = maxSide,
            MaxFileBytes = maxBytes,
        };
    }

    public static ImageOpenEvaluation EvaluateBitmap(Image image, ImageOpenSource source, long fileSizeBytes = 0)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        return EvaluateDimensions(image.Width, image.Height, source, fileSizeBytes);
    }

    /// <summary>
    /// Full file check: size first, then decode via <paramref name="loadBitmap"/>, then dimensions.
    /// On rejection after decode, the loaded bitmap is disposed.
    /// </summary>
    public static ImageOpenEvaluation EvaluateAndLoad(
        string filePath,
        ImageOpenSource source,
        Func<string, Bitmap> loadBitmap,
        out Bitmap? bitmap)
    {
        bitmap = null;
        var sizeEval = EvaluateFileSize(filePath, source);
        if (!sizeEval.IsAllowed)
            return sizeEval;

        Bitmap loaded;
        try
        {
            loaded = loadBitmap(filePath);
        }
        catch
        {
            throw;
        }

        var dimEval = EvaluateDimensions(loaded.Width, loaded.Height, source, sizeEval.FileSizeBytes);
        if (!dimEval.IsAllowed)
        {
            loaded.Dispose();
            return dimEval;
        }

        bitmap = loaded;
        return dimEval;
    }
}
