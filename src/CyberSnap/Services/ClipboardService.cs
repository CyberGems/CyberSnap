using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace CyberSnap.Services;

public static class ClipboardService
{
    public static bool CopyToClipboard(Bitmap bitmap, string? filePath = null)
    {
        using var pngStream = new MemoryStream();
        CaptureOutputService.WritePng(bitmap, pngStream);
        return CopyBitmapToClipboard(bitmap, pngStream, filePath, includeDib: false);
    }

    private static bool CopyBitmapToClipboard(Bitmap bitmap, MemoryStream pngStream, string? filePath, bool includeDib)
    {
        var dataObject = new System.Windows.Forms.DataObject();

        dataObject.SetData(System.Windows.Forms.DataFormats.Bitmap, bitmap);
        if (includeDib)
            dataObject.SetData(System.Windows.Forms.DataFormats.Dib, false, ToDibStream(bitmap));

        if (pngStream.TryGetBuffer(out var pngBuffer))
            dataObject.SetData("PNG", false, new MemoryStream(pngBuffer.Array!, pngBuffer.Offset, pngBuffer.Count, writable: false, publiclyVisible: true));
        else
            dataObject.SetData("PNG", false, new MemoryStream(pngStream.ToArray(), writable: false));

        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            dataObject.SetData(System.Windows.Forms.DataFormats.FileDrop, new string[] { filePath });
            dataObject.SetData("FileNameW", filePath);
            dataObject.SetData("FileName", filePath);
            var ms = new MemoryStream(new byte[] { 1, 0, 0, 0 }); // DragDropEffects.Copy
            dataObject.SetData("Preferred DropEffect", ms);
        }

        return SetClipboardWithRetry(dataObject);
    }

    public static void CopyFileToClipboard(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        var dataObject = new System.Windows.Forms.DataObject();
        dataObject.SetData(System.Windows.Forms.DataFormats.FileDrop, new string[] { filePath });
        dataObject.SetData("FileNameW", filePath);
        dataObject.SetData("FileName", filePath);
        var ms = new MemoryStream(new byte[] { 1, 0, 0, 0 }); // DragDropEffects.Copy
        dataObject.SetData("Preferred DropEffect", ms);

        SetClipboardWithRetry(dataObject);
    }

    /// <summary>
    /// Copies the current editor pixels and a matching PNG file for Explorer.
    /// Retains DIB for image editors that use it.
    /// </summary>
    public static bool CopyImageToClipboard(Bitmap bitmap)
    {
        using var pngStream = new MemoryStream();
        CaptureOutputService.WritePng(bitmap, pngStream);

        string directory = Path.Combine(Path.GetTempPath(), "CyberSnap", "clipboard-images");
        Directory.CreateDirectory(directory);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(directory, $"CyberSnap-{DateTime.Now:yyyyMMdd-HHmmssfff}-{id[..8]}.png");
        bool created = false;
        bool copied = false;
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                created = true;
                pngStream.WriteTo(file);
            }

            copied = CopyBitmapToClipboard(bitmap, pngStream, path, includeDib: true);
        }
        finally
        {
            if (created && !copied)
                TryDeleteClipboardImage(path);
        }

        if (copied)
            PruneStaleClipboardImages(directory);
        return copied;
    }

    private static void PruneStaleClipboardImages(string directory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "CyberSnap-*.png"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    TryDeleteClipboardImage(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.LogWarning("clipboard.cleanup", "Could not prune temporary clipboard images.", ex);
        }
    }

    private static void TryDeleteClipboardImage(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.LogWarning("clipboard.cleanup", "Could not delete a temporary clipboard image.", ex);
        }
    }

    /// <summary>DIB is a BMP without its 14-byte file header.</summary>
    private static MemoryStream ToDibStream(Bitmap bitmap)
    {
        using var bmpStream = new MemoryStream();
        bitmap.Save(bmpStream, ImageFormat.Bmp);
        var bytes = bmpStream.ToArray();
        const int fileHeaderSize = 14;
        return new MemoryStream(bytes, fileHeaderSize, bytes.Length - fileHeaderSize, writable: false);
    }

    public static void CopyTextToClipboard(string text)
    {
        var dataObject = new System.Windows.Forms.DataObject();
        dataObject.SetData(System.Windows.Forms.DataFormats.UnicodeText, false, text);
        dataObject.SetData(System.Windows.Forms.DataFormats.Text, false, text);

        SetClipboardWithRetry(dataObject);
    }

    private static bool SetClipboardWithRetry(System.Windows.Forms.DataObject dataObject, int maxRetries = 3)
    {
        Exception? lastError = null;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(dataObject, true);
                return true;
            }
            catch (Exception) when (i < maxRetries - 1)
            {
                lastError = null;
                System.Threading.Thread.Sleep(50 * (i + 1));
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
            AppDiagnostics.LogWarning("clipboard.set", "Failed to write to clipboard after retries.", lastError);
        return false;
    }
}
