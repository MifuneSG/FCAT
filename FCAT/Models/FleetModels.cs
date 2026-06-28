using System.Text.Json.Serialization;

namespace FCAT.Models;

public class FleetInfo
{
    [JsonPropertyName("fleet_id")]
    public long FleetId { get; set; }

    [JsonPropertyName("is_free_move")]
    public bool IsFreeMove { get; set; }

    [JsonPropertyName("is_registered")]
    public bool IsRegistered { get; set; }

    [JsonPropertyName("is_voice_enabled")]
    public bool IsVoiceEnabled { get; set; }

    [JsonPropertyName("motd")]
    public string Motd { get; set; } = string.Empty;
}

public class CharacterFleetInfo
{
    [JsonPropertyName("fleet_id")]
    public long FleetId { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("squad_id")]
    public long SquadId { get; set; }

    [JsonPropertyName("wing_id")]
    public long WingId { get; set; }
}

public class FleetMember
{
    [JsonPropertyName("character_id")]
    public int CharacterId { get; set; }

    [JsonPropertyName("join_time")]
    public DateTime JoinTime { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("role_name")]
    public string RoleName { get; set; } = string.Empty;

    [JsonPropertyName("ship_type_id")]
    public int ShipTypeId { get; set; }

    [JsonPropertyName("solar_system_id")]
    public int SolarSystemId { get; set; }

    [JsonPropertyName("squad_id")]
    public long SquadId { get; set; }

    [JsonPropertyName("station_id")]
    public long? StationId { get; set; }

    [JsonPropertyName("takes_fleet_warp")]
    public bool TakesFleetWarp { get; set; }

    [JsonPropertyName("wing_id")]
    public long WingId { get; set; }

    // Resolved names (populated separately)
    public string CharacterName { get; set; } = string.Empty;
    public string ShipTypeName { get; set; } = string.Empty;
    public string SolarSystemName { get; set; } = string.Empty;
}

public class FleetWing
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("squads")]
    public List<FleetSquad> Squads { get; set; } = [];
}

public class FleetSquad
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class CharacterPublicInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("corporation_id")]
    public int CorporationId { get; set; }

    [JsonPropertyName("alliance_id")]
    public int? AllianceId { get; set; }

    [JsonPropertyName("security_status")]
    public double SecurityStatus { get; set; }
}

public class CorporationPublicInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [JsonPropertyName("alliance_id")]
    public int? AllianceId { get; set; }
}

public class AlliancePublicInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = string.Empty;
}

public class EsiNameResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}

/// <summary>Result of POST /v1/universe/ids/ — resolves names to typed IDs.</summary>
public class UniverseIdsResult
{
    [JsonPropertyName("characters")]
    public List<EsiNameResult>? Characters { get; set; }

    [JsonPropertyName("inventory_types")]
    public List<EsiNameResult>? InventoryTypes { get; set; }
}

/// <summary>Minimal info from GET /v1/universe/groups/{id}/ — used to tell ships from drones/structures.</summary>
public class EsiGroupInfo
{
    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }   // 6 = Ship
}

/// <summary>One entry from POST /v1/characters/affiliation/ — a pilot's corp/alliance.</summary>
public class CharAffiliation
{
    [JsonPropertyName("character_id")]   public int  CharacterId   { get; set; }
    [JsonPropertyName("corporation_id")] public int  CorporationId { get; set; }
    [JsonPropertyName("alliance_id")]    public int? AllianceId    { get; set; }
    [JsonPropertyName("faction_id")]     public int? FactionId     { get; set; }
}

// ── System intel ──
public class EsiSystem
{
    [JsonPropertyName("system_id")]        public int      SystemId        { get; set; }
    [JsonPropertyName("name")]             public string   Name            { get; set; } = string.Empty;
    [JsonPropertyName("security_status")]  public double   SecurityStatus  { get; set; }
    [JsonPropertyName("constellation_id")] public int      ConstellationId { get; set; }
    [JsonPropertyName("stargates")]        public int[]?   Stargates       { get; set; }
    [JsonPropertyName("position")]         public EsiPosition? Position    { get; set; }
}

/// <summary>A point in EVE's universe (metres). Top-down maps use X (east-west) and Z (north-south).</summary>
public class EsiPosition
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
}

public class EsiStargate
{
    [JsonPropertyName("name")]        public string Name { get; set; } = string.Empty;
    [JsonPropertyName("destination")] public EsiStargateDestination? Destination { get; set; }
}
public class EsiStargateDestination { [JsonPropertyName("system_id")] public int SystemId { get; set; } }

public class EsiConstellation
{
    [JsonPropertyName("name")]      public string Name     { get; set; } = string.Empty;
    [JsonPropertyName("region_id")] public int    RegionId { get; set; }
    [JsonPropertyName("systems")]   public int[]? Systems  { get; set; }
}

public class EsiNameOnly { [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; }

public class SystemKills
{
    [JsonPropertyName("system_id")] public int SystemId  { get; set; }
    [JsonPropertyName("ship_kills")] public int ShipKills { get; set; }
    [JsonPropertyName("pod_kills")]  public int PodKills  { get; set; }
    [JsonPropertyName("npc_kills")]  public int NpcKills  { get; set; }
}

public class SystemJumps
{
    [JsonPropertyName("system_id")]  public int SystemId  { get; set; }
    [JsonPropertyName("ship_jumps")] public int ShipJumps { get; set; }
}

public class SovEntry
{
    [JsonPropertyName("system_id")]  public int  SystemId   { get; set; }
    [JsonPropertyName("alliance_id")] public int? AllianceId { get; set; }
    [JsonPropertyName("faction_id")]  public int? FactionId  { get; set; }
}

public class CharacterLocation { [JsonPropertyName("solar_system_id")] public int SolarSystemId { get; set; } }

// ── Killmail (ESI detail, for the intel feed) ──
public class EsiKillmail
{
    [JsonPropertyName("killmail_id")]    public long     KillmailId    { get; set; }
    [JsonPropertyName("killmail_time")]  public DateTime KillmailTime  { get; set; }
    [JsonPropertyName("solar_system_id")] public int     SolarSystemId { get; set; }
    [JsonPropertyName("victim")]         public EsiKillmailVictim? Victim { get; set; }
}

public class EsiKillmailVictim
{
    [JsonPropertyName("character_id")]   public int? CharacterId { get; set; }
    [JsonPropertyName("ship_type_id")]   public int  ShipTypeId  { get; set; }
}

/// <summary>A zKillboard list entry — id + the "zkb" envelope (hash, value). Detail comes from ESI.</summary>
public class ZkillEntry
{
    [JsonPropertyName("killmail_id")] public long KillmailId { get; set; }
    [JsonPropertyName("zkb")]         public ZkbInfo? Zkb    { get; set; }
}
public class ZkbInfo
{
    [JsonPropertyName("hash")]       public string Hash       { get; set; } = string.Empty;
    [JsonPropertyName("totalValue")] public double TotalValue { get; set; }
}

/// <summary>Minimal type info returned by GET /v3/universe/types/{typeId}/</summary>
public class EsiTypeInfo
{
    [JsonPropertyName("group_id")]
    public int GroupId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
