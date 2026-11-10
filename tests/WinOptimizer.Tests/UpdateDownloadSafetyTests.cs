using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public class UpdateDownloadSafetyTests
{
    [Theory]
    [InlineData("https://github.com/reikidod/WinOptimazer/releases/download/v0.6.167-beta/WinOptimizer-win-x64.zip", true)]
    [InlineData("https://github.com/reikidod/WinOptimazer/releases/download/v0.6.167-beta/WinOptimizer-Setup.exe", true)]
    [InlineData("http://github.com/reikidod/WinOptimazer/releases/download/v0.6.167-beta/update.zip", false)]
    [InlineData("https://example.com/reikidod/WinOptimazer/releases/download/v0.6.167-beta/update.zip", false)]
    [InlineData("https://github.com/attacker/WinOptimazer/releases/download/v0.6.167-beta/update.zip", false)]
    [InlineData("https://github.com/reikidod/OtherProject/releases/download/v0.6.167-beta/update.zip", false)]
    [InlineData("https://github.com/reikidod/WinOptimazer/releases/tag/v0.6.167-beta", false)]
    [InlineData("https://github.com:444/reikidod/WinOptimazer/releases/download/v0.6.167-beta/update.zip", false)]
    [InlineData("https://user@github.com/reikidod/WinOptimazer/releases/download/v0.6.167-beta/update.zip", false)]
    [InlineData("file:///C:/Temp/update.zip", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void IsTrustedDownloadUrl_OnlyAcceptsThisRepositoriesHttpsReleaseAssets(
        string? url, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsTrustedDownloadUrl(url));
    }
    [Fact]
    public async Task CheckAsync_PropagatesCancellationInsteadOfReportingNetworkFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new UpdateService().CheckAsync(cancellation.Token));
    }

}
