using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>A node in the mini constellation map (a solar system).</summary>
public record MapNode(double NodeLeft, double NodeTop, double CenterX, double CenterY,
                      string Name, string Sec, Brush Fill, bool IsCurrent, string KillBadge, bool Hot);

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

    private Dictionary<int, (int ship, int pod)> _kills = [];
    private Dictionary<int, int>  _jumps = [];
    private Dictionary<int, int?> _sov   = [];
    private DateTime _activityAt;
    private readonly Dictionary<int, string> _nameCache = [];

    private int _currentSystemId;
    private CancellationTokenSource? _cts;

    public SystemIntelViewModel(EsiService esi, EsiAuthService auth)
    {
        _esi = esi;
        _auth = auth;
    }

    // ── Map geometry ──
    public double CanvasWidth  => 320;
    public double CanvasHeight => 250;
    private const double Cx = 160, Cy = 118, Radius = 92, NodeHalfW = 42, NodeHalfH = 17;

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _hasResult;
    [ObservableProperty] private string _status = "Reading your current system…";

    [ObservableProperty] private string _systemName   = string.Empty;
    [ObservableProperty] private string _securityText = string.Empty;
    [ObservableProperty] private Brush  _securityBrush = Brushes.Gray;
    [ObservableProperty] private string _locationLine = string.Empty;
    [ObservableProperty] private string _sovHolder    = string.Empty;
    [ObservableProperty] private string _killsLine    = string.Empty;
    [ObservableProperty] private string _jumpsLine    = string.Empty;

    public ObservableCollection<MapNode>     MapNodes  { get; } = [];
    public ObservableCollection<MapLink>     MapLinks  { get; } = [];
    public ObservableCollection<NeighborRow> Neighbors { get; } = [];

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
            var sys = await _esi.GetSystemAsync(systemId);
            if (sys == null) { Status = "Couldn't load that system."; return; }
            _currentSystemId = systemId;

            await EnsureActivityAsync(force);

            var con = await _esi.GetConstellationAsync(sys.ConstellationId);
            var region = con != null ? await _esi.GetRegionNameAsync(con.RegionId) : null;
            LocationLine = $"{con?.Name} · {region}".Trim(' ', '·');
            SovHolder = await ResolveSovAsync(systemId);

            var neighbours = new List<EsiSystem>();
            if (sys.Stargates is { Length: > 0 })
            {
                var gates = await Task.WhenAll(sys.Stargates.Select(_esi.GetStargateAsync));
                var dests = gates.Where(g => g?.Destination != null).Select(g => _esi.GetSystemAsync(g!.Destination!.SystemId));
                neighbours = (await Task.WhenAll(dests)).Where(s => s != null).Select(s => s!).ToList();
            }

            BuildHeader(sys);
            BuildNeighbours(neighbours);
            BuildMap(sys, neighbours);

            Status = $"{sys.Name} · {neighbours.Count} adjacent systems";
            HasResult = true;
        }
        finally { IsBusy = false; }
    }

    private void BuildHeader(EsiSystem sys)
    {
        SystemName    = sys.Name;
        SecurityText  = sys.SecurityStatus.ToString("0.0");
        SecurityBrush = SecBrush(sys.SecurityStatus);
        var k = _kills.GetValueOrDefault(sys.SystemId);
        KillsLine = $"{k.ship} ship · {k.pod} pod kills (1h)";
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

    private void BuildMap(EsiSystem center, List<EsiSystem> neighbours)
    {
        MapNodes.Clear();
        MapLinks.Clear();
        MapNodes.Add(MakeNode(center, Cx, Cy, isCurrent: true));

        var n = neighbours.Count;
        for (var i = 0; i < n; i++)
        {
            var ang = -Math.PI / 2 + 2 * Math.PI * i / n;
            var x = Cx + Radius * Math.Cos(ang);
            var y = Cy + Radius * Math.Sin(ang);
            MapLinks.Add(new MapLink(Cx, Cy, x, y));
            MapNodes.Add(MakeNode(neighbours[i], x, y, isCurrent: false));
        }
    }

    private MapNode MakeNode(EsiSystem sys, double cx, double cy, bool isCurrent)
    {
        var k = _kills.GetValueOrDefault(sys.SystemId);
        var hot = k.ship + k.pod > 0;
        return new MapNode(cx - NodeHalfW, cy - NodeHalfH, cx, cy,
            sys.Name, sys.SecurityStatus.ToString("0.0"), SecBrush(sys.SecurityStatus),
            isCurrent, hot ? (k.ship + k.pod).ToString() : string.Empty, hot);
    }

    private async Task<string> ResolveSovAsync(int systemId)
    {
        if (!_sov.TryGetValue(systemId, out var allianceId) || allianceId is null or 0) return string.Empty;
        if (_nameCache.TryGetValue(allianceId.Value, out var cached)) return cached;
        var name = (await _esi.ResolveNamesAsync([allianceId.Value])).GetValueOrDefault(allianceId.Value, "");
        if (name.Length > 0) _nameCache[allianceId.Value] = name;
        return name;
    }

    private async Task EnsureActivityAsync(bool force)
    {
        if (!force && DateTime.UtcNow - _activityAt < TimeSpan.FromMinutes(2) && _kills.Count > 0) return;

        var killsTask = _esi.GetSystemKillsAsync();
        var jumpsTask = _esi.GetSystemJumpsAsync();
        var sovTask   = _esi.GetSovMapAsync();
        await Task.WhenAll(killsTask, jumpsTask, sovTask);

        _kills = killsTask.Result.ToDictionary(k => k.SystemId, k => (k.ShipKills, k.PodKills));
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
}
