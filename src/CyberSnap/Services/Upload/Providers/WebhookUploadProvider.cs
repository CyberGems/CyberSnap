using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload.Providers;

/// <summary>
/// POSTs the image to a user-defined HTTPS webhook (multipart or JSON base64).
/// Optional Bearer token; public URL from JSON response or fallback public URL base.
/// </summary>
internal sealed class WebhookUploadProvider : IImageUploadProvider
{
    private static readonly HttpClient Http = CreateHttp();

    public UploadProviderKind Kind => UploadProviderKind.Custom;
    public UploadCustomProtocol? CustomProtocol => UploadCustomProtocol.Webhook;

    public bool IsConfigured(UploadRuntimeConfig config)
        => !string.IsNullOrWhiteSpace(config.WebhookUrl)
           && Uri.TryCreate(config.WebhookUrl.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

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
                LocalizationService.Translate("Configure a webhook URL in Settings → Uploads."));
        }

        progress?.Report(new UploadProgress(0.05, LocalizationService.Translate("Uploading…")));

        byte[] bytes;
        if (imageStream is MemoryStream ms)
            bytes = ms.ToArray();
        else
        {
            using var copy = new MemoryStream();
            await imageStream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            bytes = copy.ToArray();
        }

        var safeName = string.IsNullOrWhiteSpace(fileName) ? "image.png" : Path.GetFileName(fileName);
        var mime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        var field = string.IsNullOrWhiteSpace(config.WebhookFormFieldName)
            ? "image"
            : config.WebhookFormFieldName.Trim();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.WebhookUrl.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", config.UserAgent);
            if (!string.IsNullOrWhiteSpace(config.WebhookBearerToken))
            {
                request.Headers.TryAddWithoutValidation(
                    "Authorization",
                    "Bearer " + config.WebhookBearerToken.Trim());
            }

            if (config.WebhookBodyMode == UploadWebhookBodyMode.JsonBase64)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    filename = safeName,
                    content_type = mime,
                    image_base64 = Convert.ToBase64String(bytes),
                });
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }
            else
            {
                var multi = new MultipartFormDataContent();
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue(mime);
                multi.Add(part, field, safeName);
                multi.Add(new StringContent(safeName), "filename");
                request.Content = multi;
            }

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new UploadProgress(0.85, LocalizationService.Translate("Uploading…")));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapResponse(response.StatusCode, body, config, safeName);
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
            AppDiagnostics.LogError("upload.webhook", ex);
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Network,
                LocalizationService.Translate("Upload failed") + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("upload.webhook", ex);
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Unexpected,
                LocalizationService.Translate("Upload failed"));
        }
    }

    private static UploadResult MapResponse(
        HttpStatusCode status,
        string body,
        UploadRuntimeConfig config,
        string fileName)
    {
        const UploadProviderKind kind = UploadProviderKind.Custom;

        if (status == HttpStatusCode.TooManyRequests)
        {
            return UploadResult.Fail(
                kind,
                UploadErrorKind.RateLimited,
                LocalizationService.Translate("Upload rate limited. Try again later or use your own API key."));
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return UploadResult.Fail(
                kind,
                UploadErrorKind.AuthFailed,
                LocalizationService.Translate(
                    "Authentication failed. Check your Client-ID / API key in Settings → Upload."));
        }

        if ((int)status < 200 || (int)status >= 300)
        {
            var detail = TryReadError(body);
            AppDiagnostics.LogWarning("upload.webhook", $"Host rejected upload with status {(int)status}: {detail}");
            return UploadResult.Fail(
                kind,
                UploadErrorKind.HostRejected,
                string.IsNullOrWhiteSpace(detail)
                    ? LocalizationService.Translate("Upload failed")
                    : LocalizationService.Translate("Upload failed") + ": " + detail);
        }

        var url = TryReadUrl(body);
        if (string.IsNullOrWhiteSpace(url))
            url = CustomUploadHelpers.CombinePublicUrl(config.CustomPublicUrlBase, fileName);

        AppDiagnostics.LogInfo("upload.webhook", "Upload succeeded");
        return CustomUploadHelpers.SuccessPathOrUrl(
            kind,
            remotePath: fileName,
            publicUrl: string.IsNullOrWhiteSpace(url) ? null : url);
    }

    private static string? TryReadUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return FindUrl(doc.RootElement);
        }
        catch
        {
            // Plain-text URL response
            var t = body.Trim();
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var line = t.Split('\n', '\r')[0].Trim();
                if (Uri.TryCreate(line, UriKind.Absolute, out _))
                    return line;
            }
            return null;
        }
    }

    private static string? FindUrl(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)
                && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return s;
            return null;
        }

        if (el.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "url", "public_url", "link", "href", "display_url", "file_url" })
        {
            if (el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        if (el.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var nested = FindUrl(data);
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }

        return null;
    }

    private static string TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? "";
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? "";
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                    return m.GetString() ?? "";
            }
        }
        catch
        {
            // ignore
        }
        return body.Length > 160 ? body[..160] : body;
    }

    private static HttpClient CreateHttp()
        => new() { Timeout = TimeSpan.FromMinutes(2) };
}
