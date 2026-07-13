using System.Drawing;
using System.IO;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload;

/// <summary>
/// Encodes a <see cref="Bitmap"/> to PNG/JPEG bytes on the calling thread (UI thread for GDI+ safety).
/// Does not dispose the source bitmap — caller owns it.
/// </summary>
public static class UploadImageEncoder
{
    public readonly record struct EncodedImage(byte[] Bytes, string ContentType, string Extension);

    public static EncodedImage Encode(Bitmap image, UploadImageFormatPreference format, int jpegQuality)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var ms = new MemoryStream();
        if (format == UploadImageFormatPreference.Jpeg)
        {
            CaptureOutputService.WriteJpeg(image, ms, jpegQuality);
            return new EncodedImage(ms.ToArray(), "image/jpeg", "jpg");
        }

        CaptureOutputService.WritePng(image, ms);
        return new EncodedImage(ms.ToArray(), "image/png", "png");
    }
}
