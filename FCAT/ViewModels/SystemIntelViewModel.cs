using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>A node in the constellation map (a solar system).</summary>
public record MapNode(double NodeLeft, double NodeTop, double CenterX, double CenterY,
                      int SystemId, string Name, string Sec, Brush Fill, Brush SovFill,
                      string SovLabel, bool IsCurrent, string KillBadge, bool Hot);

/// <summary>A gate link between two systems on the map.</summary>
public record MapLink(double X1, double Y1, double X2, double Y2);

/// <summary>A neighbour system row (textual companion to the map).</summary>
public record NeighborRow(string Name, string Sec, Brush SecColor, string Activity, bool Hot);

/// <summary>
/// Always shows the FC's CURRENT system: security, sov, recent kills/jumps, and a small radial map
/// of its gate-connected neighbours. Auto-pulls from your in-game location (no search box) and
/// re-pulls when you jump systems.
/// </summary>
public partial class SystemIntelViewModel : ObservableObject
{
    private readonly EsiService _esi;
    private readonly EsiAuthService _auth;

    private Dictionary<int, (int ship, int pod, int npc)> _kills = [];
    private Dictionary<int, int>  _jumps = [];
    private Dictionary<int, int?> _sov   = [];
    private Dictionary<int, int>  _prevNpc = [];   // last poll's NPC kills, for the ratting delta
    private DateTime _activityAt;
    private readonly Dictionary<int, string> _nameCache = [];

    // Session caches so re-pulls (and revisited constellations) stay cheap on ESI.
    private readonly Dictionary<int, EsiSystem> _systemCache  = [];
    private readonly Dictionary<int, int>       _gateDestCache = [];   // stargateId → destination systemId

    private int _currentSystemId;
    private CancellationTokenSource? _cts;

    /// <summary>Raised (with the new system id + region name) whenever the FC jumps to a new system.</summary>
    public event Action<int, string>? SystemChanged;

    public SystemIntelViewModel(EsiService esi, EsiAuthService auth)
    {
        _esi = esi;
        _auth = auth;
    }

    // ── Map geometry — the canvas the constellation is projected into. Size is driven by the
    //    view (it fills the available space), so large constellations get room to spread out
    //    instead of piling up in a tiny fixed box. ──
    [ObservableProperty] private double _canvasWidth  = 360;
    [ObservableProperty] private double _canvasHeight = 360;
    private const double Margin = 46, NodeHalfW = 36, NodeHalfH = 15;

    // Cached so the map can re-project when the panel resizes, without re-hitting ESI.
    private List<EsiSystem> _mapSystems = [];
    private List<(int a, int b)> _mapLinks = [];

    /// <summary>Called by the view when the map area resizes — reprojects into the new size.</summary>
    public void SetCanvasSize(double w, double h)
    {
        if (w < 120 || h < 120) return;                 // ignore degenerate/initial layout passes
        if (Math.Abs(w - CanvasWidth) < 1 && Math.Abs(h - CanvasHeight) < 1) return;
        CanvasWidth = w;
        CanvasHeight = h;
        if (_mapSystems.Count > 0) BuildMap(_mapSystems, _mapLinks);
    }

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _hasResult;
    [ObservableProperty] private string _status = "Reading your current system…";

    [ObservableProperty] private string _systemName   = string.Empty;
    [ObservableProperty] private string _securityText = string.Empty;
    [ObservableProperty] private Brush  _securityBrush = Brushes.Gray;
    [ObservableProperty] private string _locationLine = string.Empty;
    [ObservableProperty] private string _sovHolder    = string.Empty;
    [ObservableProperty] private string _sovTicker    = string.Empty;
    [ObservableProperty] private string _killsLine    = string.Empty;
    [ObservableProperty] private string _jumpsLine    = string.Empty;

    public ObservableCollection<MapNode>     MapNodes   { get; } = [];
    public ObservableCollection<MapLink>     MapLinks   { get; } = [];
    public ObservableCollection<NeighborRow> Neighbors  { get; } = [];
    public ObservableCollection<NeighborRow> HotSystems { get; } = [];
    public bool HasHotSystems => HotSystems.Count > 0;

