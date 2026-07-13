using System.Drawing;
using System.IO;
using System.Text;
using CyberSnap.Models;
using CyberSnap.Services.Upload.Providers;

namespace CyberSnap.Services.Upload;

/// <summary>
/// Static facade for image share uploads. Prefer <see cref="UploadEncodedAsync"/> after UI-thread encode.
/// </summary>
public static class ImageUploadService
{
    private static readonly IImageUploadProvider[] Providers =
    [
        new CyberGemsShareProvider(),
        new ImgurAnonymousProvider(),
        new ImgBBAnonymousProvider(),
        new FtpUploadProvider(),
        new SftpUploadProvider(),
        new S3CompatibleUploadProvider(),
    ];

    public static UploadProviderKind GetDefaultProvider(AppSettings? settings = null)
    {
        settings ??= SettingsService.LoadStatic() ?? new AppSettings();
        var preferred = Enum.IsDefined(typeof(UploadProviderKind), settings.UploadDefaultProvider)
            ? settings.UploadDefaultProvider
            : UploadProviderKind.CyberGems;

        // Imgur is never the effective default unless the user configured a Client-ID.
        if (preferred == UploadProviderKind.Imgur && !UploadCredentialResolver.HasUserImgurClientId(settings))
            preferred = UploadProviderKind.CyberGems;

        // CyberGems Share is the product default when credentials exist; otherwise fall back to ImgBB.
        if (preferred == UploadProviderKind.CyberGems && !IsCyberGemsConfigured(settings))
            return UploadProviderKind.ImgBB;

        // If settings still name CyberGems but menu would show it unavailable, already fell back above.
        // Prefer configured CyberGems over a stale ImgBB default only when schema migration ran
        // (handled in SettingsService). Honor explicit ImgBB/Custom/Imgur preferences as stored.
        return preferred;
    }

    /// <summary>True when CyberGems Share has an API key (user or OOTB/env).</summary>
    public static bool IsCyberGemsConfigured(AppSettings? settings = null)
    {
        settings ??= SettingsService.LoadStatic() ?? new AppSettings();
        var key = UploadCredentialResolver.ResolveCyberGemsApiKey(settings);
        var baseUrl = CyberGemsShareProvider.NormalizeBaseUrl(
            UploadCredentialResolver.ResolveCyberGemsBaseUrl(settings));
        return !string.IsNullOrWhiteSpace(key.Value) && !string.IsNullOrWhiteSpace(baseUrl);
    }

    /// <summary>
    /// Whether Imgur should appear in Share menus / default-provider pickers.
    /// Only when the user entered their own Client-ID in Settings (no OOTB Imgur).
    /// </summary>
    public static bool IsImgurShareOptionVisible(AppSettings? settings = null)
    {
        settings ??= SettingsService.LoadStatic() ?? new AppSettings();
        return UploadCredentialResolver.HasUserImgurClientId(settings);
    }

