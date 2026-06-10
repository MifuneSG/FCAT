namespace FCAT.Models;

public enum BoostCategory
{
    Skirmish,
    Armor,
    Shield,
    Info
}

/// <summary>One command-burst charge a booster can run, with the gang link it provides.</summary>
public record BoostChargeInfo(string Name, BoostCategory Category, string Effect);

/// <summary>
/// Canonical catalogue of command-burst charges. Boosters "drag" these charges into the
/// fleet's boost channel; we match the dragged item names (as written to the Chatlogs) back
/// to this list to learn what each booster is running.
/// Names taken verbatim from EVE's charge items (see in-game boost channel MOTD).
/// </summary>
public static class BoostChargeCatalog
{
    public static readonly IReadOnlyList<BoostChargeInfo> All =
    [
        // ── Skirmish ──
        new("Evasive Maneuvers Charge",      BoostCategory.Skirmish, "Signature radius"),
        new("Interdiction Maneuvers Charge", BoostCategory.Skirmish, "Web / scram range"),
        new("Rapid Deployment Charge",       BoostCategory.Skirmish, "AB / MWD speed"),

        // ── Armor ──
        new("Rapid Repair Charge",           BoostCategory.Armor,    "Rep boost"),
        new("Armor Reinforcement Charge",    BoostCategory.Armor,    "Armor HP"),
        new("Armor Energizing Charge",       BoostCategory.Armor,    "Armor resist"),

        // ── Shield ──
        new("Active Shielding Charge",       BoostCategory.Shield,   "Rep boost"),
        new("Shield Extension Charge",       BoostCategory.Shield,   "Shield HP"),
        new("Shield Harmonizing Charge",     BoostCategory.Shield,   "Shield resist"),

        // ── Info ──
        new("Electronic Hardening Charge",   BoostCategory.Info,     "Anti-EWAR"),
        new("Electronic Superiority Charge", BoostCategory.Info,     "EWAR strength"),
        new("Sensor Optimization Charge",    BoostCategory.Info,     "Sensor str. + targeting range"),
    ];

    private static readonly Dictionary<string, BoostChargeInfo> ByName =
        All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns every catalogued charge name found as a substring of <paramref name="text"/>.</summary>
    public static IEnumerable<BoostChargeInfo> FindIn(string text)
    {
        foreach (var charge in All)
            if (text.Contains(charge.Name, StringComparison.OrdinalIgnoreCase))
                yield return charge;
    }

    public static string CategoryTag(BoostCategory c) => c switch
    {
        BoostCategory.Skirmish => "SKIRM",
        BoostCategory.Armor    => "ARMOR",
        BoostCategory.Shield   => "SHIELD",
        BoostCategory.Info     => "INFO",
        _                      => "?"
    };
}
