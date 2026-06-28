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
    [GeneratedRegex(@"^\[ \d{4}\.\d{2}\.\d{2} (\d{2}:\d{2}:\d{2}) \] ([^>]+?) > (.+)$")]
    private static partial Regex ChatLineRegex();

    private FileSystemWatcher? _watcher;
    private string? _watchedFile;
    private long _lastFilePosition;
    private bool _seekedToEnd;

    /// <summary>(timestamp, speaker, message) for each new intel line.</summary>
    public event Action<DateTime, string, string>? ReportReceived;

    public string? ActiveChannel { get; private set; }

    public string LogDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Chatlogs");

    /// <summary>Filename prefix of the intel channel to read (e.g. "Intel").</summary>
    public string ChannelPrefix { get; set; } = "Intel";

    /// <summary>Current region — only an intel channel whose name matches it is read (regional channels
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
        _seekedToEnd = false;
    }

    /// <summary>Poll for new lines (the watcher is unreliable while EVE holds the file open).</summary>
    public void Refresh()
    {
        if (!Directory.Exists(LogDirectory)) return;
        var latest = LatestFile();

        if (latest == null)
        {
            // No intel channel for the current region — show nothing rather than another region's spam.
            _watchedFile = null;
            ActiveChannel = null;
            return;
        }

        if (!string.Equals(latest, _watchedFile, StringComparison.OrdinalIgnoreCase))
        {
            _watchedFile = latest;
            _lastFilePosition = 0;
            _seekedToEnd = false;
            ActiveChannel = ChannelNameFromFile(latest);
        }
        ReadNewLines();
    }

    private string? LatestFile() =>
        Directory.GetFiles(LogDirectory, $"*{ChannelPrefix}*.txt")
                 .Where(MatchesRegion)
                 .OrderByDescending(File.GetLastWriteTime)
                 .FirstOrDefault();

    /// <summary>True if the channel's name matches the current region (or there's no region filter).
    /// Handles abbreviations like "I. Ftn Intel" → "Fountain" via a subsequence check.</summary>
    private bool MatchesRegion(string filePath)
    {
        if (string.IsNullOrWhiteSpace(RegionFilter)) return true;
        var region = RegionFilter;

        // Distinguishing tokens = anything that isn't the generic "Intel"/short prefix.
        var tokens = ChannelNameFromFile(filePath).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', '_'))
            .Where(t => t.Length >= 2 && !t.Equals("Intel", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tokens.Count == 0) return true;   // a plain "Intel" channel isn't region-specific → always read
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

    private void AttachToLatest()
    {
        var latest = LatestFile();
        if (latest == null) { ActiveChannel = null; return; }
        _watchedFile = latest;
        _lastFilePosition = 0;
        _seekedToEnd = false;
        ActiveChannel = ChannelNameFromFile(latest);
        ReadNewLines();
    }

    private static string ChannelNameFromFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(name, @"^(.*)_\d{8}_\d{6}_\d+$");
        return m.Success ? m.Groups[1].Value : name;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath != _watchedFile &&
            Path.GetFileName(e.FullPath).Contains(ChannelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AttachToLatest();
            return;
        }
        if (e.FullPath == _watchedFile) ReadNewLines();
    }

    private void ReadNewLines()
    {
        if (_watchedFile == null) return;
        try
        {
            using var stream = new FileStream(_watchedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // On first attach, start ~24 KB before the end so the feed shows recent reports immediately
            // (not the whole — possibly huge — history). Keep the offset even for UTF-16 alignment;
            // a partial first line just fails the regex and is skipped.
            if (!_seekedToEnd)
            {
                var start = stream.Length - 24_000;
                if (start < 0) start = 0;
                if (start % 2 != 0) start--;
                _lastFilePosition = start;
                _seekedToEnd = true;
            }

            stream.Seek(_lastFilePosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) != null) ParseLine(line);
            _lastFilePosition = stream.Position;
        }
        catch (IOException) { /* locked — retry next poll */ }
    }

    private void ParseLine(string line)
    {
        line = line.TrimStart('﻿', '￾');
        var m = ChatLineRegex().Match(line);
        if (!m.Success) return;

        var speaker = m.Groups[2].Value.Trim();
        if (speaker.Equals("EVE System", StringComparison.OrdinalIgnoreCase)) return;   // MOTD/system lines

        var raw = m.Groups[3].Value;
        // Drop kill links people paste into intel — EVE uses a "killReport:" url tag for them.
        if (raw.Contains("killReport:", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("zkillboard.com", StringComparison.OrdinalIgnoreCase))
            return;

        var message = Regex.Replace(raw, "<[^>]+>", "").Trim();   // strip remaining link markup
        if (message.Length == 0) return;
        ReportReceived?.Invoke(DateTime.Now, speaker, message);
    }

    public void Dispose() => StopWatching();
}
