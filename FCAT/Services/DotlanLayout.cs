using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace FCAT.Services;

/// <summary>
/// 2D map positions for every solar system, keyed by region.
///
/// EVE itself gives no usable flat map layout - a raw X/Z projection squashes a region into a thin
/// band, and neither ESI nor the SDE carries the arrangement players actually recognise. These
/// positions come from Dotlan's published region SVGs, extracted once and bundled with FCAT, so the
/// map needs no network and works offline.
///
/// ATTRIBUTION: the layout is Wollari's work (dotlan.net) - FCAT credits it on the map. If you fork
/// or redistribute this, keep the credit, and ask before relying on it commercially.
///
/// Regenerating (only needed when CCP adds or removes systems): fetch
/// https://evemaps.dotlan.net/svg/&lt;Region_Name&gt;.svg for each region and read every
/// <c>&lt;use id="sys&lt;esiSystemId&gt;" x=".." y=".." width=".." height=".."/&gt;</c>, storing the
/// box centre. 113 regions / ~9,350 systems as of 2026-08.
/// </summary>
public static class DotlanLayout
{
    private sealed record Bundle(
        Dictionary<string, Dictionary<int, Point>> Layout,
        Dictionary<int, int[]> Gates);

    private static readonly Lazy<Bundle> Data = new(Load);

    /// <summary>Positions for one region, or null if we have no layout for it (wormhole space).</summary>
    public static Dictionary<int, Point>? ForRegion(string? regionName)
    {
        if (string.IsNullOrWhiteSpace(regionName)) return null;
        return Data.Value.Layout.TryGetValue(regionName, out var systems) ? systems : null;
    }

    /// <summary>
    /// The systems one gate from <paramref name="systemId"/>, or null if we don't have it.
    ///
    /// Gates never move, so resolving them live was ~13 ESI round trips per constellation - over half
    /// the time the map spent loading. Bundling the graph makes that free. Verified against ESI.
    /// </summary>
    public static int[]? Neighbours(int systemId)
        => Data.Value.Gates.TryGetValue(systemId, out var n) ? n : null;

    /// <summary>
    /// Every system within <paramref name="maxJumps"/> gates of the origin, with its jump distance.
    /// Plain breadth-first search over the bundled graph - no ESI, no network, so "what's 5 jumps
    /// out" is free to ask. The origin itself is included at distance 0.
    /// </summary>
    public static Dictionary<int, int> JumpsWithin(int origin, int maxJumps)
    {
        var dist = new Dictionary<int, int> { [origin] = 0 };
        if (maxJumps <= 0) return dist;

        var frontier = new Queue<int>();
        frontier.Enqueue(origin);
        while (frontier.Count > 0)
        {
            var id = frontier.Dequeue();
            var d = dist[id];
            if (d >= maxJumps) continue;
            foreach (var n in Neighbours(id) ?? [])
                if (dist.TryAdd(n, d + 1))
                    frontier.Enqueue(n);
        }
        return dist;
    }

    private static Bundle Load()
    {
        var layout = new Dictionary<string, Dictionary<int, Point>>(StringComparer.OrdinalIgnoreCase);
        var gates  = new Dictionary<int, int[]>();
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("dotlan-layout.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return new Bundle(layout, gates);

            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return new Bundle(layout, gates);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty("layout", out var layoutNode))
                foreach (var region in layoutNode.EnumerateObject())
                {
                    var systems = new Dictionary<int, Point>();
                    foreach (var sys in region.Value.EnumerateObject())
                    {
                        if (!int.TryParse(sys.Name, out var id)) continue;
                        var xy = sys.Value;
                        if (xy.ValueKind != JsonValueKind.Array || xy.GetArrayLength() < 2) continue;
                        systems[id] = new Point(xy[0].GetDouble(), xy[1].GetDouble());
                    }
                    if (systems.Count > 0) layout[region.Name] = systems;
                }

            if (doc.RootElement.TryGetProperty("gates", out var gatesNode))
                foreach (var entry in gatesNode.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out var id)) continue;
                    var list = new List<int>();
                    foreach (var n in entry.Value.EnumerateArray())
                        if (int.TryParse(n.GetString(), out var nid)) list.Add(nid);
                    if (list.Count > 0) gates[id] = [.. list];
                }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt bundle shouldn't take the app down - the map falls back to live ESI.
        }
        return new Bundle(layout, gates);
    }
}
