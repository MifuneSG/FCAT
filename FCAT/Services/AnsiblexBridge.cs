namespace FCAT.Services;

/// <summary>What one zone costs this fleet, and whether a full gate can take it in one go.</summary>
public record AnsiblexZoneCost(int Zone, string Range, double Multiplier, double Tj, bool FitsOneCharge)
{
    public string Label      => $"zone {Zone}";
    public string RangeText  => Range;
    public string TjText     => Multiplier == 0 ? "free" : $"{Tj:N0} TJ";
    public string FitsText   => Multiplier == 0 ? "yes" : FitsOneCharge ? "yes" : "no";
}

/// <summary>
/// Ansiblex jump bridge capacity, as of the Cradle of War update (24.01, September 2026).
///
/// The bridge no longer burns liquid ozone. It carries a capacitor, every ship that passes through
/// drains some of it, and it refills on EVE's usual non-linear curve. A fleet can therefore run a
/// gate dry, which is a new thing for an FC to get wrong, and the arithmetic is not something you
/// do in your head while people are shouting on comms.
///
/// <para><b>Cost = the ship class's base cost x the zone of the DESTINATION.</b> The zone is worked
/// out from the light years between the gate you are jumping TO and the owning alliance's capital
/// system; where you are standing does not matter. The gate you jump FROM is the one that pays. One
/// consequence worth saying out loud: a trip out and back is not symmetrical, because coming home
/// toward the capital is priced at the lower zone.</para>
///
/// <para><b>Where these numbers come from.</b> Capacity, recharge time and the mass limit are real
/// dogma attributes on the Ansiblex, so <see cref="RefreshFromEsiAsync"/> reads them from EVE itself
/// and a balance pass changes FCAT's answers without a release. The per-class costs are NOT: they
/// are not on the ship types, not anywhere in the ESI spec, and not in hoboleaks' full dogma dump
/// (checked 2026-09-25, 2,488 attributes). They live server side, so the table below is transcribed
/// from CCP's "Force Projection: Ansiblex Capacitor Update" dev blog, which the shipped 24.01 patch
/// notes name as the authoritative source. If CCP retunes them, this file has to be edited.</para>
/// </summary>
public class AnsiblexBridge(EsiService esi)
{
    /// <summary>The Ansiblex Jump Bridge itself, which carries the capacitor attributes.</summary>
    public const int AnsiblexTypeId = 35841;

    // EVE dogma attribute ids on that type.
    private const int CapacitorCapacityAttribute = 482;   // GJ
    private const int RechargeRateAttribute      = 55;    // milliseconds for a full recharge
    private const int MaxJumpMassAttribute       = 2798;  // kg, "gateMaxJumpMass"

    /// <summary>Gate capacitor, in terajoules. 1 TJ = 1000 GJ.</summary>
    public double CapacityTj { get; private set; } = 1250;

    /// <summary>Hours to refill from empty.</summary>
    public double RechargeHours { get; private set; } = 6.25;

    /// <summary>Heaviest ship the gate will take. Supers and most capitals are out.</summary>
    public double MaxJumpMassKg { get; private set; } = 1_480_000_000;

    /// <summary>True once the three figures above came from EVE rather than the fallbacks.</summary>
    public bool VerifiedAgainstEsi { get; private set; }

    private bool _refreshed;

