using FCAT.Models;
using FluentAssertions;

namespace FCAT.Tests.Models;

public class BoostChargeCatalogTests
{
    [Fact]
    public void All_Contains15Charges()
    {
        BoostChargeCatalog.All.Should().HaveCount(15);
    }

    [Theory]
    [InlineData("Rapid Deployment Charge", BoostCategory.Skirmish)]
    [InlineData("Rapid Repair Charge", BoostCategory.Armor)]
    [InlineData("Active Shielding Charge", BoostCategory.Shield)]
    [InlineData("Electronic Hardening Charge", BoostCategory.Info)]
    [InlineData("Mining Laser Field Enhancement Charge", BoostCategory.MiningYield)]
    public void All_ContainsExpectedCharges(string name, BoostCategory expectedCategory)
    {
        BoostChargeCatalog.All.Should().Contain(c => c.Name == name && c.Category == expectedCategory);
    }

    [Fact]
    public void FindIn_MatchesSingleCharge()
    {
        var result = BoostChargeCatalog.FindIn("I've loaded Rapid Deployment Charge for the fleet").ToList();

        result.Should().ContainSingle()
            .Which.Name.Should().Be("Rapid Deployment Charge");
    }

    [Fact]
    public void FindIn_MatchesMultipleCharges()
    {
        var text = "Running Rapid Deployment Charge and Active Shielding Charge and Armor Reinforcement Charge";
        var result = BoostChargeCatalog.FindIn(text).ToList();

        result.Should().HaveCount(3);
        result.Select(c => c.Name).Should().Contain([
            "Rapid Deployment Charge",
            "Active Shielding Charge",
            "Armor Reinforcement Charge"
        ]);
    }

    [Fact]
    public void FindIn_CaseInsensitive()
    {
        var result = BoostChargeCatalog.FindIn("RAPID DEPLOYMENT CHARGE loaded").ToList();

        result.Should().ContainSingle()
            .Which.Name.Should().Be("Rapid Deployment Charge");
    }

    [Fact]
    public void FindIn_NoMatch_ReturnsEmpty()
    {
        var result = BoostChargeCatalog.FindIn("Just chatting about the fleet").ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindIn_EmptyString_ReturnsEmpty()
    {
        BoostChargeCatalog.FindIn("").Should().BeEmpty();
    }

    [Theory]
    [InlineData(BoostCategory.Skirmish, "SKIRM")]
    [InlineData(BoostCategory.Armor, "ARMOR")]
    [InlineData(BoostCategory.Shield, "SHIELD")]
    [InlineData(BoostCategory.Info, "INFO")]
    [InlineData(BoostCategory.MiningYield, "YIELD")]
    [InlineData(BoostCategory.MiningOptimal, "RANGE")]
    [InlineData(BoostCategory.MiningPreserve, "PRESV")]
    public void CategoryTag_ReturnsCorrectTag(BoostCategory category, string expected)
    {
        BoostChargeCatalog.CategoryTag(category).Should().Be(expected);
    }
}
