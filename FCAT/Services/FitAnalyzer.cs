using FCAT.Models;

namespace FCAT.Services;

/// <summary>Dogma attribute ids FCAT reads. Named because a bare 64 in an expression is unreadable.</summary>
public static class DogmaAttr
{
    public const int DamageMultiplier = 64;    // turret damage modifier, and what damage mods raise
    public const int RateOfFire       = 51;    // milliseconds between activations
    public const int RofMultiplier    = 204;   // what a damage mod multiplies rate of fire by
    public const int EmDamage         = 114;
    public const int ExplosiveDamage  = 116;
    public const int KineticDamage    = 117;
    public const int ThermalDamage    = 118;
    public const int OptimalRange     = 54;
    public const int TurretSlots      = 102;   // hardpoints, not high slots
    public const int LauncherSlots    = 101;
    public const int DroneBandwidth   = 1271;
    public const int DroneCapacity    = 283;

    /// <summary>Charge size: 1 small, 2 medium, 3 large. Both the weapon and the charge carry it,
    /// and a mismatch is why a Sabre cannot load the battleship ammo sitting next to it.</summary>
    public const int ChargeSize = 128;

    /// <summary>604-609 are the charge groups a weapon accepts.</summary>
    public const int FirstChargeGroup = 604;
    public const int LastChargeGroup  = 609;
}

/// <summary>
/// A charge a doctrine fit carries and its own guns can actually load.
///
/// Damage is the raw sum of its four types, used only to ORDER the picker - the engine does the
/// real sums. Range is filled in later by whoever has a dogma engine to hand, because it depends
/// on the gun firing it as much as on the round.
/// </summary>
public record AmmoChoice(int TypeId, string Name, double Damage, string RangeText = "")
{
    /// <summary>What the picker shows: "Void L - 6.8 + 6.3 km" once the range is known.</summary>
    public string Display => RangeText.Length > 0 ? $"{Name}  ·  {RangeText}" : Name;
}

/// <summary>
/// A doctrine fit turned into something readable/// <summary>
/// The reading of a doctrine fit that the dogma engine cannot do: which of its rounds its own guns
/// can actually load.
///
/// <para>Everything about damage, tank and mass comes from the engine - see DogmaService. What the
/// engine does not know is that Alliance Auth records no loaded charge, so something has to pick
/// one, and the fit's own cargo is the honest place to look.</para>
/// </summary>
public class FitAnalyzer(EveTypeCache types)
{
    /// <summary>Every type a fit references, for warming the cache before reading it.</summary>
    public static IEnumerable<int> TypeIdsOf(AaFitting fit) =>
        fit.Items.Select(i => i.TypeId).Append(fit.ShipTypeId).Where(id => id > 0);

    /// <summary>Warm the cache for a whole doctrine in one sweep, so opening it is not a stutter of
    /// individual lookups.</summary>
    public Task WarmAsync(IEnumerable<AaFitting> fits, CancellationToken ct = default) =>
        types.EnsureAsync(fits.SelectMany(TypeIdsOf), ct);

    /// <summary>
    /// Does this high-slot module shoot? Turrets and disintegrators carry a damage multiplier
    /// and announce themselves; a missile launcher holds only a cycle time, because the damage
    /// is all in the missile. So a launcher is judged by what it FIRES - which also keeps probe
    /// launchers, bomb launchers and interdiction spheres out, none of which are what an FC
    /// means by a gun.
    /// </summary>
    private bool IsWeapon(EveType module)
    {
        if (module.Attr(DogmaAttr.RateOfFire) <= 0) return false;
        if (module.Attr(DogmaAttr.DamageMultiplier) > 0) return true;
        return EveTypeCache.ChargeGroupIds(module).Any(types.ChargeGroupDoesDamage);
    }

    /// <summary>
    /// The ammunition this fit carries    /// <summary>
    /// The ammunition this fit carries that its own weapons can load.
    ///
    /// <para>Alliance Auth records no loaded charge - its importer reads "Neutron Blaster Cannon II,
    /// Void L" and keeps only the gun. What it does keep is the EFT's cargo section, so the ammo the
    /// doctrine actually ships with is usually sitting right there. Reading it back is both cheaper
    /// and more honest than picking a "best" charge on the alliance's behalf: their fit says what
    /// they load, so FCAT loads that.</para>
    ///
    /// <para>Cargo is not filtered by the fit's author, so it also holds ammo for a different hull,
    /// spare cap boosters and so on. Only charges this fit's guns accept come back.</para>
    /// </summary>
    public List<AmmoChoice> AmmoFor(AaFitting fit)
    {
        var weapons = fit.Items
            .Where(i => i.Slot == FitSlot.High)
            .Select(i => types.Known(i.TypeId))
            .Where(t => t != null && IsWeapon(t!))
            .ToList();

        if (weapons.Count == 0) return [];

        return fit.Items
            .Where(i => i.Slot == FitSlot.Cargo)
            .Select(i => types.Known(i.TypeId))
            .Where(c => c != null && Damages(c!))
            .Where(c => weapons.Any(w => CanLoad(w!, c!)))
            .GroupBy(c => c!.TypeId)
            .Select(g => new AmmoChoice(g.Key, g.First()!.Name, DamageOf(g.First()!)))
            .ToList();
    }

    /// <summary>The ammo a fit should be calculated with: the FC's pick when this fit's guns take
    /// it, otherwise whatever the fit carries, otherwise nothing.</summary>
    public int? AmmoToUse(AaFitting fit, int? preferred)
    {
        var carried = AmmoFor(fit);
        if (carried.Count == 0) return null;
        if (preferred is > 0 && carried.Any(a => a.TypeId == preferred)) return preferred;
        return carried[0].TypeId;
    }

    private static bool Damages(EveType charge) => DamageOf(charge) > 0;

    private static double DamageOf(EveType charge) =>
        charge.Attr(DogmaAttr.EmDamage) + charge.Attr(DogmaAttr.ExplosiveDamage)
      + charge.Attr(DogmaAttr.KineticDamage) + charge.Attr(DogmaAttr.ThermalDamage);

    /// <summary>Whether a gun can take a charge: the right family, and the right size. Size is only
    /// compared when both carry one - missiles do not, and their group alone decides.</summary>
    private static bool CanLoad(EveType weapon, EveType charge)
    {
        var accepted = false;
        for (var attr = DogmaAttr.FirstChargeGroup; attr <= DogmaAttr.LastChargeGroup; attr++)
            if ((int)weapon.Attr(attr) == charge.GroupId) { accepted = true; break; }
        if (!accepted) return false;

        var weaponSize = weapon.Attr(DogmaAttr.ChargeSize);
        var chargeSize = charge.Attr(DogmaAttr.ChargeSize);
        return weaponSize <= 0 || chargeSize <= 0 || Math.Abs(weaponSize - chargeSize) < 0.01;
    }

}
