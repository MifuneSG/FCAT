using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// Synthetic fleet for Demo / Sandbox mode — lets an FC exercise the whole app (roster,
/// hierarchy, composition advisories, cap chain, dashboard fleet card + readiness, alerts)
/// without a live fleet. EsiService returns this data when <see cref="EsiService.DemoMode"/>
/// is on. Deterministic so names/ids stay stable across polls. Intel/system data is NOT faked
/// (it already works solo off the FC's real current system).
/// </summary>
public static class DemoData
{
    public const long FleetId = 99_000_001;

    private const int    StagingSystemId = 30004759;   // arbitrary stable id for the staging system
    private const string StagingName     = "1DQ1-A";

    // Wing / squad ids.
    private const long WAnchor = 1, WDps = 2, WLogi = 3;
    private const long SqTackle = 10, SqEwar = 11, SqLine = 12, SqLogi = 13;

    // (typeId, name, groupId) — groupId drives ShipRoleClassifier.
    private static readonly (int Id, string Name, int Grp)[] Hulls =
    {
        (11987, "Guardian",  832), // logi
        (11978, "Scimitar",  832), // logi
        (22456, "Sabre",     541), // interdictor  -> tackle
        (11196, "Stiletto",  831), // interceptor  -> tackle
        (22474, "Damnation", 540), // command ship -> booster
        (11961, "Huginn",    906), // recon        -> ewar
        (641,   "Megathron",  27), // battleship   -> DPS
        (12005, "Ishtar",    358), // HAC          -> DPS
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

    public static CharacterFleetInfo Fleet() =>
        new() { FleetId = FleetId, Role = "fleet_commander", WingId = WAnchor, SquadId = SqTackle };

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
