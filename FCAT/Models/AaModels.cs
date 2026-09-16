using System.Text.Json.Serialization;

namespace FCAT.Models;

/// <summary>
/// The shapes the Alliance Auth connector serves, and the few derived judgements FCAT makes about
/// them. Everything here mirrors aa-connector/fcatconnector/views.py exactly - if a field moves
/// there it has to move here, because the connector is versioned separately from the app and an FC
/// can be running an older one.
///
/// All of it is OPTIONAL data. Every consumer has to read correctly when the lists are empty,
/// because plenty of FCs have no Alliance Auth at all, and the ones who do can be offline, have a
/// revoked key, or belong to an auth that runs neither source app.
/// </summary>
public class AaIndex
{
    /// <summary>Connector version, e.g. "0.1.0". Shown in setup so a mismatch is visible.</summary>
    [JsonPropertyName("connector")] public string Connector { get; set; } = string.Empty;

    /// <summary>The auth username the key resolved to - proof the key is bound to the right person.</summary>
    [JsonPropertyName("user")] public string User { get; set; } = string.Empty;

    [JsonPropertyName("sources")] public AaSources Sources { get; set; } = new();
}

/// <summary>Which source apps this auth actually runs. Both are optional on the auth side.</summary>
public class AaSources
{
    [JsonPropertyName("doctrines")]  public bool Doctrines  { get; set; }
    [JsonPropertyName("structures")] public bool Structures { get; set; }
}

/// <summary>A doctrine: a name, the categories it is filed under, and the fits that make it up.</summary>
public class AaDoctrine
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>A LIST - the fittings app lets a doctrine sit in several categories, or none. The
    /// connector deliberately does not pick one, so grouping is FCAT's call.</summary>
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];

    [JsonPropertyName("fitting_ids")] public List<int> FittingIds { get; set; } = [];
}

/// <summary>One fit: the hull, and its modules as type ids against slot flags.</summary>
public class AaFitting
{
    [JsonPropertyName("id")]             public int    Id           { get; set; }
    [JsonPropertyName("name")]           public string Name         { get; set; } = string.Empty;
    [JsonPropertyName("ship_type_id")]   public int    ShipTypeId   { get; set; }
    [JsonPropertyName("ship_type_name")] public string ShipTypeName { get; set; } = string.Empty;
    [JsonPropertyName("description")]    public string Description  { get; set; } = string.Empty;
    [JsonPropertyName("categories")]     public List<string>   Categories { get; set; } = [];
    [JsonPropertyName("items")]          public List<AaFitItem> Items      { get; set; } = [];
}

/// <summary>
/// One line of a fit.
///
/// flag is a STRING, not the numeric inventory flag ESI uses: the fittings app stores
/// "HiSlot0".."HiSlot7", "MedSlot0-7", "LoSlot0-7", "RigSlot0-2", "SubSystemSlot0-3",
/// "ServiceSlot0-7", "DroneBay", "FighterBay", "Cargo" or "Invalid".
///
/// There is no ammunition here. The fittings app's EFT importer splits "Heavy Beam Laser II,
/// Aurora L" into module and charge and then only ever writes the module - the charge is parsed and
/// dropped. So a fit arriving through the connector has weapons and no loads, and anything computing
/// damage has to choose ammunition itself. Ammo listed in the EFT's own cargo section does survive,
/// as Cargo lines, but nothing records which gun it was meant for.
/// </summary>
public class AaFitItem
{
    [JsonPropertyName("type_id")]  public int    TypeId   { get; set; }
    [JsonPropertyName("flag")]     public string Flag     { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public int    Quantity { get; set; } = 1;

    [JsonIgnore] public FitSlot Slot => FitSlots.Parse(Flag);

    /// <summary>Which slot of its kind, from the flag's trailing digits. The dogma engine wants the
    /// index, and a fit that skips a slot must not have its remaining modules shuffled up.</summary>
    [JsonIgnore] public int SlotIndex => FitSlots.IndexOf(Flag);

    /// <summary>True for the modules actually bolted to the hull, as against drones and cargo.</summary>
    [JsonIgnore] public bool IsFitted =>
        Slot is FitSlot.High or FitSlot.Mid or FitSlot.Low or FitSlot.Rig or FitSlot.Subsystem or FitSlot.Service;
}

public enum FitSlot { High, Mid, Low, Rig, Subsystem, Service, DroneBay, FighterBay, Cargo, Unknown }

public static class FitSlots
{
    /// <summary>Slot from the fittings app's flag string. The trailing index is ignored - a fit holds
    /// at most one item per slot, so which high slot a gun sits in never changes an answer.</summary>
    public static FitSlot Parse(string flag)
    {
        if (string.IsNullOrEmpty(flag)) return FitSlot.Unknown;
        if (flag.StartsWith("HiSlot",        StringComparison.Ordinal)) return FitSlot.High;
        if (flag.StartsWith("MedSlot",       StringComparison.Ordinal)) return FitSlot.Mid;
        if (flag.StartsWith("LoSlot",        StringComparison.Ordinal)) return FitSlot.Low;
        if (flag.StartsWith("RigSlot",       StringComparison.Ordinal)) return FitSlot.Rig;
        if (flag.StartsWith("SubSystemSlot", StringComparison.Ordinal)) return FitSlot.Subsystem;
        if (flag.StartsWith("ServiceSlot",   StringComparison.Ordinal)) return FitSlot.Service;
        if (flag == "DroneBay")   return FitSlot.DroneBay;
        if (flag == "FighterBay") return FitSlot.FighterBay;
        if (flag == "Cargo")      return FitSlot.Cargo;
        return FitSlot.Unknown;
    }

