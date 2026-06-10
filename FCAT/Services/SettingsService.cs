using System.IO;
using System.Text.Json;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>Loads and persists <see cref="AppSettings"/> to %APPDATA%\FCAT\settings.json.</summary>
public class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Current { get; private set; } = new();

    public SettingsService() => Load();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            Current = new();   // corrupt/unreadable — fall back to defaults
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch
        {
            // Non-fatal — settings just won't persist this run.
        }
    }
}
