using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace CyberSnap.Services;

public static class ClipboardService
{
    public static void CopyToClipboard(Bitmap bitmap, string? filePath = null)
    {
        var dataObject = new System.Windows.Forms.DataObject();

        dataObject.SetData(System.Windows.Forms.DataFormats.Bitmap, bitmap);

        using var pngStream = new MemoryStream();
        CaptureOutputService.WritePng(bitmap, pngStream);
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

        SetClipboardWithRetry(dataObject);
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
    /// Image only, no file references: Bitmap + DIB + PNG. DIB is what picky
    /// targets like Photoshop paste reliably; omitting FileDrop keeps Explorer
    /// and friends from preferring the file over the pixels.
    /// </summary>
    public static void CopyImageToClipboard(Bitmap bitmap)
    {
        var dataObject = new System.Windows.Forms.DataObject();

        dataObject.SetData(System.Windows.Forms.DataFormats.Bitmap, bitmap);
        dataObject.SetData(System.Windows.Forms.DataFormats.Dib, false, ToDibStream(bitmap));

        using var pngStream = new MemoryStream();
        CaptureOutputService.WritePng(bitmap, pngStream);
        if (pngStream.TryGetBuffer(out var pngBuffer))
            dataObject.SetData("PNG", false, new MemoryStream(pngBuffer.Array!, pngBuffer.Offset, pngBuffer.Count, writable: false, publiclyVisible: true));
        else
            dataObject.SetData("PNG", false, new MemoryStream(pngStream.ToArray(), writable: false));

        SetClipboardWithRetry(dataObject);
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

    private static void SetClipboardWithRetry(System.Windows.Forms.DataObject dataObject, int maxRetries = 3)
    {
        Exception? lastError = null;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(dataObject, true);
                return;
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
    }
}
