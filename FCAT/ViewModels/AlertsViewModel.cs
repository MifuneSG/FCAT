using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// The Alerts page: the session feed, plus all alert configuration in one place.
///
/// Configuration used to be four stacked sections in the Settings page's right column, with the
/// built-in alerts and the FC's own rules in separate blocks. Here every alert FCAT can raise is
/// ONE list, and a new one is created by answering a few questions rather than filling in a form.
/// </summary>
public partial class AlertsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public AlertHub Hub { get; }
    public ObservableCollection<FcAlert> Alerts => Hub.Alerts;

    public AlertsViewModel(AlertHub hub, SettingsService settings)
    {
        Hub = hub;
        _settings = settings;
        _alertSoundsEnabled = settings.Current.AlertSoundsEnabled;
        LoadRows();
    }

    // Feed / Configure tabs
    [ObservableProperty] private string _tab = "Feed";
    public bool IsFeedTab   => Tab == "Feed";
    public bool IsConfigTab => Tab == "Configure";

    partial void OnTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsFeedTab));
        OnPropertyChanged(nameof(IsConfigTab));
    }

    [RelayCommand] private void SetTab(string t) => Tab = t;

    // The one list of every alert FCAT can raise
    public ObservableCollection<AlertConfigRow> Rows { get; } = [];

    [ObservableProperty] private bool _alertSoundsEnabled = true;
    [RelayCommand] private void ToggleSounds() { AlertSoundsEnabled = !AlertSoundsEnabled; Save(); }

    private void LoadRows()
    {
        var s = _settings.Current;
        bool On(AlertType t) => !s.MutedAlertTypes.Contains(t.ToString());

        Rows.Clear();
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.Tackled, "Tackled",
            "You are scrammed or pointed", AlertSeverity.Critical, s.TackledSound, On(AlertType.Tackled)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.IntelHostile, "Intel call-out",
            "Your system or one next door named in intel", AlertSeverity.Critical, s.IntelHostileSound, On(AlertType.IntelHostile)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.DpsLoss, "DPS loss",
            "The fleet's damage line is dying", AlertSeverity.Warning, s.DpsLossSound, On(AlertType.DpsLoss)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.LogiRatio, "Logi thin",
            "Logi dropped below your ratio", AlertSeverity.Warning, s.LogiRatioSound, On(AlertType.LogiRatio)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.LogiChain, "Cap chain",
            "A cap-chain logi dropped", AlertSeverity.Warning, s.LogiChainSound, On(AlertType.LogiChain)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.BoostLost, "Booster down",
            "A booster was podded, links lost", AlertSeverity.Warning, s.BoostLostSound, On(AlertType.BoostLost)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.CapTrouble, "Cap out",
            "A module shut off from low cap", AlertSeverity.Warning, s.CapTroubleSound, On(AlertType.CapTrouble)));

        foreach (var r in s.CustomAlerts) Rows.Add(AlertConfigRow.ForRule(r));
    }

    private string SoundOf(AlertType t) => Rows.FirstOrDefault(r => r.BuiltIn == t)?.Sound ?? "None";

    /// <summary>Writes the whole list back to settings. Cheap, so it runs on every toggle.</summary>
    [RelayCommand]
    private void Save()
    {
        var s = _settings.Current;
        s.AlertSoundsEnabled = AlertSoundsEnabled;

        s.TackledSound      = SoundOf(AlertType.Tackled);
        s.CapTroubleSound   = SoundOf(AlertType.CapTrouble);
        s.BoostLostSound    = SoundOf(AlertType.BoostLost);
        s.LogiChainSound    = SoundOf(AlertType.LogiChain);
        s.DpsLossSound      = SoundOf(AlertType.DpsLoss);
        s.LogiRatioSound    = SoundOf(AlertType.LogiRatio);
        s.IntelHostileSound = SoundOf(AlertType.IntelHostile);
        s.MutedAlertTypes   = Rows.Where(r => r.BuiltIn != null && !r.Enabled)
                                  .Select(r => r.BuiltIn!.Value.ToString()).ToList();

        foreach (var row in Rows.Where(r => r.Rule != null))
        {
            row.Rule!.Enabled = row.Enabled;
            row.Rule.Sound    = row.Sound;
        }
        _settings.Save();
    }

    /// <summary>Fires an alert as if it had triggered, so a cue can be checked without waiting.</summary>
    [RelayCommand]
    private void Test(AlertConfigRow? row)
    {
        if (row == null) return;
        Hub.Raise(row.Rule != null
            ? new FcAlert
              {
                  Timestamp = DateTime.Now, AlertType = AlertType.Custom,
                  CustomTag = row.Rule.Name, CustomHeadline = string.IsNullOrWhiteSpace(row.Rule.Headline) ? $"Test: {row.Name}" : row.Rule.Headline,
                  CustomSound = row.Sound, SeverityOverride = row.Severity,
                  Detail = $"Test fire - {row.Trigger}",
              }
            : new FcAlert
              {
                  Timestamp = DateTime.Now, AlertType = row.BuiltIn!.Value,
                  Detail = "Test fire", SeverityOverride = row.Severity,
              });
    }

    [RelayCommand]
    private void Delete(AlertConfigRow? row)
    {
        if (row?.Rule == null) return;   // built-ins can be muted, not removed
        _settings.Current.CustomAlerts.RemoveAll(r => r.Id == row.Rule.Id);
        Rows.Remove(row);
        _settings.Save();
    }

    /// <summary>Adds a user .wav so any alert can use it.</summary>
    [RelayCommand]
    private void ImportSound()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Choose an alert sound",
            Filter = "WAV audio (*.wav)|*.wav",
        };
        if (dlg.ShowDialog() != true) return;

        var name = SoundService.ImportSound(dlg.FileName);
        Status = name == null
            ? "Couldn't use that file. Alert sounds must be .wav (mp3 and ogg won't play)."
            : $"Added \"{name}\". Click an alert's sound to cycle to it.";
        if (name != null) SoundService.Play(name);
    }

    [ObservableProperty] private string _status = string.Empty;

    // New-alert flow
    // Asked as a short series of questions rather than one big form - you pick what to watch, what
    // to watch for, then how loud it should be.
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private int  _step;          // 1 = source, 2 = pattern, 3 = loudness + name
    [ObservableProperty] private string _newSource   = "IntelChannel";
    [ObservableProperty] private string _newMatch    = "Contains";
    [ObservableProperty] private string _newPattern  = string.Empty;
    [ObservableProperty] private string _newName     = string.Empty;
    [ObservableProperty] private string _newSeverity = "Warning";
    [ObservableProperty] private string _newSound    = "Beep";
    [ObservableProperty] private string _newError    = string.Empty;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;

    public bool SourceIsIntel   => NewSource == "IntelChannel";
    public bool SourceIsGameLog => NewSource == "GameLog";
    public bool MatchIsContains => NewMatch == "Contains";
    public bool MatchIsRegex    => NewMatch == "Regex";
    public bool SeverityIsInfo     => NewSeverity == "Info";
    public bool SeverityIsWarning  => NewSeverity == "Warning";
    public bool SeverityIsCritical => NewSeverity == "Critical";

    /// <summary>Says plainly what each source can see, so a rule isn't written against data EVE
    /// never writes.</summary>
    public string SourceHint => SourceIsGameLog
        ? "Your own game log - only your character's lines, and only while you're in a fleet session."
        : "Messages in the intel channel, while the Intel page is open.";

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1)); OnPropertyChanged(nameof(IsStep2)); OnPropertyChanged(nameof(IsStep3));
    }

    partial void OnNewSourceChanged(string value)
    {
        OnPropertyChanged(nameof(SourceIsIntel)); OnPropertyChanged(nameof(SourceIsGameLog));
        OnPropertyChanged(nameof(SourceHint));
    }

    partial void OnNewMatchChanged(string value)
    {
        OnPropertyChanged(nameof(MatchIsContains)); OnPropertyChanged(nameof(MatchIsRegex));
    }

    partial void OnNewSeverityChanged(string value)
    {
        OnPropertyChanged(nameof(SeverityIsInfo)); OnPropertyChanged(nameof(SeverityIsWarning));
        OnPropertyChanged(nameof(SeverityIsCritical));
    }

    [RelayCommand] private void SetNewSource(string s)   => NewSource   = s;
    [RelayCommand] private void SetNewMatch(string s)    => NewMatch    = s;
    [RelayCommand] private void SetNewSeverity(string s) => NewSeverity = s;

    [RelayCommand]
    private void CycleNewSound()
    {
        var choices = SoundService.Presets.Concat(SoundService.CustomSounds()).ToArray();
        var i = Array.FindIndex(choices, c => c.Equals(NewSound, StringComparison.OrdinalIgnoreCase));
        NewSound = choices[(i + 1) % choices.Length];
        SoundService.Play(NewSound);
    }

    [RelayCommand]
    private void StartCreate()
    {
        NewSource = "IntelChannel"; NewMatch = "Contains"; NewPattern = string.Empty;
        NewName = string.Empty; NewSeverity = "Warning"; NewSound = "Beep"; NewError = string.Empty;
        Step = 1;
        IsCreating = true;
    }

    [RelayCommand] private void CancelCreate() { IsCreating = false; NewError = string.Empty; }

    [RelayCommand]
    private void NextStep()
    {
        NewError = string.Empty;
        if (Step == 2)
        {
            var match = Enum.Parse<AlertRuleMatch>(NewMatch);
            if (string.IsNullOrWhiteSpace(NewPattern)) { NewError = "Enter the text to watch for."; return; }
            if (!CustomAlertService.IsValidPattern(match, NewPattern))
            {
                NewError = "That regex isn't valid. Check the brackets and escapes, or switch to \"contains\".";
                return;
            }
            if (string.IsNullOrWhiteSpace(NewName)) NewName = NewPattern.Trim();   // sensible default
        }
        if (Step < 3) Step++;
    }

    [RelayCommand] private void PrevStep() { NewError = string.Empty; if (Step > 1) Step--; }

    [RelayCommand]
    private void FinishCreate()
    {
        if (string.IsNullOrWhiteSpace(NewName)) { NewError = "Give the alert a short name - it's the tag in the feed."; return; }

        var rule = new CustomAlertRule
        {
            Name     = NewName.Trim(),
            Pattern  = NewPattern.Trim(),
            Source   = Enum.Parse<AlertRuleSource>(NewSource),
            Match    = Enum.Parse<AlertRuleMatch>(NewMatch),
            Severity = Enum.Parse<AlertSeverity>(NewSeverity),
            Sound    = NewSound,
        };
        _settings.Current.CustomAlerts.Add(rule);
        _settings.Save();
        Rows.Add(AlertConfigRow.ForRule(rule));

        IsCreating = false;
        Status = $"Alert \"{rule.Name}\" added.";
    }
}
