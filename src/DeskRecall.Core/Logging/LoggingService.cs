using System.Collections.Concurrent;
using System.Text;

namespace DeskRecall.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message)
{
    public string LevelTag => Level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warning => "WRN", _ => "ERR" };
    public string Format() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag}] {Source}: {Message}";
}

public interface ILogService
{
    void Log(LogLevel level, string source, string message, Exception? ex = null);
    IReadOnlyList<LogEntry> Recent { get; }
    event Action<LogEntry>? EntryAdded;
    string? LogDirectory { get; }
    bool Verbose { get; set; }
}

public static class LogExtensions
{
    public static void Debug(this ILogService l, string src, string msg) => l.Log(LogLevel.Debug, src, msg);
    public static void Info(this ILogService l, string src, string msg) => l.Log(LogLevel.Info, src, msg);
    public static void Warn(this ILogService l, string src, string msg, Exception? ex = null) => l.Log(LogLevel.Warning, src, msg, ex);
    public static void Error(this ILogService l, string src, string msg, Exception? ex = null) => l.Log(LogLevel.Error, src, msg, ex);
}

/// <summary>
/// Lokales Datei-Protokoll (eine Datei pro Tag) plus Ringpuffer für die Diagnoseseite.
/// Keine Telemetrie – nichts verlässt den Rechner.
/// </summary>
public sealed class LoggingService : ILogService, IDisposable
{
    private const int RingSize = 600;
    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly object _fileLock = new();
    private readonly string? _dir;
    private readonly int _retentionDays;
    private StreamWriter? _writer;
    private DateOnly _writerDay;

    public LoggingService(string? logDirectory, int retentionDays = 14)
    {
        _dir = logDirectory;
        _retentionDays = Math.Max(1, retentionDays);
        if (_dir is not null)
        {
            try { Directory.CreateDirectory(_dir); CleanupOld(); } catch { /* Protokoll darf nie die App stoppen */ }
        }
    }

    public event Action<LogEntry>? EntryAdded;
    public string? LogDirectory => _dir;
    public bool Verbose { get; set; }
    public IReadOnlyList<LogEntry> Recent => _ring.ToArray();

    public void Log(LogLevel level, string source, string message, Exception? ex = null)
    {
        if (level == LogLevel.Debug && !Verbose) return;
        if (ex is not null) message += $" | {ex.GetType().Name}: {ex.Message}" + (ex.HResult != 0 ? $" (0x{ex.HResult:X8})" : "");
        var entry = new LogEntry(DateTimeOffset.Now, level, source, message);
        _ring.Enqueue(entry);
        while (_ring.Count > RingSize && _ring.TryDequeue(out _)) { }
        WriteToFile(entry, ex);
        try { EntryAdded?.Invoke(entry); } catch { }
    }

    private void WriteToFile(LogEntry entry, Exception? ex)
    {
        if (_dir is null) return;
        lock (_fileLock)
        {
            try
            {
                var today = DateOnly.FromDateTime(entry.Time.LocalDateTime);
                if (_writer is null || today != _writerDay)
                {
                    _writer?.Dispose();
                    var path = Path.Combine(_dir, $"deskrecall-{today:yyyyMMdd}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDay = today;
                }
                _writer.WriteLine(entry.Format());
                if (ex is not null && entry.Level == LogLevel.Error && Verbose) _writer.WriteLine(ex.ToString());
            }
            catch { }
        }
    }

    private void CleanupOld()
    {
        if (_dir is null) return;
        var cutoff = DateTime.Now.AddDays(-_retentionDays);
        foreach (var f in Directory.EnumerateFiles(_dir, "deskrecall-*.log"))
            try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
    }

    public void Dispose() { lock (_fileLock) { _writer?.Dispose(); _writer = null; } }
}

/// <summary>Logger für Tests.</summary>
public sealed class MemoryLogService : ILogService
{
    private readonly List<LogEntry> _entries = new();
    public event Action<LogEntry>? EntryAdded;
    public string? LogDirectory => null;
    public bool Verbose { get; set; } = true;
    public IReadOnlyList<LogEntry> Recent { get { lock (_entries) return _entries.ToArray(); } }
    public void Log(LogLevel level, string source, string message, Exception? ex = null)
    {
        var e = new LogEntry(DateTimeOffset.Now, level, source, ex is null ? message : message + " | " + ex.Message);
        lock (_entries) _entries.Add(e);
        EntryAdded?.Invoke(e);
    }
}
