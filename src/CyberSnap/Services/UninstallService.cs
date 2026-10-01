using CyberSnap.Helpers;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CyberSnap.Services;

public static class UninstallService
{
    public static void EnsureStartMenuShortcut()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return;
        if (LooksLikeBuildOutputPath(exe))
            return;

        var programsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs");
        Directory.CreateDirectory(programsDir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            throw new InvalidOperationException("Windows shortcut service is unavailable.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            // Main app shortcut
            var shortcutPath = Path.Combine(programsDir, "CyberSnap.lnk");
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = "CyberSnap screenshot tool";
            shortcut.Save();

            // Editor shortcut
            var editorShortcutPath = Path.Combine(programsDir, "CyberSnap Editor.lnk");
            var legacyEditorShortcutPath = Path.Combine(programsDir, "CyberSnap Annotations Editor.lnk");
            TryDeleteShortcut(legacyEditorShortcutPath);
            dynamic editorShortcut = shell.CreateShortcut(editorShortcutPath);
            editorShortcut.TargetPath = exe;
            editorShortcut.Arguments = "--editor";
            editorShortcut.WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;
            var editorIconPath = WindowIcons.FilePath(WindowIconKind.Editor);
            editorShortcut.IconLocation = File.Exists(editorIconPath) ? editorIconPath : exe + ",0";
            editorShortcut.Description = "CyberSnap Editor";
            editorShortcut.Save();
        }
        finally
        {
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
        }
    }

