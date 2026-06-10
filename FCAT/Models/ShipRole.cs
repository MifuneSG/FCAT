namespace FCAT.Models;

public enum ShipRole
{
    Unknown,
    Logi,           // T1/T2 logistics cruisers + logistics frigates
    CapLogi,        // Force Auxiliary (FAX)
    Booster,        // Command Ships + Command Destroyers
    Titan,
    Supercarrier,
    CapDPS,         // Dreadnought + Carrier
    Tackle,         // Interceptors + Heavy Interdictors
    Bubble,         // Interdictors (dictor)
    EWAR,           // Recon Ships + Electronic Attack Ships
    Support,        // Scanning / scout hulls (Covert Ops) — not damage dealers
    Industrial,     // Orca, Rorqual, transports
    Mining,         // Mining Barges + Exhumers
    DPS             // Everything else (battleships, HACs, T1 cruisers, etc.)
}

/// <summary>
/// Maps ESI group_id values to fleet roles.
/// group_id comes from GET /v3/universe/types/{typeId}/ → group_id field.
/// </summary>
public static class ShipRoleClassifier
{
    private static readonly Dictionary<int, ShipRole> GroupMap = new()
    {
        // ── Logistics ──────────────────────────────────────────────
        { 832,  ShipRole.Logi  },   // Logistics cruisers (Scimitar, Basilisk, Guardian, Oneiros)
        { 1527, ShipRole.Logi  },   // Logistics Frigates (Kirin, Deacon, Thalia, Scalpel)

        // ── Force Auxiliary ────────────────────────────────────────
        { 1538, ShipRole.CapLogi }, // Force Auxiliary (Apostle, Minokawa, Ninazu, Lif)

        // ── Boosters ───────────────────────────────────────────────
        { 540,  ShipRole.Booster }, // Command Ships (Sleipnir, Claymore, Damnation, Nighthawk, etc.)
        { 1534, ShipRole.Booster }, // Command Destroyers (Bifrost, Magus, Stork, Pontifex)

        // ── Capitals ───────────────────────────────────────────────
        { 30,   ShipRole.Titan        }, // Titan
        { 659,  ShipRole.Supercarrier }, // Supercarrier (Aeon, Nyx, Hel, Wyvern)
        { 547,  ShipRole.CapDPS       }, // Carrier
        { 485,  ShipRole.CapDPS       }, // Dreadnought

        // ── Industrial / Mining ────────────────────────────────────
        { 883,  ShipRole.Industrial }, // Capital Industrial (Rorqual)
        { 941,  ShipRole.Industrial }, // Industrial Command Ships (Orca, Porpoise)
        { 28,   ShipRole.Industrial }, // Industrial haulers (Badger, Iteron, etc.)
        { 380,  ShipRole.Industrial }, // Transport Ships (Deep Space Transport, Blockade Runner)
        { 463,  ShipRole.Mining     }, // Mining Barge (Procurer, Retriever, Covetor)
        { 543,  ShipRole.Mining     }, // Exhumer (Skiff, Mackinaw, Hulk)
        { 1283, ShipRole.Mining     }, // Expedition Frigates (Prospect, Endurance)

        // ── Tackle ─────────────────────────────────────────────────
        { 831,  ShipRole.Tackle }, // Interceptor (Stiletto, Crow, Ares, Malediction, etc.)
        { 894,  ShipRole.Tackle }, // Heavy Interdictor (Broadsword, Devoter, Phobos, Onyx)

        // ── Interdictors ───────────────────────────────────────────
        { 541,  ShipRole.Bubble }, // Interdictor (Sabre, Flycatcher, Heretic, Eris)

        // ── EWAR ───────────────────────────────────────────────────
        { 906,  ShipRole.EWAR  }, // Combat Recon (Huginn, Lachesis, Rook, Curse)
        { 833,  ShipRole.EWAR  }, // Force Recon (Falcon, Arazu, Pilgrim, Rapier)
        { 893,  ShipRole.EWAR  }, // Electronic Attack Ships (Kitsune, Sentinel, Hyena, Keres)

        // ── Support / scout ────────────────────────────────────────
        { 830,  ShipRole.Support }, // Covert Ops (Helios, Buzzard, Cheetah, Anathema — scanners)
        { 1022, ShipRole.Support }, // Prototype Exploration Ship (Zephyr)
        // NOTE: T1 exploration frigates (Heron/Magnate/Probe/Imicus) and the Venture share
        // group 25 (Frigate) with combat frigates, so they can't be split out by hull alone.

        // NOTE: Tactical Destroyers (group 1305 — Svipul, Confessor, Jackdaw, Hecate) are NOT
        // mapped, so they fall through to DPS, which is correct for nearly all doctrines.
    };

    /// <summary>EVE group_id 29 = Capsule (pod). A pilot in a pod has lost their ship.</summary>
    public const int CapsuleGroupId = 29;

    public static bool IsCapsule(int groupId) => groupId == CapsuleGroupId;

    /// <summary>Returns the fleet role for an EVE ship group_id. Unknown types default to DPS.</summary>
    public static ShipRole Classify(int groupId) =>
        GroupMap.TryGetValue(groupId, out var role) ? role : ShipRole.DPS;
}
