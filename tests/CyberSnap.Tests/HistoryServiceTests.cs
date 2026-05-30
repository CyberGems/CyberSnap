using Xunit;
using CyberSnap.Services;
using System.Reflection;

namespace CyberSnap.Tests;

public sealed class HistoryServiceTests
{
    [Fact]
    public void HistoryStorageLivesInPictures()
    {
        var picturesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "CyberSnap History");

        Assert.Equal(picturesRoot, HistoryService.HistoryDir);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), HistoryService.HistoryDir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), HistoryService.HistoryDir, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(picturesRoot, "history.db"), HistoryService.DatabasePath);
    }

    [Fact]
    public void NotifyChanged_ContinuesWhenOneHandlerThrows()
    {
        var service = new HistoryService();
        bool healthyHandlerCalled = false;
        service.Changed += () => throw new InvalidOperationException("boom");
        service.Changed += () => healthyHandlerCalled = true;

        var notifyChanged = typeof(HistoryService).GetMethod("NotifyChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(notifyChanged);

        var ex = Record.Exception(() => notifyChanged!.Invoke(service, null));

        Assert.Null(ex);
        Assert.True(healthyHandlerCalled);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var service = new HistoryService();

        var ex = Record.Exception(() =>
        {
            service.Dispose();
            service.Dispose();
        });

        Assert.Null(ex);
    }

    [Theory]
    [InlineData("clip.mp4")]
    public void GetKindForPath_RecognizesVideoFiles(string fileName)
    {
        Assert.Equal(HistoryKind.Video, HistoryEntryUtilities.GetKindForPath(fileName));
        Assert.True(HistoryEntryUtilities.IsSupportedHistoryFile(fileName));
    }

    [Fact]
    public void GetKindForPath_StillRecognizesGifFiles()
    {
        Assert.Equal(HistoryKind.Gif, HistoryEntryUtilities.GetKindForPath("clip.gif"));
    }

    [Fact]
    public void HistoryFileDeleteFailuresAreLogged()
    {
        var serviceCode = File.ReadAllText(RepoPath("src", "CyberSnap", "Services", "HistoryService.cs"));
        var ioCode = File.ReadAllText(RepoPath("src", "CyberSnap", "Services", "HistoryService.IO.cs"));

        var deleteEntryBlock = GetMethodBlock(serviceCode, "public void DeleteEntry(HistoryEntry entry)");
        Assert.Contains("TryDeleteHistoryFile_NoLock(entry.FilePath, \"delete entry\");", deleteEntryBlock);

        var deleteEntriesBlock = GetMethodBlock(serviceCode, "public void DeleteEntries(IEnumerable<HistoryEntry> entries)");
        Assert.Contains("TryDeleteHistoryFile_NoLock(entry.FilePath, \"delete entries\");", deleteEntriesBlock);

        var clearAllBlock = GetMethodBlock(serviceCode, "public void ClearAll()");
        Assert.Contains("TryDeleteHistoryFile_NoLock(e.FilePath, \"clear all\");", clearAllBlock);

        var clearKindBlock = GetMethodBlock(serviceCode, "private void ClearEntriesByKind_NoLock(HistoryKind kind)");
        Assert.Contains("TryDeleteHistoryFile_NoLock(e.FilePath, $\"clear {kind}\");", clearKindBlock);

        var deleteHelperBlock = GetMethodBlock(serviceCode, "private static void TryDeleteHistoryFile_NoLock(string? filePath, string context)");
        Assert.Contains("File.Delete(filePath);", deleteHelperBlock);
        Assert.Contains("catch (Exception ex)", deleteHelperBlock);
        Assert.Contains("AppDiagnostics.LogWarning(", deleteHelperBlock);
        Assert.Contains("\"history.file-delete\"", deleteHelperBlock);

        var retentionBlock = GetMethodBlock(ioCode, "public void PruneByRetention(HistoryRetentionPeriod retention)");
        Assert.Contains("TryDeleteHistoryFile_NoLock(e.FilePath, \"retention cleanup\");", retentionBlock);

        var pruneCountBlock = GetMethodBlock(ioCode, "private void PruneByCount_NoLock(int maxCount, bool deleteOriginalFiles)");
        Assert.Contains("TryDeleteHistoryFile_NoLock(entry.FilePath, \"count prune\");", pruneCountBlock);

        Assert.DoesNotContain("try { File.Delete(entry.FilePath); } catch { }", serviceCode);
        Assert.DoesNotContain("try { File.Delete(e.FilePath); } catch { }", serviceCode);
        Assert.DoesNotContain("try { File.Delete(e.FilePath); } catch { }", ioCode);
    }

    [Fact]
    public void PruneByCount_PrunesCorrectNumberOfNewestEntries()
    {
        var service = new HistoryService();

        // Prevent the flush timer from writing to disk/db in test thread
        var flushTimerField = typeof(HistoryService).GetField("_flushTimer", BindingFlags.NonPublic | BindingFlags.Instance);
        var oldTimer = flushTimerField?.GetValue(service) as IDisposable;
        oldTimer?.Dispose();
        flushTimerField?.SetValue(service, new System.Threading.Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite));

        // Create test entries
        var entries = new List<HistoryEntry>
        {
            new() { FilePath = "file1.png", CapturedAt = DateTime.Now.AddMinutes(-1) },
            new() { FilePath = "file2.png", CapturedAt = DateTime.Now.AddMinutes(-2) },
            new() { FilePath = "file3.png", CapturedAt = DateTime.Now.AddMinutes(-3) },
            new() { FilePath = "file4.png", CapturedAt = DateTime.Now.AddMinutes(-4) },
            new() { FilePath = "file5.png", CapturedAt = DateTime.Now.AddMinutes(-5) }
        };

        // Populate entries using reflection
        var entriesField = typeof(HistoryService).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
        entriesField?.SetValue(service, entries);

        var rebuildMethod = typeof(HistoryService).GetMethod("RebuildEntryLookup_NoLock", BindingFlags.NonPublic | BindingFlags.Instance);
        rebuildMethod?.Invoke(service, null);

        // Prune to 3 entries
        service.PruneByCount(3, deleteOriginalFiles: false);

        // Verify the newest 3 entries are kept (which are index 0, 1, 2 in the newest-first list)
        Assert.Equal(3, service.Entries.Count);
        Assert.Equal("file1.png", service.Entries[0].FilePath);
        Assert.Equal("file2.png", service.Entries[1].FilePath);
        Assert.Equal("file3.png", service.Entries[2].FilePath);

        // Verify that entries 4 and 5 were removed
        Assert.DoesNotContain(service.Entries, e => e.FilePath == "file4.png");
        Assert.DoesNotContain(service.Entries, e => e.FilePath == "file5.png");
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find repo file: {Path.Combine(parts)}");
    }

    private static string GetMethodBlock(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method: {signature}");

        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart > start, $"Could not find method body: {signature}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not read method body: {signature}");
    }

}
