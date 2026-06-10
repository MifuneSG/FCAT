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

    // ── Alert sounds ──
    public bool   AlertSoundsEnabled { get; set; } = true;
    public string TackledSound       { get; set; } = "Alarm";
    public string CapTroubleSound    { get; set; } = "Beep";
    public string BoostLostSound     { get; set; } = "Double Beep";

    // ── Alert overlay (on-screen, over the game) ──
    public bool   OverlayLocked { get; set; }
    public double OverlayLeft   { get; set; } = 60;
    public double OverlayTop    { get; set; } = 220;

    public static string DefaultLogsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs");

    [JsonIgnore] public string GamelogsPath => Path.Combine(EveLogsPath, "Gamelogs");
    [JsonIgnore] public string ChatlogsPath => Path.Combine(EveLogsPath, "Chatlogs");
}
