using System;
using System.Threading.Tasks;
using Xunit;
using CyberSnap.Services;

namespace CyberSnap.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void GetCurrentVersion_ReturnsVersionMatchesAssembly()
    {
        var version = UpdateService.GetCurrentVersion();
        Assert.NotNull(version);
        Assert.Equal(new Version(1, 5, 1, 0), version);
    }

    [Fact]
    public void GetCurrentVersionLabel_ReturnsFormattedLabel()
    {
        var label = UpdateService.GetCurrentVersionLabel();
        Assert.Equal("v1.5.1", label);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ConnectsToGitHubAndParsesLatestRelease()
    {
        var result = await UpdateService.CheckForUpdatesAsync();
        Assert.NotNull(result);
        Assert.NotNull(result.CurrentVersion);
        Assert.NotNull(result.StatusMessage);

        if (result.LatestVersion != null)
        {
            // GitHub currently has v1.4.1 as the latest release tag.
            // Under local version 1.5.1, there should not be any update available.
            Assert.Equal(new Version(1, 5, 0), result.LatestVersion);
            Assert.Equal("v1.5.0", result.LatestVersionLabel);
            Assert.False(result.IsUpdateAvailable);
            Assert.Contains("up to date", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
    }
}
