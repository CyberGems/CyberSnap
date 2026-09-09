using System.IO;
using System.Diagnostics;
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
    string StatusMessage,
    string? ReleaseNotes = null);

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
            var releaseNotes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
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
                // Search for the setup installer first, as we need an executable to run the installer
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("CyberSnap-Setup-", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        assetName = name;
                        if (asset.TryGetProperty("sha256", out var sha))
                            assetSha256 = sha.GetString();
                        break;
                    }
                }

                // Fallback to architecture-specific channel (e.g. portable zip) if installer is not found
                if (downloadUrl is null)
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
            }

            // Prefer label-based download as fallback
            if (downloadUrl is null && root.TryGetProperty("zipball_url", out var zip))
                downloadUrl = zip.GetString();

            var isAvailable = latestVersion is not null && latestVersion > currentVersion;
            var status = isAvailable
                ? string.Format(LocalizationService.Translate("Update {0} available"), tagName)
                : string.Format(LocalizationService.Translate("You're up to date on {0}"), currentLabel);

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
                status,
                releaseNotes);
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult(
                currentVersion, null, currentLabel, string.Empty, null, null, null, null, false,
                LocalizationService.Translate("Could not check for updates. Check your internet connection."));
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult(
                currentVersion, null, currentLabel, string.Empty, null, null, null, null, false,
                LocalizationService.Translate("Update check timed out. Try again later."));
        }
        catch (JsonException)
        {
            return new UpdateCheckResult(
                currentVersion, null, currentLabel, string.Empty, null, null, null, null, false,
                LocalizationService.Translate("Unexpected response from update server."));
        }
    }

    public static async Task DownloadUpdateAsync(string downloadUrl, string destinationPath, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var response = await GitHubHttp.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        var totalRead = 0L;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                var percentage = (double)totalRead / totalBytes * 100.0;
                progress.Report(percentage);
            }
        }
    }

    public static void LaunchInstallerAndExit(string installerPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
        };
        Process.Start(psi);
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
    }

    /// <summary>
    /// Generates a clean 3-4 bullet plain-text teaser of the release notes for toasts and compact previews.
    /// Filters out markdown headings, badges, hashes, and download tables (CyberLauncher model).
    /// </summary>
    public static string PeekReleaseNotes(string? body, int maxChars = 220)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var lines = body
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n")
            .Split('\n');

        var extracted = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("---") || line.StartsWith("***") || line.StartsWith("___")) continue;
            if (line.StartsWith("|") || line.StartsWith("<!--") || line.StartsWith("-->")) continue;
            if (line.StartsWith("#")) continue;
            if (line.StartsWith("Welcome to", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Crafted with", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Release Notes", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("VirusTotal", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase)) continue;

            // Top-level bullet points
            if (rawLine.StartsWith("- ") || rawLine.StartsWith("* ") || rawLine.StartsWith("• "))
            {
                var clean = rawLine.Substring(2).Trim();
                clean = clean.Replace("**", "").Replace("__", "").Replace("`", "").TrimEnd(':').Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    extracted.Add("• " + clean);
                    if (extracted.Count >= 4)
                        break;
                }
            }
        }

        // If no top-level bullets found, fall back to any bullet points
        if (extracted.Count == 0)
        {
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("---") || line.StartsWith("***") || line.StartsWith("___") || line.StartsWith("|") || line.StartsWith("#")) continue;
                if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
                {
                    var clean = line.Substring(2).Trim();
                    clean = clean.Replace("**", "").Replace("__", "").Replace("`", "").TrimEnd(':').Trim();
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        extracted.Add("• " + clean);
                        if (extracted.Count >= 4)
                            break;
                    }
                }
            }
        }

        if (extracted.Count == 0)
        {
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("---") || line.StartsWith("|"))
                    continue;
                extracted.Add(line.Replace("**", "").Replace("__", "").Replace("`", ""));
                if (extracted.Count >= 3)
                    break;
            }
        }

        var text = string.Join("\n", extracted);
        if (text.Length <= maxChars)
            return text;

        return text.Substring(0, maxChars).TrimEnd() + "…";
    }
}
