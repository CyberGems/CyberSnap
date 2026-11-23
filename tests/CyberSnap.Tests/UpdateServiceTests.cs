using System.Text.Json;
using CyberSnap.Services;

namespace CyberSnap.Tests;

public class UpdateServiceTests
{
    private const string Tag = "v2.3.4";
    private const string Setup = "CyberSnap-Setup-v2.3.4.exe";
    private const string Portable = "CyberSnap-v2.3.4-Portable-win-x64.zip";

    [Theory]
    [InlineData(false, Setup)]
    [InlineData(true, Portable)]
    public void SelectsOnlyThePackageForTheRunningEdition(bool portable, string expected)
    {
        var update = UpdateService.ReadRelease(Release(Setup, Portable), new Version(2, 3, 3), portable, "win-x64");

        Assert.True(update.IsUpdateAvailable);
        Assert.Equal(expected, update.Asset?.Name);
        Assert.Equal(portable, update.Asset?.IsPortable);
        Assert.Equal(new string('a', 64), update.Asset?.Sha256);
    }

    [Theory]
    [InlineData(true, Setup)]
    [InlineData(false, Portable)]
    [InlineData(true, "CyberSnap-v2.3.4-Portable-win-arm64.zip")]
    public void KeepsTheReleaseAvailableWithoutDownloadingAnIncompatiblePackage(bool portable, string name)
    {
        var update = UpdateService.ReadRelease(Release(name), new Version(2, 3, 3), portable, "win-x64");

        Assert.True(update.IsUpdateAvailable);
        Assert.Null(update.Asset);
        Assert.EndsWith($"/releases/tag/{Tag}", update.ReleaseUrl);
    }

    [Fact]
    public void MatchesAnInstalledVersionWithAnImplicitZeroRevision()
    {
        var update = UpdateService.ReadRelease(Release(Setup), new Version(2, 3, 4), false, "win-x64");

        Assert.False(update.IsUpdateAvailable);
    }

    [Fact]
    public void NotesExcludeTheOtherLanguageAndInstallerInstructions()
    {
        const string notes = """
            ### Highlights
            - **Fast** capture
            <details><summary>More details</summary>
            - Unrelated item
            </details>
            <details open><summary>Español</summary>
            ### Novedades
            - Captura **rápida**
            ### ℹ️ Antes de instalar
            - Instrucciones del instalador
            </details>
            ### ℹ️ Before you install
            - Installer instructions
            """;

        Assert.Equal("Fast capture", UpdateService.PeekReleaseNotes(notes, "en"));
        Assert.Equal("Captura rápida", UpdateService.PeekReleaseNotes(notes, "es-CR"));
    }

    private static string Release(params string[] names) => JsonSerializer.Serialize(new
    {
        tag_name = Tag,
        assets = names.Select(name => new
        {
            name,
            browser_download_url = $"https://github.com/CyberGems/CyberSnap/releases/download/{Tag}/{name}",
            size = 100,
            digest = $"sha256:{new string('a', 64)}"
        })
    });
}
