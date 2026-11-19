using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class DiagnosticProfileValidatorTests
{
    private const string ValidProfile = """
    {
      "ConfirmTimeoutSec": 25,
      "SettleSec": 2,
      "AutoApplyOnServiceStart": true,
      "WatchUsbReconnect": true,
      "Tweaks": [
        {
          "Name": "USB selective suspend off",
          "Type": "usb-suspend",
          "Enabled": true,
          "Target": "both"
        },
        {
          "Name": "Mouse polling rate",
          "Type": "polling",
          "Method": "hid-report",
          "Enabled": false,
          "Target": "mouse",
          "Vid": "25A7",
          "Pid": "1234",
          "DefaultHz": 1000,
          "Levels": [125, 250, 500, 1000],
          "ReportsByHz": { "125": "", "1000": "0102A0" }
        }
      ]
    }
    """;

    [Fact]
    public void Validate_AcceptsSupportedProfile()
    {
        DiagnosticProfileValidator.Validate(ValidProfile);
    }

    [Theory]
    [InlineData("""{"Tweaks":[]}""")]
    [InlineData("""{"Tweaks":[{"Name":"x","Type":"unknown","Enabled":true}]}""")]
    [InlineData("""{"Tweaks":[{"Name":"x","Type":"polling","Method":"hid-report","Enabled":true,"Levels":[0]}]}""")]
    [InlineData("""{"Tweaks":[{"Name":"x","Type":"polling","Method":"hid-report","Enabled":true,"Vid":"ZZZZ"}]}""")]
    [InlineData("""{"Tweaks":[{"Name":"x","Type":"usb-suspend","Enabled":true},{"Name":"X","Type":"usb-suspend","Enabled":false}]}""")]
    public void Validate_RejectsUnsupportedOrUnsafeProfiles(string json)
    {
        Assert.Throws<InvalidDataException>(() => DiagnosticProfileValidator.Validate(json));
    }

    [Fact]
    public void Validate_RequiresTweaksArray()
    {
        Assert.Throws<InvalidDataException>(() => DiagnosticProfileValidator.Validate("""{"AutoApplyOnServiceStart":true}"""));
    }
}
