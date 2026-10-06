using ModbusTester.Core.Models;

namespace ModbusTester.Core.Services;

/// <summary>Thread-safe in-memory communication log. UI subscribes to <see cref="EntryAdded"/>.</summary>
public sealed class CommunicationLogger
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = [];
    private readonly Func<DateTime> _clock;

    public CommunicationLogger(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.Now);

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    public void Log(LogKind kind, string message)
    {
        var entry = new LogEntry(_clock(), kind, message);
        lock (_lock)
            _entries.Add(entry);
        EntryAdded?.Invoke(this, entry);
    }

    public void Info(string message) => Log(LogKind.Info, message);
    public void Error(string message) => Log(LogKind.Error, message);

    public void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }
}
