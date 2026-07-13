using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload.Providers;

internal sealed class ImgBBAnonymousProvider : IImageUploadProvider
{
    private static readonly HttpClient Http = CreateHttp();

    public UploadProviderKind Kind => UploadProviderKind.ImgBB;
    public UploadCustomProtocol? CustomProtocol => null;

    public bool IsConfigured(UploadRuntimeConfig config)
        => !string.IsNullOrWhiteSpace(config.ImgBBApiKey);

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
            // ImgBB accepts binary, base64, or URL. Base64 in the form field is the most
            // reliable across hosts; key stays in the query string (redacted in logs).
            var uri = "https://api.imgbb.com/1/upload?key=" + Uri.EscapeDataString(config.ImgBBApiKey!);

            byte[] bytes;
            if (imageStream is MemoryStream ms)
            {
                bytes = ms.ToArray();
            }
            else
            {
                using var copy = new MemoryStream();
                await imageStream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                bytes = copy.ToArray();
            }

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(Convert.ToBase64String(bytes)), "image");
            if (!string.IsNullOrWhiteSpace(fileName))
                content.Add(new StringContent(Path.GetFileNameWithoutExtension(fileName)), "name");

            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", config.UserAgent);
            request.Content = content;

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new UploadProgress(0.85, LocalizationService.Translate("Uploading…")));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapResponse(response.StatusCode, body, config.UsingDefaultImgBBCredential);
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
            AppDiagnostics.LogError("upload.imgbb", ex);
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Network,
                LocalizationService.Translate("Upload failed") + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("upload.imgbb", ex);
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
            AppDiagnostics.LogWarning("upload.imgbb", $"Host rejected upload with status {(int)status}: {detail}");
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

            // ImgBB: { "success": true, "data": { "url": "...", "display_url": "..." } }
            if (root.TryGetProperty("success", out var successEl))
            {
                bool failed = successEl.ValueKind == JsonValueKind.False
                    || (successEl.ValueKind == JsonValueKind.Number && successEl.GetInt32() == 0);
                if (failed)
                {
                    var detail = TryReadErrorMessage(root);
                    AppDiagnostics.LogWarning("upload.imgbb", "API success=false: " + detail);
                    return UploadResult.Fail(
                        Kind,
                        UploadErrorKind.HostRejected,
                        string.IsNullOrWhiteSpace(detail)
                            ? LocalizationService.Translate("Upload failed")
                            : LocalizationService.Translate("Upload failed") + ": " + detail);
                }
            }

            if (!root.TryGetProperty("data", out var data))
            {
                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.Unexpected,
                    LocalizationService.Translate("Upload failed"));
            }

            string? url = null;
            if (data.TryGetProperty("url", out var urlEl))
                url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url) && data.TryGetProperty("display_url", out var displayEl))
                url = displayEl.GetString();

            if (string.IsNullOrWhiteSpace(url))
            {
                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.Unexpected,
                    LocalizationService.Translate("Upload failed"));
            }

            AppDiagnostics.LogInfo("upload.imgbb", "Upload succeeded");
            return UploadResult.Ok(
                Kind,
                publicUrl: url,
                clipboardText: url,
                hasOpenableHttpUrl: true);
        }
        catch (JsonException ex)
        {
            AppDiagnostics.LogError("upload.imgbb", ex, "Failed to parse ImgBB response");
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
        if (root.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                return msg.GetString() ?? "";
            if (err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? "";
        }
        if (root.TryGetProperty("status_txt", out var statusTxt))
            return statusTxt.GetString() ?? "";
        return "";
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return client;
    }
}
