using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// The hunt board: where to take a jump-capable fleet.
///
/// This asks a different question from the constellation map. That one is about gates - who is next
/// door, how do I leave. A jump drive ignores gates entirely, so everything here measures straight-line
/// light years through <see cref="SystemSpace"/>, and the answer routinely lands in another region.
///
/// Two ways to read the board:
/// <list type="bullet">
/// <item><b>Activity</b> - where the ratting is. Ranked on the CHANGE in NPC kills far more than the
/// total, because a big total only means "this is a ratting pocket" while a rising count means someone
/// is out there right now.</item>
/// <item><b>Coverage</b> - where to stage. Ranks by how much NEW ground a system opens up if you seed
/// a cyno there, which is a different system entirely from wherever the ratting happens to be.</item>
/// </list>
///
/// Mid-points chain: each hop measures from the last one, and anything the earlier hops could already
/// reach is not counted as new. That is what makes a second jump worth planning rather than guessing.
/// </summary>
public partial class HuntViewModel : ObservableObject
{
    private readonly EsiService _esi;
    private readonly EsiAuthService _auth;
    private readonly SystemSearchService _search;
    private readonly JumpDrives _drives;
    private readonly SettingsService _settings;

    /// <summary>Rows on the board. Past this the list stops being a shortlist.</summary>
    private const int MaxRows = 40;

    /// <summary>Width of a full activity bar, in pixels.</summary>
    private const double BarMaxWidth = 40.0;

    /// <summary>Breathing room around the region map so edge systems aren't clipped.</summary>
    private const double RegionMapMargin = 26.0;

    /// <summary>Only the top few systems get a label - every system named is an unreadable map.</summary>
    private const int MaxMapLabels = 6;

    /// <summary>Half a map dot, for converting a centre into a top-left.</summary>
    private const double NodeHalf = 12.0;

    public HuntViewModel(EsiService esi, EsiAuthService auth, SystemSearchService search,
                         JumpDrives drives, SettingsService settings)
    {
        _esi = esi;
        _auth = auth;
        _search = search;
        _drives = drives;
        _settings = settings;

        Hulls = new ObservableCollection<JumpClass>(drives.Classes);

        var saved = settings.Current;
        _selectedHull     = Hulls.FirstOrDefault(h => h.Name == saved.HuntHullClass) ?? Hulls[0];
        _calibrationLevel = Math.Clamp(saved.HuntCalibration, 0, 5);
        _rankMode         = saved.HuntRankMode == "Coverage" ? "Coverage" : "Activity";
    }

    // Hull + skills
    public ObservableCollection<JumpClass> Hulls { get; }
    public int[] CalibrationLevels { get; } = [0, 1, 2, 3, 4, 5];

    [ObservableProperty] private JumpClass _selectedHull;
    [ObservableProperty] private int _calibrationLevel = 5;

    public double RangeLightYears => _drives.RangeAt(SelectedHull, CalibrationLevel);
    public string RangeText       => $"{RangeLightYears:0.0} ly";
    public string CynoText        => SelectedHull.CynoText;

    /// <summary>Shows the working behind the range, and whether it came from EVE or the fallback table.</summary>
    public string RangeSourceText =>
        $"{SelectedHull.Name} {SelectedHull.BaseLightYears:0.0} ly base, " +
        $"+{_drives.CalibrationBonusPerLevel:0}% per JDC level" +
        (_drives.VerifiedAgainstEsi ? ", read from ESI" : ", last known values");

    // The route
    /// <summary>The staging chain. The last hop is what the board measures from.</summary>
    public ObservableCollection<HuntHop> Chain { get; } = [];

    public bool   HasMidpoint     => Chain.Count > 1;
    public string OriginRole      => HasMidpoint ? "Mid-point" : "Origin";
    public string OriginRoleLower => HasMidpoint ? "mid-point" : "origin";

    private int _originId;

    /// <summary>Everything the earlier hops in the chain could already reach, so a later hop can be
    /// honest about how much of its range is actually new ground.</summary>
    private HashSet<int> _reachedEarlier = [];

