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
    public void PickInstallerAsset_PrefersSetupExe_AndNeverReturnsZip()
    {
        var assets = new List<(string Name, string Url)>
        {
            ("WinOptimizer-win-x64.zip", "https://github.com/reikidod/WinOptimazer/releases/download/v1/WinOptimizer-win-x64.zip"),
            ("WinOptimizer-Setup.exe", "https://github.com/reikidod/WinOptimazer/releases/download/v1/WinOptimizer-Setup.exe"),
            ("checksums.txt", "https://github.com/reikidod/WinOptimazer/releases/download/v1/checksums.txt"),
        };

        Assert.Equal(
            "https://github.com/reikidod/WinOptimazer/releases/download/v1/WinOptimizer-Setup.exe",
            UpdateService.PickInstallerAsset(assets));
    }

    [Fact]
    public void PickInstallerAsset_ReturnsNullWhenReleaseHasNoExe()
    {
        var assets = new List<(string Name, string Url)>
        {
            ("WinOptimizer-win-x64.zip", "https://github.com/reikidod/WinOptimazer/releases/download/v1/WinOptimizer-win-x64.zip"),
        };

        Assert.Null(UpdateService.PickInstallerAsset(assets));
    }

    [Fact]
    public async Task CheckAsync_PropagatesCancellationInsteadOfReportingNetworkFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new UpdateService().CheckAsync(cancellation.Token));
    }

    [Fact]
    public void PickRelease_BetaSelectsHighestVersionNotFirstApiResult()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            [
              { "tag_name": "v0.6.189-beta", "prerelease": true, "draft": false },
              { "tag_name": "v0.6.193-beta", "prerelease": true, "draft": false },
              { "tag_name": "v0.6.192-beta", "prerelease": true, "draft": false }
            ]
            """);

        var release = UpdateService.PickRelease(document.RootElement, beta: true);

        Assert.Equal("v0.6.193-beta", release?.GetProperty("tag_name").GetString());
    }

    [Fact]
    public void PickRelease_StableChannelIgnoresNewerBetaAndSelectsHighestStable()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            [
              { "tag_name": "v0.6.193-beta", "prerelease": true, "draft": false },
              { "tag_name": "v0.4.190", "prerelease": false, "draft": false },
              { "tag_name": "v0.4.208", "prerelease": false, "draft": false }
            ]
            """);

        var release = UpdateService.PickRelease(document.RootElement, beta: false);

        Assert.Equal("v0.4.208", release?.GetProperty("tag_name").GetString());
    }

    [Fact]
    public void IsUpdate_DoesNotOfferTheSameInstalledVersionAgain()
    {
        Assert.False(UpdateService.IsUpdate(
            "0.6.193", AppSettings.BetaChannel, "0.6.193", AppSettings.BetaChannel, betaSelected: true));
    }

    [Fact]
    public void IsUpdate_AllowsSwitchFromBetaToStableAtSameVersionNumber()
    {
        Assert.True(UpdateService.IsUpdate(
            "0.6.193", AppSettings.BetaChannel, "0.6.193", AppSettings.StableChannel, betaSelected: false));
    }

    [Fact]
    public void CurrentVersionCanBeReadFromTheDisplayedBetaVersionLabel()
    {
        Assert.Equal("0.6.193", UpdateService.NormalizeVersion("0.6.193-beta"));
    }

}
