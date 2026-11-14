using WinOptimizer.Infrastructure;
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
    public void PickUpdateArchiveAsset_PrefersZipEvenWhenInstallerAssetExists()
    {
        var assets = new List<(string Name, string Url)>
        {
            ("WinOptimizer-Setup.exe", "https://github.com/reikidod/WinOptimazer/releases/download/v1/WinOptimizer-Setup.exe"),
            ("WinOptimizer-win-x64.zip", "https://github.com/reikidod/WinOptimazer/releases/download/v1/WinOptimizer-win-x64.zip"),
        };

        Assert.Equal(
            "https://github.com/reikidod/WinOptimazer/releases/download/v1/WinOptimizer-win-x64.zip",
            UpdateService.PickUpdateArchiveAsset(assets));
    }

    [Fact]
    public void PickUpdateArchiveAsset_DoesNotFallbackToSetupExe()
    {
        var assets = new List<(string Name, string Url)>
        {
            ("WinOptimizer-Setup.exe", "https://github.com/reikidod/WinOptimazer/releases/download/v1/WinOptimizer-Setup.exe"),
        };

        Assert.Null(UpdateService.PickUpdateArchiveAsset(assets));
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
    public void PickRelease_BetaRecognizesLetteredPatchAsNewer()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            [
              { "tag_name": "v0.7.7-beta", "prerelease": true, "draft": false },
              { "tag_name": "v0.7.7a-beta", "prerelease": true, "draft": false },
              { "tag_name": "v0.7.6-beta", "prerelease": true, "draft": false }
            ]
            """);

        var release = UpdateService.PickRelease(document.RootElement, beta: true);

        Assert.Equal("v0.7.7a-beta", release?.GetProperty("tag_name").GetString());
    }

    [Fact]
    public void NormalizeVersion_ParsesLetteredPatchAndBuildChannel()
    {
        Assert.Equal("0.7.7a", UpdateService.NormalizeVersion("v0.7.7a-beta"));
        Assert.Equal("0.7.7a", UpdateService.NormalizeVersion("0.7.7-a-beta"));
        Assert.Equal("0.7.7", UpdateService.NormalizeVersion("0.7.7-beta"));
        Assert.Equal("0.7.7a", UpdateService.NormalizeVersion("0.7.7-a-dev"));
        Assert.Equal("0.7.7", UpdateService.NormalizeVersion("0.7.7-stable"));
    }

    [Fact]
    public void CompareVersions_LetteredPatchIsNewerThanBaseVersion()
    {
        Assert.True(UpdateService.CompareVersions("0.7.7a", "0.7.7") > 0);
        Assert.True(UpdateService.CompareVersions("0.7.7b", "0.7.7a") > 0);
        Assert.True(UpdateService.CompareVersions("0.7.7", "0.7.7a") < 0);
    }

    [Fact]
    public void IsUpdate_OffersLetteredPatchForSameNumericVersion()
    {
        Assert.True(UpdateService.IsUpdate(
            "0.7.7", AppSettings.BetaChannel, "0.7.7a", AppSettings.BetaChannel, betaSelected: true));
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
