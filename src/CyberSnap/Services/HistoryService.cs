using System.Drawing;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CyberSnap.Models;

namespace CyberSnap.Services;

public enum HistoryKind
{
    Image,
    Gif,
    Sticker,
    Video
}

public sealed class HistoryEntry
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileSizeBytes { get; set; }
    public HistoryKind Kind { get; set; } = HistoryKind.Image;
    /// <summary>Last successful share URL (if any).</summary>
    public string? UploadUrl { get; set; }
    /// <summary>Provider name used for last share (e.g. ImgBB).</summary>
    public string? UploadProvider { get; set; }
    /// <summary>UTC ticks of last successful share.</summary>
    public long? UploadedAtTicks { get; set; }
    /// <summary>Last share error message, if any.</summary>
    public string? UploadError { get; set; }
}

public sealed class OcrHistoryEntry
{
    public string Text { get; set; } = "";
    public DateTime CapturedAt { get; set; }
}

public sealed class ColorHistoryEntry
{
    public string Hex { get; set; } = "";
    public DateTime CapturedAt { get; set; }
}

public sealed class CodeHistoryEntry
{
    public string Text { get; set; } = "";
    public string Format { get; set; } = "";
    public DateTime CapturedAt { get; set; }
}

public sealed partial class HistoryService : IDisposable
{
    /// <summary>
    /// Gallery metadata root (DB + thumb caches). Lives under AppData/portable CyberSnap\gallery.
    /// Never use a folder named "History" — capture files only go to the user save folder.
    /// </summary>
    public static string GalleryDataDir => AppStoragePaths.GalleryDataDirectory;

    /// <summary>Obsolete name kept for call-site compatibility; same as <see cref="GalleryDataDir"/>.</summary>
    public static string HistoryDir => GalleryDataDir;

    public static string ThumbnailDir => Path.Combine(GalleryDataDir, "cache", "video-thumbs");
    public static string ImageThumbnailDir => Path.Combine(GalleryDataDir, "cache", "thumbs");
    public static string DatabasePath => Path.Combine(GalleryDataDir, "gallery.db");

    // Legacy locations we may read/migrate FROM — never create these again.
    private static readonly string LegacyPicturesHistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CyberSnap History");
    private static readonly string LegacyAppDataHistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyberSnap", "history");

    private static readonly string MigrationIndexPath = Path.Combine(GalleryDataDir, "index.json");
    private static readonly string MigrationOcrIndexPath = Path.Combine(GalleryDataDir, "ocr_index.json");
    private static readonly string MigrationColorIndexPath = Path.Combine(GalleryDataDir, "color_index.json");

