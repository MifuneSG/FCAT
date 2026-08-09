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

/// <summary>A node in the constellation map (a solar system). IsExit = a system in another
/// constellation reachable by one gate, drawn at the edge as a way out. Pulse = a kill has landed
/// since the last poll (drives the fresh-kill pulse). SovLabel is surfaced on hover only.</summary>
/// <summary>IsCurrent = the system on screen. IsHome = where the FC actually is, which differs once
/// they click off to explore - the map keeps marking home so they don't lose their own position.</summary>
public record MapNode(double NodeLeft, double NodeTop,
                      int SystemId, string Name, string SovLabel, string Stats, bool IsCurrent,
                      string KillBadge, bool Hot, bool Pulse, bool IsExit, bool IsHome);

/// <summary>A gate link between two systems on the map. IsExit = the link leaves the constellation
/// (drawn dashed, so the way out reads differently from internal gates).</summary>
public record MapLink(double X1, double Y1, double X2, double Y2, bool IsExit);

/// <summary>A fight worth knowing about: somewhere in range with PvP happening.</summary>
public record RangeRow(string Name, int Jumps, int Kills, bool Fresh)
{
    public string JumpsText => $"{Jumps}j";
    public string KillsText => Kills.ToString();
}

/// <summary>Something going on nearby that isn't a fight - ratting, a timer, an incursion.</summary>
public record ContentRow(string Name, int Jumps, string What, string Detail, ContentKind Kind)
{
    public string JumpsText => Jumps >= 0 ? $"{Jumps}j" : string.Empty;
}

public enum ContentKind { Ratting, Timer, Incursion, FactionWar }

/// <summary>A way out of here, and whether it looks safe to use.</summary>
public record EscapeRow(string Name, int Jumps, int Kills, string Note)
{
    public bool   IsHot     => Kills > 0;
    public string JumpsText => $"{Jumps}j";
    /// <summary>Quiet routes are the ones you actually want - shown first and marked.</summary>
    public string Status    => Kills > 0 ? $"{Kills} kills" : "clear";
}

/// <summary>
/// Tracks the system the FC is in and drives the map: a minimap of the surrounding constellation
/// (fixed scale, centred on you) plus the three boards an FC reads a map for - what's in range to
/// fight, what content is nearby, and where the exits are. Auto-pulls from your in-game location
/// (no search box) and re-pulls when you jump.
/// </summary>
public partial class SystemIntelViewModel : ObservableObject
{
    private readonly EsiService _esi;
    private readonly EsiAuthService _auth;
    private readonly SystemSearchService _systemSearch;
    /// <summary>Map layout for the region we're in. Null (wormhole space) = use our own layout.</summary>
    private Dictionary<int, Point>? _regionLayout;

    /// <summary>True when the map is drawn from Dotlan's coordinates, so the view can credit them.</summary>
    [ObservableProperty] private bool _usingDotlanLayout;

    private Dictionary<int, (int ship, int pod, int npc)> _kills = [];
    private Dictionary<int, int>  _jumps = [];
    private Dictionary<int, int?> _sov   = [];
    private Dictionary<int, int>  _prevNpc = [];   // last poll's NPC kills, for the ratting delta
    private bool _hasActivityBaseline;             // false until a second poll gives us something to diff
    private Dictionary<int, int>  _prevPvp = [];   // last poll's ship+pod kills, to pulse fresh kills
    private List<SovCampaign> _campaigns  = [];
    private List<Incursion>   _incursions = [];
    private Dictionary<int, FwSystem> _fw = [];
    private DateTime _activityAt;
    private readonly Dictionary<int, string> _nameCache = [];

    // Session caches so re-pulls (and revisited constellations) stay cheap on ESI.
    private readonly Dictionary<int, EsiSystem> _systemCache  = [];
    private readonly Dictionary<int, int>       _gateDestCache = [];   // stargateId -> destination systemId

    private int _currentSystemId;   // the system being VIEWED (may be pinned while exploring)
    private int _homeSystemId;      // where the FC actually is, regardless of exploring
    private CancellationTokenSource? _cts;

    /// <summary>Raised (with the new system id + region name) whenever the FC jumps to a new system.</summary>
    public event Action<int, string>? SystemChanged;

    /// <summary>Raised with the current system name + its gate neighbours, so the intel feed knows
    /// which call-outs are worth alerting on.</summary>
    public event Action<string, List<string>>? LocationContextChanged;

    public SystemIntelViewModel(EsiService esi, EsiAuthService auth, SystemSearchService systemSearch)
    {
        _esi = esi;
        _auth = auth;
        _systemSearch = systemSearch;
        _ = _systemSearch.EnsureLoadedAsync();   // local name index, for systems past the constellation
    }