    /// <summary>
    /// Menu rows for Share: Kind, whether configured, localized label.
    /// Imgur is omitted entirely until the user configures a Client-ID.
    /// Custom destination is represented as a single entry when protocol providers exist.
    /// </summary>
    public static IReadOnlyList<(UploadProviderKind Kind, bool Available, string MenuLabel)> GetMenuProviders(
        AppSettings? settings = null)
    {
        settings ??= SettingsService.LoadStatic() ?? new AppSettings();
        var config = BuildRuntimeConfig(settings, GetDefaultProvider(settings), suggestedFileName: null);

        var list = new List<(UploadProviderKind, bool, string)>(5)
        {
            // CyberGems Share first — first-party temporary links.
            (UploadProviderKind.CyberGems,
                ResolveProvider(UploadProviderKind.CyberGems, config)?.IsConfigured(config) == true,
                LocalizationService.Translate("CyberSnap Share")),
            (UploadProviderKind.ImgBB,
                ResolveProvider(UploadProviderKind.ImgBB, config)?.IsConfigured(config) == true,
                LocalizationService.Translate("ImgBB")),
        };

        // Imgur only when the user supplied a Client-ID (not listed as a dead option otherwise).
        if (UploadCredentialResolver.HasUserImgurClientId(settings))
        {
            list.Add((
                UploadProviderKind.Imgur,
                ResolveProvider(UploadProviderKind.Imgur, config)?.IsConfigured(config) == true,
                LocalizationService.Translate("Imgur")));
        }

        // Custom row only meaningful once providers are registered (PR 4+).
        var custom = ResolveProvider(UploadProviderKind.Custom, config);
        if (custom is not null)
        {
            var protocolLabel = config.CustomProtocol switch
            {
                UploadCustomProtocol.Ftp => "FTP",
                UploadCustomProtocol.Sftp => "SFTP",
                UploadCustomProtocol.S3 => "S3",
                _ => "Custom",
            };
            list.Add((
                UploadProviderKind.Custom,
                custom.IsConfigured(config),
                string.Format(LocalizationService.Translate("Custom ({0})"), protocolLabel)));
        }

        return list;
    }

    /// <summary>
    /// Primary internal API: already-encoded image bytes (PNG/JPEG).
    /// Safe to call from a background thread. Does not touch GDI+.
    /// </summary>
    public static async Task<UploadResult> UploadEncodedAsync(
        byte[] imageBytes,
        string contentType,
        string fileName,
        UploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            return UploadResult.Fail(
                request.Provider,
                UploadErrorKind.EncodingFailed,
                LocalizationService.Translate("Upload failed"));
        }

        var settings = SettingsService.LoadStatic() ?? new AppSettings();

        // Hard gate: Imgur requires a user Client-ID; never fall through to a shared key.
        if (request.Provider == UploadProviderKind.Imgur &&
            !UploadCredentialResolver.HasUserImgurClientId(settings))
        {
            return UploadResult.Fail(
                UploadProviderKind.Imgur,
                UploadErrorKind.NotConfigured,
                LocalizationService.Translate(
                    "Imgur requires your own Client-ID in Settings → Upload. Prefer CyberSnap Share for sharing."));
        }

        var config = BuildRuntimeConfig(settings, request.Provider, request.SuggestedFileName ?? fileName);

        // Prefer provided fileName if already safe; otherwise use config.RemoteFileName.
        var safeName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = config.RemoteFileName;

        var provider = ResolveProvider(request.Provider, config);
        if (provider is null)
        {
            return UploadResult.Fail(
                request.Provider,
                UploadErrorKind.NotConfigured,
                LocalizationService.Translate("Upload not configured"));
        }

        if (!provider.IsConfigured(config))
        {
            var notConfigured = request.Provider switch
            {
                UploadProviderKind.CyberGems => LocalizationService.Translate(
                    "CyberSnap Share is not configured. Paste your API key in Settings → Uploads, or set CYBERSNAP_SHARE_API_KEY and restart CyberSnap."),
                UploadProviderKind.ImgBB => LocalizationService.Translate(
                    "ImgBB is not configured. Paste your API key in Settings → Uploads, or set CYBERSNAP_IMGBB_API_KEY and restart CyberSnap."),
                UploadProviderKind.Imgur => LocalizationService.Translate(
                    "Imgur requires your own Client-ID in Settings → Upload. Prefer CyberSnap Share for sharing."),
                UploadProviderKind.Custom => LocalizationService.Translate(
                    "Configure a custom destination in Settings → Uploads."),
                _ => LocalizationService.Translate("Upload not configured"),
            };
            return UploadResult.Fail(request.Provider, UploadErrorKind.NotConfigured, notConfigured);
        }

        var usingOotb = IsUsingOotbCredential(request.Provider, config);

        var rate = ClientRateLimiter.TryAcquire(config, usingOotb);
        if (!rate.Allowed)
        {
            return UploadResult.Fail(
                request.Provider,
                rate.ErrorKind,
                rate.Message ?? LocalizationService.Translate("Upload rate limited. Try again later or use your own API key."));
        }

