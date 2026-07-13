using CyberSnap.Models;

namespace CyberSnap.Services.Upload;

internal static class UploadCredentialResolver
{
    public readonly record struct ResolvedCredential(string? Value, bool IsDefault);

    /// <summary>
    /// Imgur is <b>user-key only</b>: no CyberSnap OOTB Client-ID.
    /// The public app registration flow has been broken/unreliable for new apps;
    /// Imgur only appears in Share when the user pastes their own Client-ID in Settings.
    /// </summary>
    public static ResolvedCredential ResolveImgurClientId(AppSettings settings)
    {
        if (HasUserImgurClientId(settings))
            return new ResolvedCredential(settings.UploadImgurClientId!.Trim(), IsDefault: false);

        return new ResolvedCredential(null, IsDefault: false);
    }

    public static bool HasUserImgurClientId(AppSettings settings)
        => settings.UploadUseCustomImgurClientId &&
           !string.IsNullOrWhiteSpace(settings.UploadImgurClientId);

    /// <summary>
    /// True when the user has stored a personal ImgBB API key (toggle may lag UI paste).
    /// </summary>
    public static bool HasUserImgBBApiKey(AppSettings settings)
        => !string.IsNullOrWhiteSpace(settings.UploadImgBBApiKey);

    public static ResolvedCredential ResolveImgBBApiKey(AppSettings settings)
    {
        // Prefer a stored user key whenever present. Requiring the toggle alone caused
        // "Upload not configured" when users pasted a key but left the switch off.
        if (HasUserImgBBApiKey(settings))
        {
            return new ResolvedCredential(settings.UploadImgBBApiKey!.Trim(), IsDefault: false);
        }

        // Toggle on with empty box still means "I want my key" — do not fall back to OOTB
        // until they clear the intent (toggle off).
        if (settings.UploadUseCustomImgBBApiKey)
            return new ResolvedCredential(null, IsDefault: false);

        var ootb = DefaultCredentialVault.TryGetImgBBApiKey();
        return string.IsNullOrWhiteSpace(ootb)
            ? new ResolvedCredential(null, IsDefault: false)
            : new ResolvedCredential(ootb, IsDefault: true);
    }

    /// <summary>
    /// True when the user has stored a personal CyberGems Share API key.
    /// </summary>
    public static bool HasUserCyberGemsApiKey(AppSettings settings)
        => !string.IsNullOrWhiteSpace(settings.UploadCyberGemsApiKey);

    public static ResolvedCredential ResolveCyberGemsApiKey(AppSettings settings)
    {
        if (HasUserCyberGemsApiKey(settings))
            return new ResolvedCredential(settings.UploadCyberGemsApiKey!.Trim(), IsDefault: false);

        if (settings.UploadUseCustomCyberGemsApiKey)
            return new ResolvedCredential(null, IsDefault: false);

        var ootb = DefaultCredentialVault.TryGetCyberGemsApiKey();
        return string.IsNullOrWhiteSpace(ootb)
            ? new ResolvedCredential(null, IsDefault: false)
            : new ResolvedCredential(ootb, IsDefault: true);
    }

    public static string ResolveCyberGemsBaseUrl(AppSettings settings)
    {
        var custom = settings.UploadCyberGemsBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(custom))
            return custom;
        return Providers.CyberGemsShareProvider.DefaultBaseUrl;
    }
}
