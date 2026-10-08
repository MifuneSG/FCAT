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
    AltOffline,     // A tracked alt (cyno/scout) dropped offline - it isn't where you left it
    AltPodded,      // A tracked alt is in a pod in space, so whatever it was doing has stopped
    AltKills,       // Kills appeared in the system a tracked alt is sitting in
    CloakDropped,   // Your cloak deactivated, or refused to activate, because something got close
    NoFleetWarp,    // Pilots have fleet warp switched off, so your next warp leaves them behind
    NoBooster,      // Nobody in the fleet has posted links - the whole fleet is flying unboosted
    Custom,         // Fired by a user-defined rule (see CustomAlertRule)
    Info
}

/// <summary>
/// What an alert watches, which is how the Alerts page groups them. An FC deciding what they want
/// to be told thinks in terms of "my fleet" or "my own ship", not in terms of which EVE API the
/// answer came from - and the group is the unit they switch on and off.
/// </summary>
public enum AlertGroup
{
    /// <summary>The fleet roster and the boost channel.</summary>
    Fleet,
    /// <summary>The in-game intel channel.</summary>
    Intel,
    /// <summary>Your own client's game log.</summary>
    Ship,
    /// <summary>Characters labelled Cyno or Scout on the Account page.</summary>
    Alts,
    /// <summary>The FC's own rules.</summary>
    Own,
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

    /// <summary>
    /// Which character's game log this came from. EVE writes one log per running client, so with
    /// alts logged in the FC can be alerted about something that happened to a character other than
    /// the one they're looking at - and an alert that doesn't say whose it is would be unreadable.
    /// </summary>
    public string SourceCharacter { get; set; } = string.Empty;

    /// <summary>Set when the source is NOT the active character, so the feed can name the alt.</summary>
    public bool FromOtherClient { get; set; }

    /// <summary>
    /// Fired from the Alerts page to audition a cue, not by anything that happened in space.
    /// It behaves like a real alert everywhere the FC can see it, but stays out of the session log
    /// so auditioning a sound does not leave a phantom entry in the after-action report.
    /// </summary>
    public bool IsTest { get; set; }

    public AlertSeverity Severity => SeverityOverride ?? DefaultSeverityFor(AlertType);

    /// <summary>
    /// How loud a type is before the FC has said otherwise. Kept as one static so the Alerts page
    /// can show the same answer this property uses, rather than carrying its own copy of the table.
    /// </summary>
    public static AlertSeverity DefaultSeverityFor(AlertType type) => type switch
    {
        AlertType.Tackled      => AlertSeverity.Critical,
        AlertType.CapTrouble   => AlertSeverity.Warning,
        AlertType.BoostLost    => AlertSeverity.Warning,
        AlertType.LogiChain    => AlertSeverity.Warning,
        AlertType.DpsLoss      => AlertSeverity.Warning,
        AlertType.LogiRatio    => AlertSeverity.Warning,
        AlertType.IntelHostile => AlertSeverity.Warning,
        AlertType.AltOffline   => AlertSeverity.Warning,
        AlertType.AltPodded    => AlertSeverity.Warning,
        // Kills near an alt are worth knowing, not worth shouting - the alt is usually a scout,
        // and being near a fight is the job.
        AlertType.AltKills     => AlertSeverity.Info,
        // A dropped cloak on a covert alt means it is visible and probably about to die.
        AlertType.CloakDropped => AlertSeverity.Critical,
        AlertType.NoFleetWarp  => AlertSeverity.Warning,
        AlertType.NoBooster    => AlertSeverity.Warning,
        _                      => AlertSeverity.Info,
    };

    /// <summary>Kept so the existing alert-card styling keeps working off one flag.</summary>
    public bool IsCritical => Severity == AlertSeverity.Critical;

    /// <summary>What this alert watches. Drives how the Alerts page groups the list.</summary>
    public static AlertGroup GroupFor(AlertType type) => type switch
    {
        AlertType.DpsLoss or AlertType.LogiRatio or AlertType.LogiChain
            or AlertType.BoostLost or AlertType.NoFleetWarp or AlertType.NoBooster => AlertGroup.Fleet,
        AlertType.IntelHostile                                                     => AlertGroup.Intel,
        AlertType.Tackled or AlertType.CapTrouble                                  => AlertGroup.Ship,
        AlertType.AltOffline or AlertType.AltPodded or AlertType.AltKills
            or AlertType.CloakDropped                                              => AlertGroup.Alts,
        _                                                                          => AlertGroup.Own,
    };

    public string AlertTag => AlertType switch
    {
        AlertType.Tackled      => "TACKLED",
        AlertType.CapTrouble   => "CAP OUT",
        AlertType.BoostLost    => "BOOST LOST",
        AlertType.LogiChain    => "LOGI CHAIN",
        AlertType.DpsLoss      => "DPS LOSS",
        AlertType.LogiRatio    => "LOGI THIN",
        AlertType.IntelHostile => "INTEL",
        AlertType.AltOffline   => "ALT OFFLINE",
        AlertType.AltPodded    => "ALT PODDED",
        AlertType.AltKills     => "ALT SYSTEM",
        AlertType.CloakDropped => "CLOAK",
        AlertType.NoFleetWarp  => "FLEET WARP",
        AlertType.NoBooster    => "NO BOOST",
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
        AlertType.AltOffline   => "An alt dropped offline",
        AlertType.AltPodded    => "An alt is in a pod",
        AlertType.AltKills     => "Kills where your alt is",
        // The headline says WHICH way the cloak failed; the detail says what caused it.
        AlertType.CloakDropped => string.IsNullOrWhiteSpace(CustomHeadline) ? "Cloak dropped" : CustomHeadline,
        AlertType.NoFleetWarp  => "Pilots will not follow fleet warp",
        AlertType.NoBooster    => "Nobody is boosting",
        AlertType.Custom       => string.IsNullOrWhiteSpace(CustomHeadline) ? Detail : CustomHeadline,
        _                      => string.IsNullOrEmpty(CustomHeadline) ? Detail : CustomHeadline
    };

    /// <summary>Secondary line - attacker name for tackle, module name for cap, booster + links lost, etc.
    /// An alert from another client is suffixed with the character it happened to, since otherwise
    /// a tackle on a scout alt reads as a tackle on the FC.</summary>
    public string SubText
    {
        get
        {
            var text = AlertType switch
            {
                AlertType.Tackled => string.IsNullOrEmpty(AttackerName) ? "Unknown source" : AttackerName,
                AlertType.Custom  => Detail,
                AlertType.Info    => string.IsNullOrWhiteSpace(CustomHeadline) ? string.Empty : Detail,
                _                 => Detail
            };

            if (!FromOtherClient || SourceCharacter.Length == 0) return text;
            return text.Length == 0 ? SourceCharacter : $"{text} · {SourceCharacter}";
        }
    }
}