        if (imageBytes.Length > config.MaxBytes)
        {
            return UploadResult.Fail(
                request.Provider,
                UploadErrorKind.PayloadTooLarge,
                LocalizationService.Translate("Image is too large to upload. Try JPEG in Settings → Upload."));
        }

        AppDiagnostics.LogInfo(
            "upload.start",
            $"provider={request.Provider} bytes={imageBytes.Length} ootb={usingOotb}");

        try
        {
            await using var stream = new MemoryStream(imageBytes, writable: false);
            var result = await provider.UploadAsync(
                stream,
                contentType,
                safeName,
                imageBytes.LongLength,
                config,
                request.Progress,
                cancellationToken).ConfigureAwait(false);

            if (result.Success)
                ClientRateLimiter.RecordSuccess(usingOotb);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UploadResult.Fail(
                request.Provider,
                UploadErrorKind.Cancelled,
                LocalizationService.Translate("Upload cancelled"));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("upload.facade", ex);
            return UploadResult.Fail(
                request.Provider,
                UploadErrorKind.Unexpected,
                LocalizationService.Translate("Upload failed"));
        }
    }

    /// <summary>
    /// Convenience for callers that still hold a Bitmap on the UI thread.
    /// Encodes synchronously on the calling thread (caller owns the bitmap), then uploads bytes.
    /// </summary>
    public static Task<UploadResult> UploadBitmapAsync(
        Bitmap image,
        UploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var settings = SettingsService.LoadStatic() ?? new AppSettings();
        try
        {
            var encoded = UploadImageEncoder.Encode(
                image,
                settings.UploadImageFormat,
                settings.UploadJpegQuality);
            var fileName = BuildRemoteFileName(request.SuggestedFileName, encoded.Extension);
            return UploadEncodedAsync(
                encoded.Bytes,
                encoded.ContentType,
                fileName,
                request,
                cancellationToken);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("upload.encode", ex);
            return Task.FromResult(UploadResult.Fail(
                request.Provider,
                UploadErrorKind.EncodingFailed,
                LocalizationService.Translate("Upload failed")));
        }
    }

    internal static UploadRuntimeConfig BuildRuntimeConfig(
        AppSettings settings,
        UploadProviderKind provider,
        string? suggestedFileName)
    {
        var imgur = UploadCredentialResolver.ResolveImgurClientId(settings);
        var imgbb = UploadCredentialResolver.ResolveImgBBApiKey(settings);
        var cybergems = UploadCredentialResolver.ResolveCyberGemsApiKey(settings);
        var cyberGemsBase = UploadCredentialResolver.ResolveCyberGemsBaseUrl(settings);

        var format = settings.UploadImageFormat == UploadImageFormatPreference.Jpeg ? "jpg" : "png";
        var remoteName = BuildRemoteFileName(suggestedFileName, format);

        var maxBytes = settings.UploadMaxBytes > 0
            ? settings.UploadMaxBytes
            : 10 * 1024 * 1024;
        var minInterval = settings.UploadMinIntervalMs >= 0
            ? settings.UploadMinIntervalMs
            : 3000;
        var dailyCap = settings.UploadDailyCapOotb >= 0
            ? settings.UploadDailyCapOotb
            : 50;

        var ua = "CyberSnap/" + UpdateService.GetCurrentVersionLabel().TrimStart('v');

        var customProtocol = Enum.IsDefined(typeof(UploadCustomProtocol), settings.UploadCustomProtocol)
            ? settings.UploadCustomProtocol
            : UploadCustomProtocol.Sftp;

        return new UploadRuntimeConfig(
            Provider: provider,
            CustomProtocol: customProtocol,
            ImgurClientId: imgur.Value,
            ImgBBApiKey: imgbb.Value,
            CyberGemsApiKey: cybergems.Value,
            CyberGemsBaseUrl: cyberGemsBase,
            UsingDefaultImgurCredential: imgur.IsDefault,
            UsingDefaultImgBBCredential: imgbb.IsDefault,
            UsingDefaultCyberGemsCredential: cybergems.IsDefault,
            CustomHost: settings.UploadCustomHost ?? "",
            CustomPort: settings.UploadCustomPort,
            CustomUsername: settings.UploadCustomUsername ?? "",
            CustomPassword: settings.UploadCustomPassword,
            CustomRemoteDirectory: settings.UploadCustomRemoteDirectory ?? "",
            CustomPublicUrlBase: settings.UploadCustomPublicUrlBase ?? "",
            RemoteFileName: remoteName,
            OverwriteOnCollision: settings.UploadOverwriteOnCollision,
            UniqueSuffixOnCollision: settings.UploadUniqueSuffixOnCollision,
            AutoCreateRemoteDirectory: settings.UploadAutoCreateRemoteDirectory,
            FtpPassive: settings.UploadFtpPassive,
            FtpUseTls: settings.UploadFtpUseTls,
            FtpAllowInsecureCertificate: settings.UploadFtpAllowInsecureCertificate,
            SftpPrivateKeyPath: settings.UploadSftpPrivateKeyPath,
            SftpPrivateKeyPassphrase: settings.UploadSftpPrivateKeyPassphrase,
            SftpTrustedHostKeySha256: settings.UploadSftpTrustedHostKeySha256,
            S3Endpoint: settings.UploadS3Endpoint ?? "",
            S3Region: settings.UploadS3Region ?? "",
            S3Bucket: settings.UploadS3Bucket ?? "",
            S3AccessKey: settings.UploadS3AccessKey,
            S3SecretKey: settings.UploadS3SecretKey,
            S3SessionToken: settings.UploadS3SessionToken,
            S3KeyPrefix: settings.UploadS3KeyPrefix ?? "",
            S3ForcePathStyle: settings.UploadS3ForcePathStyle,
            S3MakePublic: settings.UploadS3MakePublic,
            MaxBytes: maxBytes,
            MinIntervalMs: minInterval,
            DailyCapOotb: dailyCap,
            HttpTimeout: TimeSpan.FromSeconds(Math.Clamp(settings.UploadHttpTimeoutSeconds, 15, 600)),
            UserAgent: ua);
    }

    private static IImageUploadProvider? ResolveProvider(UploadProviderKind kind, UploadRuntimeConfig config)
    {
        if (kind == UploadProviderKind.Custom)
        {
            return Providers.FirstOrDefault(p =>
                p.Kind == UploadProviderKind.Custom &&
                p.CustomProtocol == config.CustomProtocol);
        }

        return Providers.FirstOrDefault(p => p.Kind == kind && p.CustomProtocol is null);
    }

    private static bool IsUsingOotbCredential(UploadProviderKind kind, UploadRuntimeConfig config)
        => kind switch
        {
            UploadProviderKind.Imgur => config.UsingDefaultImgurCredential,
            UploadProviderKind.ImgBB => config.UsingDefaultImgBBCredential,
            UploadProviderKind.CyberGems => config.UsingDefaultCyberGemsCredential,
            _ => false,
        };

    public static string BuildRemoteFileName(string? suggested, string extension)
    {
        extension = extension.TrimStart('.').ToLowerInvariant();
        if (extension is not ("png" or "jpg" or "jpeg"))
            extension = "png";
        if (extension == "jpeg")
            extension = "jpg";

        var baseName = SanitizeFileName(suggested);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "cybersnap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        }
        else
        {
            // Strip extension if present so we control it.
            baseName = Path.GetFileNameWithoutExtension(baseName);
            baseName = SanitizeFileName(baseName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "cybersnap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        }

        return baseName + "." + extension;
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var trimmed = Path.GetFileName(name.Trim());
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('-');
        }

        var result = sb.ToString().Trim('-', '.');
        return result.Length > 120 ? result[..120] : result;
    }
}
