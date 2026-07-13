using System.Security.Cryptography;
using System.Text;

namespace CyberSnap.Services.Upload;

/// <summary>
/// Resolves out-of-the-box credentials for users who never paste their own key.
/// Order: environment override → embedded ciphertext (Release) → fail closed.
/// <para>
/// Perfect secrecy is impossible in a client-side OSS app. This vault only raises the cost
/// of casual scraping; rotate the key if it is abused.
/// </para>
/// </summary>
internal static class DefaultCredentialVault
{
    public const string ImgBBApiKeyEnv = "CYBERSNAP_IMGBB_API_KEY";
    public const string CyberGemsApiKeyEnv = "CYBERSNAP_SHARE_API_KEY";

    /// <summary>
    /// AES-GCM ciphertext (Base64) of the OOTB ImgBB API key.
    /// Generate with: <c>scripts/Protect-UploadVaultKey.ps1 -ApiKey "…"</c>
    /// Leave empty to disable OOTB until a key is embedded.
    /// </summary>
    /// <remarks>
    /// Format: base64(nonce[12] + tag[16] + ciphertext).
    /// </remarks>
    private const string EmbeddedImgBBCiphertextBase64 =
        // Maintainer-generated payload. Empty = no OOTB key in this build.
        "5HY5Tb3Apx5MNs4gl+MQr8dFE4htteQ/rWTaGJg4qnA5DTQfSlaq0/f8Es1HJtCvCAZ2pfKLJAHzK5Se";

    /// <summary>
    /// AES-GCM ciphertext of the OOTB CyberGems Share API key.
    /// Generate with: <c>scripts/Protect-UploadVaultKey.ps1 -ApiKey "…" -ApiKeyEnv CYBERSNAP_SHARE_API_KEY</c>
    /// Leave empty until the share server is deployed and the app key is embedded.
    /// </summary>
    private const string EmbeddedCyberGemsCiphertextBase64 = "Ni2Lvd01MndJXr7tHIXE0lyhMoWObGaBc2+xBl8oNhtlaXTAl5lPMuX1cPoi0xiqLUIbKctp2juL";

    // Multi-part entropy — do not put the whole key in one string.
    private static readonly byte[] EntropyPartA = Encoding.UTF8.GetBytes("CyberSnap.Upload.Vault.v1");
    private static readonly byte[] EntropyPartB = Encoding.UTF8.GetBytes("ImgBB.OOTB.2026");
    private static readonly byte[] EntropyPartC = "CyberGems|CyberSnap|Share"u8.ToArray();

    private static string? _cachedImgBB;
    private static bool _resolvedImgBB;
    private static string? _cachedCyberGems;
    private static bool _resolvedCyberGems;

    /// <summary>Returns OOTB ImgBB API key, or null if unavailable.</summary>
    public static string? TryGetImgBBApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ImgBBApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        if (_resolvedImgBB)
            return _cachedImgBB;

        _resolvedImgBB = true;
        _cachedImgBB = TryDecryptEmbedded(EmbeddedImgBBCiphertextBase64);
        return _cachedImgBB;
    }

    /// <summary>Returns OOTB CyberGems Share API key, or null if unavailable.</summary>
    public static string? TryGetCyberGemsApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable(CyberGemsApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        if (_resolvedCyberGems)
            return _cachedCyberGems;

        _resolvedCyberGems = true;
        _cachedCyberGems = TryDecryptEmbedded(EmbeddedCyberGemsCiphertextBase64);
        return _cachedCyberGems;
    }

    /// <summary>
    /// Encrypts a plaintext API key for embedding. Used by maintainer tooling / tests.
    /// Output is safe to paste into <see cref="EmbeddedImgBBCiphertextBase64"/> or
    /// <see cref="EmbeddedCyberGemsCiphertextBase64"/>.
    /// </summary>
    internal static string ProtectForEmbed(string plaintextApiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextApiKey);
        var key = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintextApiKey.Trim());
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);

        var packed = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, packed, nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(packed);
    }

    private static string? TryDecryptEmbedded(string? ciphertextBase64)
    {
        if (string.IsNullOrWhiteSpace(ciphertextBase64))
            return null;

        try
        {
            var packed = Convert.FromBase64String(ciphertextBase64.Trim());
            if (packed.Length < 12 + 16 + 1)
                return null;

            var nonce = packed.AsSpan(0, 12);
            var tag = packed.AsSpan(12, 16);
            var cipher = packed.AsSpan(28);
            var plain = new byte[cipher.Length];
            var key = DeriveKey();
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            var result = Encoding.UTF8.GetString(plain).Trim();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("upload.vault.decrypt", "OOTB credential decrypt failed (fail closed).", ex);
            return null;
        }
    }

    private static byte[] DeriveKey()
    {
        // SHA-256 of concatenated parts — not a secret, just anti-casual-scrape binding.
        var combined = new byte[EntropyPartA.Length + EntropyPartB.Length + EntropyPartC.Length];
        Buffer.BlockCopy(EntropyPartA, 0, combined, 0, EntropyPartA.Length);
        Buffer.BlockCopy(EntropyPartB, 0, combined, EntropyPartA.Length, EntropyPartB.Length);
        Buffer.BlockCopy(EntropyPartC, 0, combined, EntropyPartA.Length + EntropyPartB.Length, EntropyPartC.Length);
        return SHA256.HashData(combined);
    }
}
