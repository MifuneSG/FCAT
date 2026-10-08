using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// One alert in the configuration list. Built-in alerts and the FC's own rules share this row so
/// they read as one list of "alerts FCAT can raise" - splitting them across separate sections was
/// what made the old settings page hard to scan.
/// </summary>
public partial class AlertConfigRow : ObservableObject
{
    /// <summary>Set for a built-in alert; null for a user rule.</summary>
    public AlertType? BuiltIn { get; }

    /// <summary>Set for a user rule; null for a built-in.</summary>
    public CustomAlertRule? Rule { get; }

    public string Name    { get; }
    public string Trigger { get; }

    /// <summary>What this alert watches, which is the section it appears under.</summary>
    public AlertGroup Group { get; }

    /// <summary>What this alert's severity is before anyone changes it. Kept so the page can store
    /// only the severities the FC actually moved - see <c>AppSettings.AlertSeverities</c>.</summary>
    public AlertSeverity DefaultSeverity { get; }

    public bool IsCustom => Rule != null;

    private AlertConfigRow(AlertType? builtIn, CustomAlertRule? rule, string name, string trigger,
                           AlertGroup group, AlertSeverity severity, AlertSeverity defaultSeverity,
                           string sound, bool enabled)
    {
        BuiltIn         = builtIn;
        Rule            = rule;
        Name            = name;
        Trigger         = trigger;
        Group           = group;
        DefaultSeverity = defaultSeverity;
        _severity       = severity;
        _sound          = sound;
        _enabled        = enabled;
    }

    public static AlertConfigRow ForBuiltIn(AlertType type, string name, string trigger,
                                            AlertSeverity severity, string sound, bool enabled)
        => new(type, null, name, trigger, FcAlert.GroupFor(type), severity,
               FcAlert.DefaultSeverityFor(type), sound, enabled);

    public static AlertConfigRow ForRule(CustomAlertRule rule)
        => new(null, rule, string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name,
               Describe(rule), AlertGroup.Own, rule.Severity, rule.Severity,
               string.IsNullOrWhiteSpace(rule.Sound) ? "Beep" : rule.Sound, rule.Enabled);

    private static string Describe(CustomAlertRule r)
    {
        var where = r.Source == AlertRuleSource.IntelChannel ? "Intel channel" : "Game log";
        var how   = r.Match  == AlertRuleMatch.Regex ? "matches" : "contains";
        return $"{where} {how} \"{r.Pattern}\"";
    }

    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (Rule != null) Rule.Enabled = value;
    }

    // ---- severity -------------------------------------------------------------------------------

    /// <summary>
    /// How loud this alert is. Editable for built-ins as well as the FC's own rules: severity is
    /// what decides the card colour and how long the alert sits on the overlay, so an FC who wants
    /// cap-out to stop shouting at them has to be able to move it.
    /// </summary>
    [ObservableProperty] private AlertSeverity _severity;

    /// <summary>True once the FC has moved this away from the shipped severity.</summary>
    public bool SeverityChanged => Severity != DefaultSeverity;

    public bool IsInfo     => Severity == AlertSeverity.Info;
    public bool IsWarning  => Severity == AlertSeverity.Warning;
    public bool IsCritical => Severity == AlertSeverity.Critical;

    partial void OnSeverityChanged(AlertSeverity value)
    {
        if (Rule != null) Rule.Severity = value;
        OnPropertyChanged(nameof(SeverityChanged));
        OnPropertyChanged(nameof(IsInfo));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsCritical));
    }

    /// <summary>Three buttons rather than a dropdown. There are only three values, so a list that
    /// has to be opened to see what else is in it costs a click and shows less.</summary>
    [RelayCommand]
    private void SetSeverity(string name)
    {
        if (Enum.TryParse<AlertSeverity>(name, out var s)) Severity = s;
    }

    // ---- sound ----------------------------------------------------------------------------------

    [ObservableProperty] private string _sound;

    partial void OnSoundChanged(string value) => OnPropertyChanged(nameof(SoundLabel));

    /// <summary>The cue as the FC reads it. The stored value keeps an imported file's extension,
    /// because that is what plays it; the label does not need to carry ".wav" around.</summary>
    public string SoundLabel => Sound.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
        ? Sound[..^4]
        : Sound;

    /// <summary>
    /// The cues this row can choose from - the page's one shared list, handed to the row rather
    /// than looked up from it. A WPF Popup renders outside the page's visual tree, so markup inside
    /// it cannot walk up to the page to find anything; what it needs has to be on the row.
    /// </summary>
    public IEnumerable<SoundChoice> SoundChoices { get; set; } = [];

    /// <summary>
    /// The cue list is a popup rather than a combo box so each cue can carry its own play button.
    /// A combo box can only audition what you have already selected, which means picking a cue by
    /// ear meant selecting every one of them in turn - and leaving whichever you stopped on.
    /// </summary>
    [ObservableProperty] private bool _isSoundPickerOpen;

    /// <summary>Hear a cue without choosing it.</summary>
    [RelayCommand]
    private static void PreviewChoice(SoundChoice? choice) => SoundService.Play(choice?.Value);

    [RelayCommand] private void OpenSoundPicker() => IsSoundPickerOpen = !IsSoundPickerOpen;

    [RelayCommand]
    private void PickSound(SoundChoice? choice)
    {
        if (choice == null) return;
        Sound = choice.Value;
        IsSoundPickerOpen = false;
    }
}

/// <summary>
/// A section of the alert list: everything that watches one thing, with one switch for the lot.
///
/// The list used to be flat, in the order the code happened to add rows. An FC deciding what they
/// want to be told thinks in terms of "my fleet" or "my own ship", and wants to be able to say no
/// to a whole category without visiting six rows.
/// </summary>
public partial class AlertGroupRow : ObservableObject
{
    public AlertGroup Group { get; }
    public string     Title { get; }
    public string     Hint  { get; }

    /// <summary>Shown in place of the rows when there is a reason the group cannot do anything yet,
    /// so a section is never silently empty.</summary>
    public string Notice { get; }

    public bool HasNotice => Notice.Length > 0;

    public ObservableCollection<AlertConfigRow> Rows { get; } = [];

    public AlertGroupRow(AlertGroup group, string title, string hint, string notice = "")
    {
        Group  = group;
        Title  = title;
        Hint   = hint;
        Notice = notice;
    }

    public bool AnyOn => Rows.Any(r => r.Enabled);

    /// <summary>Turns the whole section off, or back on if it is already all off.</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        var turnOn = !AnyOn;
        foreach (var row in Rows) row.Enabled = turnOn;
    }

    public void Refresh() => OnPropertyChanged(nameof(AnyOn));
}