    /// <summary>
    /// Base capacitor cost in TJ per EVE ship group. Group ids were resolved against ESI's own ship
    /// category rather than assumed; every class CCP named maps to exactly one group, except the
    /// Rorqual, which is a hull rather than a class and so keys off Capital Industrial Ship.
    /// </summary>
    private static readonly Dictionary<int, double> BaseCostByGroup = new()
    {
        // 1.00 - anything that is not a warship
        [29]   = 1.00,   // Capsule
        [237]  = 1.00,   // Corvette
        [31]   = 1.00,   // Shuttle
        [28]   = 1.00,   // Hauler (ESI calls the group "Hauler")
        [513]  = 1.00,   // Freighter
        [902]  = 1.00,   // Jump Freighter
        [463]  = 1.00,   // Mining Barge
        [543]  = 1.00,   // Exhumer
        [1283] = 1.00,   // Expedition Frigate
        [1022] = 1.00,   // Prototype Exploration Ship
        [5087] = 1.00,   // Special Edition Yachts

        [25]   = 4.00,   // Frigate
        [830]  = 5.00,   // Covert Ops
        [420]  = 6.00,   // Destroyer
        [324]  = 7.00,   // Assault Frigate
        [1202] = 7.00,   // Blockade Runner
        [380]  = 7.00,   // Deep Space Transport
        [831]  = 8.00,   // Interceptor
        [541]  = 8.50,   // Interdictor
        [1534] = 9.00,   // Command Destroyer
        [893]  = 9.00,   // Electronic Attack Ship
        [1527] = 9.00,   // Logistics Frigate
        [1305] = 9.00,   // Tactical Destroyer
        [26]   = 10.00,  // Cruiser
        [941]  = 10.00,  // Industrial Command Ship
        [894]  = 10.50,  // Heavy Interdiction Cruiser
        [834]  = 11.00,  // Stealth Bomber
        [833]  = 11.50,  // Force Recon Ship
        [358]  = 11.50,  // Heavy Assault Cruiser
        [906]  = 11.50,  // Combat Recon Ship
        [1972] = 12.00,  // Flag Cruiser
        [832]  = 12.50,  // Logistics
        [963]  = 13.00,  // Strategic Cruiser
        [1201] = 14.00,  // Attack Battlecruiser
        [419]  = 14.50,  // Combat Battlecruiser
        [4902] = 15.00,  // Expedition Command Ship
        [540]  = 16.50,  // Command Ship
        [27]   = 16.50,  // Battleship
        [898]  = 18.00,  // Black Ops
        [900]  = 19.00,  // Marauder

        // The blog prints 57.00 and 104.50 in zones 2 and 3 for the Rorqual, against an asterisk
        // with no footnote anywhere in the article. Every other row is base x multiplier exactly,
        // and 19 x 2 is 38, not 57. Treated as consistent with the rest until someone confirms it
        // in game, because a made-up special case is worse than a uniform one.
        [883]  = 19.00,  // Capital Industrial Ship (Rorqual)
    };

    /// <summary>Distance bands from the alliance capital, and what they multiply the base cost by.</summary>
    private static readonly (int Zone, double MaxLy, string Range, double Multiplier)[] ZoneTable =
    {
        (1,  5.0, "0 - 5 ly",   0),
        (2, 10.0, "5.1 - 10",   2),
        (3, 15.0, "10.1 - 15",  6),
        (4, 20.0, "15.1 - 20",  9),
        (5, double.MaxValue, "20.1+", 15),
    };

    /// <summary>Base cost for a hull's group, or null when the class cannot use a bridge at all.</summary>
    public static double? BaseCost(int groupId) =>
        BaseCostByGroup.TryGetValue(groupId, out var tj) ? tj : null;

    /// <summary>Which zone a system falls in, given its distance from the capital in light years.</summary>
    public static int ZoneFor(double lightYears) =>
        ZoneTable.First(z => lightYears <= z.MaxLy).Zone;

    /// <summary>Per-zone cost for a fleet whose base costs add up to <paramref name="baseTj"/>.</summary>
    public IReadOnlyList<AnsiblexZoneCost> ZoneCosts(double baseTj) =>
        ZoneTable.Select(z =>
        {
            var tj = baseTj * z.Multiplier;
            return new AnsiblexZoneCost(z.Zone, z.Range, z.Multiplier, tj, tj <= CapacityTj);
        }).ToList();

    /// <summary>Hours for a gate to recover <paramref name="tj"/> of charge, at the average rate.</summary>
    public double RecoveryHours(double tj) =>
        CapacityTj <= 0 ? 0 : Math.Max(0, tj) / (CapacityTj / RechargeHours);

    public static string FormatTj(double tj) =>
        tj >= 1000 ? $"{tj / 1000:0.00}k TJ" : $"{tj:N0} TJ";

    /// <summary>
    /// Replaces the built-in capacity, recharge and mass figures with EVE's own. Runs once per
    /// session and fails quietly, exactly like <see cref="JumpDrives.RefreshFromEsiAsync"/>: the
    /// published values are current, so a failed refresh is worth less than an error an FC cannot
    /// act on. Note this cannot verify the per-class table, which EVE does not publish anywhere.
    /// </summary>
    public async Task RefreshFromEsiAsync()
    {
        if (_refreshed) return;
        _refreshed = true;

        try
        {
            var infos = await esi.GetShipTypeInfosAsync([AnsiblexTypeId]);
            if (!infos.TryGetValue(AnsiblexTypeId, out var gate)) return;

            if (gate.Attribute(CapacitorCapacityAttribute) is > 0 and var gj) CapacityTj    = gj / 1000.0;        // GJ -> TJ
            if (gate.Attribute(RechargeRateAttribute)      is > 0 and var ms) RechargeHours = ms / 3_600_000.0;  // ms -> hours
            if (gate.Attribute(MaxJumpMassAttribute)       is > 0 and var kg) MaxJumpMassKg = kg;

            VerifiedAgainstEsi = true;
        }
        catch
        {
            // Offline or an ESI hiccup. The built-in figures stand.
        }
    }
}