    public static void RemoveStartMenuShortcut()
    {
        var programsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs");
        var shortcutPath = Path.Combine(programsDir, "CyberSnap.lnk");
        var editorShortcutPath = Path.Combine(programsDir, "CyberSnap Editor.lnk");
        var legacyEditorShortcutPath = Path.Combine(programsDir, "CyberSnap Annotations Editor.lnk");
        try
        {
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
            if (File.Exists(editorShortcutPath))
                File.Delete(editorShortcutPath);
            if (File.Exists(legacyEditorShortcutPath))
                File.Delete(legacyEditorShortcutPath);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "uninstall.shortcut-cleanup",
                $"Failed to delete Start Menu shortcut: {ex.Message}",
                ex);
        }
    }

    public static void RegisterInstalledAppEntry()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return;
        if (LooksLikeBuildOutputPath(exe))
            return;

        var installDir = GetInstallDirectory();
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        var version = v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

        using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CyberSnap");
        if (key is null)
            throw new InvalidOperationException("Windows uninstall registry key could not be opened.");

        key.SetValue("DisplayName", "CyberSnap", RegistryValueKind.String);
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        key.SetValue("Publisher", "CyberGems", RegistryValueKind.String);
        key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
        key.SetValue("DisplayIcon", exe, RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall", RegistryValueKind.String);
        key.SetValue("URLInfoAbout", "", RegistryValueKind.String);
        key.SetValue("URLUpdateInfo", "", RegistryValueKind.String);
        key.SetValue("HelpLink", "", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
        TrySetEstimatedSize(key, installDir);
    }

    public static void RemoveInstalledAppEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
        if (key is null)
        {
            AppDiagnostics.LogWarning(
                "uninstall.registry-cleanup",
                "Windows uninstall registry parent key could not be opened.");
            return;
        }

        key.DeleteSubKeyTree("CyberSnap", throwOnMissingSubKey: false);
    }

    public static string GetInstallDirectory()
    {
        var exe = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(exe) ? "" : Path.GetDirectoryName(exe) ?? "";
    }

    public static void RemoveStartupEntry()
    {
        RemoveStartupShortcut();

        const string rk = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.OpenSubKey(rk, writable: true);
        if (key is null)
        {
            AppDiagnostics.LogWarning(
                "uninstall.startup-cleanup",
                "Windows startup registry key could not be opened.");
            return;
        }

        key.DeleteValue("CyberSnap", throwOnMissingValue: false);
    }

    /// <summary>
    /// One startup registration only: the Run key. The installer used to also drop a
    /// Startup-folder shortcut, and a dev build used to retarget Run at bin\Debug.
    /// Both showed up as "CyberSnap" and Windows launched them together.
    /// </summary>
    public static void SetStartupEntry(bool enabled)
    {
        var shortcutTarget = TryReadStartupShortcutTarget();
        RemoveStartupShortcut();

        const string rk = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.CreateSubKey(rk);
        if (key is null)
            throw new InvalidOperationException("Windows startup registry key could not be opened.");

        if (!enabled)
        {
            key.DeleteValue("CyberSnap", throwOnMissingValue: false);
            return;
        }

        var exe = ResolveStartupExecutable(shortcutTarget);
        var desired = $"\"{exe}\"";
        var existing = key.GetValue("CyberSnap") as string;
        if (string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
            return;

        key.SetValue("CyberSnap", desired, RegistryValueKind.String);
        var current = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(current)
            && LooksLikeBuildOutputPath(current)
            && !LooksLikeBuildOutputPath(exe))
        {
            AppDiagnostics.LogWarning(
                "startup.entry",
                $"Launch on startup stays on the installed copy: {exe}");
        }
    }

    /// <summary>Deletes the installer Startup-folder shortcut so it cannot launch a second copy.</summary>
    public static void RemoveStartupShortcut()
    {
        try
        {
            var path = StartupShortcutPath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("startup.remove-shortcut", ex.Message, ex);
        }
    }

    private static string ResolveStartupExecutable(string? shortcutTarget)
    {
        var current = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(current)
            && File.Exists(current)
            && !LooksLikeBuildOutputPath(current))
            return current;

        foreach (var candidate in EnumerateInstalledExecutables(shortcutTarget))
        {
            if (File.Exists(candidate) && !LooksLikeBuildOutputPath(candidate))
                return candidate;
        }

        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
            return current;

        throw new InvalidOperationException("CyberSnap could not resolve its executable path for startup.");
    }

    private static IEnumerable<string> EnumerateInstalledExecutables(string? shortcutTarget)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? location = null;
                string? icon = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CyberSnap");
                    location = key?.GetValue("InstallLocation") as string;
                    icon = key?.GetValue("DisplayIcon") as string;
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(location))
                    yield return Path.Combine(location, "CyberSnap.exe");
                if (!string.IsNullOrWhiteSpace(icon))
                {
                    var exe = icon.Split(',')[0].Trim().Trim('"');
                    if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        yield return exe;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(shortcutTarget))
            yield return shortcutTarget.Trim().Trim('"');

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "CyberSnap",
            "CyberSnap.exe");
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "CyberSnap", "CyberSnap.exe");
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "CyberSnap",
            "CyberSnap.exe");
    }

    private static string StartupShortcutPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CyberSnap.lnk");

    private static string? TryReadStartupShortcutTarget()
    {
        try
        {
            var path = StartupShortcutPath();
            if (!File.Exists(path))
                return null;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(path);
                string target = shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("startup.read-shortcut", ex.Message, ex);
            return null;
        }
    }

    public static void RemoveAppData()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyberSnap");
        // Gallery data now lives under AppData\CyberSnap\gallery (deleted with appData).
        // Do not create or repopulate any "History" folder on uninstall.
        RemoveRuntimeCaches();
        TryDeleteDirectory(appData);
    }

    public static void RemoveRuntimeCaches()
    {
    }

    public static void ScheduleInstallFolderRemoval()
    {
        var dir = GetInstallDirectory();
        if (!IsSafeInstallDirectoryForRemoval(dir))
            return;

        var cmd = $"timeout /t 2 /nobreak >nul & rmdir /s /q \"{dir}\"";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {cmd}",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
                AppDiagnostics.LogWarning(
                    "uninstall.folder-removal",
                    $"Windows did not start install folder removal for {Path.GetFileName(dir)}.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "uninstall.folder-removal",
                $"Failed to schedule install folder removal for {Path.GetFileName(dir)}: {ex.Message}",
                ex);
        }
    }

    private static void TryDeleteShortcut(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "uninstall.shortcut-cleanup",
                $"Failed to delete shortcut {Path.GetFileName(path)}: {ex.Message}",
                ex);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "uninstall.cleanup",
                $"Failed to delete uninstall cleanup directory {Path.GetFileName(path)}: {ex.Message}",
                ex);
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destinationDir, relative);
            var destFolder = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destFolder))
                Directory.CreateDirectory(destFolder);

            try
            {
                File.Copy(file, destPath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogWarning(
                    "uninstall.history-migration",
                    $"Failed to copy legacy history file {Path.GetFileName(file)}: {ex.Message}",
                    ex);
            }
        }
    }

    private static bool LooksLikeBuildOutputPath(string path) => InstallService.LooksLikeBuildOutputPath(path);

    private static void TrySetEstimatedSize(RegistryKey key, string installDir)
    {
        try
        {
            long totalBytes = 0;
            foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    totalBytes += new FileInfo(file).Length;
                }
                catch (Exception ex)
                {
                    AppDiagnostics.LogWarning(
                        "startup.register-installed-entry.estimated-size",
                        $"Failed to read installed file size for {Path.GetFileName(file)}: {ex.Message}",
                        ex);
                }
            }

            key.SetValue("EstimatedSize", (int)Math.Max(1, totalBytes / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "startup.register-installed-entry.estimated-size",
                $"Failed to estimate installed app size for {Path.GetFileName(installDir)}: {ex.Message}",
                ex);
        }
    }

    private static bool IsSafeInstallDirectoryForRemoval(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;

        var fullPath = Path.GetFullPath(dir);
        if (LooksLikeBuildOutputPath(fullPath))
            return false;

        var expectedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "CyberSnap"));

        return string.Equals(fullPath, expectedRoot, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(Path.Combine(fullPath, "CyberSnap.exe"));
    }
}