    private static readonly string LegacyIndexPath = Path.Combine(LegacyAppDataHistoryDir, "index.json");
    private static readonly string LegacyOcrIndexPath = Path.Combine(LegacyAppDataHistoryDir, "ocr_index.json");
    private static readonly string LegacyColorIndexPath = Path.Combine(LegacyAppDataHistoryDir, "color_index.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private List<HistoryEntry> _entries = new();
    private Dictionary<string, HistoryEntry> _entriesByPath = new(StringComparer.OrdinalIgnoreCase);
    private List<OcrHistoryEntry> _ocrEntries = new();
    private List<ColorHistoryEntry> _colorEntries = new();
    private List<CodeHistoryEntry> _codeEntries = new();
    private IReadOnlyList<HistoryEntry>? _imageEntries;
    private IReadOnlyList<HistoryEntry>? _gifEntries;
    private IReadOnlyList<HistoryEntry>? _videoEntries;
    private IReadOnlyList<HistoryEntry>? _mediaEntries;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _flushTimer;
    private bool _disposed;
    private bool _entriesRewritePending;
    private bool _ocrDirty;
    private bool _colorDirty;
    private bool _codeDirty;
    private readonly Dictionary<string, HistoryEntry> _pendingEntryUpserts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingEntryDeletes = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public HistoryService()
    {
        _flushTimer = new System.Threading.Timer(_ =>
        {
            try { FlushPendingWrites(); } catch (Exception ex) { AppDiagnostics.LogError("history.flush-timer", ex); }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<HistoryEntry> Entries { get { lock (_gate) return _entries.ToList(); } }
    public IReadOnlyList<HistoryEntry> ImageEntries { get { lock (_gate) return _imageEntries ??= _entries.Where(e => e.Kind == HistoryKind.Image).ToList(); } }
    public IReadOnlyList<HistoryEntry> GifEntries { get { lock (_gate) return _gifEntries ??= _entries.Where(e => e.Kind == HistoryKind.Gif).ToList(); } }
    public IReadOnlyList<HistoryEntry> VideoEntries { get { lock (_gate) return _videoEntries ??= _entries.Where(e => e.Kind == HistoryKind.Video).ToList(); } }
    public IReadOnlyList<HistoryEntry> MediaEntries { get { lock (_gate) return _mediaEntries ??= _entries.Where(e => e.Kind is HistoryKind.Gif or HistoryKind.Video).ToList(); } }
    public IReadOnlyList<OcrHistoryEntry> OcrEntries { get { lock (_gate) return _ocrEntries.ToList(); } }
    public IReadOnlyList<ColorHistoryEntry> ColorEntries { get { lock (_gate) return _colorEntries.ToList(); } }
    public IReadOnlyList<CodeHistoryEntry> CodeEntries { get { lock (_gate) return _codeEntries.ToList(); } }

    private void InvalidateFilteredCache()
    {
        _imageEntries = null;
        _gifEntries = null;
        _videoEntries = null;
        _mediaEntries = null;
    }

    private void RebuildEntryLookup_NoLock()
    {
        _entriesByPath = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.FilePath))
                _entriesByPath[entry.FilePath] = entry;
        }
    }

    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("history.changed", ex);
            }
        }
    }

    public string GetDiskFingerprint(string saveDirectory)
    {
        lock (_gate)
        {
            var hash = new HashCode();

            AddDirectorySignature(hash, GalleryDataDir);
            AddDirectoryTreeSignature(hash, saveDirectory);
            AddDirectoryTreeSignature(hash, Path.Combine(saveDirectory, "Videos"));
            AddDirectoryTreeSignature(hash, Path.Combine(saveDirectory, "GIFs"));

            AddFileSignature(hash, DatabasePath);

            hash.Add(_entries.Count);
            hash.Add(_ocrEntries.Count);
            hash.Add(_colorEntries.Count);
            hash.Add(_codeEntries.Count);

            return hash.ToHashCode().ToString("X8");
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(GalleryDataDir);
            Directory.CreateDirectory(ThumbnailDir);
            Directory.CreateDirectory(ImageThumbnailDir);
            MigrateGalleryDataFromLegacyLocations_NoLock();
            EnsureDatabase_NoLock();
            LoadFromDatabase_NoLock();
            ImportLegacyJsonIndexes_NoLock();

            MigrateLegacyStorage();
            CleanupLegacyThumbnailDirectories_NoLock();
            PruneByRetention(HistoryRetentionPeriod.Never);
            FlushPendingWrites_NoLock();
        }
    }

    /// <summary>
    /// One-time: copy gallery.db / history.db + thumb caches out of any legacy
    /// "History" folders into AppData\CyberSnap\gallery. Does not create those
    /// legacy folders and does not store capture images there.
    /// </summary>
    private static void MigrateGalleryDataFromLegacyLocations_NoLock()
    {
        try
        {
            if (!File.Exists(DatabasePath))
            {
                foreach (var legacyDb in EnumerateLegacyDatabasePaths())
                {
                    if (!File.Exists(legacyDb))
                        continue;

                    Directory.CreateDirectory(GalleryDataDir);
                    File.Copy(legacyDb, DatabasePath, overwrite: false);
                    break;
                }
            }

            TryCopyDirectoryIfEmpty(
                Path.Combine(LegacyPicturesHistoryDir, "cache", "thumbs"),
                ImageThumbnailDir);
            TryCopyDirectoryIfEmpty(
                Path.Combine(LegacyPicturesHistoryDir, "cache", "video-thumbs"),
                ThumbnailDir);
            TryCopyDirectoryIfEmpty(
                Path.Combine(LegacyAppDataHistoryDir, "cache", "thumbs"),
                ImageThumbnailDir);
            TryCopyDirectoryIfEmpty(
                Path.Combine(LegacyAppDataHistoryDir, "cache", "video-thumbs"),
                ThumbnailDir);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("gallery.migrate-data-dir", ex);
        }
    }

    private static IEnumerable<string> EnumerateLegacyDatabasePaths()
    {
        yield return Path.Combine(LegacyPicturesHistoryDir, "history.db");
        yield return Path.Combine(LegacyPicturesHistoryDir, "gallery.db");
        yield return Path.Combine(LegacyAppDataHistoryDir, "history.db");
        yield return Path.Combine(LegacyAppDataHistoryDir, "gallery.db");
    }

    private static void TryCopyDirectoryIfEmpty(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir))
            return;

        try
        {
            if (Directory.Exists(destDir) && Directory.EnumerateFileSystemEntries(destDir).Any())
                return;

            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(destDir, rel);
                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir))
                    Directory.CreateDirectory(targetDir);
                if (!File.Exists(target))
                    File.Copy(file, target, overwrite: false);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("gallery.migrate-cache", ex.Message, ex);
        }
    }

    private void CleanupLegacyThumbnailDirectories_NoLock()
    {
        var legacyThumbDirs = new[]
        {
            Path.Combine(GalleryDataDir, ".thumbs"),
            Path.Combine(GalleryDataDir, "Videos", ".thumbs"),
            Path.Combine(LegacyPicturesHistoryDir, ".thumbs"),
            Path.Combine(LegacyPicturesHistoryDir, "Videos", ".thumbs"),
        };

        foreach (var dir in legacyThumbDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    public bool CompressHistory { get; set; }
    public int JpegQuality { get; set; } = 85;
    public CaptureImageFormat CaptureImageFormat { get; set; } = CaptureImageFormat.Png;
    public HistoryRetentionPeriod RetentionPeriod { get; set; } = HistoryRetentionPeriod.Never;
    public int HistoryCountLimit { get; set; } = 0;
    public bool HistoryDeleteOriginalOnPrune { get; set; }

    public HistoryEntry SaveGifEntry(string gifPath)
        => SaveMediaEntry(gifPath);

    public HistoryEntry SaveVideoEntry(string videoPath)
        => SaveMediaEntry(videoPath);

    public HistoryEntry SaveMediaEntry(string mediaPath)
    {
        var fi = new FileInfo(mediaPath);
        if (!fi.Exists)
            throw new FileNotFoundException("Media file was not found.", mediaPath);

        var kind = HistoryEntryUtilities.GetKindForPath(mediaPath);
        if (kind is not (HistoryKind.Gif or HistoryKind.Video))
            kind = HistoryKind.Video;

        HistoryEntry entry;
        lock (_gate)
        {
            if (!_entriesByPath.TryGetValue(mediaPath, out entry!))
                entry = new HistoryEntry();
            else
                _entries.Remove(entry);

            entry.FileName = fi.Name;
            entry.FilePath = mediaPath;
            entry.CapturedAt = fi.CreationTime;
            entry.Width = 0;
            entry.Height = 0;
            entry.FileSizeBytes = fi.Length;
            entry.Kind = kind;

            _entries.Insert(0, entry);
            _entriesByPath[entry.FilePath] = entry;
            InvalidateFilteredCache();
            QueueEntryUpsert_NoLock(entry);
            PruneByCount_NoLock(HistoryCountLimit, HistoryDeleteOriginalOnPrune);
            ScheduleFlush_NoLock();
        }
        NotifyChanged();
        return entry;
    }

    public HistoryEntry TrackExistingCapture(string filePath, int width, int height, HistoryKind kind = HistoryKind.Image)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
            throw new FileNotFoundException("Capture file was not found.", filePath);

        HistoryEntry entry;
        lock (_gate)
        {
            if (!_entriesByPath.TryGetValue(filePath, out entry!))
                entry = new HistoryEntry();
            else
                _entries.Remove(entry);

            entry.FileName = info.Name;
            entry.FilePath = filePath;
            entry.CapturedAt = info.CreationTime;
            entry.Width = width;
            entry.Height = height;
            entry.FileSizeBytes = info.Length;
            entry.Kind = kind;

            _entries.Insert(0, entry);
            _entriesByPath[entry.FilePath] = entry;
            InvalidateFilteredCache();
            QueueEntryUpsert_NoLock(entry);
            PruneByCount_NoLock(HistoryCountLimit, HistoryDeleteOriginalOnPrune);
            ScheduleFlush_NoLock();
        }

        NotifyChanged();
        return entry;
    }

    public HistoryEntry RefreshExistingCapture(string filePath, int width, int height, HistoryKind kind = HistoryKind.Image)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
            throw new FileNotFoundException("Capture file was not found.", filePath);

        HistoryEntry entry;
        lock (_gate)
        {
            if (!_entriesByPath.TryGetValue(filePath, out entry!))
            {
                entry = new HistoryEntry
                {
                    CapturedAt = info.CreationTime
                };
                _entries.Insert(0, entry);
            }

            entry.FileName = info.Name;
            entry.FilePath = filePath;
            entry.Width = width;
            entry.Height = height;
            entry.FileSizeBytes = info.Length;
            entry.Kind = kind;

            _entriesByPath[entry.FilePath] = entry;
            InvalidateFilteredCache();
            TryDeleteManagedThumbnail_NoLock(filePath);
            QueueEntryUpsert_NoLock(entry);
            PruneByCount_NoLock(HistoryCountLimit, HistoryDeleteOriginalOnPrune);
            ScheduleFlush_NoLock();
        }

        NotifyChanged();
        return entry;
    }

    /// <summary>
    /// Gallery no longer stores capture image files. Use
    /// <see cref="TrackExistingCapture"/> after writing to the user save folder.
    /// </summary>
    [Obsolete("Do not write captures into a History/gallery folder. Save to SaveDirectory and call TrackExistingCapture.")]
    public HistoryEntry SaveCapture(Bitmap screenshot)
    {
        throw new InvalidOperationException(
            "CyberSnap no longer stores captures under a History folder. " +
            "Enable Save to file and use TrackExistingCapture on the saved path.");
    }

    public void SaveOcrEntry(string text)
    {
        lock (_gate)
        {
            _ocrEntries.Insert(0, new OcrHistoryEntry { Text = text, CapturedAt = DateTime.Now });
            int maxCount = HistoryCountLimit > 0 ? HistoryCountLimit : 2000;
            if (_ocrEntries.Count > maxCount)
                _ocrEntries.RemoveRange(maxCount, _ocrEntries.Count - maxCount);
            SaveOcrIndex();
        }
        NotifyChanged();
    }

    public void RemoveEntries(IEnumerable<HistoryEntry> entries)
    {
        var list = entries.Distinct().ToList();
        if (list.Count == 0)
            return;

        lock (_gate)
        {
            var paths = list.Select(entry => entry.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in list)
                TryDeleteManagedThumbnail_NoLock(entry.FilePath);
            _entries.RemoveAll(entry => paths.Contains(entry.FilePath));
            foreach (var path in paths)
                _entriesByPath.Remove(path);
            InvalidateFilteredCache();
            QueueEntryDeletes_NoLock(paths);
            ScheduleFlush_NoLock();
        }
        NotifyChanged();
    }

    public void DeleteEntry(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.RemoveAll(existing => existing.FilePath.Equals(entry.FilePath, StringComparison.OrdinalIgnoreCase));
            _entriesByPath.Remove(entry.FilePath);
            InvalidateFilteredCache();
            TryDeleteHistoryFile_NoLock(entry.FilePath, "delete entry");
            TryDeleteManagedThumbnail_NoLock(entry.FilePath);
            QueueEntryDelete_NoLock(entry.FilePath);
            ScheduleFlush_NoLock();
        }
        NotifyChanged();
        ImageSearchIndexService.PrimaryInstance?.RemoveFile(entry.FilePath);
    }

    /// <summary>Delete a saved capture from disk and remove every trace from history, thumbnails, and search.</summary>
    public bool DeleteCaptureByPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        HistoryEntry? entry;
        lock (_gate)
        {
            _entriesByPath.TryGetValue(filePath, out entry);
        }

        if (entry is not null)
        {
            DeleteEntry(entry);
            return !File.Exists(filePath);
        }

        lock (_gate)
        {
            TryDeleteHistoryFile_NoLock(filePath, "toast delete");
            TryDeleteManagedThumbnail_NoLock(filePath);
        }

        NotifyChanged();
        ImageSearchIndexService.PrimaryInstance?.RemoveFile(filePath);
        return !File.Exists(filePath);
    }

    /// <summary>Delete a saved capture file from the toast (or any caller without a HistoryService reference).</summary>
    public static bool TryDeleteSavedCapture(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var primary = PrimaryInstance;
            if (primary is not null)
                return primary.DeleteCaptureByPath(filePath);

            if (!File.Exists(filePath))
                return false;

            File.Delete(filePath);
            ImageSearchIndexService.PrimaryInstance?.RemoveFile(filePath);
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "history.toast-delete",
                $"Failed to delete saved capture {Path.GetFileName(filePath)}: {ex.Message}",
                ex);
            return false;
        }
    }

    public void DeleteEntries(IEnumerable<HistoryEntry> entries)
    {
        var list = entries.Distinct().ToList();
        if (list.Count == 0)
            return;

        lock (_gate)
        {
            var paths = list.Select(entry => entry.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in list)
            {
                TryDeleteHistoryFile_NoLock(entry.FilePath, "delete entries");
                TryDeleteManagedThumbnail_NoLock(entry.FilePath);
            }
            _entries.RemoveAll(entry => paths.Contains(entry.FilePath));
            foreach (var path in paths)
                _entriesByPath.Remove(path);
            InvalidateFilteredCache();
            QueueEntryDeletes_NoLock(paths);
            ScheduleFlush_NoLock();
        }
        NotifyChanged();
    }

    public void DeleteOcrEntry(OcrHistoryEntry entry)
    {
        lock (_gate)
        {
            _ocrEntries.RemoveAll(e => e.CapturedAt == entry.CapturedAt && e.Text == entry.Text);
            SaveOcrIndex();
        }
        NotifyChanged();
    }

    public void DeleteOcrEntries(IEnumerable<OcrHistoryEntry> entries)
    {
        var set = entries.Select(e => (e.CapturedAt, e.Text)).ToHashSet();
        if (set.Count == 0)
            return;

        lock (_gate)
        {
            _ocrEntries.RemoveAll(e => set.Contains((e.CapturedAt, e.Text)));
            SaveOcrIndex();
        }
        NotifyChanged();
    }

    public void SaveColorEntry(string hex)
    {
        lock (_gate)
        {
            _colorEntries.Insert(0, new ColorHistoryEntry { Hex = hex, CapturedAt = DateTime.Now });
            int maxCount = HistoryCountLimit > 0 ? HistoryCountLimit : 2000;
            if (_colorEntries.Count > maxCount)
                _colorEntries.RemoveRange(maxCount, _colorEntries.Count - maxCount);
            SaveColorIndex();
        }
        NotifyChanged();
    }

    public void DeleteColorEntry(ColorHistoryEntry entry)
    {
        lock (_gate)
        {
            _colorEntries.RemoveAll(e => e.CapturedAt == entry.CapturedAt && e.Hex == entry.Hex);
            SaveColorIndex();
        }
        NotifyChanged();
    }

    public void DeleteColorEntries(IEnumerable<ColorHistoryEntry> entries)
    {
        var set = entries.Select(e => (e.CapturedAt, e.Hex)).ToHashSet();
        if (set.Count == 0)
            return;

        lock (_gate)
        {
            _colorEntries.RemoveAll(e => set.Contains((e.CapturedAt, e.Hex)));
            SaveColorIndex();
        }
        NotifyChanged();
    }

    public void SaveCodeEntry(string text, string format)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var normalizedFormat = format ?? "";
        lock (_gate)
        {
            _codeEntries.RemoveAll(e =>
                string.Equals(e.Text, text, StringComparison.Ordinal) &&
                string.Equals(e.Format, normalizedFormat, StringComparison.OrdinalIgnoreCase));

            _codeEntries.Insert(0, new CodeHistoryEntry { Text = text, Format = normalizedFormat, CapturedAt = DateTime.Now });
            int maxCount = HistoryCountLimit > 0 ? HistoryCountLimit : 2000;
            if (_codeEntries.Count > maxCount)
                _codeEntries.RemoveRange(maxCount, _codeEntries.Count - maxCount);
            SaveCodeIndex();
        }
        NotifyChanged();
    }

    public void DeleteCodeEntry(CodeHistoryEntry entry)
    {
        lock (_gate)
        {
            _codeEntries.RemoveAll(e => e.CapturedAt == entry.CapturedAt && e.Text == entry.Text && e.Format == entry.Format);
            SaveCodeIndex();
        }
        NotifyChanged();
    }

    public void DeleteCodeEntries(IEnumerable<CodeHistoryEntry> entries)
    {
        var set = entries.Select(e => (e.CapturedAt, e.Text, e.Format)).ToHashSet();
        if (set.Count == 0)
            return;

        lock (_gate)
        {
            _codeEntries.RemoveAll(e => set.Contains((e.CapturedAt, e.Text, e.Format)));
            SaveCodeIndex();
        }
        NotifyChanged();
    }

    public void ClearImages()
    {
        lock (_gate)
            ClearEntriesByKind_NoLock(HistoryKind.Image);
        NotifyChanged();
    }

    public void ClearGifs()
    {
        lock (_gate)
            ClearEntriesByKind_NoLock(HistoryKind.Gif);
        NotifyChanged();
    }

    public void ClearOcr()
    {
        lock (_gate)
        {
            _ocrEntries.Clear();
            SaveOcrIndex();
        }
        NotifyChanged();
    }

    public void ClearColors()
    {
        lock (_gate)
        {
            _colorEntries.Clear();
            SaveColorIndex();
        }
        NotifyChanged();
    }

    public void ClearCodes()
    {
        lock (_gate)
        {
            _codeEntries.Clear();
            SaveCodeIndex();
        }
        NotifyChanged();
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                TryDeleteHistoryFile_NoLock(e.FilePath, "clear all");
                TryDeleteManagedThumbnail_NoLock(e.FilePath);
            }
            _entries.Clear();
            _entriesByPath.Clear();
            InvalidateFilteredCache();
            MarkEntriesRewrite_NoLock();
            ScheduleFlush_NoLock();
        }
        NotifyChanged();
    }

    private void ClearEntriesByKind_NoLock(HistoryKind kind)
    {
        var removedPaths = new List<string>();
        _entries.RemoveAll(e =>
        {
            if (e.Kind != kind)
                return false;

            TryDeleteManagedThumbnail_NoLock(e.FilePath);
            TryDeleteHistoryFile_NoLock(e.FilePath, $"clear {kind}");
            _entriesByPath.Remove(e.FilePath);
            removedPaths.Add(e.FilePath);
            return true;
        });

        if (removedPaths.Count == 0)
            return;

        InvalidateFilteredCache();
        QueueEntryDeletes_NoLock(removedPaths);
        ScheduleFlush_NoLock();
    }

    private static void TryDeleteHistoryFile_NoLock(string? filePath, string context)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "history.file-delete",
                $"Failed to delete {context} file {Path.GetFileName(filePath)}: {ex.Message}",
                ex);
        }
    }

    public void SaveEntry(HistoryEntry entry)
    {
        lock (_gate)
        {
            QueueEntryUpsert_NoLock(entry);
            ScheduleFlush_NoLock();
        }
        NotifyChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            try { _flushTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            try { FlushPendingWrites_NoLock(); } catch (Exception ex) { AppDiagnostics.LogError("history.dispose", ex); }
        }

        _flushTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Static quick-save helpers for standalone tools (ColorPicker, OCR, Scan) ──

    /// <summary>Primary HistoryService instance owned by the WPF App (set during startup).</summary>
    public static HistoryService? PrimaryInstance { get; set; }

    public static void QuickSaveColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        try
        {
            var settings = SettingsService.LoadStatic();
            if (settings is null || !settings.SaveHistory || !settings.SaveStandaloneToHistory) return;

            var primary = PrimaryInstance;
            if (primary != null)
            {
                primary.SaveColorEntry(hex);
            }
            else
            {
                using var svc = new HistoryService();
                svc.Load();
                svc.SaveColorEntry(hex);
            }
        }
        catch (Exception ex) { AppDiagnostics.LogError("history.quicksave-color", ex); }
    }

    public static void QuickSaveOcr(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var settings = SettingsService.LoadStatic();
            if (settings is null || !settings.SaveHistory || !settings.SaveStandaloneToHistory) return;

            var primary = PrimaryInstance;
            if (primary != null)
            {
                primary.SaveOcrEntry(text);
            }
            else
            {
                using var svc = new HistoryService();
                svc.Load();
                svc.SaveOcrEntry(text);
            }
        }
        catch (Exception ex) { AppDiagnostics.LogError("history.quicksave-ocr", ex); }
    }

    public static void QuickSaveCode(string text, string format)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var settings = SettingsService.LoadStatic();
            if (settings is null || !settings.SaveHistory || !settings.SaveStandaloneToHistory) return;

            var primary = PrimaryInstance;
            if (primary != null)
            {
                primary.SaveCodeEntry(text, format);
            }
            else
            {
                using var svc = new HistoryService();
                svc.Load();
                svc.SaveCodeEntry(text, format);
            }
        }
        catch (Exception ex) { AppDiagnostics.LogError("history.quicksave-code", ex); }
    }

}
