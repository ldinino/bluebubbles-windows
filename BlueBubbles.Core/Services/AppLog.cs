namespace BlueBubbles.Core.Services;

public static class AppLog
{
    private static readonly List<string> _entries = new();
    private static readonly object _lock = new();
    private const int MaxEntries = 500;

    public static event Action<string>? EntryAdded;
    public static IReadOnlyList<string> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public static void Info(string message) => Add("INFO", message);
    public static void Warn(string message) => Add("WARN", message);
    public static void Error(string message) => Add("ERROR", message);

    private static void Add(string level, string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }
        EntryAdded?.Invoke(entry);
    }
}
