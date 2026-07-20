namespace FCAT.Services;

/// <summary>One hole-class row in the fleet-mass tooltip.</summary>
public record WormholeCheck(string Label, bool OneWay, bool RoundTrip)
{
    public string OneWayText    => OneWay    ? "yes" : "no";
    public string RoundTripText => RoundTrip ? "yes" : "no";
}

/// <summary>
/// Fleet mass vs wormhole capacity. Hole totals come from the EVE University wormhole table and
/// vary +/-10% at spawn, so checks run against 90% of nominal. Fleet mass is base hull only -
/// ESI doesn't expose other pilots' fits, and fits mostly add mass, so the figure runs low.
/// </summary>
public static class WormholeMass
{
    private const double SpawnFloor = 0.90;

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

    /// <summary>Round-trip also answers whether the fleet fits a hole already at ~50%.</summary>
    public static IReadOnlyList<WormholeCheck> Checks(double fleetMass) =>
        Classes.Select(c =>
        {
            var budget = c.Total * SpawnFloor;
            return new WormholeCheck($"{c.Label} hole", fleetMass <= budget, fleetMass * 2.0 <= budget);
        }).ToList();
}
