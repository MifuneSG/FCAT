using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// A combined, live intel feed: recent kills in your current system (zKillboard + ESI detail) and
/// reports from your in-game intel chat channel (tailed from the Chatlogs, like the boost reader).
/// </summary>
public partial class IntelFeedViewModel : ObservableObject
{
    private readonly EsiService _esi;
    private readonly ZkillService _zkill;
    private readonly SystemSearchService _systems;
    private readonly SettingsService _settings;
    private readonly AlertHub _alertHub;
    private readonly CustomAlertService _customAlerts;
    private readonly IntelChannelService _intel = new();

    private CancellationTokenSource? _cts;
    private int _currentSystemId;
    private int _constellationId;
    private int _regionId;

    /// <summary>
    /// Which scale the kill feed searches. It follows the open Intel pane: the minimap is a
    /// constellation, so kills come from the constellation; the hunt board works across regions,
    /// so it widens to the region.
    /// </summary>
    private ZkillService.KillScope _killScope = ZkillService.KillScope.Constellation;
    private readonly HashSet<long> _seenKills = [];
    private readonly Dictionary<int, string> _names = [];   // type/system id -> name cache

    // Systems worth shouting about when the intel channel names them.
    private string _watchedSystem = string.Empty;
    private HashSet<string> _watchedAdjacent = new(StringComparer.OrdinalIgnoreCase);

    public IntelFeedViewModel(EsiService esi, ZkillService zkill, SystemSearchService systems,
                              SettingsService settings, AlertHub alertHub, CustomAlertService customAlerts)
    {
        _esi = esi;
        _zkill = zkill;
        _systems = systems;
        _settings = settings;
        _alertHub = alertHub;
        _customAlerts = customAlerts;
        _intel.ReportReceived += OnReport;

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = o => _filter == "All"
                                || (o is IntelEntry e && (_filter == "Kills" ? e.IsKill : !e.IsKill));
    }

    public ObservableCollection<IntelEntry> Entries { get; } = [];

    /// <summary>Filtered view the feed binds to (All / Kills / Reports).</summary>
    public ICollectionView EntriesView { get; }