    /// <summary>The trailing index on a slot flag - "HiSlot3" is 3. Bays carry none, so they get 0.</summary>
    public static int IndexOf(string flag)
    {
        if (string.IsNullOrEmpty(flag)) return 0;
        var digits = flag.Length;
        while (digits > 0 && char.IsAsciiDigit(flag[digits - 1])) digits--;
        return digits < flag.Length && int.TryParse(flag[digits..], out var index) ? index : 0;
    }
}

/// <summary>
/// A friendly Upwell structure or starbase, as the structures app sees it. Only the ones this FC
/// could already open in their own browser come back - the connector runs the structures app's own
/// visibility filter rather than a copy of it.
/// </summary>
public class AaStructure
{
    [JsonPropertyName("id")]          public long   Id         { get; set; }
    [JsonPropertyName("name")]        public string Name       { get; set; } = string.Empty;
    [JsonPropertyName("system_id")]   public int    SystemId   { get; set; }
    [JsonPropertyName("system_name")] public string SystemName { get; set; } = string.Empty;
    [JsonPropertyName("type_id")]     public int    TypeId     { get; set; }
    [JsonPropertyName("type_name")]   public string TypeName   { get; set; } = string.Empty;
    [JsonPropertyName("owner")]       public string Owner      { get; set; } = string.Empty;

    /// <summary>The structures app's own wording, lower case: "shield vulnerable", "armor reinforce",
    /// "hull reinforce", "anchoring", "offline", "online", "unknown" and the rest.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;

    /// <summary>Null when there is no fuel timer, which for an Upwell structure means unfuelled.</summary>
    [JsonPropertyName("fuel_expires_at")] public DateTimeOffset? FuelExpiresAt { get; set; }

    /// <summary>Under attack now, or sitting in a reinforcement timer.</summary>
    [JsonIgnore] public bool IsReinforced =>
        State.Contains("reinforce", StringComparison.OrdinalIgnoreCase);

    /// <summary>Not finished going up, or on its way down. Nothing to dock in either way.</summary>
    [JsonIgnore] public bool IsAnchoring =>
        State.Contains("anchor", StringComparison.OrdinalIgnoreCase);

    /// <summary>A starbase or structure that is simply switched off.</summary>
    [JsonIgnore] public bool IsOffline =>
        State.Equals("offline", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] public bool HasFuel => FuelExpiresAt is { } f && f > DateTimeOffset.UtcNow;

    /// <summary>Upwell structures carry the citadel state wording; starbases have their own set, and
    /// report no fuel timer through this app - so "no timer" only means unfuelled for an Upwell.</summary>
    [JsonIgnore] public bool IsUpwell =>
        State.Contains("vulnerable", StringComparison.OrdinalIgnoreCase) || IsReinforced || IsAnchoring;

    /// <summary>
    /// Somewhere a fleet could actually dock or tether right now. An unfuelled Upwell drops to low
    /// power and stops offering services, a reinforced one is a fight rather than a refuge, and one
    /// still anchoring is not a structure yet. This is the only judgement FCAT makes about a
    /// structure and it is deliberately pessimistic: sending an FC somewhere that turns out to be
    /// shut is worse than not offering it at all.
    /// </summary>
    [JsonIgnore] public bool CanShelter =>
        !IsReinforced && !IsAnchoring && !IsOffline && (!IsUpwell || HasFuel);

    /// <summary>Fuel left, for an FC deciding whether to stage out of it.</summary>
    [JsonIgnore] public string FuelText
    {
        get
        {
            if (FuelExpiresAt is not { } f) return IsUpwell ? "no fuel" : string.Empty;
            var left = f - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return "out of fuel";
            if (left.TotalDays >= 1)   return $"{(int)left.TotalDays}d fuel";
            return $"{(int)left.TotalHours}h fuel";
        }
    }
}

/// <summary>Everything one refresh pulled down, as it is written to disk.</summary>
public class AaSnapshot
{
    public DateTime FetchedUtc       { get; set; } = DateTime.MinValue;
    public string   ConnectorVersion { get; set; } = string.Empty;
    public string   AuthUser         { get; set; } = string.Empty;

    public List<AaDoctrine>  Doctrines  { get; set; } = [];
    public List<AaFitting>   Fittings   { get; set; } = [];
    public List<AaStructure> Structures { get; set; } = [];
}
