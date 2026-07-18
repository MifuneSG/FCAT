using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FCAT.Services;

/// <summary>One line in the after-action timeline.</summary>
public record AarEntry(DateTime Time, string Category, string Text);

/// <summary>A system + EVE time the fleet fought in - one battle-report window. An op can rack up
/// several as it moves, so the report isn't frozen to wherever the first blood landed.</summary>
public record CombatAnchor(int SystemId, string SystemName, DateTime AtUtc)
{
    // zKillboard + EVE-Tools share the /related/<systemID>/<yyyyMMddHHmm>/ scheme (EVE time = UTC).
    private string Stamp        => AtUtc.ToString("yyyyMMddHHmm");
    public string ZkillUrl      => $"https://zkillboard.com/related/{SystemId}/{Stamp}/";
    public string EvetoolsUrl   => $"https://br.evetools.org/related/{SystemId}/{Stamp}/";
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
        OnPropertyChanged(nameof(CombatAnchors));
    }

    /// <summary>Records the fleet's own side (character ids + alliance) once, so the battle report can
    /// tell our losses from our kills. Called when combat is first anchored.</summary>
    public void SetBattleSides(IEnumerable<int> friendlyCharIds, int friendlyAllianceId)
    {
        if (_friendlyCharIds.Count > 0) return;   // freeze the opening roster
        _friendlyCharIds    = [.. friendlyCharIds];
        _friendlyAllianceId = friendlyAllianceId;
    }

    public bool    HasBattleReport      => _anchors.Count > 0;
    public string  BattleReportSystem   => _anchors.Count switch
    {
        0 => string.Empty,
        1 => _anchors[0].SystemName,
        _ => $"{_anchors.Count} systems",
    };
    // Persistent link used by the AAR footer button - the op's opening fight.
    public string? BattleReportZkill    => _anchors.Count > 0 ? _anchors[0].ZkillUrl    : null;
    public string? BattleReportEvetools => _anchors.Count > 0 ? _anchors[0].EvetoolsUrl : null;

    /// <summary>Begins (or continues) the AAR for a fleet session. History is preserved across fleet
    /// re-forms within an app run - EVE hands out a new fleet ID when an FC recreates the fleet, and
    /// the FC still wants the whole op in one timeline. The manual Clear button (or restarting the app)
    /// is the only thing that wipes it.</summary>
    public void StartSession(long fleetId, string fc)
    {
        SessionStart ??= DateTime.Now;   // keep the original op start time across re-forms
        _fleetId = fleetId;
        _fc = fc;
        Record("SESSION", $"Session started — fleet {fleetId}" + (string.IsNullOrEmpty(fc) ? "" : $", FC {fc}"));
        UpdateSummary();
    }

    public void Record(string category, string text)
    {
        Entries.Insert(0, new AarEntry(DateTime.Now, category, text));
        while (Entries.Count > 2000) Entries.RemoveAt(Entries.Count - 1);
        UpdateSummary();
    }

    public void Clear()
    {
        Entries.Clear();
        SessionStart = null;
        _anchors.Clear();
        OnPropertyChanged(nameof(CombatAnchors));
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
                sb.AppendLine($"- **{a.SystemName}:** {a.ZkillUrl}");

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
            sb.AppendLine($"- `{e.Time:HH:mm:ss}`  **{e.Category}**  {e.Text}");
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
            sb.AppendLine($"Battle Report — {string.Join(", ", _anchors.Select(a => a.SystemName))}");
            foreach (var a in _anchors)
                sb.AppendLine($"{a.SystemName}: {a.ZkillUrl}");

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
                        sb.AppendLine($"• {l.Pilot} — {l.Ship} — {l.IskText}");
                }
                if (report.HasKills)
                {
                    sb.AppendLine();
                    sb.AppendLine("Kills");
                    foreach (var k in report.Kills)
                        sb.AppendLine($"• {k.Pilot} — {k.Ship} — {k.IskText}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("Timeline");
        foreach (var e in Entries.Reverse())   // chronological
            sb.AppendLine($"{e.Time:HH:mm:ss}  {e.Category}  {e.Text}");
        return sb.ToString();
    }
}
