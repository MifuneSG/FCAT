using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FCAT.Services;

/// <summary>
/// Tails an in-game intel chat channel from the EVE Chatlogs (same approach as the boost reader:
/// EVE only logs channels the local character is in, so the FC must be joined to the intel channel).
/// Raises <see cref="ReportReceived"/> for each new line so the intel feed can show it live.
/// </summary>
public partial class IntelChannelService : IDisposable
{
    [GeneratedRegex(@"^\[ (\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}) \] ([^>]+?) > (.+)$")]
    private static partial Regex ChatLineRegex();

    private FileSystemWatcher? _watcher;

    /// <summary>
    /// How far we have read into each channel file. EVE writes one chatlog per running client, so
    /// an FC with six clients open has six files for the same channel, all appended at once.
    /// Following only "the newest" one means the newest flips every time another client writes, and
    /// a single shared read position then rewinds and replays the whole file as fresh intel.
    /// </summary>
    private readonly Dictionary<string, long> _positions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The poll runs on the UI thread and the file watcher on a threadpool thread, and both read.
    /// Serialise them: the read state below is plain dictionaries, and a torn write there would
    /// lose a read position and replay a log.
    /// </summary>
    private readonly object _readLock = new();

    /// <summary>
    /// When each line was last reported, keyed by speaker + text. Every client in the channel
    /// records the same line, so without this one call-out alerts once per open client.
    /// </summary>
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.Ordinal);

    /// <summary>
    /// Clients do not agree on the clock to the second - the same line is written a second apart
    /// across six logs - so an exact timestamp match is not enough to fold the copies together.
    /// Measured against real logs the split is clean: copies land within a second of each other,
    /// while the same pilot genuinely repeating a call-out is tens of seconds later at the least.
    /// </summary>
    private static readonly TimeSpan SameLineWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// (timestamp, speaker, message, backfill) for each new intel line. Backfill is the history
    /// read when a file is first seen: it belongs in the feed, but it must not raise alerts -
    /// a call-out from twenty minutes ago is not something to shout about on launch.
    /// </summary>
    public event Action<DateTime, string, string, bool>? ReportReceived;

    /// <summary>True while replaying a file's existing tail rather than following new writes.</summary>
    private bool _backfilling;

    public string? ActiveChannel { get; private set; }

    public string LogDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Chatlogs");

    /// <summary>Filename prefix of the intel channel to read (e.g. "Intel").</summary>
    public string ChannelPrefix { get; set; } = "Intel";

    /// <summary>Current region - only an intel channel whose name matches it is read (regional channels
    /// spam constantly, so we ignore the ones for regions the fleet isn't in). Null = no region filter.</summary>
    public string? RegionFilter { get; set; }

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
        AttachToLatest();
    }

    /// <summary>
    /// Every intel channel the logs show this account has joined, most recently written first.
    /// Used to explain an empty feed: no channel at all reads differently to one for another region.
    /// </summary>
    public IReadOnlyList<string> CandidateChannels()
    {
        if (!Directory.Exists(LogDirectory)) return [];

        return Directory.GetFiles(LogDirectory, $"*{ChannelPrefix}*.txt")
                        .OrderByDescending(File.GetLastWriteTime)
                        .Select(ChannelNameFromFile)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        lock (_readLock) _positions.Clear();
    }

    /// <summary>Poll for new lines (the watcher is unreliable while EVE holds the file open).</summary>
    public void Refresh()
    {
        if (!Directory.Exists(LogDirectory)) return;

        var files = MatchingFiles();
        if (files.Count == 0)
        {
            // No intel channel for the current region - show nothing rather than another region's spam.
            ActiveChannel = null;
            lock (_readLock) _positions.Clear();
            return;
        }

        ActiveChannel = ChannelNameFromFile(files[0]);   // newest writer, for the status line only
        foreach (var f in files) ReadNewLines(f);

        // A client that closed leaves a file nobody will append to again.
        lock (_readLock)
            foreach (var gone in _positions.Keys.Except(files, StringComparer.OrdinalIgnoreCase).ToList())
                _positions.Remove(gone);
    }

    /// <summary>Every file for this channel that matches the current region, newest writer first.</summary>
    private List<string> MatchingFiles() =>
        Directory.GetFiles(LogDirectory, $"*{ChannelPrefix}*.txt")
                 .Where(MatchesRegion)
                 .OrderByDescending(File.GetLastWriteTime)
                 .ToList();

    /// <summary>True if the channel's name matches the current region (or there's no region filter).
    /// Handles abbreviations like "I. Ftn Intel" -> "Fountain" via a subsequence check.</summary>
    private bool MatchesRegion(string filePath)
    {
        if (string.IsNullOrWhiteSpace(RegionFilter)) return true;
        var region = RegionFilter;

        // Distinguishing tokens = anything that isn't the generic "Intel"/short prefix.
        var tokens = ChannelNameFromFile(filePath).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', '_'))
            .Where(t => t.Length >= 2 && !t.Equals("Intel", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tokens.Count == 0) return true;   // a plain "Intel" channel isn't region-specific -> always read
        return tokens.Any(t => region.Contains(t, StringComparison.OrdinalIgnoreCase) || IsSubsequence(t, region));
    }

    /// <summary>Are the letters of <paramref name="token"/> found in order within <paramref name="text"/>? (case-insensitive)</summary>
    private static bool IsSubsequence(string token, string text)
    {
        int ti = 0;
        foreach (var c in text)
            if (ti < token.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(token[ti])) ti++;
        return ti == token.Length;
    }

    private void AttachToLatest() => Refresh();

    private static string ChannelNameFromFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(name, @"^(.*)_\d{8}_\d{6}_\d+$");
        return m.Success ? m.Groups[1].Value : name;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!Path.GetFileName(e.FullPath).Contains(ChannelPrefix, StringComparison.OrdinalIgnoreCase)) return;
        if (!MatchesRegion(e.FullPath)) return;
        ReadNewLines(e.FullPath);
    }

    private void ReadNewLines(string path)
    {
        lock (_readLock)
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var firstSight = !_positions.TryGetValue(path, out var from);
            if (firstSight)
            {
                // First sight of this file: start ~24 KB before the end so the feed shows recent
                // reports immediately rather than the whole - possibly huge - history. Keep the
                // offset even for UTF-16 alignment; a partial first line just fails the regex.
                from = stream.Length - 24_000;
                if (from < 0) from = 0;
                if (from % 2 != 0) from--;
            }

            // A file can be truncated or rolled - never seek past the end.
            if (from > stream.Length) from = 0;

            stream.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            string? line;
            _backfilling = firstSight;
            try
            {
                while ((line = reader.ReadLine()) != null) ParseLine(line);
            }
            finally { _backfilling = false; }
            _positions[path] = stream.Position;
        }
        catch (IOException) { /* locked - retry next poll */ }
    }

    /// <summary>False if this line was already reported - another client's copy, or a re-read.</summary>
    private bool FirstSighting(string key, DateTime logTime)
    {
        if (_lastSeen.TryGetValue(key, out var prev) && (logTime - prev).Duration() <= SameLineWindow)
            return false;

        _lastSeen[key] = logTime;

        if (_lastSeen.Count > 2000)
        {
            var cutoff = logTime - TimeSpan.FromMinutes(15);
            foreach (var stale in _lastSeen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _lastSeen.Remove(stale);
        }
        return true;
    }

    private void ParseLine(string line)
    {
        line = line.TrimStart('﻿', '￾');
        var m = ChatLineRegex().Match(line);
        if (!m.Success) return;

        var speaker = m.Groups[2].Value.Trim();
        if (speaker.Equals("EVE System", StringComparison.OrdinalIgnoreCase)) return;   // MOTD/system lines

        var raw = m.Groups[3].Value;
        // Drop kill links people paste into intel - EVE uses a "killReport:" url tag for them.
        if (raw.Contains("killReport:", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("zkillboard.com", StringComparison.OrdinalIgnoreCase))
            return;

        var message = Regex.Replace(raw, "<[^>]+>", "").Trim();   // strip remaining link markup
        if (message.Length == 0) return;

        // Judge repeats on the log's own clock, not ours: a re-read happens now, but the line still
        // carries the time it was said. An unparseable stamp just skips the check rather than
        // folding every such line onto one key.
        if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy.MM.dd HH:mm:ss",
                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var logTime)
            && !FirstSighting(speaker + "|" + message, logTime))
            return;

        ReportReceived?.Invoke(DateTime.Now, speaker, message, _backfilling);
    }

    public void Dispose() => StopWatching();
}
