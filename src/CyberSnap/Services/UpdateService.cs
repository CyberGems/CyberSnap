using System.Reflection;
using System.Runtime.InteropServices;

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

    public static Task<UpdateCheckResult> CheckForUpdatesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();
        var result = new UpdateCheckResult(
            currentVersion,
            null,
            GetCurrentVersionLabel(),
            string.Empty,
            null,
            null,
            null,
            null,
            false,
            $"You're up to date on {GetCurrentVersionLabel()}");
        return Task.FromResult(result);
    }
}
