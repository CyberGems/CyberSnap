using CyberSnap.Services;
using Xunit;

namespace CyberSnap.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsFailureResultWhenExecutableIsMissing()
    {
        var missingExe = "CyberSnap-missing-" + Guid.NewGuid().ToString("N") + ".exe";

        var result = await ProcessRunner.RunAsync(
            missingExe,
            [],
            CancellationToken.None,
            startFailureMessage: "Missing helper executable.");

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Contains("Missing helper executable.", result.StdErr);
    }
}
