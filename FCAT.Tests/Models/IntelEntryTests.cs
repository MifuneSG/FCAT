using FCAT.Models;
using FluentAssertions;

namespace FCAT.Tests.Models;

public class IntelEntryTests
{
    [Fact]
    public void Tag_Kill_ReturnsKILL()
    {
        var entry = new IntelEntry { Kind = IntelKind.Kill };

        entry.Tag.Should().Be("KILL");
    }

    [Fact]
    public void Tag_Report_ReturnsINTEL()
    {
        var entry = new IntelEntry { Kind = IntelKind.Report };

        entry.Tag.Should().Be("INTEL");
    }

    [Theory]
    [InlineData(IntelStatus.Clear, "CLR")]
    [InlineData(IntelStatus.NoVisual, "NV")]
    [InlineData(IntelStatus.Incoming, "INC")]
    [InlineData(IntelStatus.None, "")]
    public void StatusText_ReturnsCorrectAbbreviation(IntelStatus status, string expected)
    {
        var entry = new IntelEntry { Status = status };

        entry.StatusText.Should().Be(expected);
    }

    [Fact]
    public void IsKill_True_ForKillKind()
    {
        new IntelEntry { Kind = IntelKind.Kill }.IsKill.Should().BeTrue();
    }

    [Fact]
    public void IsKill_False_ForReportKind()
    {
        new IntelEntry { Kind = IntelKind.Report }.IsKill.Should().BeFalse();
    }

    [Fact]
    public void HasUrl_True_WhenUrlSet()
    {
        new IntelEntry { Url = "https://zkillboard.com/kill/123/" }.HasUrl.Should().BeTrue();
    }

    [Fact]
    public void HasUrl_False_WhenUrlNull()
    {
        new IntelEntry { Url = null }.HasUrl.Should().BeFalse();
    }

    [Fact]
    public void HasUrl_False_WhenUrlEmpty()
    {
        new IntelEntry { Url = "" }.HasUrl.Should().BeFalse();
    }

    [Fact]
    public void HasStatus_True_WhenNotNone()
    {
        new IntelEntry { Status = IntelStatus.Clear }.HasStatus.Should().BeTrue();
    }

    [Fact]
    public void HasStatus_False_WhenNone()
    {
        new IntelEntry { Status = IntelStatus.None }.HasStatus.Should().BeFalse();
    }
}
