using FCAT.Models;

namespace FCAT.Services;

/// <summary>One synthetic alt row for the Demo-mode FLEET ALTS card.</summary>
public record DemoAlt(int CharacterId, string Name, string Role, string SystemName, string ShipName, string DockText, string FleetText);

/// <summary>
/// Synthetic fleet for Demo / Sandbox mode - lets an FC exercise the whole app (roster,
/// hierarchy, composition advisories, cap chain, dashboard fleet card + readiness, alerts)
/// without a live fleet. EsiService returns this data when <see cref="EsiService.DemoMode"/>
/// is on. Deterministic so names/ids stay stable across polls. Intel/system data is NOT faked
/// (it already works solo off the FC's real current system).
/// </summary>
public static class DemoData
{
    public const long FleetId = 99_000_001;

    public const int    StagingSystemId = 30004759;   // arbitrary stable id for the staging system
    public const string StagingName     = "1DQ1-A";

    // Wing / squad ids.
    private const long WAnchor = 1, WDps = 2, WLogi = 3;
    private const long SqTackle = 10, SqEwar = 11, SqLine = 12, SqLogi = 13;

    // (typeId, name, groupId, base hull mass kg) - groupId drives ShipRoleClassifier;
    // mass feeds the fleet-mass tile. Masses are the real ESI hull values.
    private static readonly (int Id, string Name, int Grp, double Mass)[] Hulls =
    {
        (11987, "Guardian",  832, 11_760_000), // logi
        (11978, "Scimitar",  832, 11_270_000), // logi
        (22456, "Sabre",     541,  1_530_000), // interdictor  -> tackle
        (11196, "Stiletto",  831,  1_173_000), // interceptor  -> tackle
        (22474, "Damnation", 540, 15_010_000), // command ship -> booster
        (11961, "Huginn",    906, 11_940_000), // recon        -> ewar
        (641,   "Megathron",  27, 98_400_000), // battleship   -> DPS
        (12005, "Ishtar",    358, 11_970_000), // HAC          -> DPS
    };

    // Composition (besides the FC): hull index, count, role, wing, squad.
    private static readonly (int Hull, int Count, string Role, long Wing, long Squad)[] Comp =
    {
        (0, 4, "squad_member",    WLogi,   SqLogi),   // Guardian x4
        (1, 2, "squad_member",    WLogi,   SqLogi),   // Scimitar x2
        (2, 2, "squad_member",    WAnchor, SqTackle), // Sabre x2
        (3, 1, "squad_commander", WAnchor, SqTackle), // Stiletto
        (4, 1, "wing_commander",  WAnchor, SqTackle), // Damnation
        (5, 1, "squad_member",    WAnchor, SqEwar),   // Huginn
        (6, 8, "squad_member",    WDps,    SqLine),   // Megathron x8
        (7, 4, "squad_member",    WDps,    SqLine),   // Ishtar x4
    };

    private static readonly string[] NamePool =
    {
        "Vargr Solheim","Tana Vek","Korrin Dax","Sera Lyn","Bjorn Hald","Mira Voss","Ix Karr",
        "Pavel Renn","Oona Skall","Dren Mox","Lys Carr","Halo Venn","Rook Vance","Zera Pike",
        "Cade Orin","Nyx Hald","Tor Vael","Esa Quill","Rurik Sol","Mae Drell","Kestrel Vyn","Ami Tovar",
    };

    // The 23 non-FC members, built once (stable ids/names/hulls).
    private static readonly List<FleetMember> Npcs = BuildNpcs();

    private static List<FleetMember> BuildNpcs()
    {
        var list = new List<FleetMember>();
        int cid = 90_000_000, k = 0;
        foreach (var (hull, count, role, wing, squad) in Comp)
            for (int i = 0; i < count; i++)
            {
                list.Add(new FleetMember
                {
                    CharacterId   = cid,
                    CharacterName = NamePool[k % NamePool.Length],
                    ShipTypeId    = Hulls[hull].Id,
                    Role          = role,
                    WingId        = wing,
                    SquadId       = squad,
                    SolarSystemId = StagingSystemId,
                    JoinTime      = DateTime.UtcNow.AddMinutes(-(15 + k)),
                });
                cid++; k++;
            }
        return list;
    }

    // The demo character is always the boss, otherwise the sandbox would report the real
    // "you're not fleet boss" state and show an empty fleet.
    public static CharacterFleetInfo Fleet(int ownCharId) =>
        new() { FleetId = FleetId, FleetBossId = ownCharId, Role = "fleet_commander",
                WingId = WAnchor, SquadId = SqTackle };

    public static List<FleetMember> Members(int ownCharId, string ownName)
    {
        var fc = new FleetMember
        {
            CharacterId   = ownCharId,
            CharacterName = string.IsNullOrEmpty(ownName) ? "You (FC)" : ownName,
            ShipTypeId    = 641,                    // Megathron
            Role          = "fleet_commander",
            WingId        = WAnchor,
            SquadId       = SqTackle,
            SolarSystemId = StagingSystemId,
            JoinTime      = DateTime.UtcNow.AddMinutes(-45),
        };
        return new List<FleetMember>(Npcs.Count + 1) { fc }.Concat(Npcs).ToList();
    }

