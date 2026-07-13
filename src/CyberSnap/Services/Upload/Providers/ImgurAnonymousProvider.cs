using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload.Providers;

internal sealed class ImgurAnonymousProvider : IImageUploadProvider
{
    private static readonly HttpClient Http = CreateHttp();

    public UploadProviderKind Kind => UploadProviderKind.Imgur;
    public UploadCustomProtocol? CustomProtocol => null;

    public bool IsConfigured(UploadRuntimeConfig config)
        => !string.IsNullOrWhiteSpace(config.ImgurClientId);

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

        try
        {
            using var content = new MultipartFormDataContent();
            var imagePart = new StreamContent(imageStream);
            imagePart.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            content.Add(imagePart, "image", fileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.imgur.com/3/image");
            request.Headers.TryAddWithoutValidation("Authorization", "Client-ID " + config.ImgurClientId);
            request.Headers.TryAddWithoutValidation("User-Agent", config.UserAgent);
            request.Content = content;

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new UploadProgress(0.85, LocalizationService.Translate("Uploading…")));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapResponse(response.StatusCode, body, config.UsingDefaultImgurCredential);
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
            AppDiagnostics.LogError("upload.imgur", ex);
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Network,
                LocalizationService.Translate("Upload failed") + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("upload.imgur", ex);
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
            AppDiagnostics.LogWarning("upload.imgur", $"Host rejected upload with status {(int)status}");
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.HostRejected,
                LocalizationService.Translate("Upload failed"));
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successEl) &&
                successEl.ValueKind == JsonValueKind.False)
            {
                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.HostRejected,
                    LocalizationService.Translate("Upload failed"));
            }

            if (!root.TryGetProperty("data", out var data))
            {
                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.Unexpected,
                    LocalizationService.Translate("Upload failed"));
            }

            string? link = null;
            if (data.TryGetProperty("link", out var linkEl))
                link = linkEl.GetString();

            string? deleteHash = null;
            if (data.TryGetProperty("deletehash", out var delEl))
                deleteHash = delEl.GetString(); // parse-only; unshare UI out of v1 (K22)

            if (string.IsNullOrWhiteSpace(link))
            {
                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.Unexpected,
                    LocalizationService.Translate("Upload failed"));
            }

            AppDiagnostics.LogInfo("upload.imgur", "Upload succeeded");
            return UploadResult.Ok(
                Kind,
                publicUrl: link,
                clipboardText: link,
                hasOpenableHttpUrl: true,
                deleteHash: deleteHash);
        }
        catch (JsonException ex)
        {
            AppDiagnostics.LogError("upload.imgur", ex, "Failed to parse Imgur response");
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Unexpected,
                LocalizationService.Translate("Upload failed"));
        }
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return client;
    }
}
