using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class PowerPlanCatalogTests
{
    [Fact]
    public void Parse_ExtractsStandardPlansAndMarksActivePlan()
    {
        const string output =
            "Existing Power Schemes (* Active)\r\n" +
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n" +
            "Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance) *\r\n";

        var plans = PowerPlanCatalog.Parse(output);

        Assert.Equal(2, plans.Count);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", plans[0].Id);
        Assert.Equal("Высокая производительность", plans[0].Name);
        Assert.True(plans[0].IsActive);
        Assert.Equal("Сбалансированный", plans[1].Name);
        Assert.False(plans[1].IsActive);
    }

    [Fact]
    public void Parse_SupportsLocalizedOutputAndCustomPlanNames()
    {
        const string output =
            "Существующие схемы управления питанием (* Активная)\r\n" +
            "GUID схемы питания: 12345678-1234-1234-1234-123456789abc  (Игровая схема) *\r\n";

        var plans = PowerPlanCatalog.Parse(output);

        var plan = Assert.Single(plans);
        Assert.Equal("Игровая схема", plan.Name);
        Assert.True(plan.IsActive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void Parse_IgnoresLinesWithoutPowerSchemeGuid(string? output)
    {
        Assert.Empty(PowerPlanCatalog.Parse(output));
    }
}
