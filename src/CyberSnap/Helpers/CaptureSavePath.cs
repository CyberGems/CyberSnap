using System.Globalization;
using System.IO;

namespace CyberSnap.Helpers;

internal static class CaptureSavePath
{
    public static string BuildPath(
        string rootDirectory,
        string fileName,
        bool useMonthlyFolder,
        DateTime? capturedAt = null)
    {
        var directory = useMonthlyFolder
            ? GetMonthDirectory(rootDirectory, capturedAt)
            : rootDirectory;
        return Path.Combine(directory, fileName);
    }

    public static string BuildAvailablePath(
        string rootDirectory,
        string fileName,
        bool useMonthlyFolder,
        DateTime? capturedAt = null)
        => GetAvailablePath(BuildPath(rootDirectory, fileName, useMonthlyFolder, capturedAt));

    public static string BuildMonthlyPath(
        string rootDirectory,
        string fileName,
        DateTime? capturedAt = null)
        => BuildPath(rootDirectory, fileName, useMonthlyFolder: true, capturedAt);

    public static string GetMonthDirectory(string rootDirectory, DateTime? capturedAt = null)
    {
        var timestamp = capturedAt ?? DateTime.Now;
        return Path.Combine(rootDirectory, timestamp.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }

    public static string GetAvailablePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (int index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory ?? "", $"{fileName} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory ?? "", $"{fileName} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>
    /// Temp root for recordings when SaveToFile is off. Not the user gallery/save folder.
    /// </summary>
    public static string TempRecordingsDirectory =>
        Path.Combine(Path.GetTempPath(), "CyberSnap", "recordings");

    public static string BuildTempRecordingPath(string extension)
    {
        var ext = string.IsNullOrWhiteSpace(extension)
            ? ".mp4"
            : (extension.StartsWith('.') ? extension : "." + extension);
        Directory.CreateDirectory(TempRecordingsDirectory);
        var fileName = $"CyberSnap_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}";
        return GetAvailablePath(Path.Combine(TempRecordingsDirectory, fileName));
    }

    public static bool IsTempRecordingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(TempRecordingsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void TryDeleteTempRecording(string? path)
    {
        if (!IsTempRecordingPath(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path!);
        }
        catch
        {
            // Best-effort; OS temp cleanup will eventually reclaim.
        }
    }
}
