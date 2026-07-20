using System.Collections.Generic;
using System.Linq;

namespace FCAT.Services;

/// <summary>One hole-class row in the fleet-mass tooltip: can the whole fleet pass one-way, and round-trip.</summary>
public record WormholeCheck(string Label, bool OneWay, bool RoundTrip)
{
    public string OneWayText    => OneWay    ? "yes" : "no";
    public string RoundTripText => RoundTrip ? "yes" : "no";
}

/// <summary>
/// Fleet-mass vs wormhole go/no-go helper for the fleet-mass tile tooltip.
///
/// Reference values (EVE University wiki, Wormhole_attributes): a hole's total mass budget
/// before it collapses is one of 100M / 500M / 750M / 1B / 2B / 3B / 3.3B / 5B kg. That total
/// is +/-10% at spawn and you can't read the exact figure in game, so we test against 90% of
/// nominal - the low end - rather than tell an FC they fit when the hole might crit early.
///
/// The fleet mass we feed in is summed BASE HULL mass from ESI. It excludes fits, which aren't
/// exposed for other pilots. Fits mostly ADD mass (plates are permanent; prop mods add while
/// hot, but holes are jumped cold), so the real number runs a little higher - the estimate is
/// slightly optimistic, which is why the readout leans on margins and says so out loud.
/// </summary>
public static class WormholeMass
{
    // Assume a hole spawned at the low end of its +/-10%, so "fits" stays on the safe side.
    private const double SpawnFloor = 0.90;

    // The hole classes an FC actually rushes a fleet through, by total mass budget.
    private static readonly (string Label, double Total)[] Classes =
    {
        ("1B", 1_000_000_000),
        ("2B", 2_000_000_000),
        ("3B", 3_000_000_000),
        ("5B", 5_000_000_000),
    };

    public static string Format(double kg) =>
        kg >= 1_000_000_000 ? $"{kg / 1_000_000_000:0.00}B kg"
      : kg >= 1_000_000     ? $"{kg / 1_000_000:0.#}M kg"
      :                       $"{kg:N0} kg";

    /// <summary>
    /// One-way / round-trip go/no-go for the whole fleet through a fresh hole of each class. The
    /// round-trip column also answers "does it fit a hole already at ~50%?" - the fleet's mass out
    /// of a half-spent budget is the same test as twice the mass out of a full one.
    /// </summary>
    public static IReadOnlyList<WormholeCheck> Checks(double fleetMass) =>
        Classes.Select(c =>
        {
            var budget = c.Total * SpawnFloor;
            return new WormholeCheck($"{c.Label} hole", fleetMass <= budget, fleetMass * 2.0 <= budget);
        }).ToList();
}