    // Half the node slot. The slot is the DOT only (the label hangs below it out of the layout box),
    // so a gate link drawn to the node centre lands on the dot.
    private const double NodeHalfW = 36, NodeHalfH = 12;

    [ObservableProperty] private bool   _hasResult;
    [ObservableProperty] private string _status = "Reading your current system…";

    [ObservableProperty] private string _systemName   = string.Empty;
    [ObservableProperty] private string _locationLine = string.Empty;
    [ObservableProperty] private int _shipKills;
    [ObservableProperty] private int _podKills;
    [ObservableProperty] private int _npcKills;
    [ObservableProperty] private int _sysJumps;

    public ObservableCollection<MapNode> MapNodes { get; } = [];
    public ObservableCollection<MapLink> MapLinks { get; } = [];

    // The three things an FC is actually reading a map for:
    //   1. what's in range to fight        -> InRange
    //   2. what content is going on nearby -> Content
    //   3. what are my escapes             -> Escapes
    public ObservableCollection<RangeRow>   InRange { get; } = [];
    public ObservableCollection<ContentRow> Content { get; } = [];
    public ObservableCollection<EscapeRow>  Escapes { get; } = [];

    public bool HasInRange => InRange.Count > 0;
    public bool HasContent => Content.Count > 0;
    public bool HasEscapes => Escapes.Count > 0;

    /// <summary>How far out "in range" reaches. 5 gates suits most subcap roams.</summary>
    [ObservableProperty] private int _rangeJumps = 5;

    partial void OnRangeJumpsChanged(int value)
    {
        if (_currentSystemId != 0) BuildRangeBoards(_currentSystemId);
    }

    [RelayCommand]
    private void CycleRange()
    {
        RangeJumps = RangeJumps switch { <= 3 => 5, 5 => 8, 8 => 10, _ => 3 };
    }

    // Auto-refresh lifecycle (driven by the view's load/unload)
    // Ref-counted because this tracker outlives any one view: page navigation can fire the incoming
    // page's Loaded before the outgoing page's Unloaded, which would otherwise stop the polling.
    private int _autoUsers;

    public void StartAuto()
    {
        _autoUsers++;
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void StopAuto()
    {
        if (_autoUsers > 0) _autoUsers--;
        if (_autoUsers > 0) return;   // another page still needs it
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
        catch (Exception ex) { Status = $"Update failed, retrying ({ex.Message})"; }
    }

    // Explore mode: the System panel can be pinned to any clicked system instead of following your
    // live location. IsPinned drives the "back to current" control and stops the auto-loop from
    // yanking the view back while you look around.
    [ObservableProperty] private bool _isPinned;

    /// <summary>Left-click a map node to focus the whole System panel on it (gate outward via exits).</summary>
    public void FocusSystem(MapNode node)
    {
        if (node.SystemId == _currentSystemId) return;
        IsPinned = true;
        _ = SafeLoad(node.SystemId);
    }

    [RelayCommand]
    private async Task ReturnToCurrent()
    {
        IsPinned = false;
        await SafeRefresh(true);   // snap back to your live location
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsPinned) await SafeLoad(_currentSystemId);   // refresh the system you're viewing
        else await SafeRefresh(true);
    }

    private async Task SafeLoad(int systemId)
    {
        try { await LoadSystemAsync(systemId, force: true); }
        catch (Exception ex) { Status = $"Couldn't load that system: {ex.Message}"; }
    }

    private async Task RefreshCurrentAsync(bool force)
    {
        var loc = await _esi.GetCharacterLocationAsync(_auth.AuthenticatedCharacterId);
        if (loc == null || loc.SolarSystemId == 0)
        {
            if (!HasResult) Status = "Couldn't read your location. Are you docked or undocked in space?";
            return;
        }

        // Track where the FC actually is even while they're exploring somewhere else, so the map can
        // keep marking home. Without this you lose your own position the moment you click a system.
        _homeSystemId = loc.SolarSystemId;

        if (IsPinned) return;   // exploring a pinned system - don't yank the view back

        // Only do the heavier system/neighbour load when you've actually jumped (or on a forced refresh).
        if (!force && loc.SolarSystemId == _currentSystemId) return;
        await LoadSystemAsync(loc.SolarSystemId, force);
    }

