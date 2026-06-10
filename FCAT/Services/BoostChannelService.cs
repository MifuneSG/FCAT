using System.IO;
using System.Text.RegularExpressions;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// Tails the fleet's boost channel (EVE Chatlogs) and tracks which command-burst charges
/// each booster has "dragged" into the channel. EVE only logs channels the local character
/// is in, so the FC must be joined to the matching "Boost N" channel for this to see anything.
///
/// Unlike the combat log we read each boost file from the BEGINNING — boosters typically post
/// their loadout once at form-up, possibly before FCAT was opened.
/// </summary>
public partial class BoostChannelService : IDisposable
{
    // [ 2024.01.15 20:30:45 ] Speaker Name > message text
    [GeneratedRegex(@"^\[ \d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2} \] ([^>]+?) > (.+)$")]
    private static partial Regex ChatLineRegex();

    private FileSystemWatcher? _watcher;
    private string?            _watchedFile;
    private long               _lastFilePosition;

    // Speaker (character name) → distinct charges they've posted this session
    private readonly Dictionary<string, HashSet<BoostChargeInfo>> _loadouts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    /// <summary>Raised whenever a booster's posted loadout changes.</summary>
    public event Action? Updated;

    /// <summary>The channel name FCAT locked onto (e.g. "Boost IV"), or null if none found.</summary>
    public string? ActiveChannel { get; private set; }

    public string LogDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     "EVE", "logs", "Chatlogs");

    /// <summary>Channel filename prefix to match (e.g. "Boost").</summary>
    public string ChannelPrefix { get; set; } = "Boost";

    public void StartWatching(string? chatlogsDirectory = null, string? channelPrefix = null)
    {
        if (!string.IsNullOrWhiteSpace(chatlogsDirectory)) LogDirectory  = chatlogsDirectory;
        if (!string.IsNullOrWhiteSpace(channelPrefix))     ChannelPrefix = channelPrefix;
        if (!Directory.Exists(LogDirectory)) return;
        StopWatching();

        _watcher = new FileSystemWatcher(LogDirectory, "*.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;

        AttachToLatestBoostLog();
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

    /// <summary>
    /// Re-reads the boost log. Call this on a timer — EVE holds the log file open while writing,
    /// so the FileSystemWatcher doesn't fire reliably; polling guarantees we see new posts.
    /// Switches to a newer session file if one has appeared.
    /// </summary>
    public void Refresh()
    {
        if (!Directory.Exists(LogDirectory)) return;

        var latest = Directory.GetFiles(LogDirectory, $"*{ChannelPrefix}*.txt")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        if (latest == null) return;

        if (!string.Equals(latest, _watchedFile, StringComparison.OrdinalIgnoreCase))
        {
            _watchedFile      = latest;
            _lastFilePosition = 0;                       // new session file — read from the top
            ActiveChannel     = ChannelNameFromFile(latest);
        }
        ReadNewLines();
    }

    /// <summary>Charges a given pilot has posted, or empty if none.</summary>
    public IReadOnlyCollection<BoostChargeInfo> GetLoadout(string characterName)
    {
        lock (_lock)
            return _loadouts.TryGetValue(characterName.Trim(), out var set)
                ? set.ToArray()
                : [];
    }

    /// <summary>Forget a pilot's loadout (e.g. when they pod out — boost is gone).</summary>
    public void ClearPilot(string characterName)
    {
        lock (_lock)
            if (_loadouts.Remove(characterName.Trim()))
                Updated?.Invoke();
    }

    private void AttachToLatestBoostLog()
    {
        // EVE names chat logs "<Channel>_<date>_<time>_<charId>.txt". Match the prefix ANYWHERE
        // in the name — channels are often named like "I. Boost FD", not just "Boost".
        var latest = Directory.GetFiles(LogDirectory, $"*{ChannelPrefix}*.txt")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();

        if (latest == null)
        {
            ActiveChannel = null;
            return;
        }

        _watchedFile      = latest;
        _lastFilePosition = 0;   // read whole file — capture loadouts posted before launch
        ActiveChannel     = ChannelNameFromFile(latest);
        ReadNewLines();
    }

    private static string ChannelNameFromFile(string path)
    {
        // "Boost IV_20240115_203045_90000001.txt" → "Boost IV"
        var name = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(name, @"^(.*)_\d{8}_\d{6}_\d+$");
        return m.Success ? m.Groups[1].Value : name;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // A brand-new, more recent boost channel file means a new fleet/session — switch to it.
        if (e.FullPath != _watchedFile &&
            Path.GetFileName(e.FullPath).Contains(ChannelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AttachToLatestBoostLog();
            return;
        }
        if (e.FullPath == _watchedFile) ReadNewLines();
    }

    private void ReadNewLines()
    {
        if (_watchedFile == null) return;
        var changed = false;
        try
        {
            using var stream = new FileStream(_watchedFile, FileMode.Open,
                                              FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(_lastFilePosition, SeekOrigin.Begin);

            // EVE chat logs are UTF-16 LE. Detect the leading BOM on the first read; fall back to
            // UTF-16 for incremental (mid-file) reads where there's no BOM to detect.
            using var reader = new StreamReader(stream, System.Text.Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (ParseLine(line)) changed = true;

            _lastFilePosition = stream.Position;
        }
        catch (IOException) { /* locked — retry on next change */ }

        if (changed) Updated?.Invoke();
    }

    private bool ParseLine(string line)
    {
        // EVE prepends a BOM (U+FEFF) to every chat-log line — strip it so the line starts with '['.
        line = line.TrimStart('﻿', '￾');

        var m = ChatLineRegex().Match(line);
        if (!m.Success) return false;

        var speaker = m.Groups[1].Value.Trim();

        // The channel MOTD is "spoken" by EVE System and lists every charge — ignore it.
        if (speaker.Equals("EVE System", StringComparison.OrdinalIgnoreCase)) return false;

        var message = Regex.Replace(m.Groups[2].Value, "<[^>]+>", "");   // strip item-link markup

        var charges = BoostChargeCatalog.FindIn(message).ToList();
        if (charges.Count == 0) return false;

        lock (_lock)
        {
            // Last message wins — a fresh post replaces the pilot's previous loadout, so
            // swapping links (e.g. shield → armor) is reflected immediately. Boosters drag
            // all their charges in a single chat message, so one message = full loadout.
            _loadouts[speaker] = [.. charges];
            return true;
        }
    }

    public void Dispose() => StopWatching();
}
