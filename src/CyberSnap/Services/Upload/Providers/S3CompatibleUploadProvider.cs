using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload.Providers;

/// <summary>Minimal S3-compatible PutObject with SigV4 (AWS, MinIO, R2, etc.).</summary>
internal sealed class S3CompatibleUploadProvider : IImageUploadProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public UploadProviderKind Kind => UploadProviderKind.Custom;
    public UploadCustomProtocol? CustomProtocol => UploadCustomProtocol.S3;

    public bool IsConfigured(UploadRuntimeConfig config)
        => !string.IsNullOrWhiteSpace(config.S3Bucket)
           && !string.IsNullOrWhiteSpace(config.S3AccessKey)
           && !string.IsNullOrWhiteSpace(config.S3SecretKey);

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
            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            var payload = ms.ToArray();

            var objectKey = BuildObjectKey(config.S3KeyPrefix, fileName);
            var region = string.IsNullOrWhiteSpace(config.S3Region) ? "us-east-1" : config.S3Region.Trim();
            var endpoint = ResolveEndpoint(config, region);
            var uri = BuildObjectUri(endpoint, config.S3Bucket, objectKey, config.S3ForcePathStyle);

            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var payloadHash = ToHex(SHA256.HashData(payload));
            var host = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);

            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["content-type"] = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                ["host"] = host,
                ["x-amz-content-sha256"] = payloadHash,
                ["x-amz-date"] = amzDate,
            };
            if (config.S3MakePublic)
                headers["x-amz-acl"] = "public-read";
            if (!string.IsNullOrWhiteSpace(config.S3SessionToken))
                headers["x-amz-security-token"] = config.S3SessionToken!;

            var signedHeaders = string.Join(";", headers.Keys);
            var canonicalHeaders = string.Join("\n", headers.Select(kv => kv.Key + ":" + kv.Value.Trim())) + "\n";
            var canonicalRequest = string.Join("\n",
                "PUT",
                uri.AbsolutePath,
                "", // query
                canonicalHeaders,
                signedHeaders,
                payloadHash);

            var credentialScope = $"{dateStamp}/{region}/s3/aws4_request";
            var stringToSign = string.Join("\n",
                "AWS4-HMAC-SHA256",
                amzDate,
                credentialScope,
                ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

            var signingKey = GetSignatureKey(config.S3SecretKey!, dateStamp, region, "s3");
            var signature = ToHex(HmacSha256(signingKey, stringToSign));
            var authHeader =
                $"AWS4-HMAC-SHA256 Credential={config.S3AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

            using var request = new HttpRequestMessage(HttpMethod.Put, uri);
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            request.Headers.TryAddWithoutValidation("User-Agent", config.UserAgent);
            if (config.S3MakePublic)
                request.Headers.TryAddWithoutValidation("x-amz-acl", "public-read");
            if (!string.IsNullOrWhiteSpace(config.S3SessionToken))
                request.Headers.TryAddWithoutValidation("x-amz-security-token", config.S3SessionToken);

            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

            progress?.Report(new UploadProgress(0.5, LocalizationService.Translate("Uploading…")));

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                var remotePath = objectKey;
                var publicUrl = CustomUploadHelpers.CombinePublicUrl(
                    config.CustomPublicUrlBase,
                    Path.GetFileName(fileName),
                    config.S3KeyPrefix);
                if (string.IsNullOrWhiteSpace(publicUrl) && !config.S3ForcePathStyle)
                {
                    // Virtual-hosted style public URL guess (may still need bucket policy).
                    publicUrl = $"https://{config.S3Bucket}.s3.{region}.amazonaws.com/{objectKey}";
                }
                else if (string.IsNullOrWhiteSpace(publicUrl))
                {
                    publicUrl = uri.ToString();
                }

                progress?.Report(new UploadProgress(1.0, LocalizationService.Translate("Uploaded")));
                AppDiagnostics.LogInfo("upload.s3", "Upload succeeded");
                return CustomUploadHelpers.SuccessPathOrUrl(Kind, remotePath, publicUrl);
            }

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                // ACL may fail under bucket-owner-enforced; retry once without ACL if that was set.
                if (config.S3MakePublic)
                {
                    var retry = await UploadWithoutAclAsync(
                        payload, contentType, objectKey, config, region, endpoint, cancellationToken)
                        .ConfigureAwait(false);
                    if (retry is not null)
                        return retry;
                }

                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.AuthFailed,
                    LocalizationService.Translate(
                        "Authentication failed. Check your Client-ID / API key in Settings → Upload."));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            AppDiagnostics.LogWarning("upload.s3", $"Host rejected with {(int)response.StatusCode}: {Truncate(body, 200)}");
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.HostRejected,
                LocalizationService.Translate("Upload failed"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UploadResult.Fail(Kind, UploadErrorKind.Cancelled, LocalizationService.Translate("Upload cancelled"));
        }
        catch (TaskCanceledException)
        {
            return UploadResult.Fail(Kind, UploadErrorKind.Timeout, LocalizationService.Translate("Upload timed out"));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("upload.s3", ex);
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.Network,
                LocalizationService.Translate("Upload failed") + ": " + ex.Message);
        }
    }

    private static async Task<UploadResult?> UploadWithoutAclAsync(
        byte[] payload,
        string contentType,
        string objectKey,
        UploadRuntimeConfig config,
        string region,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var noAcl = config with { S3MakePublic = false };
            // Recursive would loop — call Put directly with S3MakePublic false already in headers.
            var uri = BuildObjectUri(endpoint, noAcl.S3Bucket, objectKey, noAcl.S3ForcePathStyle);
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var payloadHash = ToHex(SHA256.HashData(payload));
            var host = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["content-type"] = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                ["host"] = host,
                ["x-amz-content-sha256"] = payloadHash,
                ["x-amz-date"] = amzDate,
            };
            var signedHeaders = string.Join(";", headers.Keys);
            var canonicalHeaders = string.Join("\n", headers.Select(kv => kv.Key + ":" + kv.Value.Trim())) + "\n";
            var canonicalRequest = string.Join("\n", "PUT", uri.AbsolutePath, "", canonicalHeaders, signedHeaders, payloadHash);
            var credentialScope = $"{dateStamp}/{region}/s3/aws4_request";
            var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, credentialScope,
                ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
            var signature = ToHex(HmacSha256(GetSignatureKey(noAcl.S3SecretKey!, dateStamp, region, "s3"), stringToSign));
            var auth =
                $"AWS4-HMAC-SHA256 Credential={noAcl.S3AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

            using var request = new HttpRequestMessage(HttpMethod.Put, uri);
            request.Headers.TryAddWithoutValidation("Authorization", auth);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or >= 300)
                return null;

            var publicUrl = CustomUploadHelpers.CombinePublicUrl(
                noAcl.CustomPublicUrlBase, Path.GetFileName(objectKey), noAcl.S3KeyPrefix);
            if (string.IsNullOrWhiteSpace(publicUrl))
                publicUrl = uri.ToString();
            AppDiagnostics.LogInfo("upload.s3", "Upload succeeded without ACL");
            return CustomUploadHelpers.SuccessPathOrUrl(UploadProviderKind.Custom, objectKey, publicUrl);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildObjectKey(string? prefix, string fileName)
    {
        var name = fileName.TrimStart('/');
        if (string.IsNullOrWhiteSpace(prefix))
            return name;
        return prefix.Trim().Trim('/') + "/" + name;
    }

    private static Uri ResolveEndpoint(UploadRuntimeConfig config, string region)
    {
        if (!string.IsNullOrWhiteSpace(config.S3Endpoint))
        {
            var ep = config.S3Endpoint.Trim().TrimEnd('/');
            if (!ep.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                ep = "https://" + ep;
            return new Uri(ep);
        }
        return new Uri($"https://s3.{region}.amazonaws.com");
    }

    private static Uri BuildObjectUri(Uri endpoint, string bucket, string objectKey, bool forcePathStyle)
    {
        var key = string.Join("/", objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        if (forcePathStyle || !IsAwsHost(endpoint.Host))
        {
            var builder = new UriBuilder(endpoint)
            {
                Path = $"/{bucket}/{key}"
            };
            return builder.Uri;
        }

        // Virtual-hosted–style
        var vh = new UriBuilder(endpoint)
        {
            Host = $"{bucket}.{endpoint.Host}",
            Path = "/" + key
        };
        return vh.Uri;
    }

    private static bool IsAwsHost(string host)
        => host.Contains("amazonaws.com", StringComparison.OrdinalIgnoreCase);

    private static byte[] GetSignatureKey(string secretKey, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string ToHex(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
