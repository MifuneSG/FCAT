using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// Watches the EVE gamelogs and raises alerts for events that EVE actually writes to disk.
///
/// EVE log reality (verified against community log-parser tooling + in-game behaviour):
/// the gamelog does NOT record most electronic warfare. The only EWar effect that produces a
/// reliable line is warp scramble / disruption:  "Warp scramble attempt from &lt;name&gt; to you!".
/// Webs, neuts, ECM, tracking disruptors, sensor dampeners and painters write nothing, so they
/// are not - and cannot be - detected here. We also catch the genuine cap-out line where a
/// module deactivates from insufficient capacitor, and cloak failures, since those ARE written.
///
/// Every running client writes its OWN log, so an FC with alts logged in has several live at once.
/// This watches all of them rather than only the newest file, and reads each log's "Listener:"
/// header to learn whose it is - otherwise a scout alt being decloaked would either be missed
/// entirely or reported as having happened to the FC.
/// </summary>
public partial class CombatLogService : IDisposable
{
    /// <summary>One gamelog being followed: how far we've read, and whose client wrote it.</summary>
    private sealed class LogFile
    {
        public long   Position;
        public string Listener = string.Empty;
    }

    // [ 2024.01.15 20:30:45 ] (combat) content
    [GeneratedRegex(@"^\[ (\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}) \] \((\w+)\) (.+)$")]
    private static partial Regex LogLineRegex();

    // "Warp scramble attempt from <name> to you!" (target may be "you" or "your <ship>").
    // EVE uses identical text for warp disruptors (point) and scramblers (scram).
    // The target MUST be "you" - lines like "...from you to <name>!" (this character tackling
    // someone else) must NOT fire the alert.
    [GeneratedRegex(@"warp scramble attempt from (.+?) to you", RegexOptions.IgnoreCase)]
    private static partial Regex TackleRegex();

    // "<Module> deactivates as the capacitor runs out of charge" / "...insufficient capacitor"
    [GeneratedRegex(@"(.+?) deactivat\w*(?: as)?.{0,30}capacitor", RegexOptions.IgnoreCase)]
    private static partial Regex CapOutRegex();

    // The cloak came off on its own - something got within decloak range.
    [GeneratedRegex(@"your cloak deactivates(?:\s+due to)?\s*(.*)", RegexOptions.IgnoreCase)]
    private static partial Regex CloakDropRegex();

    // The cloak refused to go on in the first place. Different problem, same urgency.
    [GeneratedRegex(@"cloaking systems are unable to activate(?:\s+due to)?\s*(.*)", RegexOptions.IgnoreCase)]
    private static partial Regex CloakBlockedRegex();

