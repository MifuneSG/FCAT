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
        // NOTE: Tactical Destroyers (group 1305 — Svipul, Confessor, Jackdaw, Hecate) are NOT
        // mapped, so they fall through to DPS, which is correct for nearly all doctrines.
    };

    /// <summary>
    /// Per-TYPE overrides for hulls whose fleet role differs from their group default. These
    /// are ships that share a broad group with unrelated hulls (e.g. the Venture sits in the
    /// generic "Frigate" group), or specialist variants we want to pin precisely. Type-ID keys
    /// mean combat variants stay correct automatically — e.g. base Osprey → Logi, but
    /// "Osprey Navy Issue" (a different type) falls through to DPS.
    ///
    /// This table is also the seed for future fit/doctrine-aware classification.
    /// </summary>
    private static readonly Dictionary<int, ShipRole> TypeOverrides = new()
    {
        // ── Mining frigate (group 25 Frigate) ──
        { 32880, ShipRole.Mining  },  // Venture

        // ── T1 exploration frigates (group 25 Frigate) → scouts/support ──
        { 605,   ShipRole.Support },  // Heron
        { 607,   ShipRole.Support },  // Imicus
        { 586,   ShipRole.Support },  // Probe
        { 29248, ShipRole.Support },  // Magnate

        // ── T1 logistics cruisers (group 26 Cruiser) → fleet logi ──
        { 620,   ShipRole.Logi    },  // Osprey   (Navy Issue 29340 stays DPS)
        { 625,   ShipRole.Logi    },  // Augoror
        { 634,   ShipRole.Logi    },  // Exequror
        { 631,   ShipRole.Logi    },  // Scythe    (Fleet Issue 29336 stays DPS)

        // ── Logistics battleship (group 27 Battleship) ──
        { 33472, ShipRole.Logi    },  // Nestor
    };

    /// <summary>EVE group_id 29 = Capsule (pod). A pilot in a pod has lost their ship.</summary>
    public const int CapsuleGroupId = 29;

    public static bool IsCapsule(int groupId) => groupId == CapsuleGroupId;

    /// <summary>
    /// The cap-CHAIN logistics ships: Guardian (Amarr) and Basilisk (Caldari), plus their T1
    /// cap-transfer precursors Osprey (shield) and Augoror (armor). These rely on energy-transfer
    /// chains to stay cap-stable, so when one drops the ring must be re-formed. The Scimitar (11978)
    /// and Oneiros (11989) are cap-independent "solo" logi and deliberately excluded — they don't chain.
    /// </summary>
    public static readonly IReadOnlySet<int> CapChainHullTypeIds = new HashSet<int>
    {
        11987, // Guardian
        11985, // Basilisk
        620,   // Osprey  (T1 shield logi — cap-chains like the Basilisk)
        625,   // Augoror (T1 armor logi — cap-chains like the Guardian)
    };

    /// <summary>
    /// Returns the fleet role for a ship. A per-type override wins over the group default;
    /// otherwise the ship's group decides, falling back to DPS for unmapped hulls.
    /// </summary>
    public static ShipRole Classify(int typeId, int groupId)
    {
        if (TypeOverrides.TryGetValue(typeId, out var overridden)) return overridden;
        return GroupMap.TryGetValue(groupId, out var role) ? role : ShipRole.DPS;
    }
}
