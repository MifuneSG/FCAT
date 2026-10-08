using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FCAT.Services;

/// <summary>
/// One line in the after-action timeline. <paramref name="Url"/> is set on the lines that have
/// something to open, like a killmail; the timeline renders those as a link.
///
/// <paramref name="MatchKey"/> and <paramref name="Confirmed"/> exist so a line written from one
/// source can later be filled in by a better one. A pilot dying is seen twice: the fleet roster
/// notices within seconds, and the killmail turns up minutes later carrying the system, the ISK and
/// a link. Those are the same event, so the second one replaces the first rather than adding a row.
/// </summary>
public record AarEntry(DateTime Time, string Category, string Text, string? Url = null,
                       string? MatchKey = null, bool Confirmed = false);

/// <summary>A system + EVE time the fleet fought in - one battle-report window. An op can rack up
/// several as it moves, so the report isn't frozen to wherever the first blood landed.</summary>
public record CombatAnchor(int SystemId, string SystemName, DateTime AtUtc)
{
    // zKillboard + EVE-Tools share the /related/<systemID>/<yyyyMMddHHmm>/ scheme (EVE time = UTC).
    // The minutes MUST be 00 - zKill buckets a battle by the hour and throws "Minutes must be 00"
    // on anything else. See <see cref="HourBucket"/>.
    private string Stamp        => HourBucket.ToString("yyyyMMddHHmm");
    public string ZkillUrl      => $"https://zkillboard.com/related/{SystemId}/{Stamp}/";
    public string EvetoolsUrl   => $"https://br.evetools.org/related/{SystemId}/{Stamp}/";

    /// <summary>The hour this fight belongs to. zKill's report for hour H covers H-1 to H+2, so
    /// rounding an anchor DOWN keeps the whole fight inside the window even when it runs long.</summary>
    public DateTime HourBucket =>
        new(AtUtc.Year, AtUtc.Month, AtUtc.Day, AtUtc.Hour, 0, 0, DateTimeKind.Utc);
}

/// <summary>
/// App-lifetime after-action log for the current op. Alerts (via <see cref="AlertHub"/>) and
/// fleet events (session start, pilot joins/leaves, from <c>FleetViewModel</c>) are recorded here
/// so the FC can review or export the op afterwards. Lives for the whole app session and survives
/// page navigation, like the alert feed.
/// </summary>
public partial class SessionLog : ObservableObject
{
    /// <summary>Newest-first for the on-screen panel; <see cref="Export"/> emits chronological order.</summary>
    public ObservableCollection<AarEntry> Entries { get; } = [];

    [ObservableProperty] private DateTime? _sessionStart;
    [ObservableProperty] private string _summary = "No session recorded yet.";

    private long _fleetId;
    private string _fc = string.Empty;

    // Battle-report anchors
    // One window per system the fleet fought in. An op can range across several systems, so we keep
    // a list rather than freezing to the first blood; the report aggregates all of them.
    private readonly List<CombatAnchor> _anchors = [];
    private static readonly TimeSpan AnchorWindow = TimeSpan.FromHours(1);   // zKill's related grouping is ~hourly

    // Who "our side" is, for classifying killmails into kills vs losses on the battle report.
    // Captured at first combat and never widened after (the fight's opening roster).
    private HashSet<int> _friendlyCharIds = [];
    private int _friendlyAllianceId;

    public IReadOnlyCollection<int>  FriendlyCharIds    => _friendlyCharIds;
    public int                       FriendlyAllianceId => _friendlyAllianceId;
    public IReadOnlyList<CombatAnchor> CombatAnchors    => _anchors;

    /// <summary>Anchors a battle-report window at a system the fleet is fighting in. Adds a new window
    /// the first time combat lands in a system (or after that system's ~hourly window has lapsed), so
    /// the report follows the op across systems instead of sticking to first blood.</summary>
    public void MarkCombat(int systemId, string systemName)
    {
        if (systemId <= 0) return;
        if (_anchors.Any(a => a.SystemId == systemId && DateTime.UtcNow - a.AtUtc < AnchorWindow)) return;
        _anchors.Add(new CombatAnchor(systemId, systemName, DateTime.UtcNow));
        MarkDirty();
        OnPropertyChanged(nameof(CombatAnchors));
        OnPropertyChanged(nameof(HasBattleReport));
        OnPropertyChanged(nameof(BattleReportSystem));
    }

