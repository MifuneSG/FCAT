using FCAT.Models;

namespace FCAT.Services;

/// <summary>One jumpable hull class, and how far it reaches.</summary>
public class JumpClass
{
    public string Name         { get; init; } = string.Empty;
    public int    GroupId      { get; init; }

    /// <summary>A representative hull of the class, used to read the real range off ESI.</summary>
    public int    SampleTypeId { get; init; }

    /// <summary>Range at Jump Drive Calibration 0, in light years.</summary>
    public double BaseLightYears { get; set; }

    /// <summary>Black Ops light on a covert cyno, which almost nothing else can.</summary>
    public bool   CovertCyno { get; set; }

    /// <summary>True once the figures came from ESI rather than the built-in fallback.</summary>
    public bool   Confirmed { get; set; }

    public string CynoText => CovertCyno ? "covert cyno" : "cyno";
}

/// <summary>
/// Jump ranges per hull class. The values below are the published ones and work offline, but they
/// are only a fallback: <see cref="RefreshFromEsiAsync"/> reads the real numbers out of EVE's own
/// dogma attributes, so a CCP balance pass changes FCAT's answers without a release.
///
/// Reading them costs nothing extra - dogma attributes ride along on the same type endpoint the
/// ship classifier already calls, so one representative hull per class settles the whole table.
/// </summary>
public class JumpDrives(EsiService esi)
{
    // EVE dogma attribute ids.
    public const int JumpDriveRangeAttribute      = 867;   // base range, light years
    public const int JumpDriveBeaconListAttribute = 3317;  // which beacon type the hull can light to
    public const int JumpDriveRangeBonusAttribute = 870;   // % range per skill level
    public const int CovertBeaconList             = 342;   // the beacon-list value meaning "covert"

    /// <summary>The Jump Drive Calibration skill - carries the per-level range bonus.</summary>
    public const int JumpDriveCalibrationTypeId = 21611;

    private bool _refreshed;

    public List<JumpClass> Classes { get; } =
    [
        new() { Name = "Black Ops",       GroupId = 898,  SampleTypeId = 22430, BaseLightYears = 4.0, CovertCyno = true },
        new() { Name = "Dreadnought",     GroupId = 485,  SampleTypeId = 19720, BaseLightYears = 3.5 },
        new() { Name = "Carrier",         GroupId = 547,  SampleTypeId = 23757, BaseLightYears = 3.5 },
        new() { Name = "Force Auxiliary", GroupId = 1538, SampleTypeId = 37604, BaseLightYears = 3.5 },
        new() { Name = "Supercarrier",    GroupId = 659,  SampleTypeId = 23913, BaseLightYears = 3.0 },
        new() { Name = "Titan",           GroupId = 30,   SampleTypeId = 671,   BaseLightYears = 3.0 },
        new() { Name = "Jump Freighter",  GroupId = 902,  SampleTypeId = 28844, BaseLightYears = 5.0 },
        new() { Name = "Rorqual",         GroupId = 883,  SampleTypeId = 28352, BaseLightYears = 5.0 },
    ];

    /// <summary>Percent added to base range per level of Jump Drive Calibration.</summary>
    public double CalibrationBonusPerLevel { get; private set; } = 20.0;

    /// <summary>True when every class's range was confirmed against ESI - the UI says so.</summary>
    public bool VerifiedAgainstEsi { get; private set; }

    public double RangeAt(JumpClass hull, int calibrationLevel)
        => hull.BaseLightYears * (1 + CalibrationBonusPerLevel * Math.Clamp(calibrationLevel, 0, 5) / 100.0);

    /// <summary>
    /// Replaces the built-in ranges with EVE's own. Runs once per session and fails quietly - the
    /// published values are close enough that a failed refresh is worth less than an error the FC
    /// can do nothing about.
    /// </summary>
    public async Task RefreshFromEsiAsync()
    {
        if (_refreshed) return;
        _refreshed = true;

        try
        {
            var typeIds = Classes.Select(c => c.SampleTypeId).Append(JumpDriveCalibrationTypeId);
            var infos   = await esi.GetShipTypeInfosAsync(typeIds);

            foreach (var hull in Classes)
            {
                if (!infos.TryGetValue(hull.SampleTypeId, out var info)) continue;

                var range = info.Attribute(JumpDriveRangeAttribute);
                if (range is not > 0) continue;

                hull.BaseLightYears = range.Value;
                hull.CovertCyno     = (int?)info.Attribute(JumpDriveBeaconListAttribute) == CovertBeaconList;
                hull.Confirmed      = true;
            }

            if (infos.TryGetValue(JumpDriveCalibrationTypeId, out var skill))
            {
                var bonus = skill.Attribute(JumpDriveRangeBonusAttribute);
                if (bonus is > 0) CalibrationBonusPerLevel = bonus.Value;
            }

            VerifiedAgainstEsi = Classes.All(c => c.Confirmed);
        }
        catch
        {
            // Offline or ESI hiccup - the built-in table stands.
        }
    }
}
