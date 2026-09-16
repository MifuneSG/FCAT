using System.IO;
using System.Text;

namespace FCAT.Services;

/// <summary>
/// A small session log, written to disk so a failure the user cannot see is still reportable.
///
/// FCAT talks to three flaky things - ESI, zKillboard and the EVE log folder - and the handlers
/// around them mostly turn a failure into "no data", because a dropped poll should never take the
/// app down. That is the right behaviour and the wrong diagnosis: a kill feed that is empty because
/// the response would not parse looks exactly like one that is empty because nothing died. Both of
/// those shipped in a release. Anything swallowed on purpose should land here on its way past.
///
/// Rules: never throw (a logger that breaks the app is worse than no logger), never block the UI
/// for long, and keep it small enough that an FC can paste it into Discord.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Beside settings, so everything FCAT owns lives in one folder.</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT", "logs");

    private static string? _path;
    private static StreamWriter? _writer;

    /// <summary>Sessions to keep. Enough to cover "it did it again yesterday", not enough to pile up.</summary>
    private const int KeepSessions = 5;

    /// <summary>Stop a runaway loop filling the disk - past this the session log stops growing.</summary>
    private const long MaxBytes = 4 * 1024 * 1024;

    public static void Start(string version)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            Prune();

            _path = Path.Combine(Directory, $"fcat-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };

            Info("app", $"FCAT {version} starting on {Environment.OSVersion}");
        }
        catch { _writer = null; }   // no log is survivable; a crash on startup is not
    }

    public static void Info(string area, string message)  => Write("INFO", area, message, null);
    public static void Warn(string area, string message, Exception? ex = null) => Write("WARN", area, message, ex);
    public static void Error(string area, string message, Exception? ex = null) => Write("ERR ", area, message, ex);

    private static void Write(string level, string area, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                if (_writer == null) return;
                if (_writer.BaseStream.Length > MaxBytes) return;

                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} [{area}] {message}");
                if (ex != null) _writer.WriteLine($"                      {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch { /* logging must never be the thing that breaks */ }
    }

    /// <summary>The tail of this session's log, for the diagnostics button in Setup.</summary>
    public static string Tail(int lines = 200)
    {
        try
        {
            lock (Gate)
            {
                if (_path == null || !File.Exists(_path)) return "(no log this session)";

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var all = reader.ReadToEnd().Split('\n');
                return string.Join("\n", all.TakeLast(lines)).TrimEnd();
            }
        }
        catch (Exception ex) { return $"(could not read the log: {ex.Message})"; }
    }

    /// <summary>Everything worth pasting into a bug report - versions, paths, then the log tail.</summary>
    public static string Diagnostics(string version, params (string Label, string Value)[] extra)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FCAT {version}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}  .NET {Environment.Version}");
        foreach (var (label, value) in extra) sb.AppendLine($"{label}: {value}");
        sb.AppendLine($"Log: {_path ?? "(none)"}");
        sb.AppendLine();
        sb.AppendLine(Tail());
        return sb.ToString();
    }

    private static void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(Directory).GetFiles("fcat-*.log")
                                                            .OrderByDescending(f => f.LastWriteTime)
                                                            .Skip(KeepSessions - 1))
                old.Delete();
        }
        catch { /* a stale log we cannot delete is not worth failing over */ }
    }
}