    /// <summary>
    /// Records the fleet's own side (character ids + alliance), so the battle report can tell our
    /// losses from our kills.
    ///
    /// The roster is ADDED to on every call rather than frozen at first blood. Freezing it meant a
    /// pilot who joined mid-op was not on our side, and the report only counts a loss as ours when
    /// the victim is - so if they were not in the FC's alliance either, their loss was dropped from
    /// the SRP sheet entirely. Silently not paying someone is the worst way for this to be wrong.
    ///
    /// Widening cannot pull in a stranger: these ids come from the fleet roster, and someone who was
    /// never in the fight has no killmail in its window to be matched against.
    /// </summary>
    public void SetBattleSides(IEnumerable<int> friendlyCharIds, int friendlyAllianceId)
    {
        foreach (var id in friendlyCharIds) _friendlyCharIds.Add(id);
        if (_friendlyAllianceId == 0) _friendlyAllianceId = friendlyAllianceId;
        MarkDirty();
    }

    public bool    HasBattleReport      => _anchors.Count > 0;

    /// <summary>How many systems the header names before it gives up and counts the rest. An op that
    /// anchors in more than a handful is usually a roam, and the names stop fitting beside Refresh.</summary>
    private const int HeaderSystemCap = 4;

    /// <summary>The systems this op fought in, named. "5 systems" told the FC nothing they could act
    /// on; the names are what they recognise and what they go looking for on zKill.</summary>
    public string  BattleReportSystem   => _anchors.Count switch
    {
        0 => string.Empty,
        <= HeaderSystemCap => string.Join(", ", _anchors.Select(a => a.SystemName)),
        _ => string.Join(", ", _anchors.Take(HeaderSystemCap).Select(a => a.SystemName))
             + $" +{_anchors.Count - HeaderSystemCap} more",
    };
    // Persistent link used by the AAR footer button - the op's opening fight.
    public string? BattleReportZkill    => _anchors.Count > 0 ? _anchors[0].ZkillUrl    : null;

    /// <summary>Begins (or continues) the AAR for a fleet session. History is preserved across fleet
    /// re-forms within an app run - EVE hands out a new fleet ID when an FC recreates the fleet, and
    /// the FC still wants the whole op in one timeline. The manual Clear button (or restarting the app)
    /// is the only thing that wipes it.</summary>
    public void StartSession(long fleetId, string fc)
    {
        SessionStart ??= DateTime.Now;   // keep the original op start time across re-forms
        _fleetId = fleetId;
        _fc = fc;
        Record("SESSION", $"Session started, fleet {fleetId}" + (string.IsNullOrEmpty(fc) ? "" : $", FC {fc}"));
        UpdateSummary();
    }

    public void Record(string category, string text, string? url = null, string? matchKey = null)
    {
        Entries.Insert(0, new AarEntry(DateTime.Now, category, text, url, matchKey));
        while (Entries.Count > 2000) Entries.RemoveAt(Entries.Count - 1);
        UpdateSummary();
        MarkDirty();
    }

