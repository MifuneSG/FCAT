using FCAT.Services;
using FluentAssertions;

namespace FCAT.Tests.Services;

public class WormholeMassTests
{
    [Theory]
    [InlineData(2_500_000_000, "2.50B kg")]
    [InlineData(1_000_000_000, "1.00B kg")]
    [InlineData(500_000_000, "500M kg")]
    [InlineData(1_500_000, "1.5M kg")]
    [InlineData(999_999, "999,999 kg")]
    [InlineData(0, "0 kg")]
    public void Format_DisplaysCorrectUnit(double kg, string expected)
    {
        WormholeMass.Format(kg).Should().Be(expected);
    }

    [Fact]
    public void Checks_Returns4Entries()
    {
        var checks = WormholeMass.Checks(0);

        checks.Should().HaveCount(4);
    }

    [Fact]
    public void Checks_SmallFleet_FitsAllHoles()
    {
        // 100M kg fleet — should fit through all holes one-way and round-trip
        var checks = WormholeMass.Checks(100_000_000);

        checks.Should().AllSatisfy(c =>
        {
            c.OneWay.Should().BeTrue();
            c.RoundTrip.Should().BeTrue();
        });
    }

    [Fact]
    public void Checks_LargeFleet_ExceedsSomeHoles()
    {
        // 2B kg fleet — exceeds 1B hole budget (0.9B after spawn floor)
        var checks = WormholeMass.Checks(2_000_000_000);

        var oneB = checks.First(c => c.Label == "1B hole");
        oneB.OneWay.Should().BeFalse();
        oneB.RoundTrip.Should().BeFalse();

        var fiveB = checks.First(c => c.Label == "5B hole");
        fiveB.OneWay.Should().BeTrue();
        fiveB.RoundTrip.Should().BeTrue();
    }

    [Fact]
    public void Checks_RoundTrip_RequiresDoubleMassFit()
    {
        // 1.3B fleet — fits 3B one-way (budget 2.7B) but NOT round-trip (2.6B > 2.7B? 2.6 < 2.7, so yes)
        // Let's use a fleet where round-trip fails but one-way passes:
        // 2B hole budget = 1.8B. Fleet of 1.5B: one-way yes (1.5 < 1.8), round-trip no (3.0 > 1.8)
        var checks = WormholeMass.Checks(1_500_000_000);

        var twoB = checks.First(c => c.Label == "2B hole");
        twoB.OneWay.Should().BeTrue();
        twoB.RoundTrip.Should().BeFalse();
    }

    [Fact]
    public void Checks_TextProperties_MatchBooleans()
    {
        var checks = WormholeMass.Checks(1_500_000_000);

        checks.Should().AllSatisfy(c =>
        {
            c.OneWayText.Should().Be(c.OneWay ? "yes" : "no");
            c.RoundTripText.Should().Be(c.RoundTrip ? "yes" : "no");
        });
    }

    [Fact]
    public void Checks_HoleLabels_AreCorrect()
    {
        var checks = WormholeMass.Checks(0);

        checks.Select(c => c.Label).Should().Equal("1B hole", "2B hole", "3B hole", "5B hole");
    }
}