    // EVE names the culprit as "a nearby <thing>", which is the part the FC needs.
    [GeneratedRegex(@"\b(?:an?|the) nearby (.+?)\s*[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex NearbyThingRegex();

    [GeneratedRegex(@"within\s+([\d,]+)\s*met", RegexOptions.IgnoreCase)]
    private static partial Regex CloakRangeRegex();

    // Every gamelog opens with a short header naming the character whose client wrote it.
    [GeneratedRegex(@"Listener:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ListenerRegex();

    private FileSystemWatcher? _watcher;

    private readonly object _filesLock = new();
    private readonly Dictionary<string, LogFile> _files = new(StringComparer.OrdinalIgnoreCase);

    // A decloak writes more than one line in quick succession; the FC needs telling once.
    private string   _lastCloakDetail = string.Empty;
    private DateTime _lastCloakTime;

    /// <summary>The FC's own extra cloak pattern (see AppSettings.CloakDroppedLogPattern).</summary>
    public static string CloakDroppedPattern { get; set; } = string.Empty;

    private static Regex? _cloakRegex;
    private static string _cloakPattern = string.Empty;

    /// <summary>The character FCAT is operating as, so alerts from other clients can be marked.</summary>
    public string ActiveCharacterName { get; set; } = string.Empty;

    public event Action<FcAlert>? AlertRaised;

    /// <summary>Every parsed gamelog line, so the FC's own rules can match against it.</summary>
    public event Action<string>? LineParsed;

    /// <summary>Cloak failures go out separately - only the alt tracker knows whether the character
    /// they happened to is one the FC actually cares about.</summary>
    public event Action<FcAlert>? CloakDropped;

    public string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     "EVE", "logs", "Gamelogs");

    /// <summary>Compiles (and caches) the FC's own pattern. A bad one simply never matches.</summary>
    private static Regex? CloakRegex()
    {
        if (CloakDroppedPattern.Length == 0) return null;
        if (_cloakPattern != CloakDroppedPattern)
        {
            try
            {
                _cloakRegex = new Regex(CloakDroppedPattern, RegexOptions.IgnoreCase,
                                        TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException) { _cloakRegex = null; }
            _cloakPattern = CloakDroppedPattern;
        }
        return _cloakRegex;
    }

    // Public API
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

        SeedRecentLogs(dir);
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileCreated;
            _watcher.Dispose();
            _watcher = null;
        }
        lock (_filesLock) _files.Clear();
    }

    // File tracking
    /// <summary>
    /// Picks up the logs of clients that were already running, positioned at their current end so
    /// only new lines count. Anything untouched for 12 hours is a previous session's log and is
    /// left alone - the Gamelogs folder is never cleaned out and accumulates thousands of files.
    /// </summary>
    private void SeedRecentLogs(string dir)
    {
        var cutoff = DateTime.Now.AddHours(-12);
        foreach (var path in Directory.EnumerateFiles(dir, "*.txt"))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTime < cutoff) continue;
                lock (_filesLock)
                    _files[path] = new LogFile { Position = info.Length, Listener = ReadListener(path) };
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads the character name out of a gamelog's header. It sits in the first few lines,
    /// so this never reads more than a dozen.</summary>
    private static string ReadListener(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 12; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                var m = ListenerRegex().Match(line);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return string.Empty;
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e) => ReadNewLines(e.FullPath, isNewFile: true);
    private void OnFileChanged(object sender, FileSystemEventArgs e) => ReadNewLines(e.FullPath, isNewFile: false);

    private void ReadNewLines(string path, bool isNewFile)
    {
        LogFile file;
        lock (_filesLock)
        {
            if (!_files.TryGetValue(path, out var known))
            {
                // A file we haven't seen. If it was just created, read it from the top - that's a
                // client starting up. If it merely changed, it predates us, so start at the end.
                long position = 0;
                if (!isNewFile)
                {
                    try { position = new FileInfo(path).Length; }
                    catch (IOException) { return; }
                    catch (UnauthorizedAccessException) { return; }
                }
                known = new LogFile { Position = position, Listener = ReadListener(path) };
                _files[path] = known;
            }
            file = known;
        }

        // Per-file lock: several clients write at once and the watcher fires on its own threads.
        lock (file)
        {
            // A client writes its header before its first combat line, so the listener often isn't
            // there on the first look.
            if (file.Listener.Length == 0) file.Listener = ReadListener(path);

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (file.Position > stream.Length) file.Position = 0;   // truncated / rolled over
                stream.Seek(file.Position, SeekOrigin.Begin);

                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null) ParseLine(line, file.Listener);
                file.Position = stream.Position;
            }
            catch (IOException) { /* locked - retry on the next change */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Parsing
    private void ParseLine(string line, string listener)
    {
        var m = LogLineRegex().Match(line);
        if (!m.Success) return;

        var timestampStr = m.Groups[1].Value;
        var category     = m.Groups[2].Value;
        var content      = m.Groups[3].Value;

        if (category is not ("combat" or "notify")) return;

        if (!DateTime.TryParseExact(timestampStr, "yyyy.MM.dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            timestamp = DateTime.UtcNow;

        // Strip EVE's HTML-like markup
        var clean = Regex.Replace(content, "<[^>]+>", "").Trim();

        LineParsed?.Invoke(clean);

        // Tackle: "Warp scramble attempt from <name> to you"
        var tackle = TackleRegex().Match(clean);
        if (tackle.Success)
        {
            var attacker = tackle.Groups[1].Value.Trim();

            // Skip when WE are the tackler ("...from you to <target>")
            if (attacker.Equals("you", StringComparison.OrdinalIgnoreCase)) return;

            AlertRaised?.Invoke(Tag(new FcAlert
            {
                Timestamp    = timestamp,
                AlertType    = AlertType.Tackled,
                AttackerName = attacker,
                RawLogLine   = line
            }, listener));
            return;
        }

        // Cap-out: a module deactivates because capacitor ran dry
        var capOut = CapOutRegex().Match(clean);
        if (capOut.Success)
        {
            AlertRaised?.Invoke(Tag(new FcAlert
            {
                Timestamp  = timestamp,
                AlertType  = AlertType.CapTrouble,
                Detail     = capOut.Groups[1].Value.Trim(),
                RawLogLine = line
            }, listener));
            return;
        }

        if (MatchCloak(clean) is { } cloak)
        {
            cloak.Timestamp  = timestamp;
            cloak.RawLogLine = line;
            CloakDropped?.Invoke(Tag(cloak, listener));
        }
    }

    /// <summary>Stamps an alert with the client it came from.</summary>
    private FcAlert Tag(FcAlert alert, string listener)
    {
        alert.SourceCharacter = listener;
        alert.FromOtherClient = listener.Length > 0
                             && ActiveCharacterName.Length > 0
                             && !listener.Equals(ActiveCharacterName, StringComparison.OrdinalIgnoreCase);
        return alert;
    }

    /// <summary>
    /// Turns a cloak line into an alert, or null. Two failures are worth telling apart: the cloak
    /// coming OFF (you are visible now) and the cloak refusing to go ON (you can't hide yet), so
    /// the headline differs and the detail names whatever caused it.
    /// </summary>
    private FcAlert? MatchCloak(string clean)
    {
        string headline, detail;

        if (CloakDropRegex().Match(clean) is { Success: true } dropped)
        {
            var tail = dropped.Groups[1].Value;
            headline = string.Empty;
            detail   = NearbyThing(tail) is { } thing ? $"Decloaked by a {thing}" : Reason(tail, "Cloak dropped");
        }
        else if (CloakBlockedRegex().Match(clean) is { Success: true } blocked)
        {
            var tail = blocked.Groups[1].Value;
            headline = "Cloak will not activate";
            if (NearbyThing(tail) is { } thing)
            {
                var range = CloakRangeRegex().Match(tail);
                detail = range.Success ? $"{thing} within {range.Groups[1].Value} m" : $"{thing} too close";
            }
            else detail = Reason(tail, "Something is too close");
        }
        else
        {
            // The FC's own pattern, for wording FCAT doesn't know about.
            var custom = CloakRegex();
            if (custom == null || !custom.IsMatch(clean)) return null;
            headline = string.Empty;
            detail   = clean;
        }

        // One decloak writes several lines; say it once.
        if (detail == _lastCloakDetail && DateTime.UtcNow - _lastCloakTime < TimeSpan.FromSeconds(10)) return null;
        _lastCloakDetail = detail;
        _lastCloakTime   = DateTime.UtcNow;

        return new FcAlert { AlertType = AlertType.CloakDropped, Detail = detail, CustomHeadline = headline };
    }

    private static string? NearbyThing(string tail)
    {
        var m = NearbyThingRegex().Match(tail);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>EVE's own wording for the cause, tidied into a sentence.</summary>
    private static string Reason(string tail, string fallback)
    {
        tail = tail.Trim().TrimEnd('.', '!');
        return tail.Length == 0 ? fallback : char.ToUpper(tail[0]) + tail[1..];
    }

    public void Dispose() => StopWatching();
}