    [ObservableProperty] private string _originName = string.Empty;
    [ObservableProperty] private string _originRegion = string.Empty;
    [ObservableProperty] private string _chainNote = string.Empty;

    // Origin picker
    [ObservableProperty] private string _originQuery = string.Empty;
    public ObservableCollection<SystemMatch> OriginSuggestions { get; } = [];
    public bool HasSuggestions => OriginSuggestions.Count > 0;

    // Board state
    [ObservableProperty] private string _status = "Reading your location…";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private int _inRangeCount;
    [ObservableProperty] private string _rankMode = "Activity";
    [ObservableProperty] private bool _coverageBusy;
    [ObservableProperty] private string _selectedMapRegion = string.Empty;

    public bool IsRankActivity => RankMode == "Activity";
    public bool IsRankCoverage => RankMode == "Coverage";

    public ObservableCollection<HuntRow>     Rows           { get; } = [];
    public ObservableCollection<HuntMapNode> MapNodes       { get; } = [];
    public ObservableCollection<HuntMapLink> MapLinks       { get; } = [];
    public ObservableCollection<HuntRegion>  RegionsInRange { get; } = [];

    private List<(int SystemId, double LightYears)> _inRange = [];
    private bool _started;

    // Activity data (ESI, refreshes ~hourly)
    private Dictionary<int, (int ship, int pod, int npc)> _kills = [];
    private Dictionary<int, int> _prevNpc = [];
    private Dictionary<int, int> _jumps = [];
    private bool _hasBaseline;
    private DateTime _activityAt;

    // Coverage is expensive, so it's cached per (system, range) and never recomputed for a pair.
    private readonly Dictionary<(int System, double Range), (int Total, int New, int Regions)> _coverage = [];

    private double _mapWidth;
    private double _mapHeight;

    // Lifecycle
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            await _search.EnsureLoadedAsync();
            await _drives.RefreshFromEsiAsync();
            OnPropertyChanged(nameof(RangeSourceText));

            if (_originId == 0) { await ClearRoute(); return; }