    // ── Auto-refresh lifecycle (driven by the view's load/unload) ──
    public void StartAuto()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void StopAuto()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await SafeRefresh(false);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct))
                await SafeRefresh(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task SafeRefresh(bool force)
    {
        try { await RefreshCurrentAsync(force); }
        catch (Exception ex) { Status = $"Update failed — retrying ({ex.Message})"; }
    }

    [RelayCommand] private async Task Refresh() => await SafeRefresh(true);

    private async Task RefreshCurrentAsync(bool force)
    {
        var loc = await _esi.GetCharacterLocationAsync(_auth.AuthenticatedCharacterId);
        if (loc == null || loc.SolarSystemId == 0)
        {
            if (!HasResult) Status = "Couldn't read your location — are you docked/undocked in space?";
            return;
        }

        // Only do the heavier system/neighbour load when you've actually jumped (or on a forced refresh).
        if (!force && loc.SolarSystemId == _currentSystemId) return;
        await LoadSystemAsync(loc.SolarSystemId, force);
    }

    private async Task LoadSystemAsync(int systemId, bool force)
    {
        IsBusy = true;
        try
        {
            var sys = await GetSystemCachedAsync(systemId);
            if (sys == null) { Status = "Couldn't load that system."; return; }
            var jumped = systemId != _currentSystemId;
            _currentSystemId = systemId;

            await EnsureActivityAsync(force);

            var con = await _esi.GetConstellationAsync(sys.ConstellationId);
            var region = con != null ? await _esi.GetRegionNameAsync(con.RegionId) : null;
            LocationLine = $"{con?.Name} · {region}".Trim(' ', '·');
            SovHolder = await ResolveSovAsync(systemId);
            SovTicker = await ResolveSovTickerAsync(systemId);

            // Load every system in the constellation (positions + stargates) for a Dotlan-style map.
            var conSystemIds = con?.Systems ?? [systemId];
            var loaded = await Task.WhenAll(conSystemIds.Select(GetSystemCachedAsync));
            var systems = loaded.Where(s => s?.Position != null).Select(s => s!).ToList();
            if (systems.All(s => s.SystemId != systemId)) systems.Add(sys);   // always include current

            // Resolve gate links that stay inside the constellation.
            var inCon = systems.Select(s => s.SystemId).ToHashSet();
            var links = await ResolveConstellationLinksAsync(systems, inCon);

            // Direct gate neighbours of the current system (for the textual list beside the map).
            var neighbourIds = links.Where(l => l.a == systemId || l.b == systemId)
                                    .Select(l => l.a == systemId ? l.b : l.a).ToHashSet();
            var neighbours = systems.Where(s => neighbourIds.Contains(s.SystemId)).ToList();

            await ResolveSovNamesAsync(systems);

            BuildHeader(sys);
            BuildNeighbours(neighbours);
            BuildHotSystems(systems);
            BuildMap(systems, links);

            Status = $"{sys.Name} · {systems.Count} systems in {con?.Name}";
            HasResult = true;

            if (jumped) SystemChanged?.Invoke(systemId, region ?? string.Empty);
        }
        finally { IsBusy = false; }
    }

    // A system is "hot" if it has a real kill rate, a high jump rate, or NPC kills rising (ratting up).
    private const int HotKills = 2;    // ship+pod kills in 1h
    private const int HotJumps = 40;   // jumps in 1h

    /// <summary>Constellation systems flagged hot by kills / jumps / rising NPC kills, ranked by heat.</summary>
    private void BuildHotSystems(List<EsiSystem> systems)
    {
        HotSystems.Clear();
        var ranked = systems
            .Select(s =>
            {
                var k = _kills.GetValueOrDefault(s.SystemId);
                var jumps = _jumps.GetValueOrDefault(s.SystemId);
                var npcDelta = _prevNpc.TryGetValue(s.SystemId, out var prev) ? k.npc - prev : 0;
                return (s, kills: k.ship + k.pod, jumps, npcDelta);
            })
            .Where(x => x.kills >= HotKills || x.jumps >= HotJumps || x.npcDelta > 0)
            .OrderByDescending(x => x.kills * 100 + Math.Max(0, x.npcDelta) * 5 + x.jumps)
            .Take(8)
            .ToList();

        foreach (var (s, kills, jumps, npcDelta) in ranked)
        {
            var (activity, hot) =
                kills > 0    ? ($"{kills} kills", true) :
                npcDelta > 0 ? ($"NPC ↑{npcDelta}", true) :
                               ($"{jumps} jumps", false);
            HotSystems.Add(new NeighborRow(s.Name, s.SecurityStatus.ToString("0.0"), SecBrush(s.SecurityStatus), activity, hot));
        }
        OnPropertyChanged(nameof(HasHotSystems));
    }

    private async Task<EsiSystem?> GetSystemCachedAsync(int id)
    {
        if (_systemCache.TryGetValue(id, out var cached)) return cached;
        var sys = await _esi.GetSystemAsync(id);
        if (sys != null) _systemCache[id] = sys;
        return sys;
    }

    /// <summary>Returns the set of undirected gate links between systems that are both in the constellation.</summary>
    private async Task<List<(int a, int b)>> ResolveConstellationLinksAsync(List<EsiSystem> systems, HashSet<int> inCon)
    {
        var links = new HashSet<(int, int)>();
        var gatesToFetch = systems.SelectMany(s => s.Stargates ?? [])
                                  .Where(g => !_gateDestCache.ContainsKey(g)).Distinct().ToList();
        if (gatesToFetch.Count > 0)
        {
            var fetched = await Task.WhenAll(gatesToFetch.Select(_esi.GetStargateAsync));
            for (var i = 0; i < gatesToFetch.Count; i++)
                if (fetched[i]?.Destination != null)
                    _gateDestCache[gatesToFetch[i]] = fetched[i]!.Destination!.SystemId;
        }

        foreach (var s in systems)
            foreach (var gate in s.Stargates ?? [])
                if (_gateDestCache.TryGetValue(gate, out var dest) && inCon.Contains(dest))
                {
                    var edge = s.SystemId < dest ? (s.SystemId, dest) : (dest, s.SystemId);
                    links.Add(edge);
                }
        return links.ToList();
    }

    private void BuildHeader(EsiSystem sys)
    {
        SystemName    = sys.Name;
        SecurityText  = sys.SecurityStatus.ToString("0.0");
        SecurityBrush = SecBrush(sys.SecurityStatus);
        var k = _kills.GetValueOrDefault(sys.SystemId);
        KillsLine = $"{k.ship} ship · {k.pod} pod · {k.npc} NPC kills (1h)";
        JumpsLine = $"{_jumps.GetValueOrDefault(sys.SystemId)} jumps (1h)";
    }

    private void BuildNeighbours(List<EsiSystem> neighbours)
    {
        Neighbors.Clear();
        foreach (var n in neighbours.OrderBy(n => n.Name))
        {
            var k = _kills.GetValueOrDefault(n.SystemId);
            var hot = k.ship + k.pod > 0;
            Neighbors.Add(new NeighborRow(n.Name, n.SecurityStatus.ToString("0.0"), SecBrush(n.SecurityStatus),
                hot ? $"{k.ship + k.pod} kills" : $"{_jumps.GetValueOrDefault(n.SystemId)} jumps", hot));
        }
    }

    /// <summary>
    /// Projects the constellation's systems onto the canvas using their real ESI positions
    /// (top-down: X → horizontal, Z → vertical, flipped to match Dotlan/in-game orientation),
    /// then draws the actual gate links between them.
    /// </summary>
    private void BuildMap(List<EsiSystem> systems, List<(int a, int b)> links)
    {
        _mapSystems = systems;   // cache so SetCanvasSize can re-project on resize
        _mapLinks   = links;

        MapNodes.Clear();
        MapLinks.Clear();
        if (systems.Count == 0) return;

        var xs = systems.Select(s => s.Position!.X).ToList();
        var zs = systems.Select(s => s.Position!.Z).ToList();
        double minX = xs.Min(), maxX = xs.Max(), minZ = zs.Min(), maxZ = zs.Max();
        double rangeX = maxX - minX, rangeZ = maxZ - minZ;
        double span = Math.Max(rangeX, rangeZ);   // uniform scale keeps the shape undistorted
        // Scale to the SMALLER dimension so the (now possibly non-square) canvas never distorts
        // the shape or pushes nodes off an edge.
        double usable = Math.Min(CanvasWidth, CanvasHeight) - 2 * Margin;

        Point Project(EsiPosition p)
        {
            // Centre each axis within the canvas; fall back to the middle when a system is alone.
            var nx = span > 0 ? (p.X - minX - rangeX / 2) / span : 0;
            var nz = span > 0 ? (p.Z - minZ - rangeZ / 2) / span : 0;
            return new Point(CanvasWidth / 2 + nx * usable, CanvasHeight / 2 - nz * usable);
        }

        var centres = systems.ToDictionary(s => s.SystemId, s => Project(s.Position!));
        RelaxOverlaps(centres);

        foreach (var (a, b) in links)
            if (centres.TryGetValue(a, out var pa) && centres.TryGetValue(b, out var pb))
                MapLinks.Add(new MapLink(pa.X, pa.Y, pb.X, pb.Y));

        foreach (var sys in systems)
        {
            var c = centres[sys.SystemId];
            MapNodes.Add(MakeNode(sys, c.X, c.Y, sys.SystemId == _currentSystemId));
        }
    }

    /// <summary>
    /// Many constellations have systems sitting at nearly identical coordinates, so a raw
    /// coordinate projection plots them on top of each other no matter how big the canvas is.
    /// This nudges any pair closer than the node footprint apart over a few iterations, then
    /// clamps everything inside the canvas — readable layout, geography roughly preserved.
    /// </summary>
    private void RelaxOverlaps(Dictionary<int, Point> centres)
    {
        const double minDist = 74;   // a node bubble + its label need roughly this much breathing room
        var ids = centres.Keys.ToList();
        var rng = new Random(17);    // fixed seed → stable layout across re-projections

        for (int iter = 0; iter < 80; iter++)
        {
            bool moved = false;
            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                {
                    var pi = centres[ids[i]];
                    var pj = centres[ids[j]];
                    double dx = pj.X - pi.X, dy = pj.Y - pi.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d >= minDist) continue;

                    if (d < 0.01)   // coincident — shove apart in a random direction
                    {
                        double ang = rng.NextDouble() * Math.PI * 2;
                        dx = Math.Cos(ang); dy = Math.Sin(ang); d = 1;
                    }
                    double push = (minDist - d) / 2;
                    double ux = dx / d, uy = dy / d;
                    centres[ids[i]] = new Point(pi.X - ux * push, pi.Y - uy * push);
                    centres[ids[j]] = new Point(pj.X + ux * push, pj.Y + uy * push);
                    moved = true;
                }
            if (!moved) break;
        }

        // Keep nodes inside the canvas after the pushing.
        foreach (var id in ids)
        {
            var p = centres[id];
            centres[id] = new Point(
                Math.Clamp(p.X, Margin, CanvasWidth  - Margin),
                Math.Clamp(p.Y, Margin, CanvasHeight - Margin));
        }
    }

    private MapNode MakeNode(EsiSystem sys, double cx, double cy, bool isCurrent)
    {
        var k = _kills.GetValueOrDefault(sys.SystemId);
        var hot = k.ship + k.pod > 0;
        var sovId = _sov.GetValueOrDefault(sys.SystemId);
        var sovLabel = sovId is > 0 ? _nameCache.GetValueOrDefault(sovId.Value, "") : "";
        return new MapNode(cx - NodeHalfW, cy - NodeHalfH, cx, cy,
            sys.SystemId, sys.Name, sys.SecurityStatus.ToString("0.0"),
            SecBrush(sys.SecurityStatus), SovBrush(sovId), sovLabel,
            isCurrent, hot ? (k.ship + k.pod).ToString() : string.Empty, hot);
    }

    /// <summary>Resolves alliance names for every sov-held system in the constellation (one batch).</summary>
    private async Task ResolveSovNamesAsync(List<EsiSystem> systems)
    {
        var ids = systems.Select(s => _sov.GetValueOrDefault(s.SystemId))
                         .Where(a => a is > 0).Select(a => a!.Value)
                         .Where(a => !_nameCache.ContainsKey(a)).Distinct().ToList();
        if (ids.Count == 0) return;
        foreach (var (id, name) in await _esi.ResolveNamesAsync(ids))
            if (name.Length > 0) _nameCache[id] = name;
    }

    private async Task<string> ResolveSovAsync(int systemId)
    {
        if (!_sov.TryGetValue(systemId, out var allianceId) || allianceId is null or 0) return string.Empty;
        if (_nameCache.TryGetValue(allianceId.Value, out var cached)) return cached;
        var name = (await _esi.ResolveNamesAsync([allianceId.Value])).GetValueOrDefault(allianceId.Value, "");
        if (name.Length > 0) _nameCache[allianceId.Value] = name;
        return name;
    }

    private readonly Dictionary<int, string> _tickerCache = [];

    private async Task<string> ResolveSovTickerAsync(int systemId)
    {
        if (!_sov.TryGetValue(systemId, out var allianceId) || allianceId is null or 0) return string.Empty;
        if (_tickerCache.TryGetValue(allianceId.Value, out var cached)) return cached;
        var ticker = (await _esi.GetAlliancePublicInfoAsync(allianceId.Value))?.Ticker ?? string.Empty;
        if (ticker.Length > 0) _tickerCache[allianceId.Value] = ticker;
        return ticker;
    }

    private async Task EnsureActivityAsync(bool force)
    {
        if (!force && DateTime.UtcNow - _activityAt < TimeSpan.FromMinutes(2) && _kills.Count > 0) return;

        var killsTask = _esi.GetSystemKillsAsync();
        var jumpsTask = _esi.GetSystemJumpsAsync();
        var sovTask   = _esi.GetSovMapAsync();
        await Task.WhenAll(killsTask, jumpsTask, sovTask);

        // Remember the previous NPC counts so we can show a ratting delta (data changes ~hourly on ESI).
        _prevNpc = _kills.ToDictionary(k => k.Key, k => k.Value.npc);
        _kills = killsTask.Result.ToDictionary(k => k.SystemId, k => (k.ShipKills, k.PodKills, k.NpcKills));
        _jumps = jumpsTask.Result.ToDictionary(j => j.SystemId, j => j.ShipJumps);
        _sov   = sovTask.Result.ToDictionary(s => s.SystemId, s => s.AllianceId);
        _activityAt = DateTime.UtcNow;
    }

    private static Brush SecBrush(double sec) => sec >= 0.45 ? Green : sec > 0.0 ? Amber : Red;

    private static readonly Brush Green = Frozen(0x3f, 0xb3, 0x50);
    private static readonly Brush Amber = Frozen(0xe3, 0xb3, 0x41);
    private static readonly Brush Red   = Frozen(0xe2, 0x57, 0x4c);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    // ── Sovereignty coloring ──
    // Each alliance gets a stable colour from this palette (hashed by id) so you can see at a
    // glance which systems share an owner; unclaimed systems are a neutral slate.
    private static readonly Brush SovNone = Frozen(0x3a, 0x44, 0x55);
    private static readonly Brush[] SovPalette =
    [
        Frozen(0x5a, 0x8f, 0xd6), Frozen(0x3f, 0xae, 0x8f), Frozen(0xc9, 0x88, 0x3e),
        Frozen(0x9b, 0x7b, 0xd4), Frozen(0xd4, 0x6a, 0x6a), Frozen(0x4d, 0xb8, 0xa0),
        Frozen(0xd0, 0xb0, 0x55), Frozen(0xc0, 0x68, 0xb0), Frozen(0x6f, 0x9b, 0x5a),
    ];

    private static Brush SovBrush(int? allianceId)
        => allianceId is > 0 ? SovPalette[(allianceId.Value & 0x7fffffff) % SovPalette.Length] : SovNone;

    // ── Per-system links (right-click a map bubble) ──
    [RelayCommand]
    private static void OpenDotlan(MapNode? node)
    {
        if (node == null) return;
        var name = node.Name.Replace(' ', '_');
        OpenUrl($"https://evemaps.dotlan.net/system/{Uri.EscapeDataString(name)}");
    }

    [RelayCommand]
    private static void OpenZkill(MapNode? node)
    {
        if (node == null) return;
        OpenUrl($"https://zkillboard.com/system/{node.SystemId}/");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — nothing useful to do */ }
    }
}
