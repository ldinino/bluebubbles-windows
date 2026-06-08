using System.Runtime.InteropServices;

namespace BlueBubbles.Core.Services;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Subsystem tag applied to every log line. One unified chronological log is kept (so
/// cross-component causality stays visible); the category is what the in-app viewer filters
/// on, rather than splitting output into per-component files.
/// </summary>
public enum LogCategory { App, Socket, Sync, Firebase, OAuth, Contacts, Api, Ui }

/// <summary>
/// App-wide logger. Keeps a bounded in-memory ring buffer (for the live viewer) and also
/// appends to a rolling per-day text file under <c>%LocalAppData%\BlueBubbles\logs\</c>.
/// Logging must never throw — all file IO is best-effort and swallows exceptions.
/// </summary>
public static class AppLog
{
    private static readonly List<string> _entries = new();
    private static readonly object _lock = new();
    private static readonly object _fileLock = new();
    private const int MaxEntries = 500;

    private const int RetentionDays = 7;
    private const long MaxTotalLogBytes = 10 * 1024 * 1024; // 10 MB across all retained files

    /// <summary>
    /// Minimum level captured to memory and disk. Defaults to <see cref="LogLevel.Info"/> so
    /// verbose <see cref="LogLevel.Debug"/> tracing is dropped unless verbosity is raised.
    /// </summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>Cheap pre-check for hot paths: returns false when a message at <paramref name="level"/>
    /// would be dropped, so callers can skip building (interpolating) it. Mirrors the filter in
    /// <see cref="Add"/>.</summary>
    public static bool IsEnabled(LogLevel level) => level >= MinLevel;

    public static event Action<string>? EntryAdded;
    public static IReadOnlyList<string> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    private static string? _logDir;
    private static DateTime _currentLogDate = DateTime.MinValue;

    /// <summary><c>%LocalAppData%\BlueBubbles\logs</c> — matches the app's data-dir convention.</summary>
    public static string LogDirectory => _logDir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlueBubbles", "logs");

    // Legacy parameterless overloads default to the App category so existing call sites compile
    // unchanged; the category overloads are preferred for new code.
    public static void Debug(string message) => Add(LogLevel.Debug, LogCategory.App, message);
    public static void Debug(LogCategory category, string message) => Add(LogLevel.Debug, category, message);
    public static void Info(string message) => Add(LogLevel.Info, LogCategory.App, message);
    public static void Info(LogCategory category, string message) => Add(LogLevel.Info, category, message);
    public static void Warn(string message) => Add(LogLevel.Warn, LogCategory.App, message);
    public static void Warn(LogCategory category, string message) => Add(LogLevel.Warn, category, message);
    public static void Error(string message) => Add(LogLevel.Error, LogCategory.App, message);
    public static void Error(LogCategory category, string message) => Add(LogLevel.Error, category, message);

    /// <summary>
    /// Prepare the log directory, prune stale/oversized files, and write a session banner.
    /// Safe to call once at startup; lazy file creation also works without it.
    /// </summary>
    public static void Initialize()
    {
        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                LogRetention.Prune(LogDirectory, RetentionDays, MaxTotalLogBytes);
                _currentLogDate = DateTime.Now.Date;
            }
            catch { /* never crash the app over logging setup */ }
        }

        var os = Environment.OSVersion.VersionString;
        var arch = RuntimeInformation.OSArchitecture;
        Info(LogCategory.App,
            $"==== Session start — BlueBubbles v{AppInfo.Version} — {os} ({arch}) — .NET {Environment.Version} ====");
    }

    private static void Add(LogLevel level, LogCategory category, string message)
    {
        if (level < MinLevel) return;

        var now = DateTime.Now;
        var entry = FormatLine(now, level, category, message);

        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }

        WriteToFile(now, entry);
        EntryAdded?.Invoke(entry);
    }

    /// <summary>
    /// The exact line format written to memory and disk:
    /// <c>[2026-06-01 14:03:12.123] [INFO ] [Socket] message</c>. Public so the format contract
    /// the category filter parses can be unit-tested directly.
    /// </summary>
    public static string FormatLine(DateTime timestamp, LogLevel level, LogCategory category, string message) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{LevelTag(level)}] [{category}] {message}";

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "?????"
    };

    private static void WriteToFile(DateTime now, string entry)
    {
        lock (_fileLock)
        {
            try
            {
                if (now.Date != _currentLogDate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    LogRetention.Prune(LogDirectory, RetentionDays, MaxTotalLogBytes);
                    _currentLogDate = now.Date;
                }

                var path = Path.Combine(LogDirectory, $"bluebubbles-{now:yyyy-MM-dd}.log");
                File.AppendAllText(path, entry + Environment.NewLine);
            }
            catch { /* never throw from logging */ }
        }
    }
}
