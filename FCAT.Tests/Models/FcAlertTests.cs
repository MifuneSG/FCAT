using FCAT.Models;
using FluentAssertions;

namespace FCAT.Tests.Models;

public class FcAlertTests
{
    [Theory]
    [InlineData(AlertType.Tackled, "TACKLED")]
    [InlineData(AlertType.CapTrouble, "CAP OUT")]
    [InlineData(AlertType.BoostLost, "BOOST LOST")]
    [InlineData(AlertType.LogiChain, "LOGI CHAIN")]
    [InlineData(AlertType.DpsLoss, "DPS LOSS")]
    [InlineData(AlertType.Info, "INFO")]
    public void AlertTag_ReturnsCorrectTag(AlertType type, string expected)
    {
        var alert = new FcAlert { AlertType = type };

        alert.AlertTag.Should().Be(expected);
    }

    [Theory]
    [InlineData(AlertType.Tackled, "Point / scram on you")]
    [InlineData(AlertType.CapTrouble, "Module offline — cap")]
    [InlineData(AlertType.BoostLost, "Booster down")]
    [InlineData(AlertType.LogiChain, "Cap chain needs adjusting")]
    [InlineData(AlertType.DpsLoss, "Fleet DPS dropping")]
    public void Headline_ReturnsCorrectText(AlertType type, string expected)
    {
        var alert = new FcAlert { AlertType = type };

        alert.Headline.Should().Be(expected);
    }

    [Fact]
    public void Headline_Info_ReturnsDetail()
    {
        var alert = new FcAlert { AlertType = AlertType.Info, Detail = "Custom info" };

        alert.Headline.Should().Be("Custom info");
    }

    [Fact]
    public void SubText_Tackled_ReturnsAttackerName()
    {
        var alert = new FcAlert { AlertType = AlertType.Tackled, AttackerName = "EvilPirate" };

        alert.SubText.Should().Be("EvilPirate");
    }

    [Fact]
    public void SubText_Tackled_NoAttacker_ReturnsUnknown()
    {
        var alert = new FcAlert { AlertType = AlertType.Tackled };

        alert.SubText.Should().Be("Unknown source");
    }

    [Theory]
    [InlineData(AlertType.CapTrouble)]
    [InlineData(AlertType.BoostLost)]
    [InlineData(AlertType.LogiChain)]
    [InlineData(AlertType.DpsLoss)]
    public void SubText_NonTackle_ReturnsDetail(AlertType type)
    {
        var alert = new FcAlert { AlertType = type, Detail = "Some detail" };

        alert.SubText.Should().Be("Some detail");
    }

    [Fact]
    public void IsCritical_Tackled_TrueByDefault()
    {
        var alert = new FcAlert { AlertType = AlertType.Tackled };

        alert.IsCritical.Should().BeTrue();
    }

    [Fact]
    public void IsCritical_NonTackle_FalseByDefault()
    {
        var alert = new FcAlert { AlertType = AlertType.CapTrouble };

        alert.IsCritical.Should().BeFalse();
    }

    [Fact]
    public void IsCritical_CriticalOverride_True_OverridesDefault()
    {
        var alert = new FcAlert { AlertType = AlertType.DpsLoss, CriticalOverride = true };

        alert.IsCritical.Should().BeTrue();
    }

    [Fact]
    public void IsCritical_CriticalOverride_False_OverridesDefault()
    {
        var alert = new FcAlert { AlertType = AlertType.Tackled, CriticalOverride = false };

        alert.IsCritical.Should().BeFalse();
    }
}
