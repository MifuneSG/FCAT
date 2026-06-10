namespace FCAT.Models;

/// <summary>
/// Alert types that can actually be sourced from the EVE local gamelog.
///
/// IMPORTANT — EVE log reality:
/// The gamelog only writes lines for a small subset of EWar. The ONLY electronic-warfare
/// effect reliably present is warp scramble/disruption ("Warp scramble attempt from X").
/// Stasis webifiers, energy neutralizers, ECM, tracking disruptors, sensor dampeners and
/// target painters produce NO gamelog line, so they cannot be detected client-side and are
/// intentionally absent here. Do not add them back as log parsers — they will never fire.
/// </summary>
public enum AlertType
{
    Tackled,        // Warp scramble / disruption — you are held and cannot warp
    CapTrouble,     // A module shut off due to insufficient capacitor (real combat-log line)
    BoostLost,      // A booster podded out — their gang links dropped
    Info
}

public class FcAlert
{
    public DateTime  Timestamp    { get; set; }
    public AlertType AlertType    { get; set; }
    public string    AttackerName { get; set; } = string.Empty;
    public string    Detail       { get; set; } = string.Empty;
    public string    RawLogLine   { get; set; } = string.Empty;

    public bool IsCritical => AlertType is AlertType.Tackled;

    public string AlertTag => AlertType switch
    {
        AlertType.Tackled    => "TACKLED",
        AlertType.CapTrouble => "CAP OUT",
        AlertType.BoostLost  => "BOOST LOST",
        _                    => "INFO"
    };

    /// <summary>Human headline shown in the alert card.</summary>
    public string Headline => AlertType switch
    {
        AlertType.Tackled    => "Point / scram on you",
        AlertType.CapTrouble => "Module offline — cap",
        AlertType.BoostLost  => "Booster down",
        _                    => Detail
    };

    /// <summary>Secondary line — attacker name for tackle, module name for cap, booster + links lost, etc.</summary>
    public string SubText => AlertType switch
    {
        AlertType.Tackled    => string.IsNullOrEmpty(AttackerName) ? "Unknown source" : AttackerName,
        AlertType.CapTrouble => Detail,
        AlertType.BoostLost  => Detail,
        _                    => string.Empty
    };
}
