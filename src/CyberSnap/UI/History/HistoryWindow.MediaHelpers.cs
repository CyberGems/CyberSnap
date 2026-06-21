using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using CyberSnap.Models;
using CyberSnap.Helpers;
using CyberSnap.Native;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class HistoryWindow
{
    private static readonly Lazy<BitmapSource> VideoPlaceholder = new(CreateVideoPlaceholder);
    private static readonly Lazy<BitmapSource> ImagePlaceholder = new(CreateImagePlaceholder);
    private const int HistoryThumbDecodePixelWidth = 400;

    private static bool TryGetThumbFromCache(string path, out BitmapSource? image) => SettingsMediaCache.TryGetThumb(path, out image);

    private static void StoreThumbInCache(string path, BitmapSource image) => SettingsMediaCache.StoreThumb(path, image);

    private static void RemoveThumbFromCache(string path) => SettingsMediaCache.RemoveThumb(path);

    internal static void InvalidateThumbCache(string path) => RemoveThumbFromCache(path);

    internal static void ClearThumbCache() => SettingsMediaCache.Clear();

    internal static void TrimThumbCache(int keepCount) => SettingsMediaCache.Trim(keepCount);

    internal static void WarmRecentHistoryThumbs(IEnumerable<HistoryEntry> entries, int maxCount = 24)
    {
        foreach (var entry in entries
                     .OrderByDescending(item => item.CapturedAt)
                     .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
                     .Take(maxCount))
        {
            PrimeThumbLoad(entry.FilePath, GetHistoryThumbPath(entry), entry.Kind);
        }
    }

    internal static void WarmHistoryThumbsInBackground(IEnumerable<HistoryEntry> entries, int maxCount = 192, int immediateCount = 48, int batchSize = 24) =>
        SettingsMediaCache.WarmHistoryThumbsInBackground(
            entries,
            (cacheKey, thumbPath, kind) => PrimeThumbLoad(cacheKey, GetHistoryThumbPath(cacheKey, kind), kind),
            maxCount,
            immediateCount,
            batchSize);

    private static string GetHistoryThumbPath(HistoryEntry entry) => GetHistoryThumbPath(entry.FilePath, entry.Kind);

    private static string GetHistoryThumbPath(string filePath, HistoryKind kind) =>
        kind == HistoryKind.Video ? GetVideoThumbnailPath(filePath) : filePath;

    private static BitmapImage? LoadPackImage(string relativePath) => SettingsMediaCache.LoadPackImage(relativePath);

    private static BitmapSource? LoadThumbSource(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = fs;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = HistoryThumbDecodePixelWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(path);
                return BitmapPerf.ToBitmapSource(bmp);
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool TryLoadCachedThumbnailSource(string cacheKey, string thumbPath, string? sourcePath, HistoryKind kind, out BitmapSource? image)
    {
        image = null;
        var diskPath = GetExistingCachedThumbnailPath(thumbPath, sourcePath ?? cacheKey, kind);
        if (string.IsNullOrWhiteSpace(diskPath))
            return false;

        image = LoadThumbSource(diskPath);
        if (image is null)
        {
            AppDiagnostics.LogWarning("history.thumb-cache.read", $"Discarding unreadable thumbnail cache file {Path.GetFileName(diskPath)}.");
            TryDeleteThumbnailCacheFile(diskPath);
            return false;
        }

        StoreThumbInCache(cacheKey, image);
        return true;
    }

    private static BitmapSource? LoadOrCreateThumbnailSource(string loadPath, string sourcePath, HistoryKind kind)
    {
        var persistentPath = GetPersistentThumbnailPath(sourcePath, kind);
        if (!string.IsNullOrWhiteSpace(persistentPath) && File.Exists(persistentPath))
        {
            var cached = LoadThumbSource(persistentPath);
            if (cached is not null)
                return cached;

            AppDiagnostics.LogWarning("history.thumb-cache.read", $"Discarding unreadable thumbnail cache file {Path.GetFileName(persistentPath)}.");
            TryDeleteThumbnailCacheFile(persistentPath);
        }

        var bitmap = LoadThumbSource(loadPath);
        if (bitmap is not null)
            SavePersistentThumbnail(bitmap, sourcePath, kind);

        return bitmap;
    }

    private static string? GetExistingCachedThumbnailPath(string thumbPath, string sourcePath, HistoryKind kind)
    {
        if (kind == HistoryKind.Video)
        {
            if (!File.Exists(thumbPath))
                return null;

            if (IsUsableVideoThumbnail(thumbPath))
                return thumbPath;

            TryDeleteVideoThumbnailFile(thumbPath, "cached unusable video thumbnail");
            return null;
        }

        var persistentPath = GetPersistentThumbnailPath(sourcePath, kind);
        return !string.IsNullOrWhiteSpace(persistentPath) && File.Exists(persistentPath)
            ? persistentPath
            : null;
    }

    private static string? GetPersistentThumbnailPath(string sourcePath, HistoryKind kind)
    {
        if (kind is not (HistoryKind.Image or HistoryKind.Gif or HistoryKind.Sticker) || !File.Exists(sourcePath))
            return null;

        try
        {
            var info = new FileInfo(sourcePath);
            var pathKey = HistoryEntryUtilities.GetStablePathKey(sourcePath);
            return Path.Combine(HistoryService.ImageThumbnailDir, $"{pathKey}-{info.Length:X16}-{info.LastWriteTimeUtc.Ticks:X16}.png");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("history.thumb-cache.path", $"Failed to resolve thumbnail cache path for {Path.GetFileName(sourcePath)}: {ex.Message}", ex);
            return null;
        }
    }

    private static void SavePersistentThumbnail(BitmapSource bitmap, string sourcePath, HistoryKind kind)
    {
        var thumbPath = GetPersistentThumbnailPath(sourcePath, kind);
        if (string.IsNullOrWhiteSpace(thumbPath) || File.Exists(thumbPath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
            using var stream = new FileStream(thumbPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("history.thumb-cache.save", $"Failed to save thumbnail cache file {Path.GetFileName(thumbPath)}: {ex.Message}", ex);
        }
    }

    private static void TryDeleteThumbnailCacheFile(string thumbPath)
    {
        try
        {
            File.Delete(thumbPath);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("history.thumb-cache.delete", $"Failed to delete thumbnail cache file {Path.GetFileName(thumbPath)}: {ex.Message}", ex);
        }
    }

    private static BitmapSource CreateVideoPlaceholder()
    {
        using var bmp = new Bitmap(320, 180, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.FromArgb(30, 30, 30));

            using var border = new System.Drawing.Pen(System.Drawing.Color.FromArgb(70, 255, 255, 255), 2f);
            g.DrawRectangle(border, 1, 1, bmp.Width - 3, bmp.Height - 3);

            using var badgeBg = new SolidBrush(System.Drawing.Color.FromArgb(180, 0, 0, 0));
            var badgeRect = new RectangleF(bmp.Width / 2f - 46, bmp.Height / 2f - 22, 92, 44);
            g.FillRoundedRectangle(badgeBg, badgeRect, 10);

            using var badgeText = new SolidBrush(System.Drawing.Color.FromArgb(235, 255, 255, 255));
            using var font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont.FontFamily, 13f, System.Drawing.FontStyle.Bold, GraphicsUnit.Point);
            var text = "VIDEO";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, badgeText, badgeRect.X + (badgeRect.Width - size.Width) / 2f,
                badgeRect.Y + (badgeRect.Height - size.Height) / 2f - 1f);
        }

        return BitmapPerf.ToBitmapSource(bmp);
    }

    private static BitmapSource CreateImagePlaceholder()
    {
        using var bmp = new Bitmap(320, 180, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            
            // Premium background: Dark slate gradient matching CyberGems colors
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Color.FromArgb(15, 17, 26), System.Drawing.Color.FromArgb(28, 30, 46), LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, 0, 0, bmp.Width, bmp.Height);
            }

            // Accent border
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(30, 0, 255, 255), 2f))
            {
                g.DrawRectangle(pen, 1, 1, bmp.Width - 3, bmp.Height - 3);
            }

            // Draw picture icon outline in the center with the accent color
            using (var accentBrush = new SolidBrush(System.Drawing.Color.FromArgb(80, 0, 255, 255)))
            using (var accentPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(120, 0, 255, 255), 2f))
            {
                // Draw picture frame
                var frameRect = new Rectangle(bmp.Width / 2 - 25, bmp.Height / 2 - 20, 50, 40);
                g.DrawRectangle(accentPen, frameRect);

                // Draw sun
                g.FillEllipse(accentBrush, frameRect.X + 32, frameRect.Y + 8, 8, 8);

                // Draw mountains
                var pts = new[]
                {
                    new System.Drawing.Point(frameRect.X + 2, frameRect.Bottom - 2),
                    new System.Drawing.Point(frameRect.X + 18, frameRect.Y + 18),
                    new System.Drawing.Point(frameRect.X + 28, frameRect.Y + 28),
                    new System.Drawing.Point(frameRect.X + 36, frameRect.Y + 20),
                    new System.Drawing.Point(frameRect.Right - 2, frameRect.Bottom - 2)
                };
                g.DrawLines(accentPen, pts);
            }

            // Draw "IMAGE" text below the icon
            using (var font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont.FontFamily, 9f, System.Drawing.FontStyle.Bold))
            using (var textBrush = new SolidBrush(System.Drawing.Color.FromArgb(140, 160, 180, 210)))
            {
                var text = "NO IMAGE";
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, textBrush, (bmp.Width - size.Width) / 2f, bmp.Height / 2f + 25f);
            }
        }

        return BitmapPerf.ToBitmapSource(bmp);
    }

    private static BitmapSource GetHistoryPlaceholder(HistoryKind kind) =>
        kind == HistoryKind.Image || kind == HistoryKind.Sticker
            ? ImagePlaceholder.Value
            : VideoPlaceholder.Value;

    private static bool IsStaleHistoryPlaceholder(BitmapSource? source, HistoryKind kind) =>
        source is not null &&
        (kind == HistoryKind.Image || kind == HistoryKind.Gif || kind == HistoryKind.Video) &&
        ReferenceEquals(source, GetHistoryPlaceholder(kind));

    private static string FormatStorageSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string FormatTimeAgo(DateTime dt)
    {
        var span = DateTime.Now - dt;
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        bool es = lang == "es";
        if (span.TotalMinutes < 1) return es ? "Ahora" : "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}{(es ? "min" : "m")} {(es ? "atrás" : "ago")}";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}{(es ? "h" : "h")} {(es ? "atrás" : "ago")}";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}{(es ? "d" : "d")} {(es ? "atrás" : "ago")}";
        return dt.ToString("MMM d");
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, System.Drawing.Brush brush, RectangleF rect, float radius)
    {
        using var path = new GraphicsPath();
        float d = radius * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
