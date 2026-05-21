using Xunit;

namespace CyberSnap.Tests;

public sealed class WinUISettingsShellTests
{
    [Fact]
    public void SettingsSchemaRendererUsesRealWinUiControls()
    {
        var source = File.ReadAllText(RepoPath("src", "CyberSnap.WinUI", "Views", "MainPage.xaml.cs"));

        Assert.Contains("using Microsoft.UI.Xaml.Controls;", source);
        Assert.Contains("SettingsValueKind.Toggle => BuildToggleSwitch(item)", source);
        Assert.Contains("private static ToggleSwitch BuildToggleSwitch(SettingDefinition item)", source);
        Assert.Contains("new ComboBox", source);
        Assert.Contains("private static TextBox BuildTextBox(SettingDefinition item)", source);
        Assert.Contains("private static NumberBox BuildNumberBox(SettingDefinition item)", source);
        Assert.Contains("private static Button BuildActionButton(SettingDefinition item)", source);
        Assert.DoesNotContain("Text = $\"Kind: {item.ValueKind}\"", source);
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

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
