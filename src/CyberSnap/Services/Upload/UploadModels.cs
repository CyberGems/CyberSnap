using CyberSnap.Models;

namespace CyberSnap.Services.Upload;

/// <summary>
/// Immutable snapshot of everything a provider needs for one upload.
/// Built once at the start of <see cref="ImageUploadService.UploadEncodedAsync"/>.
/// Mid-flight settings edits do not affect an in-flight request.
/// </summary>
public sealed record UploadRuntimeConfig(
    UploadProviderKind Provider,
    UploadCustomProtocol CustomProtocol,
    string? ImgurClientId,
    string? ImgBBApiKey,
    bool UsingDefaultImgurCredential,
    bool UsingDefaultImgBBCredential,
    string CustomHost,
    int CustomPort,
    string CustomUsername,
    string? CustomPassword,
    string CustomRemoteDirectory,
    string CustomPublicUrlBase,
    string RemoteFileName,
    bool OverwriteOnCollision,
    bool UniqueSuffixOnCollision,
    bool AutoCreateRemoteDirectory,
    bool FtpPassive,
    bool FtpUseTls,
    bool FtpAllowInsecureCertificate,
    string? SftpPrivateKeyPath,
    string? SftpPrivateKeyPassphrase,
    string? SftpTrustedHostKeySha256,
    string S3Endpoint,
    string S3Region,
    string S3Bucket,
    string? S3AccessKey,
    string? S3SecretKey,
    string? S3SessionToken,
    string S3KeyPrefix,
    bool S3ForcePathStyle,
    bool S3MakePublic,
    int MaxBytes,
    int MinIntervalMs,
    int DailyCapOotb,
    TimeSpan HttpTimeout,
    string UserAgent);

public sealed record UploadRequest(
    UploadProviderKind Provider,
    string? SuggestedFileName = null,
    IProgress<UploadProgress>? Progress = null);

public sealed record UploadProgress(double Fraction, string StatusMessage);

public sealed record UploadResult(
    bool Success,
    string? PublicUrl,
    string? ClipboardText,
    UploadProviderKind Provider,
    string? DeleteHash = null,
    string? RemotePath = null,
    bool HasOpenableHttpUrl = false,
    UploadErrorKind ErrorKind = UploadErrorKind.None,
    string? ErrorMessage = null,
    bool DefaultCredentialRejected = false)
{
    public static UploadResult Fail(
        UploadProviderKind provider,
        UploadErrorKind kind,
        string message,
        bool defaultCredentialRejected = false)
        => new(
            Success: false,
            PublicUrl: null,
            ClipboardText: null,
            Provider: provider,
            ErrorKind: kind,
            ErrorMessage: message,
            DefaultCredentialRejected: defaultCredentialRejected);

    public static UploadResult Ok(
        UploadProviderKind provider,
        string? publicUrl,
        string? clipboardText,
        bool hasOpenableHttpUrl,
        string? deleteHash = null,
        string? remotePath = null)
        => new(
            Success: true,
            PublicUrl: publicUrl,
            ClipboardText: clipboardText,
            Provider: provider,
            DeleteHash: deleteHash,
            RemotePath: remotePath,
            HasOpenableHttpUrl: hasOpenableHttpUrl);
}

public enum UploadErrorKind
{
    None,
    Cancelled,
    NotConfigured,
    Network,
    Timeout,
    RateLimited,
    PayloadTooLarge,
    AuthFailed,
    HostRejected,
    HostKeyMismatch,
    CertificateInvalid,
    EncodingFailed,
    Unexpected
}
