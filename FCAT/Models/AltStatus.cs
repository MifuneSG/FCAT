namespace FCAT.Models;

/// <summary>
/// Live state of one authorized character. The FC's own alts are positioned deliberately - a cyno
/// sitting on a beacon, a scout parked a few jumps out - so "is it still there, still alive, still
/// online" is a question worth answering continuously rather than only when a page is open.
/// </summary>
public class AltStatus
{
    public int    CharacterId { get; init; }
    public string Name        { get; init; } = string.Empty;

    /// <summary>The FC-assigned label (Main / Cyno / Scout / …). Cyno and Scout are the ones that
    /// raise alerts - see <see cref="Services.AltTracker.IsCovertRole"/>.</summary>
    public string Role { get; set; } = "Main";

    /// <summary>The character FCAT is operating as - excluded from alt alerts, since the FC is
    /// already looking at it.</summary>
    public bool IsActiveCharacter { get; set; }

    public bool   Online     { get; set; }
    public int    SystemId   { get; set; }
    public string SystemName { get; set; } = string.Empty;
    public int    ShipTypeId { get; set; }
    public string ShipName   { get; set; } = string.Empty;

    /// <summary>In a pod. Combined with <see cref="Docked"/> this separates a loss from a reship.</summary>
    public bool InCapsule { get; set; }

    public int    StationId     { get; set; }
    public string StationName   { get; set; } = string.Empty;
    public string StructureName { get; set; } = string.Empty;

    public long FleetId { get; set; }

    /// <summary>Docked at an NPC station or a citadel. A podded character that is docked has
    /// respawned and is safe; a podded one in space has just lost its ship.</summary>
    public bool Docked => StationId > 0 || StructureName.Length > 0;

    /// <summary>Where it's docked, for the status line. Citadels resolve by name, NPC stations by id.</summary>
    public string DockedAt =>
        StationId > 0        ? (StationName.Length > 0 ? StationName : "station")
      : StructureName.Length > 0 ? StructureName
      :                            string.Empty;
}
