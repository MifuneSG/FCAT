namespace FCAT.Models;

/// <summary>Where a custom rule watches for its trigger.</summary>
public enum AlertRuleSource
{
    /// <summary>Messages in the in-game intel channel (chatlogs).</summary>
    IntelChannel,
    /// <summary>Lines in your own combat/game log. Only YOUR character's log is written by EVE.</summary>
    GameLog
}

/// <summary>How the rule's text is compared against the incoming line.</summary>
public enum AlertRuleMatch
{
    Contains,
    Regex
}

/// <summary>
/// A user-defined alert: watch a source for a phrase (or regex) and raise an alert when it appears.
/// Kept deliberately simple - the two sources below are the only text feeds EVE actually gives a
/// third-party tool, so a rule can't reference things like pilots-in-space that no API exposes.
/// </summary>
public class CustomAlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Shown as the alert's tag in the feed, e.g. "SUPERS".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Headline on the alert card; falls back to the matched line when empty.</summary>
    public string Headline { get; set; } = string.Empty;

    public AlertRuleSource Source { get; set; } = AlertRuleSource.IntelChannel;
    public AlertRuleMatch  Match  { get; set; } = AlertRuleMatch.Contains;

    /// <summary>The phrase or regex to look for.</summary>
    public string Pattern { get; set; } = string.Empty;

    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;

    /// <summary>Sound preset name, or a path to the user's own audio file. Empty = severity default.</summary>
    public string Sound { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Don't re-fire this rule more often than this (a busy intel channel repeats a lot).</summary>
    public int ThrottleSeconds { get; set; } = 15;
}
