using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload.Providers;

/// <summary>
/// Uploads to CyberSnap Share at cybersnap.cybergems.org (or a configured base URL).
/// POST /v1/upload with Bearer API key; public link is https://…/{id} (48h TTL by default).
/// </summary>
internal sealed class CyberGemsShareProvider : IImageUploadProvider
{
    public const string DefaultBaseUrl = "https://cybersnap.cybergems.org";

    private static readonly HttpClient Http = CreateHttp();

    public UploadProviderKind Kind => UploadProviderKind.CyberGems;
    public UploadCustomProtocol? CustomProtocol => null;

    public bool IsConfigured(UploadRuntimeConfig config)
        => !string.IsNullOrWhiteSpace(config.CyberGemsApiKey)
           && !string.IsNullOrWhiteSpace(NormalizeBaseUrl(config.CyberGemsBaseUrl));

    public async Task<UploadResult> UploadAsync(
        Stream imageStream,
        string contentType,
        string fileName,
        long contentLength,
        UploadRuntimeConfig config,
        IProgress<UploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(config))
        {
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.NotConfigured,
                LocalizationService.Translate("Upload not configured"));
        }

        progress?.Report(new UploadProgress(0.05, LocalizationService.Translate("Uploading…")));

        var baseUrl = NormalizeBaseUrl(config.CyberGemsBaseUrl)!;
        var uploadUri = baseUrl.TrimEnd('/') + "/v1/upload";

        try
        {
            using var content = new MultipartFormDataContent();
            var imagePart = new StreamContent(imageStream);
            imagePart.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            content.Add(imagePart, "image", string.IsNullOrWhiteSpace(fileName) ? "image.png" : fileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUri);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.CyberGemsApiKey);
            // Fallback when some hosts strip Authorization on PHP.
            request.Headers.TryAddWithoutValidation("X-CyberSnap-Key", config.CyberGemsApiKey);
            request.Headers.TryAddWithoutValidation("User-Agent", config.UserAgent);
            request.Content = content;

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new UploadProgress(0.85, LocalizationService.Translate("Uploading…")));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapResponse(response.StatusCode, body, config.UsingDefaultCyberGemsCredential);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Cancelled,
                LocalizationService.Translate("Upload cancelled"));
        }
        catch (TaskCanceledException)
        {
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Timeout,
                LocalizationService.Translate("Upload timed out"));
        }
        catch (HttpRequestException ex)
        {
            AppDiagnostics.LogError("upload.cybergems", ex);
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Network,
                LocalizationService.Translate("Upload failed") + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("upload.cybergems", ex);
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Unexpected,
                LocalizationService.Translate("Upload failed"));
        }
    }

    private UploadResult MapResponse(HttpStatusCode status, string body, bool usingDefault)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.RateLimited,
                LocalizationService.Translate("Upload rate limited. Try again later or use your own API key."));
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var msg = usingDefault
                ? LocalizationService.Translate(
                    "CyberSnap's shared key was rejected — add your own key in Settings → Upload.")
                : LocalizationService.Translate(
                    "Authentication failed. Check your Client-ID / API key in Settings → Upload.");
            return UploadResult.Fail(Kind, UploadErrorKind.AuthFailed, msg, defaultCredentialRejected: usingDefault);
        }

        if (status == HttpStatusCode.RequestEntityTooLarge)
        {
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.PayloadTooLarge,
                LocalizationService.Translate("Image is too large to upload. Try JPEG in Settings → Upload."));
        }

        if ((int)status < 200 || (int)status >= 300)
        {
            var detail = TryReadErrorMessageFromBody(body);
            AppDiagnostics.LogWarning("upload.cybergems", $"Host rejected upload with status {(int)status}: {detail}");
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.HostRejected,
                string.IsNullOrWhiteSpace(detail)
                    ? LocalizationService.Translate("Upload failed")
                    : LocalizationService.Translate("Upload failed") + ": " + detail);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("ok", out var okEl)
                && okEl.ValueKind == JsonValueKind.False)
            {
                var detail = TryReadErrorMessage(root);
                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.HostRejected,
                    string.IsNullOrWhiteSpace(detail)
                        ? LocalizationService.Translate("Upload failed")
                        : LocalizationService.Translate("Upload failed") + ": " + detail);
            }

            string? url = null;
            if (root.TryGetProperty("url", out var urlEl))
                url = urlEl.GetString();

            if (string.IsNullOrWhiteSpace(url))
            {
                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.Unexpected,
                    LocalizationService.Translate("Upload failed"));
            }

            AppDiagnostics.LogInfo("upload.cybergems", "Upload succeeded");
            return UploadResult.Ok(
                Kind,
                publicUrl: url,
                clipboardText: url,
                hasOpenableHttpUrl: true);
        }
        catch (JsonException ex)
        {
            AppDiagnostics.LogError("upload.cybergems", ex, "Failed to parse CyberGems Share response");
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Unexpected,
                LocalizationService.Translate("Upload failed"));
        }
    }

    private static string TryReadErrorMessageFromBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return TryReadErrorMessage(doc.RootElement);
        }
        catch
        {
            return "";
        }
    }

    private static string TryReadErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            return msg.GetString() ?? "";
        if (root.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? "";
            if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var nested))
                return nested.GetString() ?? "";
        }
        return "";
    }

    /// <summary>Returns a normalized https base URL, or null if invalid.</summary>
    public static string? NormalizeBaseUrl(string? baseUrl)
    {
        var raw = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("https" or "http"))
            return null;
        // Production should be HTTPS; allow http only for local dev.
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return client;
    }
}
