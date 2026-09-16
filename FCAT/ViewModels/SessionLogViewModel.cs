using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>The after-action report view - session timeline plus a fleshed-out battle report
/// (ISK destroyed vs lost, efficiency, kill/loss lists, SRP sheet) pulled from zKillboard.</summary>
public partial class SessionLogViewModel : ObservableObject
{
    private readonly BattleReportService _battleReport;
    private readonly ShellViewModel _shell;

    public SessionLog Log { get; }

    public SessionLogViewModel(SessionLog log, BattleReportService battleReport, ShellViewModel shell)
    {
        Log = log;
        _battleReport = battleReport;
        _shell = shell;

        // Auto-pull the report if this op already anchored a fight; killmails lag, so a Refresh is offered.
        if (Log.HasBattleReport) _ = LoadReportAsync();

        BuildAuthPanels();
        _shell.Aa.Updated += OnAaUpdated;
    }

    /// <summary>The connector refreshes on its own schedule; repaint when it does.</summary>
    private void OnAaUpdated() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(BuildAuthPanels);

    // FAT and SRP, read from the FC's own Alliance Auth. Both panels hide completely without it,
    // which is the normal case. Nothing here writes: creating a link and approving a payout stay on
    // the website, because the connector is read-only and that is worth keeping.

    [ObservableProperty] private bool   _hasFat;
    [ObservableProperty] private string _fatFleet    = string.Empty;
    [ObservableProperty] private string _fatStatus   = string.Empty;
    [ObservableProperty] private string _fatCounts   = string.Empty;
    [ObservableProperty] private bool   _fatComplete;

    /// <summary>What auth is doing about attendance, when it tracks by ESI rather than by clicks.</summary>
    [ObservableProperty] private string _fatTracking = string.Empty;

    /// <summary>False when attendance for this op is not actually being recorded.</summary>
    [ObservableProperty] private bool _fatHealthy = true;

    /// <summary>
    /// Pilots in the fleet who have not clicked the link.
    ///
    /// This is the half auth cannot do. Its own page lists who turned up; only FCAT knows who was
    /// actually in the fleet, so only FCAT can name the ones still owing a click - which is the
    /// thing an FC would otherwise chase by eye at the end of an op.
    /// </summary>
    public ObservableCollection<string> FatMissing { get; } = [];

    [ObservableProperty] private bool _hasSrp;
    public ObservableCollection<AaSrpFleet> SrpFleets { get; } = [];

    private string _fatUrl = string.Empty;

