using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace CyberSnap.Services;

public enum TranslationModel
{
    Google = 1,
    MyMemory = 3
}

public static class TranslationService
{
    private static readonly HttpClient GoogleHttp = CreateGoogleHttpClient();
    private static readonly HttpClient MyMemoryHttp = CreateMyMemoryHttpClient();

    public static string GetModelLabel(TranslationModel model) => model switch
    {
        TranslationModel.Google => "Google Translate",
        TranslationModel.MyMemory => "MyMemory (free web)",
        _ => "MyMemory (free web)"
    };

    public static readonly IReadOnlyList<(string Code, string Name)> SupportedLanguages = new[]
    {
        ("auto", "Auto-detect"),
        ("ar", "Arabic"),
        ("az", "Azerbaijani"),
        ("bn", "Bengali"),
        ("ca", "Catalan"),
        ("cs", "Czech"),
        ("da", "Danish"),
        ("de", "German"),
        ("el", "Greek"),
        ("en", "English"),
        ("eo", "Esperanto"),
        ("es", "Spanish"),
        ("fa", "Persian"),
        ("fi", "Finnish"),
        ("fr", "French"),
        ("ga", "Irish"),
        ("hi", "Hindi"),
        ("id", "Indonesian"),
        ("it", "Italian"),
        ("ja", "Japanese"),
        ("ko", "Korean"),
        ("nb", "Norwegian"),
        ("nl", "Dutch"),
        ("pl", "Polish"),
        ("pt", "Portuguese"),
        ("ro", "Romanian"),
        ("ru", "Russian"),
        ("sk", "Slovak"),
        ("sl", "Slovenian"),
        ("sq", "Albanian"),
        ("sr", "Serbian"),
        ("sv", "Swedish"),
        ("th", "Thai"),
        ("tl", "Tagalog"),
        ("tr", "Turkish"),
        ("uk", "Ukrainian"),
        ("ur", "Urdu"),
        ("vi", "Vietnamese"),
        ("zh", "Chinese"),
    };

    public static string GetLanguageName(string code)
    {
        foreach (var (c, n) in SupportedLanguages)
            if (c.Equals(code, StringComparison.OrdinalIgnoreCase)) return n;
        return code;
    }