    private async Task LoadSystemAsync(int systemId, bool force)
    {
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

            // Load every system in the constellation (positions + stargates) for a Dotlan-style map.
            var conSystemIds = con?.Systems ?? [systemId];
            var loaded = await Task.WhenAll(conSystemIds.Select(GetSystemCachedAsync));
            var systems = loaded.Where(s => s?.Position != null).Select(s => s!).ToList();
            if (systems.All(s => s.SystemId != systemId)) systems.Add(sys);   // always include current

            // One neighbour lookup drives both the internal links and the exits.
            var inCon = systems.Select(s => s.SystemId).ToHashSet();
            var gateMap = await ResolveNeighboursAsync(systems);

            var linkSet   = new HashSet<(int a, int b)>();
            var exitEdges = new List<(int a, int b)>();
            foreach (var s in systems)
                foreach (var dest in gateMap.GetValueOrDefault(s.SystemId, []))
                    if (inCon.Contains(dest))
                        linkSet.Add(s.SystemId < dest ? (s.SystemId, dest) : (dest, s.SystemId));
                    else
                        exitEdges.Add((s.SystemId, dest));
            var links = linkSet.ToList();
            var exitLoaded  = await Task.WhenAll(exitEdges.Select(e => e.b).Distinct().Select(GetSystemCachedAsync));
            var exitSystems = exitLoaded.Where(s => s?.Position != null).Select(s => s!).ToList();

            // Direct gate neighbours of the current system (for the textual list beside the map).
            var neighbourIds = links.Where(l => l.a == systemId || l.b == systemId)
                                    .Select(l => l.a == systemId ? l.b : l.a).ToHashSet();
            var neighbours = systems.Where(s => neighbourIds.Contains(s.SystemId)).ToList();

            await ResolveSovNamesAsync(systems.Concat(exitSystems).ToList());

            // Bundled with the app, so this is a dictionary lookup - no network, no waiting.
            _regionLayout = DotlanLayout.ForRegion(region);

            BuildHeader(sys);
            BuildMap(systems, links, exitSystems, exitEdges);
            BuildRangeBoards(systemId);   // needs _mapExits, so it runs after BuildMap
            LocationContextChanged?.Invoke(sys.Name, neighbours.Select(n => n.Name).ToList());

            Status = $"{sys.Name} · {systems.Count} systems in {con?.Name}";
            HasResult = true;

            if (jumped) SystemChanged?.Invoke(systemId, region ?? string.Empty);
        }
        catch (Exception ex) { Status = $"Couldn't load that system: {ex.Message}"; }
    }

    private async Task<EsiSystem?> GetSystemCachedAsync(int id)
    {
        if (_systemCache.TryGetValue(id, out var cached)) return cached;
        var sys = await _esi.GetSystemAsync(id);
        if (sys != null) _systemCache[id] = sys;
        return sys;
    }

    /// <summary>
    /// Which systems each of these is one gate from.
    ///
    /// Gates never move, so this comes from the bundled graph - it used to be ~13 ESI round trips per
    /// constellation, over half the time the map took to appear. Anything the bundle doesn't cover
    /// (wormhole space, or a system CCP added since) still falls back to resolving stargates live.
    /// </summary>
    private async Task<Dictionary<int, int[]>> ResolveNeighboursAsync(List<EsiSystem> systems)
    {
        var result  = new Dictionary<int, int[]>();
        var needEsi = new List<EsiSystem>();

        foreach (var s in systems)
        {
            var bundled = DotlanLayout.Neighbours(s.SystemId);
            if (bundled != null) result[s.SystemId] = bundled;
            else needEsi.Add(s);
        }
        if (needEsi.Count == 0) return result;

        var gatesToFetch = needEsi.SelectMany(s => s.Stargates ?? [])
                                  .Where(g => !_gateDestCache.ContainsKey(g)).Distinct().ToList();
        if (gatesToFetch.Count > 0)
        {
            var fetched = await Task.WhenAll(gatesToFetch.Select(_esi.GetStargateAsync));
            for (var i = 0; i < gatesToFetch.Count; i++)
                if (fetched[i]?.Destination != null)
                    _gateDestCache[gatesToFetch[i]] = fetched[i]!.Destination!.SystemId;
        }

        foreach (var s in needEsi)
            result[s.SystemId] = (s.Stargates ?? [])
                .Select(g => _gateDestCache.GetValueOrDefault(g))
                .Where(d => d != 0).Distinct().ToArray();

        return result;
    }

    private void BuildHeader(EsiSystem sys)
    {
        SystemName    = sys.Name;
        var k = _kills.GetValueOrDefault(sys.SystemId);
        ShipKills = k.ship; PodKills = k.pod; NpcKills = k.npc;
        SysJumps  = _jumps.GetValueOrDefault(sys.SystemId);
    }

    private static string SovEventLabel(string type) => type switch
    {
        "ihub_defense"     => "IHub",
        "tcu_defense"      => "TCU",
        "station_defense"  => "Station",
        "station_freeport" => "Freeport",
        _                  => "Sov",
    };

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    // Systems the FC can reach without leaving the map's own data - used by all three boards.
    private Dictionary<int, int> _reach = [];

    /// <summary>
    /// Fills the three boards an FC actually reads a map for. All of it runs off the bundled gate
    /// graph plus the activity data we already poll, so it costs nothing extra on ESI.
    /// </summary>
    private void BuildRangeBoards(int systemId)
    {
        _reach = DotlanLayout.JumpsWithin(systemId, RangeJumps);

        BuildInRange(systemId);
        BuildContent(systemId);
        BuildEscapes();

        OnPropertyChanged(nameof(HasInRange));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(HasEscapes));
    }

    /// <summary>1. What's in range to fight - PvP happening within reach, closest and hottest first.</summary>
    private void BuildInRange(int systemId)
    {
        InRange.Clear();
        var rows = _reach
            .Where(kv => kv.Key != systemId)
            .Select(kv =>
            {
                var k = _kills.GetValueOrDefault(kv.Key);
                var pvp = k.ship + k.pod;
                var fresh = pvp > _prevPvp.GetValueOrDefault(kv.Key);
                return new { Id = kv.Key, Jumps = kv.Value, Kills = pvp, Fresh = fresh };
            })
            .Where(x => x.Kills > 0)
            // A big fight 2 jumps out beats a small one 7 jumps out.
            .OrderByDescending(x => x.Kills * 10 - x.Jumps)
            .Take(8)
            .ToList();

        foreach (var r in rows)
            InRange.Add(new RangeRow(NameOf(r.Id), r.Jumps, r.Kills, r.Fresh));
    }

    /// <summary>2. What content is going on around me - ratting, sov timers, incursions, FW.</summary>
    private void BuildContent(int systemId)
    {
        Content.Clear();

        // Ratting: rank on the CHANGE in NPC kills, not the hourly total. A big total just means
        // "this is a ratting pocket"; a rising count means someone is out there right now.
        //
        // _prevNpc is empty until the second activity poll, and treating that as a huge spike would
        // light up the whole board on launch - so we show nothing until there's a real baseline.
        if (_hasActivityBaseline)
        {
            var ratting = _reach
                .Where(kv => kv.Key != systemId)
                .Select(kv => new
                {
                    Id = kv.Key,
                    Jumps = kv.Value,
                    Delta = _kills.GetValueOrDefault(kv.Key).npc - _prevNpc.GetValueOrDefault(kv.Key),
                })
                .Where(x => x.Delta > 0)
                .OrderByDescending(x => x.Delta * 5 - x.Jumps)
                .Take(4);

            foreach (var r in ratting)
                Content.Add(new ContentRow(NameOf(r.Id), r.Jumps, "Ratting",
                    $"+{r.Delta} NPC", ContentKind.Ratting));
        }

        // Sov timers anywhere in reach - these are scheduled fights.
        foreach (var c in _campaigns.Where(c => _reach.ContainsKey(c.SolarSystemId))
                                    .OrderBy(c => c.StartTime).Take(4))
        {
            var mins = (c.StartTime - DateTime.UtcNow).TotalMinutes;
            var when = mins <= 0 ? "now" : mins < 60 ? $"{mins:0}m" : $"{mins / 60:0.0}h";
            Content.Add(new ContentRow(NameOf(c.SolarSystemId), _reach[c.SolarSystemId],
                SovEventLabel(c.EventType), when, ContentKind.Timer));
        }

        // Incursion in the constellation you're standing in.
        var inc = _incursions.FirstOrDefault(i => _reach.Keys.Any(id =>
            _systemCache.TryGetValue(id, out var s) && s.ConstellationId == i.ConstellationId));
        if (inc != null)
            Content.Add(new ContentRow("Constellation", -1, "Incursion", Capitalize(inc.State), ContentKind.Incursion));

        // Contested faction-warfare systems in reach.
        foreach (var fw in _fw.Values.Where(f => _reach.ContainsKey(f.SolarSystemId)
                                              && !string.Equals(f.Contested, "uncontested", StringComparison.OrdinalIgnoreCase))
                                     .Take(3))
            Content.Add(new ContentRow(NameOf(fw.SolarSystemId), _reach[fw.SolarSystemId],
                "FW", Capitalize(fw.Contested), ContentKind.FactionWar));
    }

    /// <summary>
    /// 3. What are my escapes - the ways out of this constellation, quietest first, because an exit
    /// with a fight on it isn't an escape.
    /// </summary>
    private void BuildEscapes()
    {
        Escapes.Clear();
        var rows = _mapExits
            .Select(e =>
            {
                var k = _kills.GetValueOrDefault(e.SystemId);
                var jumps = _reach.GetValueOrDefault(e.SystemId, 1);
                return new EscapeRow(e.Name, jumps, k.ship + k.pod,
                    _sov.GetValueOrDefault(e.SystemId) is > 0 ? "sov" : string.Empty);
            })
            .OrderBy(r => r.IsHot)        // clear routes first
            .ThenBy(r => r.Jumps)
            .ToList();

        foreach (var r in rows) Escapes.Add(r);
    }

    /// <summary>
    /// A system's name. The in-range and content boards reach past the constellation we loaded, so
    /// the local name index is the fallback - without it those boards print raw system ids.
    /// </summary>
    private string NameOf(int systemId) =>
        _systemCache.TryGetValue(systemId, out var s) ? s.Name
        : _systemSearch.NameOf(systemId) ?? systemId.ToString();

    /// <summary>
    /// Lays the constellation out as a clean 2D schematic (Dotlan-style) rather than a raw
    /// coordinate plot: the real ESI positions only seed the layout (so orientation still matches
    /// the game), then a force-directed pass spreads the gate graph out evenly and fills the panel.
    /// Exits are folded in as leaf nodes, so they settle around the edge on their own.
    /// </summary>
    private void BuildMap(List<EsiSystem> systems, List<(int a, int b)> links,
                          List<EsiSystem> exits, List<(int a, int b)> exitEdges)
    {
        _mapSystems   = systems;   // cached so a panel re-shape can re-lay out without re-hitting ESI
        _mapLinks     = links;
        _mapExits     = exits;
        _mapExitEdges = exitEdges;

        MapNodes.Clear();
        MapLinks.Clear();
        if (systems.Count == 0) return;

        var allSystems = systems.Concat(exits).ToList();
        var allEdges   = links.Concat(exitEdges).ToList();

        var centres = DotlanCentres(allSystems) ?? LayoutPixels(Latticize(ForceLayout(allSystems, allEdges), allEdges));

        // Minimap rule: YOU are always the origin. The view then just pins (0,0) to the middle of
        // the panel, so the map is centred on you at a fixed scale and the rest runs off the edge -
        // instead of being squeezed down to fit, which is what made big constellations unreadable.
        if (centres.TryGetValue(_currentSystemId, out var origin))
            centres = centres.ToDictionary(kv => kv.Key,
                                           kv => new Point(kv.Value.X - origin.X, kv.Value.Y - origin.Y));

        void AddLink(int a, int b, bool isExit)
        {
            if (centres.TryGetValue(a, out var pa) && centres.TryGetValue(b, out var pb))
                MapLinks.Add(new MapLink(pa.X, pa.Y, pb.X, pb.Y, isExit));
        }
        foreach (var (a, b) in links)     AddLink(a, b, false);
        foreach (var (a, b) in exitEdges) AddLink(a, b, true);

        // A Dotlan layout can miss a system (an exit in a neighbouring region), so never index blind.
        foreach (var sys in systems)
            if (centres.TryGetValue(sys.SystemId, out var c))
                MapNodes.Add(MakeNode(sys, c.X, c.Y, sys.SystemId == _currentSystemId));
        foreach (var e in exits)
            if (centres.TryGetValue(e.SystemId, out var c))
                MapNodes.Add(MakeNode(e, c.X, c.Y, false, isExit: true));
    }

    // The force layout works in its own unit space; IdealDist is the target on-screen-independent
    // gate length, and everything else is relative to it.
    private const double IdealDist = 1.0;

    /// <summary>
    /// Pulls the organic force layout onto an integer lattice, the way the in-game 2D map reads:
    /// systems sit on shared rows and columns so their gate links run horizontally or vertically
    /// instead of at arbitrary angles, and the whole constellation packs tight.
    ///
    /// Rounding (not tolerance-clustering) is what makes this work - snapping continuous positions
    /// that are all ~1 unit apart to the NEAREST integer cell naturally lands neighbours on the same
    /// row or column, where clustering by tolerance just gave every node its own lane.
    /// </summary>
    // Cached so the escape list can be rebuilt without re-hitting ESI.
    private List<EsiSystem> _mapSystems = [];
    private List<(int a, int b)> _mapLinks = [];
    private List<EsiSystem> _mapExits = [];
    private List<(int a, int b)> _mapExitEdges = [];

    private Dictionary<int, Vector> Latticize(Dictionary<int, Vector> pos, List<(int a, int b)> edges)
    {
        var ids = pos.Keys.ToList();
        if (ids.Count == 0) return pos;

        var adj = ids.ToDictionary(i => i, _ => new List<int>());
        foreach (var (a, b) in edges)
            if (adj.ContainsKey(a) && adj.ContainsKey(b)) { adj[a].Add(b); adj[b].Add(a); }

        // The nudge pass moves one system at a time, so it settles into whatever local minimum it
        // started next to - the first version produced a sheared staircase when a clean rectangular
        // block (the arrangement Dotlan and the in-game map use) scored better. Trying several
        // starting grids and keeping the cheapest gets that block; the graphs are constellation-sized
        // so this is still trivial work.
        Dictionary<int, (int c, int r)>? best = null;
        double bestCost = double.MaxValue;

        // Cost per attempt grows with the square of the system count, so ease off on big
        // constellations - this runs on the UI thread when you jump.
        double[] scales = [1.3, 1.5, 1.7, 2.0];
        int rounds  = ids.Count <= 16 ? 4 : ids.Count <= 28 ? 2 : 1;
        for (int variant = 0; variant < scales.Length * rounds; variant++)
        {
            var cand = PlaceAndRelax(pos, ids, adj, scales[variant % scales.Length], variant);
            var cost = LayoutCost(cand, edges);
            if (cost < bestCost) { bestCost = cost; best = cand; }
        }

        return best!.ToDictionary(kv => kv.Key, kv => new Vector(kv.Value.c, kv.Value.r));
    }

    /// <summary>One lattice attempt: round onto the grid at <paramref name="scale"/> (with a little
    /// per-variant jitter so attempts differ), then relax each system into the best nearby free cell.</summary>
    private static Dictionary<int, (int c, int r)> PlaceAndRelax(
        Dictionary<int, Vector> pos, List<int> ids, Dictionary<int, List<int>> adj, double scale, int variant)
    {
        // Fixed seed per variant -> the whole layout stays deterministic across reloads.
        var rng = new Random(17 + variant * 101);
        double jx = (rng.NextDouble() - 0.5) * 0.9, jy = (rng.NextDouble() - 0.5) * 0.9;

        var cell = new Dictionary<int, (int c, int r)>();
        var taken = new HashSet<(int, int)>();
        foreach (var id in ids.OrderBy(i => pos[i].Y).ThenBy(i => pos[i].X).ThenBy(i => i))
        {
            var want = ((int)Math.Round(pos[id].X * scale + jx), (int)Math.Round(pos[id].Y * scale + jy));
            var free = NearestFreeCell(want.Item1, want.Item2, taken);
            taken.Add(free);
            cell[id] = free;
        }

        for (int sweep = 0; sweep < 12; sweep++)
        {
            bool improved = false;
            foreach (var id in ids)
            {
                if (adj[id].Count == 0) continue;
                var cur = cell[id];
                double best = EdgeCost(id, cur, cell, adj);
                var bestCell = cur;

                for (int dr = -3; dr <= 3; dr++)
                    for (int dc = -3; dc <= 3; dc++)
                    {
                        if (dc == 0 && dr == 0) continue;
                        var cand = (cur.c + dc, cur.r + dr);
                        if (taken.Contains(cand)) continue;
                        var cost = EdgeCost(id, cand, cell, adj);
                        if (cost < best - 1e-9) { best = cost; bestCell = cand; }
                    }

                if (bestCell != cur)
                {
                    taken.Remove(cur);
                    taken.Add(bestCell);
                    cell[id] = bestCell;
                    improved = true;
                }
            }
            if (!improved) break;
        }
        return cell;
    }

    /// <summary>
    /// Whole-layout score - the same terms EdgeCost uses, summed over every gate once.
    /// </summary>
    private static double LayoutCost(Dictionary<int, (int c, int r)> cell, List<(int a, int b)> edges)
    {
        double cost = 0;
        foreach (var (a, b) in edges)
        {
            if (!cell.TryGetValue(a, out var A) || !cell.TryGetValue(b, out var B)) continue;
            double dx = Math.Abs(A.c - B.c), dy = Math.Abs(A.r - B.r);
            cost += dx + dy;
            if (dx > 0 && dy > 0) cost += 0.6 * Math.Min(dx, dy);
            foreach (var (other, oc) in cell)
            {
                if (other == a || other == b) continue;
                if (OnSegment(A, B, oc)) cost += 8;
            }
        }

        return cost;
    }

    /// <summary>
    /// Cost of a node sitting at <paramref name="at"/>: link length, a penalty for links that are
    /// neither horizontal nor vertical, and a heavy penalty for any link that would pass THROUGH
    /// another system. That last term is what stops a dense constellation collapsing into one
    /// column, where overlapping links hide each other and imply gates that don't exist.
    /// </summary>
    private static double EdgeCost(int id, (int c, int r) at,
                                   Dictionary<int, (int c, int r)> cell, Dictionary<int, List<int>> adj)
    {
        double cost = 0;
        foreach (var n in adj[id])
        {
            var o = cell[n];
            double dx = Math.Abs(at.c - o.c), dy = Math.Abs(at.r - o.r);
            cost += dx + dy;                                  // prefer short links
            if (dx > 0 && dy > 0) cost += 0.6 * Math.Min(dx, dy);   // prefer straight ones

            foreach (var (other, oc) in cell)
            {
                if (other == id || other == n) continue;
                if (OnSegment(at, o, oc)) cost += 8;          // link would run over another system
            }
        }
        return cost;
    }

    /// <summary>True if lattice cell <paramref name="p"/> lies on the open segment a-b.</summary>
    private static bool OnSegment((int c, int r) a, (int c, int r) b, (int c, int r) p)
    {
        if ((p.c == a.c && p.r == a.r) || (p.c == b.c && p.r == b.r)) return false;
        // Collinear?
        long cross = (long)(b.c - a.c) * (p.r - a.r) - (long)(b.r - a.r) * (p.c - a.c);
        if (cross != 0) return false;
        // Within the bounding box?
        return p.c >= Math.Min(a.c, b.c) && p.c <= Math.Max(a.c, b.c)
            && p.r >= Math.Min(a.r, b.r) && p.r <= Math.Max(a.r, b.r);
    }

    /// <summary>Nearest lattice cell to (c,r) that isn't taken, searched in growing rings.</summary>
    private static (int c, int r) NearestFreeCell(int c, int r, HashSet<(int, int)> taken)
    {
        if (!taken.Contains((c, r))) return (c, r);
        for (int radius = 1; radius < 64; radius++)
            for (int dr = -radius; dr <= radius; dr++)
                for (int dc = -radius; dc <= radius; dc++)
                {
                    if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != radius) continue;   // ring edge only
                    if (!taken.Contains((c + dc, r + dr))) return (c + dc, r + dr);
                }
        return (c, r);
    }

    /// <summary>
    /// Fruchterman-Reingold force layout: every node repels every other node, and each gate pulls
    /// its two systems together. Seeded from the real ESI coordinates so the result keeps the
    /// constellation's real orientation, then relaxed into an even, planar-ish spread. Deterministic
    /// (fixed seed + iteration count) so the same constellation always lays out identically.
    /// </summary>
    private static Dictionary<int, Vector> ForceLayout(List<EsiSystem> systems, List<(int a, int b)> edges)
    {
        var ids = systems.Select(s => s.SystemId).ToList();
        var pos = new Dictionary<int, Vector>();

        double minX = systems.Min(s => s.Position!.X), maxX = systems.Max(s => s.Position!.X);
        double minZ = systems.Min(s => s.Position!.Z), maxZ = systems.Max(s => s.Position!.Z);
        double realSpan = Math.Max(maxX - minX, maxZ - minZ);
        double seedScale = realSpan > 0 ? Math.Sqrt(ids.Count) * IdealDist / realSpan : 0;

        var rng = new Random(17);   // fixed seed -> stable layout across reloads and resizes
        foreach (var s in systems)
        {
            // Top-down: X -> horizontal, Z -> vertical (flipped so north is up). A hair of jitter
            // stops coincident seeds sitting exactly on each other, where they'd feel no force.
            double x = (s.Position!.X - minX) * seedScale + rng.NextDouble() * 0.01;
            double y = (maxZ - s.Position!.Z) * seedScale + rng.NextDouble() * 0.01;
            pos[s.SystemId] = new Vector(x, y);
        }

        double k = IdealDist;
        double temp = Math.Sqrt(ids.Count) * IdealDist * 0.5;   // max move per step, cooled each pass
        var disp = new Dictionary<int, Vector>();
        const int iterations = 300;
        for (int iter = 0; iter < iterations; iter++)
        {
            foreach (var id in ids) disp[id] = new Vector(0, 0);

            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                {
                    var d = pos[ids[i]] - pos[ids[j]];
                    double dist = Math.Max(d.Length, 0.01);
                    var push = d / dist * (k * k / dist);   // repulsion, falls off with distance
                    disp[ids[i]] += push;
                    disp[ids[j]] -= push;
                }

            foreach (var (a, b) in edges)
            {
                if (!pos.ContainsKey(a) || !pos.ContainsKey(b)) continue;
                var d = pos[a] - pos[b];
                double dist = Math.Max(d.Length, 0.01);
                var pull = d / dist * (dist * dist / k);   // attraction, grows with distance
                disp[a] -= pull;
                disp[b] += pull;
            }

            foreach (var id in ids)
            {
                var dp = disp[id];
                double len = Math.Max(dp.Length, 0.01);
                pos[id] += dp / len * Math.Min(len, temp);   // clamp the step to the temperature
            }
            temp *= 0.97;   // cool down so early passes spread hard and later ones just settle
        }
        return pos;
    }

    // One lattice cell in pixels, for the fallback layout. Roughly matches the spacing DotlanScale
    // gives real systems, so J-space maps read at the same density as k-space ones.
    private const double IdealPixel = 74;

    /// <summary>
    /// Positions taken straight from Dotlan's region layout, cropped to the systems on screen and
    /// scaled up to a comfortable on-screen spacing. Returns null when the layout isn't available or
    /// doesn't cover these systems, so the caller falls back to FCAT's own layout.
    /// </summary>
    /// <summary>
    /// Fixed pixels per Dotlan unit. Systems sit roughly 40-70 units apart in Dotlan's region SVG,
    /// so this puts them ~70-125px apart on screen whatever the constellation looks like.
    /// </summary>
    private const double DotlanScale = 1.75;

    /// <summary>
    /// Positions straight from Dotlan's region layout, at a FIXED scale. Nothing is fitted to the
    /// panel - that's what made a big constellation shrink until it was unreadable. Like a game
    /// minimap, the scale never changes and anything that doesn't fit is simply off the edge.
    /// Returns null when the layout doesn't cover these systems, so the caller falls back to ours.
    /// </summary>
    private Dictionary<int, Point>? DotlanCentres(List<EsiSystem> systems)
    {
        UsingDotlanLayout = false;
        if (_regionLayout == null) return null;

        // Exits sit in a neighbouring region often enough that a few misses are normal; a majority
        // miss means this layout isn't the right one for these systems.
        var known = systems.Where(s => _regionLayout.ContainsKey(s.SystemId)).ToList();
        if (known.Count < Math.Max(2, systems.Count / 2)) return null;

        UsingDotlanLayout = true;
        return known.ToDictionary(s => s.SystemId,
            s => new Point(_regionLayout[s.SystemId].X * DotlanScale,
                           _regionLayout[s.SystemId].Y * DotlanScale));
    }

    /// <summary>
    /// Scales the lattice to pixels. No canvas sizing or centring here - BuildMap re-centres
    /// everything on the system you're in, and the view crops to the panel.
    /// </summary>
    private static Dictionary<int, Point> LayoutPixels(Dictionary<int, Vector> pos)
        => pos.ToDictionary(kv => kv.Key,
                            kv => new Point(kv.Value.X * IdealPixel, kv.Value.Y * IdealPixel));

    private MapNode MakeNode(EsiSystem sys, double cx, double cy, bool isCurrent, bool isExit = false)
    {
        var k = _kills.GetValueOrDefault(sys.SystemId);
        var pvp = k.ship + k.pod;
        var hot = pvp > 0;
        var pulse = pvp > _prevPvp.GetValueOrDefault(sys.SystemId);   // a kill landed since the last poll

        // Sov owner and the activity line are resolved for the hover tooltip only - since the map
        // moved to its own page, the per-system numbers live on the node rather than a side rail.
        var sovId = _sov.GetValueOrDefault(sys.SystemId);
        var sovLabel = sovId is > 0 ? _nameCache.GetValueOrDefault(sovId.Value, "") : "";
        var stats = $"{pvp} kills · {_jumps.GetValueOrDefault(sys.SystemId)} jumps · {k.npc} NPC";

        // Only worth marking home separately when you're looking somewhere else.
        var isHome = sys.SystemId == _homeSystemId && _homeSystemId != _currentSystemId;

        return new MapNode(cx - NodeHalfW, cy - NodeHalfH,
            sys.SystemId, sys.Name, sovLabel, stats, isCurrent,
            hot ? pvp.ToString() : string.Empty, hot, pulse, isExit, isHome);
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

    private async Task EnsureActivityAsync(bool force)
    {
        if (!force && DateTime.UtcNow - _activityAt < TimeSpan.FromMinutes(2) && _kills.Count > 0) return;

        var killsTask = _esi.GetSystemKillsAsync();
        var jumpsTask = _esi.GetSystemJumpsAsync();
        var sovTask   = _esi.GetSovMapAsync();
        var campTask  = _esi.GetSovCampaignsAsync();
        var incTask   = _esi.GetIncursionsAsync();
        var fwTask    = _esi.GetFwSystemsAsync();
        await Task.WhenAll(killsTask, jumpsTask, sovTask, campTask, incTask, fwTask);

        // Remember the previous counts so we can show a ratting delta and pulse fresh PvP kills
        // (this data changes ~hourly on ESI). The first poll has nothing to compare against.
        _hasActivityBaseline = _kills.Count > 0;
        _prevNpc = _kills.ToDictionary(k => k.Key, k => k.Value.npc);
        _prevPvp = _kills.ToDictionary(k => k.Key, k => k.Value.ship + k.Value.pod);
        _kills = killsTask.Result.ToDictionary(k => k.SystemId, k => (k.ShipKills, k.PodKills, k.NpcKills));
        _jumps = jumpsTask.Result.ToDictionary(j => j.SystemId, j => j.ShipJumps);
        _sov   = sovTask.Result.ToDictionary(s => s.SystemId, s => s.AllianceId);
        _campaigns  = campTask.Result;
        _incursions = incTask.Result;
        _fw = fwTask.Result.GroupBy(f => f.SolarSystemId).ToDictionary(g => g.Key, g => g.First());
        _activityAt = DateTime.UtcNow;
    }

    // Per-system links (right-click a map bubble)
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
        catch { /* no browser / blocked - nothing useful to do */ }
    }
}
