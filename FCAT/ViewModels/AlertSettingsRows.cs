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

    public string SeverityLabel => Severity switch
    {
        AlertSeverity.Critical => "CRITICAL",
        AlertSeverity.Warning  => "WARNING",
        _                      => "INFO",
    };

    /// <summary>Picking a cue plays it, so you hear what you chose without a separate test.
    /// Only fires on a real change - the constructor seeds the backing field directly.</summary>
    private bool _quietSound;

    partial void OnSoundChanged(string value)
    {
        if (!_quietSound) SoundService.Play(value);
    }

    /// <summary>Changes the cue without previewing it. Used when a sound is deleted out from under
    /// the row - hearing the replacement fire is noise, not feedback.</summary>
    public void SetSoundQuietly(string value)
    {
        if (Sound.Equals(value, StringComparison.OrdinalIgnoreCase)) return;
        _quietSound = true;
        Sound = value;
        _quietSound = false;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (Rule != null) Rule.Enabled = value;
    }

    [RelayCommand] private void ToggleEnabled() => Enabled = !Enabled;
}
