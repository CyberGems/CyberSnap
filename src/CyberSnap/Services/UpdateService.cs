using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberSnap.Services;

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version? LatestVersion,
    string LatestVersionLabel,
    string ReleaseUrl,
    UpdateAsset? Asset,
    DateTimeOffset? PublishedAt,
    string? ReleaseNotes,
    string? ErrorKey = null)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;

    public string GetStatusMessage() => ErrorKey is not null
        ? LocalizationService.Translate(ErrorKey)
        : IsUpdateAvailable
            ? LocalizationService.Translate("Update available")
            : LocalizationService.Translate("CyberSnap is up to date");
}

public sealed record UpdateAsset(string Name, Uri Url, long? Size, string? Sha256, bool IsPortable);
public readonly record struct UpdateDownloadProgress(long Downloaded, long? Total);

public static class UpdateService
{
    private const string ReleasePageBase = "https://github.com/CyberGems/CyberSnap/releases";
    private const string AssetPathBase = "/CyberGems/CyberSnap/releases/download/";

    private static readonly HttpClient GitHubHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static UpdateService()
    {
        GitHubHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CyberSnap");
        GitHubHttp.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version GetCurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static string GetCurrentVersionLabel()
    {
        var version = GetCurrentVersion();
        return version.Revision > 0 ? $"v{version}" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string GetRuntimeChannel() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.X86 => "win-x86",
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64"
    };

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var current = GetCurrentVersion();
        try
        {
            var json = await GitHubHttp.GetStringAsync(
                "https://api.github.com/repos/CyberGems/CyberSnap/releases/latest").ConfigureAwait(false);
            return ReadRelease(json, current, !InstallService.IsInstalled(), GetRuntimeChannel());
        }
        catch (HttpRequestException ex)
        {
            AppDiagnostics.LogWarning("updates.check", ex.Message, ex);
            return CheckFailed(current, "Could not check for updates. Check your internet connection.");
        }
        catch (OperationCanceledException)
        {
            return CheckFailed(current, "Update check timed out. Try again later.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            AppDiagnostics.LogWarning("updates.check.response", ex.Message, ex);
            return CheckFailed(current, "Unexpected response from update server.");
        }
    }

    private static UpdateCheckResult CheckFailed(Version current, string message) =>
        new(current, null, "", "", null, null, null, message);

    internal static UpdateCheckResult ReadRelease(string json, Version current, bool portable, string channel)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = ReadString(root, "tag_name");
        if (tag is null || !Regex.IsMatch(tag, @"\A[vV]?\d+(?:\.\d+){1,3}\z") ||
            !Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
            return CheckFailed(current, "Unexpected response from update server.");

        static Version Normalize(Version version) =>
            new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
        var latest = Normalize(parsed);
        var releaseUrl = $"{ReleasePageBase}/tag/{Uri.EscapeDataString(tag)}";
        var expected = portable ? $"CyberSnap-{tag}-Portable-{channel}.zip" : $"CyberSnap-Setup-{tag}.exe";
        UpdateAsset? asset = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assets.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(item, "name"), expected, StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(ReadString(item, "browser_download_url"), UriKind.Absolute, out var url) ||
                    url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.IsDefaultPort ||
                    url.UserInfo.Length != 0 || !string.Equals(Uri.UnescapeDataString(url.AbsolutePath),
                        AssetPathBase + tag + "/" + expected, StringComparison.OrdinalIgnoreCase))
                    continue;

                var size = item.TryGetProperty("size", out var value) && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out var length) && length > 0 ? length : (long?)null;
                var digest = ReadString(item, "digest");
                var sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                    ? digest["sha256:".Length..]
                    : ReadString(item, "sha256");
                asset = new UpdateAsset(expected, url, size, sha, portable);
                break;
            }
        }

        DateTimeOffset? publishedAt = root.TryGetProperty("published_at", out var date) &&
            date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;
        return new UpdateCheckResult(Normalize(current), latest, tag, releaseUrl, asset, publishedAt, ReadString(root, "body"));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static async Task<string> DownloadUpdateAsync(
        UpdateAsset asset, string folder, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, asset.Name);
        var partial = $"{destination}.{Guid.NewGuid():N}.part";

        try
        {
            using var response = await GitHubHttp.GetAsync(asset.Url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = asset.Size ?? response.Content.Headers.ContentLength;
            long downloaded = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                var lastReport = Stopwatch.StartNew();
                progress.Report(new UpdateDownloadProgress(0, total));
                while (true)
                {
                    int count = await content.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    downloaded += count;
                    if (total is long expected && downloaded > expected)
                        throw new InvalidDataException("Update size exceeds GitHub release metadata.");
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (lastReport.ElapsedMilliseconds >= 120)
                    {
                        progress.Report(new UpdateDownloadProgress(downloaded, total));
                        lastReport.Restart();
                    }
                }
            }

            if (downloaded == 0 || total is long size && downloaded != size)
                throw new InvalidDataException("Update is empty or incomplete.");
            if (asset.Sha256 is not null &&
                !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update checksum does not match GitHub release metadata.");

            await using (var file = File.OpenRead(partial))
            {
                var header = new byte[4];
                await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                if (asset.IsPortable ? !header.AsSpan().SequenceEqual("PK\u0003\u0004"u8) : !header.AsSpan(0, 2).SequenceEqual("MZ"u8))
                    throw new InvalidDataException("The release asset is not in the expected format.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            progress.Report(new UpdateDownloadProgress(downloaded, total ?? downloaded));
            return destination;
        }
        finally
        {
            try { File.Delete(partial); }
            catch (IOException ex) { AppDiagnostics.LogWarning("updates.cleanup", ex.Message, ex); }
            catch (UnauthorizedAccessException ex) { AppDiagnostics.LogWarning("updates.cleanup", ex.Message, ex); }
        }
    }

    public static void LaunchInstaller(string installerPath)
    {
        var psi = new ProcessStartInfo(installerPath)
        {
            Arguments = "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
        };
        using var installer = Process.Start(psi) ?? throw new InvalidOperationException("The installer did not start.");
    }

    /// <summary>Returns up to four highlights in the interface language.</summary>
    public static string PeekReleaseNotes(string? body, string language)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        const string detailsPattern = @"<details\b[^>]*>\s*<summary\b[^>]*>(?<label>.*?)</summary>(?<notes>.*?)</details>";
        const RegexOptions html = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        var spanish = language.StartsWith("es", StringComparison.OrdinalIgnoreCase)
            ? Regex.Matches(body, detailsPattern, html).FirstOrDefault(block =>
                Regex.IsMatch(block.Groups["label"].Value, "Español|Spanish", RegexOptions.IgnoreCase))
            : null;
        var text = spanish?.Groups["notes"].Value ?? Regex.Replace(body, detailsPattern, "", html);
        text = Regex.Replace(text, @"<!--.*?-->|<p\b[^>]*>.*?</p>|```.*?```", "", html);
        text = Regex.Replace(text,
            @"^#{2,6}[^\S\r\n]+[^\r\n]*?(?:Before you install|Antes de instalar|Downloads|Descargas|VirusTotal|Checksums|Sumas de verificación)[^\r\n]*(?:\r?\n|\z).*?(?=^#{1,6}[^\S\r\n]|\z)",
            "", html | RegexOptions.Multiline);

        var highlights = new List<string>();
        var paragraphs = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '|' or '>' or '<' ||
                line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("***", StringComparison.Ordinal) ||
                line.StartsWith("___", StringComparison.Ordinal) || line.Contains("Full changelog:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Crafted with precision", StringComparison.OrdinalIgnoreCase))
                continue;

            var isBullet = line.Length > 2 && line[1] == ' ' && line[0] is '-' or '*' or '+' or '•';
            var content = isBullet ? line[2..] : line;
            content = Regex.Replace(content, @"\[([^\]]+)\]\([^)]*\)", "$1");
            content = content.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
            int prefixLength = 0;
            foreach (var rune in content.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(rune) && Rune.GetUnicodeCategory(rune) is not
                    (UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.NonSpacingMark or UnicodeCategory.Format))
                    break;
                prefixLength += rune.Utf16SequenceLength;
            }
            content = content[prefixLength..];
            if (content.Length == 0) continue;
            if (isBullet) highlights.Add(content);
            else paragraphs.Add(content);
        }

        return string.Join("\n", (highlights.Count == 0 ? paragraphs : highlights).Distinct().Take(4));
    }
}
