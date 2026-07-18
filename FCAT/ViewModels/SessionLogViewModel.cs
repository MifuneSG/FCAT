using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
                ? "No kills on zKillboard yet — they lag a few minutes. Try Refresh."
                : $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { ReportStatus = $"Couldn't reach zKillboard — {ex.Message}"; }
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
        catch (Exception ex) { StatusMessage = $"Save failed — {ex.Message}"; }
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
        catch (Exception ex) { StatusMessage = $"Couldn't open the browser — {ex.Message}"; }
    }

    [RelayCommand]
    private void Back() => _shell.BackToMenu();
}
