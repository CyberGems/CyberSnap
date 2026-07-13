using System.IO;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload.Providers;

internal static class CustomUploadHelpers
{
    public static string CombinePublicUrl(string? urlBase, string fileName, string? keyPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(urlBase))
            return "";

        var baseUrl = urlBase.Trim().TrimEnd('/');
        var name = fileName.TrimStart('/');
        if (!string.IsNullOrWhiteSpace(keyPrefix))
        {
            var prefix = keyPrefix.Trim().Trim('/') + "/";
            name = prefix + name;
        }
        return baseUrl + "/" + name;
    }

    public static string CombineRemotePath(string directory, string fileName)
    {
        var dir = (directory ?? "").Replace('\\', '/').Trim();
        var name = fileName.TrimStart('/');
        if (string.IsNullOrEmpty(dir) || dir == "/")
            return "/" + name;
        return dir.TrimEnd('/') + "/" + name;
    }

    public static string WithUniqueSuffix(string fileName, int attempt)
    {
        if (attempt <= 0) return fileName;
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{stem}-{attempt}{ext}";
    }

    public static UploadResult SuccessPathOrUrl(
        UploadProviderKind provider,
        string remotePath,
        string? publicUrl)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(publicUrl)
            && (publicUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        return UploadResult.Ok(
            provider,
            publicUrl: hasUrl ? publicUrl : null,
            clipboardText: hasUrl ? publicUrl : remotePath,
            hasOpenableHttpUrl: hasUrl,
            remotePath: remotePath);
    }
}
