using System.IO;
using System.Media;

namespace FCAT.Services;

/// <summary>
/// Plays short alert tones. Presets are synthesized to WAV on first use (no bundled audio
/// assets) and cached in %APPDATA%\FCAT\sounds. Playback is throttled per key so a burst of
/// the same alert doesn't machine-gun the speaker.
/// </summary>
public static class SoundService
{
    public static readonly string[] Presets = ["None", "Beep", "Double Beep", "Alarm", "Low Buzz", "Siren"];

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT", "sounds");

    private static readonly Dictionary<string, SoundPlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime>    _lastPlayed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where user-imported sounds are copied, so an alert keeps working if the original
    /// file is moved or deleted.</summary>
    public static readonly string CustomDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT", "sounds", "custom");

    /// <summary>Plays a built-in preset name, or a path to the user's own .wav file.</summary>
    public static void Play(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset) || preset.Equals("None", StringComparison.OrdinalIgnoreCase))
            return;
        try { GetPlayer(preset)?.Play(); } catch { /* audio device unavailable or bad file - ignore */ }
    }

    /// <summary>User-imported sound files, newest first (bare filenames, as stored in settings).</summary>
    public static string[] CustomSounds()
    {
        try
        {
            if (!Directory.Exists(CustomDir)) return [];
            return new DirectoryInfo(CustomDir).GetFiles("*.wav")
                .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.Name).ToArray();
        }
        catch { return []; }
    }

    /// <summary>
    /// Copies a user's .wav into the custom-sound folder and returns the stored name (or null if it
    /// couldn't be read). SoundPlayer only handles PCM WAV - mp3/ogg won't play, so we reject them
    /// up front rather than failing silently at alert time.
    /// </summary>
    public static string? ImportSound(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            if (!Path.GetExtension(sourcePath).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return null;

            Directory.CreateDirectory(CustomDir);
            var name = Path.GetFileName(sourcePath);
            var dest = Path.Combine(CustomDir, name);
            for (int i = 1; File.Exists(dest) && !SamePath(sourcePath, dest); i++)
            {
                name = $"{Path.GetFileNameWithoutExtension(sourcePath)} ({i}).wav";
                dest = Path.Combine(CustomDir, name);
            }
            if (!SamePath(sourcePath, dest)) File.Copy(sourcePath, dest, overwrite: true);

            // Verify it actually loads before we let a rule depend on it.
            using (var probe = new SoundPlayer(dest)) probe.Load();
            return name;
        }
        catch { return null; }
    }

    public static void DeleteCustomSound(string name)
    {
        try
        {
            _players.Remove(name);
            var path = Path.Combine(CustomDir, name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* in use or already gone - nothing useful to do */ }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Plays only if the same key hasn't fired within <paramref name="minGap"/>.</summary>
    public static void PlayThrottled(string? preset, string key, TimeSpan minGap)
    {
        if (_lastPlayed.TryGetValue(key, out var t) && DateTime.UtcNow - t < minGap) return;
        _lastPlayed[key] = DateTime.UtcNow;
        Play(preset);
    }

    private static SoundPlayer? GetPlayer(string preset)
    {
        if (_players.TryGetValue(preset, out var existing)) return existing;

        string path;
        var segs = Segments(preset);
        if (segs != null)
        {
            Directory.CreateDirectory(Dir);
            path = Path.Combine(Dir, $"{preset.Replace(' ', '_').ToLowerInvariant()}.wav");
            if (!File.Exists(path)) WriteWav(path, segs);
        }
        else
        {
            // Not a built-in preset - treat it as a user sound (bare name in the custom folder,
            // or an absolute path the user pointed at).
            path = Path.IsPathRooted(preset) ? preset : Path.Combine(CustomDir, preset);
            if (!File.Exists(path)) return null;
        }

        var player = new SoundPlayer(path);
        player.Load();
        _players[preset] = player;
        return player;
    }

    // (frequency Hz, duration ms) - frequency 0 = silence.
    private static (int freq, int ms)[]? Segments(string preset) => preset switch
    {
        "Beep"        => [(880, 160)],
        "Double Beep" => [(880, 110), (0, 70), (880, 110)],
        "Alarm"       => [(988, 200), (740, 200), (988, 200), (740, 200)],
        "Low Buzz"    => [(170, 420)],
        "Siren"       => [(660, 130), (990, 130), (660, 130), (990, 130), (660, 130), (990, 130)],
        _             => null,
    };

    private static void WriteWav(string path, (int freq, int ms)[] segs)
    {
        const int sr = 44100;
        var samples = new List<short>();

        foreach (var (freq, ms) in segs)
        {
            int n = sr * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                // 10 ms fade in/out per segment to avoid clicks
                double env = Math.Clamp(Math.Min(i, n - i) / (sr * 0.01), 0, 1);
                double s   = freq == 0 ? 0 : Math.Sin(2 * Math.PI * freq * i / sr);
                samples.Add((short)(s * env * 0.35 * short.MaxValue));
            }
        }

        using var fs = new FileStream(path, FileMode.Create);
        using var w  = new BinaryWriter(fs);
        int dataLen = samples.Count * 2;

        w.Write("RIFF".ToCharArray()); w.Write(36 + dataLen); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);    // PCM, mono
        w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);                 // rate, byte rate, block align, bits
        w.Write("data".ToCharArray()); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
    }
}