            await EnsureActivityAsync(force: false);
            Recompute();
        }
        catch (Exception ex)
        {
            Status = $"Couldn't start the hunt view ({ex.Message}). Pick a system and try Measure.";
            _started = false;   // let the next visit retry
        }
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await EnsureActivityAsync(force: true);
        Recompute();
    }

    private void SavePicks()
    {
        _settings.Current.HuntHullClass   = SelectedHull.Name;
        _settings.Current.HuntCalibration = CalibrationLevel;
        _settings.Current.HuntRankMode    = RankMode;
        _settings.Save();
    }

    // Origin + chain
    /// <summary>Resets the route to a single origin.</summary>
    public async Task SetOriginAsync(int systemId)
    {
        Chain.Clear();
        Chain.Add(new HuntHop { SystemId = systemId, Name = _search.NameOf(systemId) ?? systemId.ToString() });
        OriginQuery = string.Empty;
        await ApplyChainAsync();
    }

    /// <summary>Back to where the FC actually is.</summary>
    [RelayCommand]
    private async Task ClearRoute()
    {
        var loc = await _esi.GetCharacterLocationAsync(_auth.AuthenticatedCharacterId);
        if (loc == null || loc.SolarSystemId == 0) { Status = "Couldn't read your location."; return; }
        await SetOriginAsync(loc.SolarSystemId);
    }

    [RelayCommand]
    private async Task PickOrigin(SystemMatch? match)
    {
        if (match != null) await SetOriginAsync(match.Id);
    }

    [RelayCommand]
    private async Task UseTypedOrigin()
    {
        var typed = OriginQuery.Trim();
        if (typed.Length == 0) return;

        var id = _search.ResolveId(typed) ?? _search.Search(typed).FirstOrDefault()?.Id;
        if (id is not > 0) { Status = $"No system called \"{typed}\"."; return; }
        await SetOriginAsync(id.Value);
    }

    [RelayCommand]
    private void DismissSuggestions()
    {
        OriginSuggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    [RelayCommand]
    private async Task AddMidpoint(HuntRow? row)
    {
        if (row == null || Chain.Any(h => h.SystemId == row.SystemId)) return;
        Chain.Add(new HuntHop { SystemId = row.SystemId, Name = row.Name });
        await ApplyChainAsync();
    }

    /// <summary>Rewinds the route to this hop. Only trims from the end, so the chain stays a route.</summary>
    [RelayCommand]
    private async Task PopTo(HuntHop? hop)
    {
        if (hop == null) return;
        var at = Chain.IndexOf(hop);
        if (at < 0 || at == Chain.Count - 1) return;

        while (Chain.Count > at + 1) Chain.RemoveAt(Chain.Count - 1);
        await ApplyChainAsync();
    }

    /// <summary>Re-points the board at the last hop and recomputes what the earlier hops already covered.</summary>
    private async Task ApplyChainAsync()
    {
        if (Chain.Count == 0) return;

        var last = Chain[^1];
        _originId    = last.SystemId;
        OriginName   = last.Name;
        OriginRegion = DotlanLayout.RegionOf(last.SystemId) ?? string.Empty;

        for (var i = 0; i < Chain.Count; i++) Chain[i].IsLast = i == Chain.Count - 1;

        OnPropertyChanged(nameof(HasMidpoint));
        OnPropertyChanged(nameof(OriginRole));
        OnPropertyChanged(nameof(OriginRoleLower));

        // Everything reachable from an EARLIER hop - the last one is what we're measuring, so it's excluded.
        _reachedEarlier = [];
        for (var i = 0; i < Chain.Count - 1; i++)
            foreach (var (id, _) in SystemSpace.WithinRange(Chain[i].SystemId, RangeLightYears))
                _reachedEarlier.Add(id);

        await EnsureActivityAsync(force: false);
        Recompute();
    }

    // Activity
    private async Task EnsureActivityAsync(bool force)
    {
        // ESI only moves this hourly, so re-asking inside a couple of minutes buys nothing.
        if (!force && _kills.Count > 0 && DateTime.UtcNow - _activityAt < TimeSpan.FromMinutes(2)) return;

        try
        {
            var killsTask = _esi.GetSystemKillsAsync();
            var jumpsTask = _esi.GetSystemJumpsAsync();
            await Task.WhenAll(killsTask, jumpsTask);

            // Keep the previous counts so the board can show a ratting delta. The first poll has
            // nothing to compare against, and treating that as a spike would light up every row.
            _hasBaseline = _kills.Count > 0;
            _prevNpc = _kills.ToDictionary(k => k.Key, k => k.Value.npc);

            _kills = killsTask.Result.ToDictionary(k => k.SystemId, k => (k.ShipKills, k.PodKills, k.NpcKills));
            _jumps = jumpsTask.Result.ToDictionary(j => j.SystemId, j => j.ShipJumps);
            _activityAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Status = $"Couldn't load activity data ({ex.Message})";
        }
    }

    /// <summary>
    /// Ratting volume as a band. The FC is choosing between systems, not auditing them, so the exact
    /// NPC count matters less than which bracket it falls in.
    /// </summary>
    private static RattingLevel Classify(int npcKills) => npcKills switch
    {
        >= 600 => RattingLevel.Heavy,
        >= 200 => RattingLevel.Steady,
        > 0    => RattingLevel.Light,
        _      => RattingLevel.Quiet,
    };

    private string StatsLineFor(int systemId)
    {
        var k = _kills.GetValueOrDefault(systemId);
        return $"Jumps {_jumps.GetValueOrDefault(systemId):N0}   ·   Kills {k.ship}/{k.pod}   ·   NPC {k.npc:N0}";
    }

    // Coverage
    /// <summary>
    /// Works out what each in-range system would open up if you staged there. That is a full range
    /// sweep per candidate over every system in New Eden, so it runs off the UI thread and the result
    /// is cached - the answer only changes when the range does.
    /// </summary>
    private async Task EnsureCoverageAsync()
    {
        var range = RangeLightYears;
        var candidates = _inRange.Select(x => x.SystemId)
                                 .Where(id => !_coverage.ContainsKey((id, range)))
                                 .ToList();

        if (candidates.Count > 0)
        {
            // "New" means new to the whole route, so everything already reachable counts as covered.
            var covered = new HashSet<int>(_reachedEarlier);
            foreach (var (id, _) in _inRange) covered.Add(id);
            covered.Add(_originId);

            CoverageBusy = true;
            var computed = await Task.Run(() =>
            {
                var results = new List<(int SystemId, int Total, int New, int Regions)>(candidates.Count);
                foreach (var id in candidates)
                {
                    var onward = SystemSpace.WithinRange(id, range);
                    var regions = onward.Select(r => DotlanLayout.RegionOf(r.SystemId))
                                        .Where(r => !string.IsNullOrEmpty(r))
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .Count();
                    results.Add((id, onward.Count, onward.Count(r => !covered.Contains(r.SystemId)), regions));
                }
                return results;
            });
            CoverageBusy = false;

            foreach (var (id, total, fresh, regions) in computed)
                _coverage[(id, range)] = (total, fresh, regions);
        }

        Recompute();
    }

    // The board
    private void Recompute()
    {
        OnPropertyChanged(nameof(RangeLightYears));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(RangeSourceText));
        OnPropertyChanged(nameof(CynoText));

        Rows.Clear();
        if (_originId == 0) return;

        if (!SystemSpace.Knows(_originId))
        {
            Status = $"{OriginName} isn't in the position bundle, so range can't be measured from it.";
            HasResult = false;
            MapNodes.Clear();
            return;
        }

        _inRange = SystemSpace.WithinRange(_originId, RangeLightYears);
        InRangeCount = _inRange.Count;

        var rows = _inRange.Select(x =>
        {
            var k = _kills.GetValueOrDefault(x.SystemId);
            var delta = _hasBaseline ? k.npc - _prevNpc.GetValueOrDefault(x.SystemId) : 0;
            var region = DotlanLayout.RegionOf(x.SystemId) ?? string.Empty;
            var cov = _coverage.GetValueOrDefault((x.SystemId, RangeLightYears));

            return new HuntRow(
                x.SystemId, _search.NameOf(x.SystemId) ?? x.SystemId.ToString(), region, x.LightYears,
                k.npc, delta, k.ship + k.pod, Classify(k.npc),
                OutOfRegion: !string.Equals(region, OriginRegion, StringComparison.OrdinalIgnoreCase),
                NewFromHere: HasMidpoint && !_reachedEarlier.Contains(x.SystemId),
                cov.Total, cov.New, cov.Regions);
        }).ToList();

        rows = IsRankCoverage
            // Staging: what does this open up that nothing else already reaches.
            ? rows.OrderByDescending(r => r.OnwardNew)
                  .ThenByDescending(r => r.OnwardTotal)
                  .ThenByDescending(r => r.OnwardRegions)
                  .ToList()
            // Hunting: new ground first, then rising ratting weighted well above the raw total, then close.
            : rows.OrderByDescending(r => r.NewFromHere)
                  .ThenByDescending(r => r.NpcDelta * 20 + r.NpcKills)
                  .ThenBy(r => r.LightYears)
                  .ToList();

        var shortlist = rows.Take(MaxRows).ToList();

        // The bar is relative to the busiest system on the board, so it reads as a comparison.
        double Magnitude(HuntRow r) => IsRankCoverage ? r.OnwardTotal : r.NpcKills;
        var peak = shortlist.Count > 0 ? shortlist.Max(Magnitude) : 0;
        for (var i = 0; i < shortlist.Count; i++)
        {
            shortlist[i].Rank = i + 1;
            shortlist[i].BarWidth = peak > 0 ? Math.Round(BarMaxWidth * Magnitude(shortlist[i]) / peak) : 0;
        }

        foreach (var row in shortlist) Rows.Add(row);

        BuildRegionList();

        Status = $"{InRangeCount} systems in range of {OriginName}";
        if (HasMidpoint)
        {
            var fresh = _inRange.Count(x => !_reachedEarlier.Contains(x.SystemId));
            var hops  = Chain.Count - 1;
            ChainNote = $"{fresh} of them are new ground. " +
                        $"{hops} jump{(hops == 1 ? "" : "s")} from {Chain[0].Name}, so fatigue applies.";
        }
        else ChainNote = string.Empty;

        HasResult = true;
        BuildMap();
    }

    // Map
    public void SetMapSize(double width, double height)
    {
        _mapWidth = width;
        _mapHeight = height;
        BuildMap();
    }

    /// <summary>
    /// The regions the current range touches, as filter chips. Jump range crosses regions freely, so
    /// without this the map would have to pick one arbitrarily. Home region leads, then by system count.
    /// </summary>
    private void BuildRegionList()
    {
        var keep = SelectedMapRegion;
        RegionsInRange.Clear();

        var counts = _inRange.Select(x => DotlanLayout.RegionOf(x.SystemId))
                             .Where(r => !string.IsNullOrEmpty(r))
                             .GroupBy(r => r!)
                             .ToDictionary(g => g.Key, g => g.Count());

        if (OriginRegion.Length > 0)
            RegionsInRange.Add(new HuntRegion { Name = OriginRegion, Count = counts.GetValueOrDefault(OriginRegion) });

        foreach (var (name, count) in counts.Where(kv => kv.Key != OriginRegion).OrderByDescending(kv => kv.Value))
            RegionsInRange.Add(new HuntRegion { Name = name, Count = count });

        // Keep the FC's chosen region across a recompute if it's still in range.
        SelectedMapRegion = RegionsInRange.Any(r => r.Name == keep) ? keep : OriginRegion;
        foreach (var region in RegionsInRange) region.IsSelected = region.Name == SelectedMapRegion;
    }

    [RelayCommand]
    private void SetMapRegion(HuntRegion? region)
    {
        if (region == null) return;
        SelectedMapRegion = region.Name;
        foreach (var r in RegionsInRange) r.IsSelected = r.Name == region.Name;
        BuildMap();
    }

    /// <summary>
    /// Plots the selected region, scaled to fit the panel. Unlike the constellation minimap this one
    /// DOES fit to the viewport - a region is the unit here, and the point is seeing the whole of it
    /// at once with the in-range systems standing out against the rest.
    /// </summary>
    private void BuildMap()
    {
        MapNodes.Clear();
        MapLinks.Clear();
        if (_mapWidth <= 0 || _mapHeight <= 0 || SelectedMapRegion.Length == 0) return;

        var layout = DotlanLayout.ForRegion(SelectedMapRegion);
        if (layout == null || layout.Count == 0) return;

        double minX = layout.Values.Min(p => p.X), maxX = layout.Values.Max(p => p.X);
        double minY = layout.Values.Min(p => p.Y), maxY = layout.Values.Max(p => p.Y);
        var spanX = Math.Max(maxX - minX, 1);
        var spanY = Math.Max(maxY - minY, 1);

        var pad   = RegionMapMargin * 2;
        var scale = Math.Min((_mapWidth - pad) / spanX, (_mapHeight - pad) / spanY);
        var offX  = (_mapWidth  - spanX * scale) / 2;
        var offY  = (_mapHeight - spanY * scale) / 2;

        var rangeById = _inRange.ToDictionary(x => x.SystemId, x => x.LightYears);
        var labelled  = Rows.Take(MaxMapLabels).Select(r => r.SystemId).ToHashSet();

        foreach (var (id, point) in layout)
        {
            var inRange  = rangeById.ContainsKey(id);
            var isOrigin = id == _originId;
            var k        = _kills.GetValueOrDefault(id);
            var delta    = _hasBaseline ? k.npc - _prevNpc.GetValueOrDefault(id) : 0;
            var name     = _search.NameOf(id) ?? id.ToString();

            var rangeLine = isOrigin ? OriginRole
                          : inRange  ? $"{rangeById[id]:0.0} ly, in range"
                          :            "Out of range";

            var level = Classify(k.npc);
            var tone  = isOrigin ? "Origin" : !inRange ? "Out" : level.ToString();

            // The ring calls out a reason to look, and only for systems actually in range.
            var ring = !inRange || isOrigin ? "None"
                     : k.ship + k.pod > 0   ? "Contested"
                     : delta > 0            ? "Rising"
                     :                        "None";

            MapNodes.Add(new HuntMapNode(
                offX + (point.X - minX) * scale - NodeHalf,
                offY + (point.Y - minY) * scale - NodeHalf,
                id, name, inRange, isOrigin, tone, ring, DotSize(isOrigin, inRange, level),
                ShowLabel: isOrigin || labelled.Contains(id),
                Tip: $"{name}\n{rangeLine}", RangeLine: rangeLine, StatsLine: StatsLineFor(id)));
        }

        // Gates, drawn once per pair. They aren't how you travel here, but they're how the region reads.
        var centres = MapNodes.ToDictionary(n => n.SystemId, n => (X: n.Left + NodeHalf, Y: n.Top + NodeHalf));
        var drawn = new HashSet<(int, int)>();
        foreach (var node in MapNodes)
            foreach (var neighbour in DotlanLayout.Neighbours(node.SystemId) ?? [])
            {
                if (!centres.ContainsKey(neighbour)) continue;
                var pair = node.SystemId < neighbour ? (node.SystemId, neighbour) : (neighbour, node.SystemId);
                if (!drawn.Add(pair)) continue;

                var a = centres[node.SystemId];
                var b = centres[neighbour];
                MapLinks.Add(new HuntMapLink(a.X, a.Y, b.X, b.Y));
            }
    }

    /// <summary>Dot size carries ratting volume, so a busy system is visibly bigger.</summary>
    private static double DotSize(bool isOrigin, bool inRange, RattingLevel level)
    {
        if (isOrigin) return 12;
        if (!inRange) return 6;
        return level switch
        {
            RattingLevel.Heavy  => 13,
            RattingLevel.Steady => 11,
            RattingLevel.Light  => 9,
            _                   => 7,
        };
    }

    [RelayCommand]
    private async Task AddMidpointFromMap(HuntMapNode? node)
    {
        if (node == null || node.IsOrigin || Chain.Any(h => h.SystemId == node.SystemId)) return;
        Chain.Add(new HuntHop { SystemId = node.SystemId, Name = node.Name });
        await ApplyChainAsync();
    }

    // Per-system links
    [RelayCommand] private static void OpenDotlanSystem(HuntMapNode? node) { if (node != null) OpenDotlan(node.Name); }
    [RelayCommand] private static void OpenZkillSystem(HuntMapNode? node)  { if (node != null) OpenZkill(node.SystemId); }
    [RelayCommand] private static void OpenDotlan(HuntRow? row)            { if (row  != null) OpenDotlan(row.Name); }
    [RelayCommand] private static void OpenZkill(HuntRow? row)             { if (row  != null) OpenZkill(row.SystemId); }

    private static void OpenDotlan(string systemName)
        => OpenUrl($"https://evemaps.dotlan.net/system/{Uri.EscapeDataString(systemName.Replace(' ', '_'))}");

    private static void OpenZkill(int systemId)
        => OpenUrl($"https://zkillboard.com/system/{systemId}/");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked - nothing useful to do */ }
    }

    // Property change handlers
    partial void OnSelectedHullChanged(JumpClass value)
    {
        SavePicks();
        _ = ApplyChainAsync();   // range changed, so the whole board moves
    }

    partial void OnCalibrationLevelChanged(int value)
    {
        SavePicks();
        _ = ApplyChainAsync();
    }

    [RelayCommand] private void SetRankMode(string mode) => RankMode = mode;

    partial void OnRankModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsRankActivity));
        OnPropertyChanged(nameof(IsRankCoverage));
        SavePicks();

        // Coverage has to be worked out before it can be ranked on; activity is already in hand.
        if (IsRankCoverage) _ = EnsureCoverageAsync();
        else Recompute();
    }

    partial void OnOriginQueryChanged(string value)
    {
        OriginSuggestions.Clear();
        if (value.Trim().Length >= 2)
            foreach (var match in _search.Search(value.Trim())) OriginSuggestions.Add(match);
        OnPropertyChanged(nameof(HasSuggestions));
    }
}
