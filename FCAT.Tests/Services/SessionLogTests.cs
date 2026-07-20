using FCAT.Services;
using FluentAssertions;

namespace FCAT.Tests.Services;

public class SessionLogTests
{
    private readonly SessionLog _log = new();

    [Fact]
    public void Record_AddsEntry()
    {
        _log.Record("TEST", "Something happened");

        _log.Entries.Should().ContainSingle();
        _log.Entries[0].Category.Should().Be("TEST");
        _log.Entries[0].Text.Should().Be("Something happened");
    }

    [Fact]
    public void Record_InsertsAtTop_NewestFirst()
    {
        _log.Record("A", "First");
        _log.Record("B", "Second");

        _log.Entries[0].Category.Should().Be("B");
        _log.Entries[1].Category.Should().Be("A");
    }

    [Fact]
    public void Record_CapsAt2000()
    {
        for (int i = 0; i < 2050; i++)
            _log.Record("X", $"Entry {i}");

        _log.Entries.Should().HaveCount(2000);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _log.Record("A", "test");
        _log.Record("B", "test");

        _log.Clear();

        _log.Entries.Should().BeEmpty();
        _log.SessionStart.Should().BeNull();
        _log.Summary.Should().Be("No session recorded yet.");
    }

    [Fact]
    public void StartSession_SetsSessionStart()
    {
        _log.StartSession(12345, "FC Name");

        _log.SessionStart.Should().NotBeNull();
        _log.Entries.Should().ContainSingle();
        _log.Entries[0].Category.Should().Be("SESSION");
    }

    [Fact]
    public void StartSession_PreservesOriginalStart()
    {
        _log.StartSession(111, "FC1");
        var first = _log.SessionStart;

        Thread.Sleep(10);
        _log.StartSession(222, "FC2");

        _log.SessionStart.Should().Be(first);
    }

    [Fact]
    public void HasBattleReport_False_WhenNoAnchors()
    {
        _log.HasBattleReport.Should().BeFalse();
    }

    [Fact]
    public void MarkCombat_AddsAnchor()
    {
        _log.MarkCombat(30004759, "Jita");

        _log.HasBattleReport.Should().BeTrue();
        _log.CombatAnchors.Should().ContainSingle();
        _log.CombatAnchors[0].SystemName.Should().Be("Jita");
    }

    [Fact]
    public void MarkCombat_InvalidSystemId_Ignored()
    {
        _log.MarkCombat(0, "Nowhere");
        _log.MarkCombat(-1, "Negative");

        _log.HasBattleReport.Should().BeFalse();
    }

    [Fact]
    public void BattleReportSystem_SingleSystem_ReturnsName()
    {
        _log.MarkCombat(30004759, "Jita");

        _log.BattleReportSystem.Should().Be("Jita");
    }

    [Fact]
    public void BattleReportSystem_MultipleSystems_ReturnsCount()
    {
        _log.MarkCombat(30004759, "Jita");
        _log.MarkCombat(30002187, "Amarr");

        _log.BattleReportSystem.Should().Be("2 systems");
    }

    [Fact]
    public void BattleReportSystem_NoSystems_ReturnsEmpty()
    {
        _log.BattleReportSystem.Should().BeEmpty();
    }

    [Fact]
    public void SetBattleSides_FreezesOnFirstCall()
    {
        _log.SetBattleSides([1, 2, 3], 99);
        _log.SetBattleSides([4, 5, 6], 88);

        _log.FriendlyCharIds.Should().BeEquivalentTo([1, 2, 3]);
        _log.FriendlyAllianceId.Should().Be(99);
    }

    [Fact]
    public void Export_ProducesMarkdownWithHeader()
    {
        _log.StartSession(111, "TestFC");
        _log.Record("ALERT", "Tackled by someone");

        var md = _log.Export();

        md.Should().StartWith("# FCAT After-Action Report");
        md.Should().Contain("Fleet 111");
        md.Should().Contain("FC TestFC");
        md.Should().Contain("## Timeline");
        md.Should().Contain("ALERT");
        md.Should().Contain("Tackled by someone");
    }

    [Fact]
    public void Export_WithBattleReport_IncludesAnchors()
    {
        _log.StartSession(111, "FC");
        _log.MarkCombat(30004759, "Jita");

        var md = _log.Export();

        md.Should().Contain("## Battle Report");
        md.Should().Contain("Jita");
        md.Should().Contain("zkillboard.com");
    }

    [Fact]
    public void ExportForCopy_ProducesPlainText()
    {
        _log.StartSession(111, "TestFC");
        _log.Record("INFO", "Test event");

        var text = _log.ExportForCopy();

        text.Should().StartWith("FCAT After-Action Report");
        text.Should().NotContain("#");
        text.Should().Contain("Timeline");
    }

    [Fact]
    public void BattleReportZkill_Null_WhenNoAnchors()
    {
        _log.BattleReportZkill.Should().BeNull();
        _log.BattleReportEvetools.Should().BeNull();
    }

    [Fact]
    public void BattleReportZkill_ReturnsFirstAnchorUrl()
    {
        _log.MarkCombat(30004759, "Jita");

        _log.BattleReportZkill.Should().StartWith("https://zkillboard.com/related/30004759/");
        _log.BattleReportEvetools.Should().StartWith("https://br.evetools.org/related/30004759/");
    }

    [Fact]
    public void Clear_ResetsAnchorsAndSides()
    {
        _log.MarkCombat(30004759, "Jita");
        _log.SetBattleSides([1], 99);

        _log.Clear();

        _log.HasBattleReport.Should().BeFalse();
        _log.CombatAnchors.Should().BeEmpty();
        _log.FriendlyAllianceId.Should().Be(0);
    }
}
