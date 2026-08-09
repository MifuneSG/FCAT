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
    public AlertSeverity Severity { get; }

    public bool IsCustom => Rule != null;

    private AlertConfigRow(AlertType? builtIn, CustomAlertRule? rule, string name, string trigger,
                           AlertSeverity severity, string sound, bool enabled)
    {
        BuiltIn  = builtIn;
        Rule     = rule;
        Name     = name;
        Trigger  = trigger;
        Severity = severity;
        _sound   = sound;
        _enabled = enabled;
    }

    public static AlertConfigRow ForBuiltIn(AlertType type, string name, string trigger,
                                            AlertSeverity severity, string sound, bool enabled)
        => new(type, null, name, trigger, severity, sound, enabled);

    public static AlertConfigRow ForRule(CustomAlertRule rule)
        => new(null, rule, string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name,
               Describe(rule), rule.Severity,
               string.IsNullOrWhiteSpace(rule.Sound) ? "Beep" : rule.Sound, rule.Enabled);

    private static string Describe(CustomAlertRule r)
    {
        var where = r.Source == AlertRuleSource.IntelChannel ? "Intel channel" : "Game log";
        var how   = r.Match  == AlertRuleMatch.Regex ? "matches" : "contains";
        return $"{where} {how} \"{r.Pattern}\"";
    }

    [ObservableProperty] private string _sound;
    [ObservableProperty] private bool   _enabled;

    /// <summary>A user sound keeps its filename, minus the extension.</summary>
    public string SoundLabel => Sound.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? Sound[..^4] : Sound;

    public string SeverityLabel => Severity switch
    {
        AlertSeverity.Critical => "CRITICAL",
        AlertSeverity.Warning  => "WARNING",
        _                      => "INFO",
    };

    partial void OnSoundChanged(string value) => OnPropertyChanged(nameof(SoundLabel));

    partial void OnEnabledChanged(bool value)
    {
        if (Rule != null) Rule.Enabled = value;
    }

    /// <summary>Steps to the next cue - built-in presets first, then any imported files.</summary>
    [RelayCommand]
    private void CycleSound()
    {
        var choices = SoundService.Presets.Concat(SoundService.CustomSounds()).ToArray();
        var i = Array.FindIndex(choices, c => c.Equals(Sound, StringComparison.OrdinalIgnoreCase));
        Sound = choices[(i + 1) % choices.Length];
        SoundService.Play(Sound);
    }

    [RelayCommand] private void ToggleEnabled() => Enabled = !Enabled;
}
