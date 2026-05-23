using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CyberSnap.Services;

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version? LatestVersion,
    string LatestVersionLabel,
    string ReleaseUrl,
    string? DownloadUrl,
    string? AssetName,
    string? AssetSha256,
    DateTimeOffset? PublishedAt,
    bool IsUpdateAvailable,
    string StatusMessage);

public static class UpdateService
{
    public static Version GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? new Version(0, 0, 0)
            : new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    public static string GetCurrentVersionLabel()
    {
        var v = GetCurrentVersion();
        return v.Revision > 0 ? $"v{v}" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public static string GetRuntimeChannel() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.X86 => "win-x86",
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64"
    };

    private static readonly HttpClient GitHubHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static UpdateService()
    {
        GitHubHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CyberSnap");
        GitHubHttp.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();
        var currentLabel = GetCurrentVersionLabel();

        try
        {
            var json = await GitHubHttp.GetStringAsync(
                "https://api.github.com/repos/CyberGems/CyberSnap/releases/latest",
                cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var releaseUrl = root.GetProperty("html_url").GetString() ?? "";
            DateTimeOffset? publishedAt = root.TryGetProperty("published_at", out var pub) ? pub.GetDateTimeOffset() : null;

            var latestVersionStr = tagName.TrimStart('v');
            if (!Version.TryParse(latestVersionStr, out var latestVersion))
                latestVersion = null;

            // Try to find download asset for current architecture
            string? downloadUrl = null;
            string? assetName = null;
            string? assetSha256 = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                var channel = GetRuntimeChannel();
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Contains(channel, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        assetName = name;
                        if (asset.TryGetProperty("sha256", out var sha))
                            assetSha256 = sha.GetString();
                        break;
                    }
                }
            }

            // Prefer label-based download as fallback
            if (downloadUrl is null && root.TryGetProperty("zipball_url", out var zip))
                downloadUrl = zip.GetString();

            var isAvailable = latestVersion is not null && latestVersion > currentVersion;
            var status = isAvailable
                ? $"Update {tagName} available"
                : $"You're up to date on {currentLabel}";

            return new UpdateCheckResult(
                currentVersion,
                latestVersion,
                tagName,
                releaseUrl,
                downloadUrl,
                assetName,
                assetSha256,
                publishedAt,
                isAvailable,
                status);
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult(
                currentVersion, null, currentLabel, string.Empty, null, null, null, null, false,
                "Could not check for updates. Check your internet connection.");
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult(
                currentVersion, null, currentLabel, string.Empty, null, null, null, null, false,
                "Update check timed out. Try again later.");
        }
        catch (JsonException)
        {
            return new UpdateCheckResult(
                currentVersion, null, currentLabel, string.Empty, null, null, null, null, false,
                "Unexpected response from update server.");
        }
    }
}
