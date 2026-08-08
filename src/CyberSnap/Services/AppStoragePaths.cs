using System.IO;

namespace CyberSnap.Services;

internal static class AppStoragePaths
{
    private static readonly string RoamingCyberSnapDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CyberSnap");

    public static string SettingsPath => Path.Combine(GetStorageDirectory(), "settings.json");
    public static string LogDirectory => Path.Combine(GetStorageDirectory(), "logs");

    /// <summary>
    /// Gallery index, thumbnails, and search DB. Never named "History" —
    /// capture files themselves live only in the user-configured save folder.
    /// </summary>
    public static string GalleryDataDirectory => Path.Combine(GetStorageDirectory(), "gallery");

    public static string ResolveSettingsPath(string? explicitSettingsPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitSettingsPath))
            return Path.GetFullPath(explicitSettingsPath);

        return Path.GetFullPath(SettingsPath);
    }

    /// <summary>
    /// Single unified storage location: always Roaming AppData, whether the app runs
    /// installed, portable, or from a dev build. Prevents diverging settings/achievement
    /// profiles when alternating between dev and installed copies.
    /// </summary>
    internal static string ResolveStorageDirectory(string? runningDirectory, bool isInstalled)
        => RoamingCyberSnapDirectory;

    private static string GetStorageDirectory() =>
        ResolveStorageDirectory(InstallService.GetRunningAppDirectory(), InstallService.IsInstalled());
}
