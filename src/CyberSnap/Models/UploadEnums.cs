namespace CyberSnap.Models;

/// <summary>Host family for image share uploads (settings + menu).</summary>
public enum UploadProviderKind
{
    Imgur = 0,
    ImgBB = 1,
    /// <summary>Single custom destination; protocol selected via <see cref="UploadCustomProtocol"/>.</summary>
    Custom = 2,
    /// <summary>CyberGems Share (cybersnap.cybergems.org) — default temporary public links.</summary>
    CyberGems = 3,
}

/// <summary>Protocol for the single custom upload destination.</summary>
public enum UploadCustomProtocol
{
    Ftp = 0,
    Sftp = 1,
    S3 = 2,
    /// <summary>HTTP(S) webhook: POST multipart or JSON base64 to a user URL.</summary>
    Webhook = 3,
}

/// <summary>Body format for custom webhook uploads.</summary>
public enum UploadWebhookBodyMode
{
    /// <summary>multipart/form-data with the image file field.</summary>
    Multipart = 0,
    /// <summary>application/json with base64 image payload.</summary>
    JsonBase64 = 1,
}

/// <summary>Encoded image format preferred for share uploads.</summary>
public enum UploadImageFormatPreference
{
    Png = 0,
    Jpeg = 1,
}