    public static string ResolveTargetLanguage(string? toCode, string? interfaceLanguageSetting = null, CultureInfo? systemCulture = null)
    {
        var requested = string.IsNullOrWhiteSpace(toCode) ? "auto" : toCode.Trim();
        if (!string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
            return NormalizeSupportedLanguage(requested) ?? "en";

        var preferred = LocalizationService.ResolveContentLanguageCode(interfaceLanguageSetting, systemCulture);
        return NormalizeSupportedLanguage(preferred) ?? "en";
    }

    public static string ResolveSourceLanguage(string? fromCode)
    {
        var requested = string.IsNullOrWhiteSpace(fromCode) ? "auto" : fromCode.Trim();
        if (string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
            return "auto";

        return NormalizeSupportedLanguage(requested) ?? "auto";
    }

    private static string? NormalizeSupportedLanguage(string languageCode)
    {
        var normalized = languageCode.Trim().Replace('_', '-');
        foreach (var (code, _) in SupportedLanguages)
        {
            if (string.Equals(code, normalized, StringComparison.OrdinalIgnoreCase))
                return code;
        }

        var neutral = normalized.Split('-', 2)[0];
        foreach (var (code, _) in SupportedLanguages)
        {
            if (string.Equals(code, neutral, StringComparison.OrdinalIgnoreCase) && code != "auto")
                return code;
        }

        return null;
    }

    // --- Translate ---

    public static async Task<string> TranslateAsync(string text, string fromCode, string toCode, TranslationModel model, CancellationToken cancellationToken = default)
    {
        fromCode = ResolveSourceLanguage(fromCode);
        toCode = ResolveTargetLanguage(toCode);

        if (model == TranslationModel.Google)
        {
            var apiKey = _googleApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Google Translate API key not set. Add it in Settings \u2192 OCR.");

            try
            {
                return await TranslateWithGoogleAsync(text, fromCode, toCode, apiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("translation.google.translate", ex);
                throw;
            }
        }

        // MyMemory
        // MyMemory supports auto-detect via the literal "Autodetect" source code
        // (handled inside TranslateWithMyMemoryAsync).
        try
        {
            return await TranslateWithMyMemoryAsync(text, fromCode, toCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("translation.mymemory.translate", ex);
            throw;
        }
    }

    private static string? _googleApiKey;

    public static void SetGoogleApiKey(string? key)
    {
        _googleApiKey = key;
    }

    public static bool HasGoogleApiKey => !string.IsNullOrWhiteSpace(_googleApiKey);

    public static bool SupportsAutoDetect(TranslationModel model) =>
        model is TranslationModel.Google or TranslationModel.MyMemory;

    public static Task<string?> GetConfigurationErrorAsync(string fromCode, TranslationModel model, CancellationToken cancellationToken = default)
    {
        if (model == TranslationModel.Google)
        {
            return Task.FromResult<string?>(
                string.IsNullOrWhiteSpace(_googleApiKey)
                    ? "Google Translate API key not set. Add it in Config -> OCR."
                    : null);
        }

        // MyMemory — free web API, always ready
        return Task.FromResult<string?>(null);
    }

    public static async Task EnsureReadyAsync(string fromCode, TranslationModel model, CancellationToken cancellationToken = default)
    {
        var error = await GetConfigurationErrorAsync(fromCode, model, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException(error);
    }

    private static async Task<string> TranslateWithGoogleAsync(string text, string fromCode, string toCode, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"language/translate/v2?key={Uri.EscapeDataString(apiKey)}")
        {
            Content = new FormUrlEncodedContent(BuildGoogleForm(text, fromCode, toCode))
        };

        using var response = await GoogleHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractGoogleError(payload) ?? $"Google Translate request failed ({(int)response.StatusCode}).");

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("translations")[0]
            .GetProperty("translatedText")
            .GetString() ?? "";
    }

    private static HttpClient CreateGoogleHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://translation.googleapis.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CyberSnap/1.0");
        return client;
    }

    private static HttpClient CreateMyMemoryHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.mymemory.translated.net/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CyberSnap/1.0");
        return client;
    }

    private static async Task<string> TranslateWithMyMemoryAsync(string text, string fromCode, string toCode, CancellationToken cancellationToken)
    {
        var isAuto = string.Equals(fromCode, "auto", StringComparison.OrdinalIgnoreCase);
        var langPair = isAuto ? $"Autodetect|{toCode}" : $"{fromCode}|{toCode}";

        var url = $"get?q={Uri.EscapeDataString(text)}&langpair={langPair}";

        using var response = await MyMemoryHttp.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"MyMemory translation failed ({(int)response.StatusCode}).");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        int statusCode = 0;
        if (root.TryGetProperty("responseStatus", out var status))
        {
            if (status.ValueKind == JsonValueKind.Number)
                statusCode = status.GetInt32();
            else if (status.ValueKind == JsonValueKind.String && int.TryParse(status.GetString(), out var parsed))
                statusCode = parsed;
        }

        if (statusCode != 200)
        {
            var msg = "MyMemory translation failed.";
            if (root.TryGetProperty("responseDetails", out var details) && details.ValueKind == JsonValueKind.String)
                msg = details.GetString() ?? msg;
            throw new InvalidOperationException(msg);
        }

        if (root.TryGetProperty("responseData", out var data) &&
            data.TryGetProperty("translatedText", out var translated) &&
            translated.ValueKind == JsonValueKind.String)
        {
            var result = translated.GetString() ?? "";
            return System.Net.WebUtility.HtmlDecode(result);
        }

        throw new InvalidOperationException("MyMemory returned an unexpected response.");
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildGoogleForm(string text, string fromCode, string toCode)
    {
        yield return new("q", text);
        yield return new("target", toCode);
        if (!string.Equals(fromCode, "auto", StringComparison.OrdinalIgnoreCase))
            yield return new("source", fromCode);
        yield return new("format", "text");
    }

    private static string? ExtractGoogleError(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var message))
                    return message.GetString();
                return error.ToString();
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
    }
}