    private void BuildAuthPanels()
    {
        var aa = _shell.Aa;

        // --- FAT
        FatMissing.Clear();
        var link = aa.CurrentFatLink;
        HasFat = link != null;

        if (link != null)
        {
            _fatUrl   = link.Url;
            FatFleet  = link.Fleet;
            FatStatus = link.TimeLeft;

            var clicked = link.Attendees
                .Select(a => a.CharacterName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var roster  = _shell.ActiveSession?.AllMembers;
            // The dashboard knows the fleet id before Fleet Ops has ever been opened, so fall back
            // to it - otherwise opening the AAR straight from the dashboard downgrades a definite
            // "tracking this fleet" into a vague "tracking a fleet" for no reason.
            var fleetId = _shell.ActiveSession?.SessionFleetId is > 0 and var live
                ? live
                : _shell.DetectedFleetId;

            // An ESI link tracks the fleet itself, so the useful question is not who has clicked -
            // it is whether auth is watching THIS fleet. FCAT is the only thing that knows both
            // sides of that, and a tracker quietly pointed at a fleet that has since re-formed is
            // how an op ends up with no attendance recorded at all.
            if (link.IsEsi)
            {
                var tracksThisFleet = fleetId != 0 && link.EsiFleetId == fleetId;

                FatTracking = link.HasEsiTrouble
                    ? $"Auth stopped tracking: {link.EsiError}"
                    : !link.EsiRegistered ? "Auth is not tracking any fleet"
                    : fleetId == 0        ? "Tracking a fleet via ESI"
                    : tracksThisFleet     ? "Tracking this fleet"
                    : "Tracking a DIFFERENT fleet - this op is not being recorded";

                FatHealthy = link.EsiRegistered && !link.HasEsiTrouble
                             && (fleetId == 0 || tracksThisFleet);

                FatCounts   = roster is { Count: > 0 }
                    ? $"{clicked.Count} of {roster.Count} registered"
                    : $"{clicked.Count} registered";
                FatComplete = FatHealthy;
            }
            else
            {
                // A clickable link, so chasing the ones who have not clicked is exactly the job.
                FatTracking = string.Empty;
                FatHealthy  = true;

                if (roster is { Count: > 0 })
                {
                    foreach (var m in roster
                                 .Where(m => !clicked.Contains(m.CharacterName))
                                 .OrderBy(m => m.CharacterName, StringComparer.OrdinalIgnoreCase))
                        FatMissing.Add(m.CharacterName);

                    // Counted against the ROSTER, not the attendee list. The question is how many of
                    // THIS fleet clicked, so someone who clicked then left must not pad the number -
                    // and the count has to agree with the names listed under it.
                    FatCounts   = $"{roster.Count - FatMissing.Count} of {roster.Count} clicked";
                    FatComplete = FatMissing.Count == 0;
                }
                else
                {
                    FatCounts   = $"{clicked.Count} clicked";
                    FatComplete = true;   // nothing to chase without a roster
                }
            }
        }

        // --- SRP
        SrpFleets.Clear();
        foreach (var fleet in aa.SrpFleets.Take(5)) SrpFleets.Add(fleet);
        HasSrp = SrpFleets.Count > 0;
    }

    /// <summary>The FAT link on the clipboard, ready to broadcast. FCAT cannot make one - this is
    /// the one the FC already made, fetched so they do not have to go and find it.</summary>
    [RelayCommand]
    private void CopyFatLink()
    {
        if (string.IsNullOrWhiteSpace(_fatUrl)) { StatusMessage = "No link to copy."; return; }
        try
        {
            System.Windows.Clipboard.SetText(_fatUrl);
            StatusMessage = "FAT link copied - paste it into fleet.";
        }
        catch { StatusMessage = "Couldn't access the clipboard."; }
    }

    /// <summary>The names still owing a click, ready to paste into fleet chat.</summary>
    [RelayCommand]
    private void CopyFatMissing()
    {
        if (FatMissing.Count == 0) { StatusMessage = "Everyone has clicked."; return; }
        try
        {
            System.Windows.Clipboard.SetText(string.Join(", ", FatMissing) + " - click FAT");
            StatusMessage = $"{FatMissing.Count} name(s) copied.";
        }
        catch { StatusMessage = "Couldn't access the clipboard."; }
    }

    [ObservableProperty] private string _statusMessage = string.Empty;

    // Battle report
    [ObservableProperty] private BattleReport? _report;
    [ObservableProperty] private bool   _isLoadingReport;
    [ObservableProperty] private string _reportStatus = string.Empty;

    public bool HasReport => Report != null;
    partial void OnReportChanged(BattleReport? value) => OnPropertyChanged(nameof(HasReport));

    [RelayCommand]
    private async Task LoadReport() => await LoadReportAsync();

    private async Task LoadReportAsync()
    {
        if (!Log.HasBattleReport || IsLoadingReport) return;
        IsLoadingReport = true;
        ReportStatus = "Pulling kills from zKillboard…";
        try
        {
            var report = await _battleReport.BuildAsync(
                Log.CombatAnchors, Log.FriendlyCharIds, Log.FriendlyAllianceId);

            Report = report;
            ReportStatus = report == null
                ? "No kills on zKillboard yet. They lag a few minutes, try Refresh."
                : $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { ReportStatus = $"Couldn't reach zKillboard: {ex.Message}"; }
        finally { IsLoadingReport = false; }
    }

    // Plain-text AAR (battle report + SRP + timeline) - reads cleanly pasted into Discord.
    [RelayCommand]
    private void Copy()
    {
        if (Log.Entries.Count == 0) { StatusMessage = "Nothing to copy yet."; return; }
        try
        {
            System.Windows.Clipboard.SetText(Log.ExportForCopy(Report));
            StatusMessage = "AAR copied to clipboard.";
        }
        catch { StatusMessage = "Couldn't access the clipboard."; }
    }

    [RelayCommand]
    private void Save()
    {
        if (Log.Entries.Count == 0) { StatusMessage = "Nothing to save yet."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save after-action report",
            FileName = $"FCAT-AAR-{(Log.SessionStart ?? DateTime.Now):yyyyMMdd-HHmm}.md",
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, Log.Export(Report));
            StatusMessage = $"Saved to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void Clear()
    {
        if (Log.Entries.Count == 0) { StatusMessage = "Nothing to clear."; return; }

        // The AAR now persists across fleet re-forms, so a stray click could wipe a whole op. Confirm.
        var confirm = System.Windows.MessageBox.Show(
            "Clear the after-action report? This wipes the whole session timeline and can't be undone.",
            "Clear AAR",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        Log.Clear();
        Report = null;
        ReportStatus = string.Empty;
        StatusMessage = "Cleared.";
    }

    // Opens the op's zKillboard related-kills battle report in the browser. Same system+time also
    // works on EVE-Tools (Log.BattleReportEvetools) - zKill is the default.
    [RelayCommand]
    private void OpenBattleReport()
    {
        if (Log.BattleReportZkill is not { } url) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = $"Couldn't open the browser: {ex.Message}"; }
    }

    [RelayCommand]
    private void Back()
    {
        _shell.Aa.Updated -= OnAaUpdated;   // this page is rebuilt per visit; do not stack handlers
        _shell.BackToMenu();
    }
}