    /// <summary>
    /// Fills in an earlier line with better information, keeping its original time.
    ///
    /// The time matters: a pilot died when the roster said they died, not when zKillboard got round
    /// to publishing it two minutes later. Returns false when there is nothing to fill in, which is
    /// the caller's cue to record a fresh line - FCAT opened mid-fight, say, so it never saw the
    /// roster transition and the killmail is the first it has heard of the loss.
    ///
    /// Walks oldest-first so that a pilot who dies twice has their first killmail land on their
    /// first death, rather than on whichever row happens to be nearest the top.
    /// </summary>
    public bool Upgrade(string matchKey, string text, string? url)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (e.Confirmed || e.MatchKey != matchKey) continue;
            Entries[i] = e with { Text = text, Url = url, Confirmed = true };
            MarkDirty();
            return true;
        }
        return false;
    }

    public void Clear()
    {
        Entries.Clear();
        SessionStart = null;
        _anchors.Clear();
        try { if (File.Exists(SavePath)) File.Delete(SavePath); }
        catch (IOException ex) { Log.Warn("aar", "could not delete the saved report", ex); }
        _dirty = false;
        OnPropertyChanged(nameof(CombatAnchors));
        OnPropertyChanged(nameof(HasBattleReport));
        OnPropertyChanged(nameof(BattleReportSystem));
        _friendlyCharIds = [];
        _friendlyAllianceId = 0;
        Summary = "No session recorded yet.";
    }

    private void UpdateSummary()
    {
        if (SessionStart is not { } start) { Summary = "No session recorded yet."; return; }
        var span = DateTime.Now - start;
        var dur = span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";
        Summary = $"Fleet {_fleetId}"
                + (string.IsNullOrEmpty(_fc) ? "" : $" · FC {_fc}")
                + $" · {Entries.Count} events · {dur}";
    }

    // ---------------------------------------------------------------------------------------------
    // Staying alive across a restart
    //
    // The AAR used to live only in memory, so closing FCAT - or crashing, three hours into an op -
    // took the whole timeline and the SRP sheet with it, with no warning and nothing to recover.
    // Everything else FCAT holds is either re-fetchable live data or saved in settings.json; this
    // was the one thing it produced that could simply vanish.
    //
    // Writes are coalesced onto a timer rather than done per line. A busy fight records several
    // lines a second, and rewriting the file on each one would be pointless churn; three seconds is
    // far less than anyone loses by closing the app, which is the case this exists for.

    private static string SavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT", "aar.json");

    /// <summary>
    /// How stale a saved report can be and still be picked up. This is here to survive a restart
    /// during an op, not to reopen last week's - an FC launching FCAT the next evening should get a
    /// clean sheet rather than yesterday's timeline to clear by hand.
    /// </summary>
    private static readonly TimeSpan RestoreWindow = TimeSpan.FromHours(12);

    private sealed record SavedLog(DateTime? SessionStart, long FleetId, string Fc,
                                   List<AarEntry> Entries, List<CombatAnchor> Anchors,
                                   List<int> FriendlyCharIds, int FriendlyAllianceId);

    private readonly DispatcherTimer? _autosave;
    private readonly Lock _fileLock = new();
    private bool _dirty;

    private void MarkDirty() => _dirty = true;

    public SessionLog()
    {
        Restore();

        // No dispatcher means no app - a test harness, say. Everything else still works; it just
        // does not autosave, and Flush() is there for anything that wants to force it.
        if (Application.Current?.Dispatcher is not { } dispatcher) return;
        _autosave = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
                                        (_, _) => Flush(), dispatcher);
        _autosave.Start();
    }

    /// <summary>Writes the report to disk if anything has changed since the last write.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;

        // Snapshot on the caller's thread (the UI one), write off it: Entries is an
        // ObservableCollection, so reading it from the writer thread would be a race.
        var snapshot = new SavedLog(SessionStart, _fleetId, _fc, [.. Entries], [.. _anchors],
                                    [.. _friendlyCharIds], _friendlyAllianceId);
        Task.Run(() => Write(snapshot));
    }

    private void Write(SavedLog snapshot)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);

                // Via a temp file, so a crash mid-write cannot leave a half-written report behind -
                // which would be a worse outcome than the one this whole feature exists to prevent.
                var temp = SavePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
                File.Move(temp, SavePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            // Not fatal - the report is still on screen and still exportable by hand. But an FC who
            // thinks it is being saved and finds out otherwise after a crash deserves the warning.
            Log.Warn("aar", "could not save the after-action report", ex);
        }
    }

    private void Restore()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            if (JsonSerializer.Deserialize<SavedLog>(File.ReadAllText(SavePath)) is not { } saved) return;

            var newest = saved.Entries.Count > 0 ? saved.Entries.Max(e => e.Time) : saved.SessionStart;
            if (newest is null || DateTime.Now - newest > RestoreWindow) return;

            foreach (var e in saved.Entries) Entries.Add(e);
            foreach (var a in saved.Anchors) _anchors.Add(a);
            _friendlyCharIds    = [.. saved.FriendlyCharIds];
            _friendlyAllianceId = saved.FriendlyAllianceId;
            _fleetId            = saved.FleetId;
            _fc                 = saved.Fc;
            SessionStart        = saved.SessionStart;
            UpdateSummary();
        }
        catch (Exception ex)
        {
            // A report we cannot read is not worth taking the app down for, but it is worth saying -
            // silence here looks exactly like "there was nothing to restore".
            Log.Warn("aar", "could not read the saved after-action report", ex);
        }
    }

    /// <summary>Renders the AAR as Markdown, oldest event first. When a fetched <paramref name="report"/>
    /// is supplied its totals + kill/loss (SRP) lists are embedded; otherwise just the battle-report links.</summary>
    public string Export(BattleReport? report = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# FCAT After-Action Report");
        if (SessionStart is { } start)
            sb.AppendLine($"Fleet {_fleetId}" + (string.IsNullOrEmpty(_fc) ? "" : $" · FC {_fc}")
                + $" · started {start:yyyy-MM-dd HH:mm:ss} · {Entries.Count} events");
        if (HasBattleReport)
        {
            sb.AppendLine();
            sb.AppendLine("## Battle Report");
            sb.AppendLine($"- **Systems:** {string.Join(", ", _anchors.Select(a => a.SystemName))}");
            foreach (var a in _anchors)
                sb.AppendLine($"- **{a.SystemName}:** {a.ZkillUrl} · [EVE-Tools]({a.EvetoolsUrl})");

            if (report != null)
            {
                sb.AppendLine($"- **Destroyed:** {report.IskDestroyedText} ({report.KillCount} kills)");
                sb.AppendLine($"- **Lost:** {report.IskLostText} ({report.LossCount} losses)");
                sb.AppendLine($"- **Efficiency:** {report.EfficiencyText}");

                if (report.HasLosses)
                {
                    sb.AppendLine();
                    sb.AppendLine("### Losses (SRP)");
                    sb.AppendLine("| Pilot | Ship | Value |");
                    sb.AppendLine("| --- | --- | --- |");
                    foreach (var l in report.Losses)
                        sb.AppendLine($"| {l.Pilot} | {l.Ship} | {l.IskText} |");
                }

                if (report.HasKills)
                {
                    sb.AppendLine();
                    sb.AppendLine("### Kills");
                    sb.AppendLine("| Pilot | Ship | Value |");
                    sb.AppendLine("| --- | --- | --- |");
                    foreach (var k in report.Kills)
                        sb.AppendLine($"| {k.Pilot} | {k.Ship} | {k.IskText} |");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Timeline");
        foreach (var e in Entries.Reverse())   // chronological
            sb.AppendLine($"- `{e.Time:HH:mm:ss}`  **{e.Category}**  {e.Text}"
                + (string.IsNullOrEmpty(e.Url) ? string.Empty : $"  <{e.Url}>"));
        return sb.ToString();
    }

    /// <summary>Plain-text AAR for pasting into chat (Discord). No Markdown tables/headings - those
    /// render as raw pipes in Discord - just clean lines that read well anywhere.</summary>
    public string ExportForCopy(BattleReport? report = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FCAT After-Action Report");
        if (SessionStart is { } start)
            sb.AppendLine($"Fleet {_fleetId}" + (string.IsNullOrEmpty(_fc) ? "" : $" · FC {_fc}")
                + $" · started {start:yyyy-MM-dd HH:mm:ss} · {Entries.Count} events");

        if (HasBattleReport)
        {
            sb.AppendLine();
            sb.AppendLine($"Battle Report: {string.Join(", ", _anchors.Select(a => a.SystemName))}");
            foreach (var a in _anchors)
                sb.AppendLine($"{a.SystemName}: {a.ZkillUrl}  |  {a.EvetoolsUrl}");

            if (report != null)
            {
                sb.AppendLine($"Destroyed: {report.IskDestroyedText} ({report.KillCount} kills)");
                sb.AppendLine($"Lost: {report.IskLostText} ({report.LossCount} losses)");
                sb.AppendLine($"Efficiency: {report.EfficiencyText}");

                if (report.HasLosses)
                {
                    sb.AppendLine();
                    sb.AppendLine("Losses (SRP)");
                    foreach (var l in report.Losses)
                        sb.AppendLine($"• {l.Pilot} · {l.Ship} · {l.IskText}");
                }
                if (report.HasKills)
                {
                    sb.AppendLine();
                    sb.AppendLine("Kills");
                    foreach (var k in report.Kills)
                        sb.AppendLine($"• {k.Pilot} · {k.Ship} · {k.IskText}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("Timeline");
        foreach (var e in Entries.Reverse())   // chronological
            sb.AppendLine($"{e.Time:HH:mm:ss}  {e.Category}  {e.Text}"
                + (string.IsNullOrEmpty(e.Url) ? string.Empty : $"  {e.Url}"));
        return sb.ToString();
    }
}
