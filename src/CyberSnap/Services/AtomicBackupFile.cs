using System.IO;

namespace CyberSnap.Services;

internal static class AtomicBackupFile
{
    internal static void Write(string path, byte[] bytes, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) { AppDiagnostics.LogWarning("backup.temp-cleanup", ex.Message, ex); }
            }
        }
    }
}
