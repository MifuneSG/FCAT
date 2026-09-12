using System.IO;
using System.Reflection;
using System.Text.Json;

namespace FCAT.Services;

/// <summary>
/// Real 3D positions for every solar system, in light years, for jump-range work.
///
/// This is a different question from the one <see cref="DotlanLayout"/> answers. That one is about
/// how a constellation is DRAWN - a flat schematic players recognise. This one is about where
/// systems actually ARE, because a jump drive doesn't care about gates or regions, only straight-line
/// distance. Nothing about the map layout can answer "what is within 8 light years of here".
///
/// Positions are bundled rather than fetched: ESI serves one system per call, and a range check
/// measures the origin against all of New Eden at once. Live that would be thousands of requests
/// per lookup; bundled it is a dictionary scan.
///
/// The asset stores coordinates already converted to light years, so <see cref="Distance"/> is a
/// plain Euclidean distance with no unit conversion at call time.
///
/// Regenerating (only needed when CCP adds or removes systems): read every id from
/// GET /v1/universe/systems/, then GET /v4/universe/systems/{id}/ for its <c>position</c> (metres)
/// and <c>security_status</c>, and write <c>{"ly": {"&lt;systemId&gt;": [x, y, z, security]}}</c> with
/// each axis divided by <see cref="MetresPerLightYear"/>. 5,485 jump-reachable systems as of 2026-08.
/// </summary>
public static class SystemSpace
{
    /// <summary>One system's position (light years) and security status.</summary>
    public readonly record struct SystemPoint(double X, double Y, double Z, double Security)
    {
        /// <summary>
        /// EVE refuses jump drives into high security space, and the cutoff is EVE's own rounding:
        /// anything that displays as 0.5 or above is hisec, so the real boundary sits at 0.45.
        /// </summary>
        public bool JumpLegal => Security < 0.45;
    }

    /// <summary>Metres in a light year - the divisor used to build the bundled asset from ESI.</summary>
    public const double MetresPerLightYear = 9_460_700_000_000_000d;

    private static readonly Lazy<Dictionary<int, SystemPoint>> Points = new(Load);

    public static int Count => Points.Value.Count;

    public static SystemPoint? Get(int systemId)
        => Points.Value.TryGetValue(systemId, out var p) ? p : null;

    public static bool Knows(int systemId) => Points.Value.ContainsKey(systemId);

    /// <summary>Straight-line distance in light years, or null if either system isn't in the bundle.</summary>
    public static double? LightYearsBetween(int a, int b)
        => Get(a) is { } pa && Get(b) is { } pb ? Distance(pa, pb) : null;

    private static double Distance(SystemPoint a, SystemPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Every system a jump drive could reach from here, nearest first. Hisec is excluded outright -
    /// it is in range geometrically and unreachable in practice, so listing it would be noise.
    /// </summary>
    public static List<(int SystemId, double LightYears)> WithinRange(int originId, double lightYears)
    {
        var hits = new List<(int SystemId, double LightYears)>();
        if (Get(originId) is not { } origin || lightYears <= 0) return hits;

        foreach (var (id, point) in Points.Value)
        {
            if (id == originId || !point.JumpLegal) continue;
            var ly = Distance(origin, point);
            if (ly <= lightYears) hits.Add((id, ly));
        }

        hits.Sort((x, y) => x.LightYears.CompareTo(y.LightYears));
        return hits;
    }

    private static Dictionary<int, SystemPoint> Load()
    {
        var points = new Dictionary<int, SystemPoint>();
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("system-space.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return points;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return points;
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("ly", out var ly)) return points;
            foreach (var entry in ly.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out var id)) continue;
                var xyz = entry.Value;
                if (xyz.ValueKind != JsonValueKind.Array || xyz.GetArrayLength() < 4) continue;
                points[id] = new SystemPoint(
                    xyz[0].GetDouble(), xyz[1].GetDouble(), xyz[2].GetDouble(), xyz[3].GetDouble());
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt bundle shouldn't take the app down - Hunt just finds nothing in range.
        }
        return points;
    }
}
