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

    /// <summary>Colour theme - "Nebula" (violet, default) or "Carbon" (amber/gold).</summary>
    public string Theme { get; set; } = "Nebula";

    // Alert sounds
    public bool   AlertSoundsEnabled { get; set; } = true;
    public string TackledSound       { get; set; } = "Alarm";
    public string CapTroubleSound    { get; set; } = "Beep";
    public string BoostLostSound     { get; set; } = "Double Beep";

    /// <summary>Seconds before an alert auto-clears from the on-screen overlay. The in-app Alerts
    /// list always keeps the full session history regardless of this. 0 = never clear the overlay.</summary>
    public int AlertClearSeconds { get; set; } = 60;

    // Alert overlay (on-screen, over the game)
    public bool   OverlayEnabled { get; set; }
    public bool   OverlayLocked { get; set; }
    public double OverlayLeft   { get; set; } = 60;
    public double OverlayTop    { get; set; } = 220;

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
