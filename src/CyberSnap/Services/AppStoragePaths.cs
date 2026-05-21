using System.IO;

namespace CyberSnap.Services;

internal static class AppStoragePaths
{
    private static readonly string RoamingCyberSnapDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CyberSnap");

    public static string SettingsPath => Path.Combine(GetStorageDirectory(), "settings.json");
    public static string LogDirectory => Path.Combine(GetStorageDirectory(), "logs");

    public static string ResolveSettingsPath(string? explicitSettingsPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitSettingsPath))
            return Path.GetFullPath(explicitSettingsPath);

        return Path.GetFullPath(SettingsPath);
    }

    internal static string ResolveStorageDirectory(string? runningDirectory, bool isInstalled)
    {
        if (isInstalled || string.IsNullOrWhiteSpace(runningDirectory))
            return RoamingCyberSnapDirectory;

        return Path.Combine(Path.GetFullPath(runningDirectory), "CyberSnap");
    }

    private static string GetStorageDirectory() =>
        ResolveStorageDirectory(InstallService.GetRunningAppDirectory(), InstallService.IsInstalled());
}
