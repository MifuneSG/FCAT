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

/// <summary>A charge a doctrine fit carries and its own guns can actually load. Damage is the sum
/// of its four types, used only to order the picker - the engine does the real sums.</summary>
public record AmmoChoice(int TypeId, string Name, double Damage);

/// <summary>One line of a resolved fit, with the name filled in from the type cache.</summary>
public record FitLine(int TypeId, string Name, FitSlot Slot, int Quantity);

/// <summary>
/// A doctrine fit turned into something readable: named modules in slot order, the weapons it
/// actually mounts, and what it carries in the drone bay.
///
/// <para>This deliberately stops short of computing damage. Getting a fit's DPS right means running
/// EVE's dogma properly - every modifier, its operator, the stacking penalty, and the skill each
/// ship bonus is gated on - and ESI does not serve the last of those: the effect endpoint says what
/// an effect modifies but not what it applies to. A number that is close is worse than no number,
/// because an FC will plan around it. So this resolves the fit and leaves the damage to whatever we
/// settle on for that.</para>
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

    /// <summary>The fit as named lines, grouped the way an FC reads one: high, mid, low, rigs, then
    /// what is in the bays. Unknown types keep their id rather than vanishing.</summary>
    public List<FitLine> Lines(AaFitting fit)
    {
        static int Order(FitSlot slot) => slot switch
        {
            FitSlot.High => 0, FitSlot.Mid => 1, FitSlot.Low => 2, FitSlot.Rig => 3,
            FitSlot.Subsystem => 4, FitSlot.Service => 5, FitSlot.DroneBay => 6,
            FitSlot.FighterBay => 7, FitSlot.Cargo => 8, _ => 9,
        };

        return fit.Items
            .Select(i => new FitLine(i.TypeId, NameOf(i.TypeId), i.Slot, i.Quantity))
            .OrderBy(l => Order(l.Slot))
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The fit's weapons: high-slot modules that cycle and hurt something.
    ///
    /// <para>Turrets and disintegrators carry a damage multiplier, so they announce themselves. A
    /// MISSILE launcher does not - it holds a cycle time and nothing else, because all of the damage
    /// lives in the missile. Testing for a damage multiplier alone therefore reads every Caracal,
    /// Cerberus and bomber as unarmed, and a doctrine full of them as support. So a launcher is
    /// judged by what it fires: does its charge group do damage. That also keeps probe launchers,
    /// bomb launchers and interdiction sphere launchers out, which all cycle and none of which are
    /// what an FC means by a gun.</para>
    /// </summary>
    public List<FitLine> Weapons(AaFitting fit) =>
        fit.Items
            .Where(i => i.Slot == FitSlot.High)
            .Where(i => types.Known(i.TypeId) is { } t && IsWeapon(t))
            .GroupBy(i => i.TypeId)
            .Select(g => new FitLine(g.Key, NameOf(g.Key), FitSlot.High, g.Count()))
            .ToList();

    private bool IsWeapon(EveType module)
    {
        if (module.Attr(DogmaAttr.RateOfFire) <= 0) return false;
        if (module.Attr(DogmaAttr.DamageMultiplier) > 0) return true;
        return EveTypeCache.ChargeGroupIds(module).Any(types.ChargeGroupDoesDamage);
    }

    /// <summary>
    /// Damage modules - heat sinks, gyros, magnetic field stabilisers, ballistic control systems.
    /// Found by what they do (raise the damage multiplier, or shorten the cycle) rather than by a
    /// list of group ids that would need updating whenever CCP adds one.
    /// </summary>
    public List<FitLine> DamageMods(AaFitting fit) =>
        fit.Items
            .Where(i => i.Slot is FitSlot.Low or FitSlot.Mid)
            .Where(i => types.Known(i.TypeId) is { } t
                        && (t.Attr(DogmaAttr.DamageMultiplier) > 1 || (t.Attr(DogmaAttr.RofMultiplier) is > 0 and < 1)))
            .GroupBy(i => i.TypeId)
            .Select(g => new FitLine(g.Key, NameOf(g.Key), FitSlot.Low, g.Count()))
            .ToList();

    /// <summary>
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

    /// <summary>What is in the drone bay, as stacks.</summary>
    public List<FitLine> Drones(AaFitting fit) =>
        fit.Items
            .Where(i => i.Slot == FitSlot.DroneBay)
            .Select(i => new FitLine(i.TypeId, NameOf(i.TypeId), FitSlot.DroneBay, i.Quantity))
            .ToList();

    /// <summary>
    /// The fit's fleet role. The hull's group already gets this right nearly always, so this only
    /// overrides where the modules genuinely change the answer - a hull that mounts no weapons is
    /// not the DPS its group claims, whatever the classifier says.
    /// </summary>
    public ShipRole Role(AaFitting fit)
    {
        var hull = types.Known(fit.ShipTypeId);
        var byGroup = hull == null
            ? ShipRole.Unknown
            : ShipRoleClassifier.Classify(fit.ShipTypeId, hull.GroupId);

        // A T3 cruiser or a Command Ship fitted with nothing that shoots is doing another job.
        if (byGroup == ShipRole.DPS && Weapons(fit).Count == 0) return ShipRole.Support;
        return byGroup;
    }

    /// <summary>A one-line summary for a fit row: "5x Heavy Beam Laser II · 2 damage mods · 5 drones".</summary>
    public string Summary(AaFitting fit)
    {
        var parts = new List<string>();

        foreach (var weapon in Weapons(fit))
            parts.Add($"{weapon.Quantity}x {weapon.Name}");

        var mods = DamageMods(fit).Sum(m => m.Quantity);
        if (mods > 0) parts.Add($"{mods} damage mod{(mods == 1 ? string.Empty : "s")}");

        var drones = Drones(fit).Sum(d => d.Quantity);
        if (drones > 0) parts.Add($"{drones} drones");

        return parts.Count > 0 ? string.Join(" · ", parts) : "no weapons fitted";
    }

    private string NameOf(int typeId) => types.Known(typeId)?.Name ?? typeId.ToString();
}
