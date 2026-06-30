using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FCAT.Services;

/// <summary>One line in the after-action timeline.</summary>
public record AarEntry(DateTime Time, string Category, string Text);

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

    /// <summary>Begins (or continues) the AAR for a fleet session. History is preserved across fleet
    /// re-forms within an app run — EVE hands out a new fleet ID when an FC recreates the fleet, and
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

    /// <summary>Renders the timeline as Markdown, oldest event first.</summary>
    public string Export()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# FCAT After-Action Report");
        if (SessionStart is { } start)
            sb.AppendLine($"Fleet {_fleetId}" + (string.IsNullOrEmpty(_fc) ? "" : $" · FC {_fc}")
                + $" · started {start:yyyy-MM-dd HH:mm:ss} · {Entries.Count} events");
        sb.AppendLine();
        sb.AppendLine("## Timeline");
        foreach (var e in Entries.Reverse())   // chronological
            sb.AppendLine($"- `{e.Time:HH:mm:ss}`  **{e.Category}**  {e.Text}");
        return sb.ToString();
    }
}
