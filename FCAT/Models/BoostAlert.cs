namespace FCAT.Models;

/// <summary>
/// Alert types that can actually be sourced from the EVE local gamelog.
/// IMPORTANT - EVE log reality:
/// The gamelog only writes lines for a small subset of EWar. The ONLY electronic-warfare
/// effect reliably present is warp scramble/disruption ("Warp scramble attempt from X").
/// Stasis webifiers, energy neutralizers, ECM, tracking disruptors, sensor dampeners and
/// target painters produce NO gamelog line, so they cannot be detected client-side and are
/// intentionally absent here. Do not add them back as log parsers - they will never fire.
/// </summary>
public enum AlertType
{
    Tackled,        // Warp scramble / disruption - you are held and cannot warp
    CapTrouble,     // A module shut off due to insufficient capacitor (real combat-log line)
    BoostLost,      // A booster podded out - their gang links dropped
    LogiChain,      // A cap-chain logi (Guardian/Basilisk) dropped - the ring needs re-forming
    DpsLoss,        // The fleet has lost a chunk of its DPS line to deaths (30/50/75% of baseline)
    LogiRatio,      // Logi are alive but too thin for the fleet size - reps won't hold
    IntelHostile,   // Your system (or a neighbour) was called out in the in-game intel channel
    Custom,         // Fired by a user-defined rule (see CustomAlertRule)
    Info
}

/// <summary>
/// How loud an alert should be. Drives the card colour, which sound preset plays, and how long it
/// sits on the overlay - so routine chatter clears fast while a tackle stays put.
/// </summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Critical
}

public class FcAlert
{
    public DateTime  Timestamp    { get; set; }
    public AlertType AlertType    { get; set; }
    public string    AttackerName { get; set; } = string.Empty;
    public string    Detail       { get; set; } = string.Empty;
    public string    RawLogLine   { get; set; } = string.Empty;

    /// <summary>Set to escalate/soften a single alert past its type default - e.g. DPS loss only
    /// goes red at the worst step, and a custom rule carries whatever the user picked.</summary>
    public AlertSeverity? SeverityOverride { get; set; }

    /// <summary>Headline for Custom/Info alerts, which have no fixed wording.</summary>
    public string CustomHeadline { get; set; } = string.Empty;

    /// <summary>Tag for Custom alerts, so a user rule can name itself in the feed.</summary>
    public string CustomTag { get; set; } = string.Empty;

    /// <summary>Sound preset for Custom alerts; empty falls back to the severity default.</summary>
    public string CustomSound { get; set; } = string.Empty;

    public AlertSeverity Severity => SeverityOverride ?? AlertType switch
    {
        AlertType.Tackled      => AlertSeverity.Critical,
        AlertType.CapTrouble   => AlertSeverity.Warning,
        AlertType.BoostLost    => AlertSeverity.Warning,
        AlertType.LogiChain    => AlertSeverity.Warning,
        AlertType.DpsLoss      => AlertSeverity.Warning,
        AlertType.LogiRatio    => AlertSeverity.Warning,
        AlertType.IntelHostile => AlertSeverity.Warning,
        _                      => AlertSeverity.Info,
    };

    /// <summary>Kept so the existing alert-card styling keeps working off one flag.</summary>
    public bool IsCritical => Severity == AlertSeverity.Critical;

    public string AlertTag => AlertType switch
    {
        AlertType.Tackled      => "TACKLED",
        AlertType.CapTrouble   => "CAP OUT",
        AlertType.BoostLost    => "BOOST LOST",
        AlertType.LogiChain    => "LOGI CHAIN",
        AlertType.DpsLoss      => "DPS LOSS",
        AlertType.LogiRatio    => "LOGI THIN",
        AlertType.IntelHostile => "INTEL",
        AlertType.Custom       => string.IsNullOrWhiteSpace(CustomTag) ? "CUSTOM" : CustomTag,
        _                      => "INFO"
    };

    /// <summary>Human headline shown in the alert card. Written as an FC would call it on comms -
    /// what happened, in the fewest words that still say what to do about it.</summary>
    public string Headline => AlertType switch
    {
        AlertType.Tackled      => "You are tackled",
        AlertType.CapTrouble   => "Module shut down - out of cap",
        AlertType.BoostLost    => "Booster is down - links lost",
        AlertType.LogiChain    => "Cap chain broken - re-form the ring",
        AlertType.DpsLoss      => "Fleet DPS dropping",
        AlertType.LogiRatio    => "Logi too thin for fleet size",
        AlertType.IntelHostile => "Hostiles called in intel",
        AlertType.Custom       => string.IsNullOrWhiteSpace(CustomHeadline) ? Detail : CustomHeadline,
        _                      => string.IsNullOrEmpty(CustomHeadline) ? Detail : CustomHeadline
    };

    /// <summary>Secondary line - attacker name for tackle, module name for cap, booster + links lost, etc.</summary>
    public string SubText => AlertType switch
    {
        AlertType.Tackled => string.IsNullOrEmpty(AttackerName) ? "Unknown source" : AttackerName,
        AlertType.Custom  => Detail,
        AlertType.Info    => string.IsNullOrWhiteSpace(CustomHeadline) ? string.Empty : Detail,
        _                 => Detail
    };
}