    public static List<FleetWing> Wings() => new()
    {
        new FleetWing { Id = WAnchor, Name = "Anchor", Squads =
            { new FleetSquad { Id = SqTackle, Name = "Tackle" }, new FleetSquad { Id = SqEwar, Name = "EWAR" } } },
        new FleetWing { Id = WDps, Name = "DPS", Squads =
            { new FleetSquad { Id = SqLine, Name = "Battleline" } } },
        new FleetWing { Id = WLogi, Name = "Logistics", Squads =
            { new FleetSquad { Id = SqLogi, Name = "Logi" } } },
    };

    public static Dictionary<int, int> GroupIds(IEnumerable<int> typeIds)
    {
        var map = new Dictionary<int, int>();
        foreach (var id in typeIds.Distinct())
        {
            var hull = Array.Find(Hulls, h => h.Id == id);
            if (hull.Id != 0) map[id] = hull.Grp;
        }
        return map;
    }

    public static Dictionary<int, EsiTypeInfo> TypeInfos(IEnumerable<int> typeIds)
    {
        var map = new Dictionary<int, EsiTypeInfo>();
        foreach (var id in typeIds.Distinct())
        {
            var hull = Array.Find(Hulls, h => h.Id == id);
            if (hull.Id != 0)
                map[id] = new EsiTypeInfo { GroupId = hull.Grp, Name = hull.Name, Mass = hull.Mass };
        }
        return map;
    }

    // Synthetic boost loadouts. Live, these come from the boost channel (Chatlogs), which demo can't
    // read - so we hand the demo booster(s) a realistic armor-fleet loadout to exercise the  row +
    // boost-coverage tally. "Dren Mox" is the demo Damnation (an armor command ship).
    private static BoostChargeInfo Charge(string name) => BoostChargeCatalog.All.First(c => c.Name == name);
    private static readonly Dictionary<string, BoostChargeInfo[]> BoostLoadouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dren Mox"] =
        [
            Charge("Armor Energizing Charge"),        // Armor resist
            Charge("Rapid Repair Charge"),            // Armor rep boost
            Charge("Interdiction Maneuvers Charge"),  // Skirmish - web/scram range
        ],
    };

    /// <summary>A demo booster's posted charges (empty for non-boosters). Mirrors
    /// <c>BoostChannelService.GetLoadout</c> so Demo mode shows loaded scripts.</summary>
    public static IReadOnlyCollection<BoostChargeInfo> BoostLoadout(string pilotName) =>
        BoostLoadouts.TryGetValue(pilotName.Trim(), out var l) ? l : [];

    /// <summary>Synthetic alts for the dashboard FLEET ALTS card in Demo mode - the real card polls
    /// the FC's account store per-character, which is empty in demo, so this stands in for it.</summary>
    public static List<DemoAlt> Alts() => new()
    {
        new(95_100_001, "Kall Scoutsson", "Scout",  "J5A-IX",  "Stiletto",  "In space",              "In your fleet"),
        new(95_100_002, "Lucent Beacon",  "Cyno",   StagingName, "Venture", "In space",              "In your fleet"),
        new(95_100_003, "Freight Vega",   "Hauler", StagingName, "Bowhead", "⚓ Docked · Keepstar",   string.Empty),
        new(95_100_004, "Doomsday Idle",  "Titan",  StagingName, "Avatar",  "⚓ Docked · Keepstar",   string.Empty),
    };

    /// <summary>A synthetic battle report for Demo mode - zKill can't return kills for a fake fleet,
    /// so this stands in so the AAR shows a full report offline.</summary>
    public static BattleReport BattleReport()
    {
        List<BattleReportLine> Lines((string pilot, string ship, double isk)[] rows) =>
            rows.Select(r => new BattleReportLine(
                r.pilot, r.ship, r.isk, FCAT.Services.BattleReport.FormatIsk(r.isk),
                "https://zkillboard.com/")).ToList();

        var kills = Lines(new (string, string, double)[]
        {
            ("Vng. Praetor",  "Nightmare",  1_640_000_000),
            ("Hostile Reaver","Legion",       420_000_000),
            ("Red Vanguard",  "Guardian",     310_000_000),
            ("Dusk Reaver",   "Sabre",         62_000_000),
            ("Void Runner",   "Stiletto",      41_000_000),
        });
        var losses = Lines(new (string, string, double)[]
        {
            ("Kestrel Voss",  "Megathron",    280_000_000),
            ("Ash Kader",     "Guardian",     305_000_000),
            ("Ren Halloway",  "Ishtar",       210_000_000),
        });

        var destroyed = kills.Sum(k => k.Isk);
        var lost      = losses.Sum(l => l.Isk);
        return new BattleReport
        {
            IskDestroyed = destroyed,
            IskLost      = lost,
            Efficiency   = (int)Math.Round(100 * destroyed / (destroyed + lost)),
            Kills        = kills,
            Losses       = losses,
        };
    }

    public static Dictionary<int, string> Names(IEnumerable<int> ids, int ownCharId, string ownName)
    {
        var map = new Dictionary<int, string>();
        foreach (var id in ids.Distinct())
        {
            if (id == ownCharId)               map[id] = string.IsNullOrEmpty(ownName) ? "You (FC)" : ownName;
            else if (id == StagingSystemId)    map[id] = StagingName;
            else
            {
                var npc  = Npcs.Find(m => m.CharacterId == id);
                var hull = Array.Find(Hulls, h => h.Id == id);
                if (npc != null)        map[id] = npc.CharacterName;
                else if (hull.Id != 0)  map[id] = hull.Name;
            }
        }
        return map;
    }
}
