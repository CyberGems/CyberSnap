using Xunit;

namespace CyberSnap.Tests;

public sealed class AppLifecyclePolishTests
{
    [Fact]
    public void SettingsOpenFailuresShowRecoveryCopy()
    {
        var source = File.ReadAllText(RepoPath("src", "CyberSnap", "App", "App.Lifecycle.cs"));

        var showSettingsBlock = GetMethodBlock(source, "public void ShowSettings()");
        Assert.Contains("ShowSettingsOpenFailed(ex, \"lifecycle.show-settings\", \"lifecycle.show-settings.toast\");", showSettingsBlock);
        Assert.Contains("ShowSettingsOpenFailed(ex, \"lifecycle.show-settings.init\", \"lifecycle.show-settings.init.toast\");", showSettingsBlock);
        Assert.DoesNotContain("ToastWindow.ShowError(\"Settings failed to open\", ex.Message);", showSettingsBlock);

        var failureBlock = GetMethodBlock(source, "private static void ShowSettingsOpenFailed(Exception ex, string diagnosticKey, string toastDiagnosticKey)");
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", failureBlock);
        Assert.Contains("CyberSnap could not open Settings. Try again from the tray menu, or restart CyberSnap if it keeps failing.", failureBlock);
        Assert.Contains("{ex.Message}", failureBlock);
        Assert.Contains("catch (Exception toastEx)", failureBlock);
        Assert.Contains("AppDiagnostics.LogError(toastDiagnosticKey, toastEx);", failureBlock);
    }

    [Fact]
    public void UninstallCancellationLeavesRecoverableFeedback()
    {
        var source = File.ReadAllText(RepoPath("src", "CyberSnap", "App", "App.Lifecycle.cs"));

        var uninstallBlock = GetMethodBlock(source, "private void BeginUninstall()");
        Assert.Contains("ThemedConfirmDialog.Confirm(", uninstallBlock);
        Assert.Contains("_settingsWindow?.ShowUninstallCanceledStatus();", uninstallBlock);
        Assert.Contains("ToastWindow.Show(\"Uninstall canceled\", \"CyberSnap was left installed.\");", uninstallBlock);
        Assert.Contains("return;", uninstallBlock);
    }

    [Fact]
    public void StartupCanOpenSettingsForVisualTesting()
    {
        var source = File.ReadAllText(RepoPath("src", "CyberSnap", "App", "App.Startup.cs"));
        var startupBlock = GetMethodBlock(source, "protected override void OnStartup(StartupEventArgs e)");

        Assert.Contains("openSettingsOnStartup", startupBlock);
        Assert.Contains("a.Equals(\"--settings\", StringComparison.OrdinalIgnoreCase)", startupBlock);
        Assert.Contains("a.Equals(\"/settings\", StringComparison.OrdinalIgnoreCase)", startupBlock);
        Assert.Contains("if (openSettingsAfterWizard || openSettingsOnStartup)", startupBlock);
        Assert.Contains("ShowSettings();", startupBlock);
    }

    private static string GetMethodBlock(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method: {signature}");

        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart > start, $"Could not find method body: {signature}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not read method body: {signature}");
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find repo file: {Path.Combine(parts)}");
    }
}