    [ObservableProperty] private string _filter = "All";
    public bool IsAll     => Filter == "All";
    public bool IsKills   => Filter == "Kills";
    public bool IsReports => Filter == "Reports";

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(IsKills));
        OnPropertyChanged(nameof(IsReports));
    }

    [RelayCommand]
    private void SetFilter(string mode) { Filter = mode; EntriesView.Refresh(); }

    [ObservableProperty] private string _channelStatus = "Intel channel: not found";

    private string _region = string.Empty;

    /// <summary>
    /// "Not found" on its own is useless - it could mean no intel channel is joined at all, or that
    /// the ones joined are for other regions. Name what's there so the FC knows which it is.
    /// </summary>
    private void UpdateChannelStatus()
    {
        if (!string.IsNullOrEmpty(_intel.ActiveChannel))
        {
            ChannelStatus = $"Intel channel: {_intel.ActiveChannel}";
            return;
        }

        var joined = _intel.CandidateChannels();
        ChannelStatus = joined.Count == 0 ? "Intel channel: none joined in game"
                      : _region.Length > 0 ? $"Intel channel: none for {_region} · you have {string.Join(", ", joined.Take(3))}"
                      : "Intel channel: not found";
    }

    /// <summary>Point the feed at the FC's current system + region (called when they jump).</summary>
    public void SetSystem(SystemScope scope)
    {
        var region = scope.RegionName;
        _region = region ?? string.Empty;
        _intel.RegionFilter = string.IsNullOrWhiteSpace(region) ? null : region;
        _constellationId = scope.ConstellationId;
        _regionId        = scope.RegionId;
        if (scope.SystemId != _currentSystemId)
        {
            _currentSystemId = scope.SystemId;
            _seenKills.Clear();

            // Poll now rather than waiting out the 90s tick. The ids only arrive once the
            // constellation has loaded, which is after the feed started, so the first tick
            // always found nothing to search by and the feed sat empty for a minute and a half.
            _ = SafePollKillsAsync();
        }
        _intel.Refresh();   // re-select the intel channel for the (possibly new) region
        UpdateChannelStatus();
    }

    // Lifecycle (driven by the view load/unload)
    public void StartAuto()
    {
        if (_cts != null) return;
        _intel.StartWatching(_settings.Current.ChatlogsPath, _settings.Current.IntelChannelPrefix);
        UpdateChannelStatus();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void StopAuto()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _intel.StopWatching();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            var tick = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do
            {
                _intel.Refresh();   // pull new intel-chat lines (watcher is unreliable on open files)
                UpdateChannelStatus();
                if (tick++ % 9 == 0) await SafePollKillsAsync();   // zKill ~every 90s (be gentle on their API)
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task SafePollKillsAsync()
    {
        try { await PollKillsAsync(); } catch { /* feed just skips this tick */ }
    }

    /// <summary>
    /// How far back a killmail can be and still be worth showing. zKill's list endpoints lag by
    /// hours (see ZkillService), so a tight window here silently empties the feed - the old
    /// two-hour cutoff discarded literally every kill the API returned, in every system.
    /// Each row carries the kill's own timestamp, so an old one reads as old.
    /// </summary>
    private static readonly TimeSpan KillHorizon = TimeSpan.FromHours(24);

    private async Task PollKillsAsync()
    {
        var id = _killScope switch
        {
            ZkillService.KillScope.Constellation => _constellationId,
            ZkillService.KillScope.Region        => _regionId,
            _                                    => _currentSystemId,
        };
        if (id == 0) return;

        var kills = await _zkill.GetRecentKillsAsync(_killScope, id);

        // Newest-first from zKill; take the newest few unseen so we don't flood on the first poll.
        var fresh = new List<ZkillEntry>();
        foreach (var k in kills)
            if (k.Zkb != null && _seenKills.Add(k.KillmailId)) fresh.Add(k);
        fresh = fresh.Take(8).ToList();

        // Build oldest->newest so the newest ends up at the top after inserting at 0.
        for (var i = fresh.Count - 1; i >= 0; i--)
        {
            var km = await _esi.GetKillmailAsync(fresh[i].KillmailId, fresh[i].Zkb!.Hash);
            if (km?.Victim == null) continue;
            if (DateTime.UtcNow - km.KillmailTime > KillHorizon) continue;   // skip stale

            var ship = await NameAsync(km.Victim.ShipTypeId);
            var sys  = await NameAsync(km.SolarSystemId);
            Add(new IntelEntry
            {
                Time = km.KillmailTime.ToLocalTime(),
                Kind = IntelKind.Kill,
                System = sys,
                Detail = $"{ship} down",
                Meta = FormatIsk(fresh[i].Zkb!.TotalValue),
                Url = $"https://zkillboard.com/kill/{km.KillmailId}/",
            });
        }
    }

    /// <summary>
    /// Widen or narrow the kill search to match the pane the FC is looking at. Re-polls from
    /// scratch, because the wider scope has kills the narrower one never returned.
    /// </summary>
    public void SetKillScope(ZkillService.KillScope scope)
    {
        if (scope == _killScope) return;
        _killScope = scope;
        _seenKills.Clear();
        _ = SafePollKillsAsync();
    }

    /// <summary>The systems an intel call-out should raise an alert for - where you are, and (if the
    /// FC wants it) the systems one gate out. Pushed in when the constellation loads.</summary>
    public void SetWatchedSystems(string current, IEnumerable<string> adjacent)
    {
        _watchedSystem   = current ?? string.Empty;
        _watchedAdjacent = new HashSet<string>(adjacent ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private void OnReport(DateTime time, string speaker, string message, bool backfill)
    {
        // Every intel line is offered to the FC's own rules, even the ones that aren't system
        // reports - but not the history replayed when a log is first opened.
        if (!backfill) _customAlerts.OnIntelMessage(message);

        // A real intel report names a system (char > system > ship, or "<system> nv/clr"). Questions
        // ("any hostiles in X?") and chatter aren't reports. (Kill links were already filtered upstream.)
        if (message.EndsWith('?')) return;
        if (_systems.DetectSystemMatch(message) is not { } m) return;

        var status = DetectStatus(message);
        Add(new IntelEntry
        {
            Time = time, Kind = IntelKind.Report,
            System = m.Name, Status = status, Detail = BuildDetail(message, m.Token), Meta = speaker,
        });

        if (!backfill) RaiseIntelAlertIfWatched(m.Name, status, speaker, message);
    }

    /// <summary>
    /// Shouts when the intel channel names your system (critical) or one next door (warning). A
    /// "clear" call is the opposite of a threat, so it never alerts - it only shows in the feed.
    /// </summary>
    private void RaiseIntelAlertIfWatched(string system, IntelStatus status, string speaker, string message)
    {
        if (!_settings.Current.IntelHostileAlertEnabled) return;
        if (status == IntelStatus.Clear) return;

        bool here = system.Equals(_watchedSystem, StringComparison.OrdinalIgnoreCase);
        bool next = !here && _settings.Current.IntelHostileAdjacentToo && _watchedAdjacent.Contains(system);
        if (!here && !next) return;

        var where = here ? "your system" : "next door";
        App.Current.Dispatcher.Invoke(() => _alertHub.Raise(new FcAlert
        {
            Timestamp        = DateTime.Now,
            AlertType        = AlertType.IntelHostile,
            SeverityOverride = here ? AlertSeverity.Critical : AlertSeverity.Warning,
            Detail           = $"{system} ({where}): {message.Trim()} [{speaker}]",
            RawLogLine       = message,
        }));
    }

    private static readonly HashSet<string> StatusWords = new(StringComparer.OrdinalIgnoreCase)
        { "clr", "clear", "cleared", "nv", "jumped", "jump", "jumping", "inc", "incoming", "gate", "gating", "in" };

    private static IntelStatus DetectStatus(string message)
    {
        foreach (var raw in message.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim('.', ',', '!').ToLowerInvariant();
            if (w is "clr" or "clear" or "cleared") return IntelStatus.Clear;
            if (w is "nv") return IntelStatus.NoVisual;
            if (w is "jumped" or "jump" or "jumping" or "inc" or "incoming" or "gate" or "gating") return IntelStatus.Incoming;
        }
        return IntelStatus.None;
    }

    /// <summary>The pilots/ship left after removing the system token and status words.</summary>
    private static string BuildDetail(string message, string systemToken)
    {
        var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals(systemToken, StringComparison.OrdinalIgnoreCase)
                     && !StatusWords.Contains(w.Trim('.', ',', '!')));
        return string.Join(' ', parts).Trim();
    }

    private void Add(IntelEntry entry)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            // Newest at the top, by arrival. Do NOT sort this by Time: reports are stamped when
            // they are read, kills carry the time of the kill hours earlier, and the list is capped
            // - sorting pushes every kill below every report and the cap then evicts them, which
            // empties the Kills tab completely.
            Entries.Insert(0, entry);
            while (Entries.Count > 100) Entries.RemoveAt(Entries.Count - 1);
        });
    }

    private async Task<string> NameAsync(int id)
    {
        if (id <= 0) return "?";
        if (_names.TryGetValue(id, out var n)) return n;
        var name = (await _esi.ResolveNamesAsync([id])).GetValueOrDefault(id, id.ToString());
        _names[id] = name;
        return name;
    }

    private static string FormatIsk(double isk) => isk switch
    {
        >= 1_000_000_000 => $"{isk / 1_000_000_000:0.0}B ISK",
        >= 1_000_000     => $"{isk / 1_000_000:0.0}M ISK",
        >= 1_000         => $"{isk / 1_000:0.0}K ISK",
        _                => $"{isk:0} ISK",
    };

    [RelayCommand]
    private static void Open(IntelEntry? entry)
    {
        if (entry?.Url is not { Length: > 0 } url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
