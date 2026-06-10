using System.IO;
using System.Text.RegularExpressions;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// Watches the FC's EVE gamelog and raises alerts for events that EVE actually writes to disk.
///
/// EVE log reality (verified against community log-parser tooling + in-game behaviour):
/// the gamelog does NOT record most electronic warfare. The only EWar effect that produces a
/// reliable line is warp scramble / disruption:  "Warp scramble attempt from &lt;name&gt; to you!".
/// Webs, neuts, ECM, tracking disruptors, sensor dampeners and painters write nothing, so they
/// are not — and cannot be — detected here. We also catch the genuine cap-out line where a
/// module deactivates from insufficient capacitor, since that IS written to the combat log.
/// </summary>
public partial class CombatLogService : IDisposable
{
    // [ 2024.01.15 20:30:45 ] (combat) content
    [GeneratedRegex(@"^\[ (\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}) \] \((\w+)\) (.+)$")]
    private static partial Regex LogLineRegex();

    // "Warp scramble attempt from <name> to <you/your ship>!"
    // EVE uses identical text for warp disruptors (point) and scramblers (scram).
    [GeneratedRegex(@"warp scramble attempt from (.+?) to ", RegexOptions.IgnoreCase)]
    private static partial Regex TackleRegex();

    // "<Module> deactivates as the capacitor runs out of charge" / "...insufficient capacitor"
    [GeneratedRegex(@"(.+?) deactivat\w*(?: as)?.{0,30}capacitor", RegexOptions.IgnoreCase)]
    private static partial Regex CapOutRegex();

    private FileSystemWatcher? _watcher;
    private string?            _watchedFile;
    private long               _lastFilePosition;

    public event Action<FcAlert>? AlertRaised;

    public string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     "EVE", "logs", "Gamelogs");

    // ── Public API ────────────────────────────────────────────────────────────
    public void StartWatching(string? logDirectory = null)
    {
        var dir = logDirectory ?? LogDirectory;
        if (!Directory.Exists(dir)) return;

        StopWatching();

        _watcher = new FileSystemWatcher(dir, "*.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileCreated;

        AttachToLatestLogFile(dir);
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _watchedFile = null;
        _lastFilePosition = 0;
    }

    // ── File tracking ─────────────────────────────────────────────────────────
    private void AttachToLatestLogFile(string dir)
    {
        var latest = Directory.GetFiles(dir, "*.txt")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        if (latest == null) return;

        _watchedFile      = latest;
        _lastFilePosition = new FileInfo(latest).Length;   // only watch new lines
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _watchedFile      = e.FullPath;
        _lastFilePosition = 0;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath != _watchedFile) return;
        ReadNewLines();
    }

    private void ReadNewLines()
    {
        if (_watchedFile == null) return;
        try
        {
            using var stream = new FileStream(_watchedFile, FileMode.Open,
                                              FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(_lastFilePosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
                ParseLine(line);
            _lastFilePosition = stream.Position;
        }
        catch (IOException) { /* file locked — retry next change */ }
    }

    // ── Parsing ───────────────────────────────────────────────────────────────
    private void ParseLine(string line)
    {
        var m = LogLineRegex().Match(line);
        if (!m.Success) return;

        var timestampStr = m.Groups[1].Value;
        var category      = m.Groups[2].Value;
        var content       = m.Groups[3].Value;

        if (category is not ("combat" or "notify")) return;

        if (!DateTime.TryParseExact(timestampStr, "yyyy.MM.dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var timestamp))
            timestamp = DateTime.UtcNow;

        // Strip EVE's HTML-like markup
        var clean = Regex.Replace(content, "<[^>]+>", "").Trim();

        // ── Tackle: "Warp scramble attempt from <name> to you" ──
        var tackle = TackleRegex().Match(clean);
        if (tackle.Success)
        {
            var attacker = tackle.Groups[1].Value.Trim();

            // Skip when WE are the tackler ("...from you to <target>")
            if (attacker.Equals("you", StringComparison.OrdinalIgnoreCase)) return;

            AlertRaised?.Invoke(new FcAlert
            {
                Timestamp    = timestamp,
                AlertType    = AlertType.Tackled,
                AttackerName = attacker,
                RawLogLine   = line
            });
            return;
        }

        // ── Cap-out: a module deactivates because capacitor ran dry ──
        var capOut = CapOutRegex().Match(clean);
        if (capOut.Success)
        {
            AlertRaised?.Invoke(new FcAlert
            {
                Timestamp  = timestamp,
                AlertType  = AlertType.CapTrouble,
                Detail     = capOut.Groups[1].Value.Trim(),
                RawLogLine = line
            });
        }
    }

    public void Dispose() => StopWatching();
}
