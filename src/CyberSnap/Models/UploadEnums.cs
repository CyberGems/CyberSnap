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
}

/// <summary>Encoded image format preferred for share uploads.</summary>
public enum UploadImageFormatPreference
{
    Png = 0,
    Jpeg = 1,
}
