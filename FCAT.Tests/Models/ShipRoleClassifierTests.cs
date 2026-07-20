using FCAT.Models;
using FluentAssertions;

namespace FCAT.Tests.Models;

public class ShipRoleClassifierTests
{
    [Theory]
    [InlineData(832, ShipRole.Logi)]
    [InlineData(1527, ShipRole.Logi)]
    [InlineData(1538, ShipRole.CapLogi)]
    [InlineData(540, ShipRole.Booster)]
    [InlineData(1534, ShipRole.Booster)]
    [InlineData(30, ShipRole.Titan)]
    [InlineData(659, ShipRole.Supercarrier)]
    [InlineData(547, ShipRole.CapDPS)]
    [InlineData(485, ShipRole.CapDPS)]
    [InlineData(883, ShipRole.Industrial)]
    [InlineData(941, ShipRole.Industrial)]
    [InlineData(28, ShipRole.Industrial)]
    [InlineData(380, ShipRole.Industrial)]
    [InlineData(463, ShipRole.Mining)]
    [InlineData(543, ShipRole.Mining)]
    [InlineData(1283, ShipRole.Mining)]
    [InlineData(831, ShipRole.Tackle)]
    [InlineData(894, ShipRole.Tackle)]
    [InlineData(541, ShipRole.Bubble)]
    [InlineData(906, ShipRole.EWAR)]
    [InlineData(833, ShipRole.EWAR)]
    [InlineData(893, ShipRole.EWAR)]
    [InlineData(830, ShipRole.Support)]
    [InlineData(1022, ShipRole.Support)]
    public void Classify_GroupMapped_ReturnsCorrectRole(int groupId, ShipRole expected)
    {
        var result = ShipRoleClassifier.Classify(typeId: 99999, groupId);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(32880, ShipRole.Mining)]   // Venture
    [InlineData(605, ShipRole.Support)]    // Heron
    [InlineData(607, ShipRole.Support)]    // Imicus
    [InlineData(586, ShipRole.Support)]    // Probe
    [InlineData(29248, ShipRole.Support)]  // Magnate
    [InlineData(620, ShipRole.Logi)]       // Osprey
    [InlineData(625, ShipRole.Logi)]       // Augoror
    [InlineData(634, ShipRole.Logi)]       // Exequror
    [InlineData(631, ShipRole.Logi)]       // Scythe
    [InlineData(33472, ShipRole.Logi)]     // Nestor
    public void Classify_TypeOverride_TakesPrecedenceOverGroup(int typeId, ShipRole expected)
    {
        // Use a group that would map to DPS (or any other role) — the type override should win
        var result = ShipRoleClassifier.Classify(typeId, groupId: 25);

        result.Should().Be(expected);
    }

    [Fact]
    public void Classify_UnknownGroupAndType_FallsToDPS()
    {
        var result = ShipRoleClassifier.Classify(typeId: 1, groupId: 9999);

        result.Should().Be(ShipRole.DPS);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(832, false)]
    [InlineData(0, false)]
    public void IsCapsule_IdentifiesCapsuleGroup(int groupId, bool expected)
    {
        ShipRoleClassifier.IsCapsule(groupId).Should().Be(expected);
    }

    [Fact]
    public void CapsuleGroupId_Is29()
    {
        ShipRoleClassifier.CapsuleGroupId.Should().Be(29);
    }

    [Theory]
    [InlineData(11987, true)]   // Guardian
    [InlineData(11985, true)]   // Basilisk
    [InlineData(620, true)]     // Osprey
    [InlineData(625, true)]     // Augoror
    [InlineData(11978, false)]  // Scimitar — solo logi, not cap-chain
    [InlineData(11989, false)]  // Oneiros — solo logi, not cap-chain
    [InlineData(99999, false)]  // Random type
    public void CapChainHullTypeIds_ContainsOnlyCapChainLogi(int typeId, bool expected)
    {
        ShipRoleClassifier.CapChainHullTypeIds.Contains(typeId).Should().Be(expected);
    }
}
