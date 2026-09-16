using System.IO;
using System.Text.Json.Serialization;

namespace FCAT.Models;

/// <summary>User-configurable settings, persisted to %APPDATA%\FCAT\settings.json.</summary>
public class AppSettings
{
    /// <summary>Root EVE logs folder (the one containing Gamelogs and Chatlogs).</summary>
    public string EveLogsPath { get; set; } = DefaultLogsPath;

    /// <summary>Filename prefix of the boost chat channel logs (e.g. "Boost").</summary>
    public string BoostChannelPrefix { get; set; } = "Boost";

    /// <summary>Filename prefix of the in-game intel chat channel logs (e.g. "Intel").</summary>
    public string IntelChannelPrefix { get; set; } = "Intel";

    /// <summary>The system the fleet forms up in - used by the straggler check. Name + id.</summary>
    public string FormupSystem   { get; set; } = string.Empty;
    public int    FormupSystemId { get; set; }

    /// <summary>Ping/MOTD profiles (one per alliance + a personal one) and the active selection.</summary>
    public List<PingProfile> PingProfiles      { get; set; } = [];
    public string            ActivePingProfile { get; set; } = string.Empty;

    /// <summary>Remembered inputs for the Custom ping profile, so they survive restarts.
    /// Cleared by the "Clear saved ping" button in Settings.</summary>
    public CustomPingState CustomPing { get; set; } = new();

    /// <summary>Colour theme: Nebula (violet, default), Carbon, Photon or Rust. See ThemeService.</summary>
    public string Theme { get; set; } = "Nebula";

    // Alliance Auth connector. Optional, and off until an FC pastes their own auth's address -
    // most of FCAT's users have no auth at all and never touch this. The API KEY is deliberately
    // NOT here: it is a credential, so it lives DPAPI-encrypted in aa.dat beside the ESI tokens,
    // along with the data it fetched. See AaConnectorService.
    /// <summary>Root URL of the FC's Alliance Auth, e.g. https://auth.example.com. Empty = not set up.</summary>
    public string AaBaseUrl { get; set; } = string.Empty;

    /// <summary>Lets an FC switch the connector off without throwing their key away.</summary>
    public bool AaEnabled { get; set; } = true;

    // Hunt: remembered so the FC's own hull and skills aren't re-picked every session.
    /// <summary>Jump-capable hull class the hunt board measures with. See JumpDrives.Classes.</summary>
    public string HuntHullClass { get; set; } = "Black Ops";

    /// <summary>Jump Drive Calibration level, 0-5. Most FCs flying these have it maxed.</summary>
    public int HuntCalibration { get; set; } = 5;

    /// <summary>Hunt board ordering: "Activity" (where the ratting is) or "Coverage" (where to stage).</summary>
    public string HuntRankMode { get; set; } = "Activity";

    // Alert sounds. Each value is either a built-in preset name or a user-imported .wav filename.
    public bool   AlertSoundsEnabled { get; set; } = true;
    public string TackledSound       { get; set; } = "Alarm";
    public string CapTroubleSound    { get; set; } = "Beep";
    public string BoostLostSound     { get; set; } = "Double Beep";
    public string LogiChainSound     { get; set; } = "Double Beep";
    public string DpsLossSound       { get; set; } = "Low Buzz";
    public string LogiRatioSound     { get; set; } = "Low Buzz";
    public string IntelHostileSound  { get; set; } = "Siren";
    public string AltOfflineSound    { get; set; } = "Double Beep";
    public string AltPoddedSound     { get; set; } = "Low Buzz";
    public string AltKillsSound      { get; set; } = "Beep";
    public string CloakDroppedSound  { get; set; } = "Alarm";

    /// <summary>
    /// An extra regex the gamelog is checked against for a cloak failure, on top of the two lines
    /// FCAT already knows. EVE has changed this wording before and localised clients differ, so an
    /// FC who sees a line FCAT misses can add it without waiting for a release. Empty = off.
    /// </summary>
    public string CloakDroppedLogPattern { get; set; } = string.Empty;

    /// <summary>Alert types the FC has muted, by <c>AlertType</c> name. Muting stops the sound only -
    /// the alert still appears in the feed and the AAR log.</summary>
    public List<string> MutedAlertTypes { get; set; } = [];

    /// <summary>User-defined alert rules (intel-channel / gamelog text triggers).</summary>
    public List<CustomAlertRule> CustomAlerts { get; set; } = [];

    /// <summary>Seconds before an alert auto-clears from the on-screen overlay, per severity, so a
    /// tackle lingers while routine chatter clears fast. The in-app Alerts list always keeps the
    /// full session history regardless. 0 = never clear from the overlay.</summary>
    public int AlertClearSeconds { get; set; } = 60;   // Warning, and the fallback
    public int AlertClearSecondsCritical { get; set; } = 120;
    public int AlertClearSecondsInfo     { get; set; } = 30;

    /// <summary>Minimum gap between repeats of the same alert type's sound.</summary>
    public int AlertSoundThrottleSeconds { get; set; } = 2;

    // Logi-ratio alert: warn when logi drop below this share of the fleet (0 = off).
    public bool   LogiRatioAlertEnabled { get; set; } = true;
    public double LogiRatioThreshold    { get; set; } = 0.10;   // 10% of fleet, a common floor

    /// <summary>Raise an alert when the intel channel names your system or one next door.</summary>
    public bool IntelHostileAlertEnabled  { get; set; } = true;
    public bool IntelHostileAdjacentToo   { get; set; } = true;

    // Alert overlay (on-screen, over the game)
    public bool   OverlayEnabled { get; set; }
    public bool   OverlayLocked { get; set; }
    public double OverlayLeft   { get; set; } = 60;
    public double OverlayTop    { get; set; } = 220;

    // Map overlay - the constellation map as its own always-on-top window over the game.
    // Sized as well as positioned, since unlike the alert list you want to scale a map.
    public bool   MapOverlayEnabled { get; set; }
    public bool   MapOverlayLocked  { get; set; }
    public double MapOverlayLeft    { get; set; } = 60;
    public double MapOverlayTop     { get; set; } = 60;
    public double MapOverlayWidth   { get; set; } = 520;
    public double MapOverlayHeight  { get; set; } = 400;

    public static string DefaultLogsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs");

    [JsonIgnore] public string GamelogsPath => Path.Combine(EveLogsPath, "Gamelogs");
    [JsonIgnore] public string ChatlogsPath => Path.Combine(EveLogsPath, "Chatlogs");
}

/// <summary>Remembered field values for the Custom ping profile.</summary>
public class CustomPingState
{
    public string Hurf       { get; set; } = string.Empty;
    public string Comms      { get; set; } = string.Empty;
    public string Doctrine   { get; set; } = string.Empty;
    public string Fittings   { get; set; } = string.Empty;
    public string MainAnchor { get; set; } = string.Empty;
    public string LogiAnchor { get; set; } = string.Empty;
    public string Notes      { get; set; } = string.Empty;
}
