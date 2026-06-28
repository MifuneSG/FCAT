using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
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
    private readonly IntelChannelService _intel = new();

    private CancellationTokenSource? _cts;
    private int _currentSystemId;
    private readonly HashSet<long> _seenKills = [];
    private readonly Dictionary<int, string> _names = [];   // type/system id → name cache

    public IntelFeedViewModel(EsiService esi, ZkillService zkill, SystemSearchService systems, SettingsService settings)
    {
        _esi = esi;
        _zkill = zkill;
        _systems = systems;
        _settings = settings;
        _intel.ReportReceived += OnReport;
    }

    public ObservableCollection<IntelEntry> Entries { get; } = [];
    [ObservableProperty] private string _channelStatus = "Intel channel: not found";

    /// <summary>Point the feed at the FC's current system + region (called when they jump).</summary>
    public void SetSystem(int systemId, string region)
    {
        _intel.RegionFilter = string.IsNullOrWhiteSpace(region) ? null : region;
        if (systemId != _currentSystemId)
        {
            _currentSystemId = systemId;
            _seenKills.Clear();   // show recent kills for the new system on the next poll
        }
        _intel.Refresh();   // re-select the intel channel for the (possibly new) region
    }

    // ── Lifecycle (driven by the view load/unload) ──
    public void StartAuto()
    {
        if (_cts != null) return;
        _intel.StartWatching(_settings.Current.ChatlogsPath, _settings.Current.IntelChannelPrefix);
        ChannelStatus = $"Intel channel: {_intel.ActiveChannel ?? "not found"}";
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
                ChannelStatus = $"Intel channel: {_intel.ActiveChannel ?? "not found"}";
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

    private async Task PollKillsAsync()
    {
        if (_currentSystemId == 0) return;
        var kills = await _zkill.GetRecentSystemKillsAsync(_currentSystemId);

        // Newest-first from zKill; take the newest few unseen so we don't flood on the first poll.
        var fresh = new List<ZkillEntry>();
        foreach (var k in kills)
            if (k.Zkb != null && _seenKills.Add(k.KillmailId)) fresh.Add(k);
        fresh = fresh.Take(8).ToList();

        // Build oldest→newest so the newest ends up at the top after inserting at 0.
        for (var i = fresh.Count - 1; i >= 0; i--)
        {
            var km = await _esi.GetKillmailAsync(fresh[i].KillmailId, fresh[i].Zkb!.Hash);
            if (km?.Victim == null) continue;
            if (DateTime.UtcNow - km.KillmailTime > TimeSpan.FromHours(2)) continue;   // skip stale

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

    private void OnReport(DateTime time, string speaker, string message)
    {
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
