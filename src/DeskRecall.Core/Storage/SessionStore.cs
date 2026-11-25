using System.Globalization;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;

namespace DeskRecall.Core.Storage;

public enum SaveOutcome { Saved, Unchanged, Rejected, Failed }

public sealed record HistoryEntry(string FilePath, DateTimeOffset CreatedAt, SessionKind Kind, int DesktopCount, int AppCount, int WindowCount, string? Hash);

public interface ISessionStore
{
    SessionSnapshot? LoadLast();
    SaveOutcome Save(SessionSnapshot snapshot, bool force = false);
    IReadOnlyList<HistoryEntry> GetHistory();
    SessionSnapshot? LoadHistory(string filePath);
    bool DeleteHistory(string filePath);
    void ClearHistory();
    int MaxHistory { get; set; }
    string? LastHash { get; }
    DateTimeOffset? LastSavedAt { get; }
    void Protect(string? filePath);
    event Action? HistoryChanged;
}

/// <summary>
/// Speichert die letzte Sitzung (last-session.json) und einen begrenzten Verlauf (history/*.json).
/// Alle Schreibvorgänge sind atomar. Unveränderte automatische Sicherungen werden nicht dupliziert.
/// </summary>
public sealed class SessionStore : ISessionStore
{
    private readonly AppDataPaths _paths;
    private readonly ILogService _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, HistoryEntry> _summaryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _protected = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastHash;
    private bool _lastHashLoaded;

    public SessionStore(AppDataPaths paths, ILogService log, int maxHistory = 25)
    {
        _paths = paths;
        _log = log;
        MaxHistory = maxHistory;
        Directory.CreateDirectory(paths.History);
        AtomicFile.CleanupTemp(paths.Sessions);
        AtomicFile.CleanupTemp(paths.History);
    }

    public int MaxHistory { get; set; }
    public string? LastHash { get { lock (_lock) { EnsureLastHash(); return _lastHash; } } }
    public DateTimeOffset? LastSavedAt { get; private set; }
    public event Action? HistoryChanged;

    /// <summary>Schützt einen Verlaufseintrag vor der automatischen Bereinigung (z. B. die Sitzung vor dem Neustart).</summary>
    public void Protect(string? filePath) { if (filePath is not null) lock (_lock) _protected.Add(Path.GetFullPath(filePath)); }

    private void EnsureLastHash()
    {
        if (_lastHashLoaded) return;
        _lastHashLoaded = true;
        var last = LoadLastCore();
        _lastHash = last is null ? null : last.ContentHash ?? SessionHasher.Compute(last);
        if (last is not null) LastSavedAt ??= last.CreatedAt;
    }

    public SessionSnapshot? LoadLast() { lock (_lock) return LoadLastCore(); }

    private SessionSnapshot? LoadLastCore()
    {
        var s = AtomicFile.ReadWithFallback(_paths.LastSessionFile, ParseValid);
        if (s is not null) return s;
        // Letzte gültige Sitzung aus dem Verlauf
        foreach (var e in GetHistoryCore())
        {
            var h = LoadFile(e.FilePath);
            if (h is not null)
            {
                _log.Warn("Sitzung", $"last-session.json fehlt oder ist defekt – verwende Verlauf {Path.GetFileName(e.FilePath)}");
                return h;
            }
        }
        return null;
    }

    private static SessionSnapshot? ParseValid(string json)
    {
        var s = Json.Deserialize<SessionSnapshot>(json);
        if (s is null || s.SchemaVersion < 1 || s.Windows is null || s.Desktops is null) return null;
        s.Monitors ??= new();
        return s;
    }

    private SessionSnapshot? LoadFile(string path)
    {
        try { return File.Exists(path) ? ParseValid(File.ReadAllText(path)) : null; }
        catch (Exception ex) { _log.Warn("Sitzung", $"Verlaufseintrag unlesbar: {Path.GetFileName(path)}", ex); return null; }
    }

    public SaveOutcome Save(SessionSnapshot snapshot, bool force = false)
    {
        SaveOutcome result;
        lock (_lock)
        {
            try
            {
                snapshot.ContentHash = SessionHasher.Compute(snapshot);
                EnsureLastHash();
                var isAutomatic = snapshot.Kind is SessionKind.Auto or SessionKind.Shutdown;
                if (!force && isAutomatic && snapshot.ContentHash == _lastHash)
                    return SaveOutcome.Unchanged;

                var json = Json.Serialize(snapshot);
                AtomicFile.WriteAllText(_paths.LastSessionFile, json);

                var name = $"{snapshot.CreatedAt.ToLocalTime():yyyyMMdd-HHmmss-fff}_{snapshot.Kind.ToString().ToLowerInvariant()}.json";
                var histPath = Path.GetFullPath(Path.Combine(_paths.History, name));
                AtomicFile.WriteAllText(histPath, json, keepBackup: false);
                _summaryCache[histPath] = Summarize(histPath, snapshot);

                _lastHash = snapshot.ContentHash;
                LastSavedAt = snapshot.CreatedAt;
                ApplyRetention();
                result = SaveOutcome.Saved;
            }
            catch (Exception ex)
            {
                _log.Error("Sitzung", "Sicherung fehlgeschlagen", ex);
                return SaveOutcome.Failed;
            }
        }
        HistoryChanged?.Invoke();
        return result;
    }

    private static HistoryEntry Summarize(string path, SessionSnapshot s) =>
        new(Path.GetFullPath(path), s.CreatedAt, s.Kind, s.DesktopCount, s.AppCount, s.Windows.Count, s.ContentHash);

    public IReadOnlyList<HistoryEntry> GetHistory() { lock (_lock) return GetHistoryCore(); }

    private List<HistoryEntry> GetHistoryCore()
    {
        var list = new List<HistoryEntry>();
        if (!Directory.Exists(_paths.History)) return list;
        foreach (var f in Directory.EnumerateFiles(_paths.History, "*.json"))
        {
            var full = Path.GetFullPath(f);
            if (!_summaryCache.TryGetValue(full, out var e))
            {
                var s = LoadFile(full);
                if (s is null) continue;
                e = Summarize(full, s);
                _summaryCache[full] = e;
            }
            list.Add(e);
        }
        return list.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.FilePath, StringComparer.Ordinal).ToList();
    }

    public SessionSnapshot? LoadHistory(string filePath) { lock (_lock) return LoadFile(filePath); }

    public bool DeleteHistory(string filePath)
    {
        bool ok;
        lock (_lock)
        {
            try { File.Delete(filePath); _summaryCache.Remove(Path.GetFullPath(filePath)); ok = true; }
            catch (Exception ex) { _log.Warn("Sitzung", "Verlaufseintrag konnte nicht gelöscht werden", ex); ok = false; }
        }
        if (ok) HistoryChanged?.Invoke();
        return ok;
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            foreach (var e in GetHistoryCore().Skip(1)) // die neueste bleibt immer erhalten
                try { File.Delete(e.FilePath); _summaryCache.Remove(e.FilePath); } catch { }
        }
        HistoryChanged?.Invoke();
    }

    private void ApplyRetention()
    {
        var all = GetHistoryCore();
        foreach (var e in HistoryRetention.SelectForDeletion(all, MaxHistory, _protected))
        {
            try { File.Delete(e.FilePath); _summaryCache.Remove(e.FilePath); }
            catch (Exception ex) { _log.Warn("Sitzung", "Alter Verlaufseintrag konnte nicht gelöscht werden", ex); }
        }
    }
}

public static class HistoryRetention
{
    /// <summary>
    /// Behält die neuesten <paramref name="max"/> Einträge. Geschützte Einträge werden nie gelöscht.
    /// Automatische Sicherungen werden vor manuellen entfernt, jeweils die ältesten zuerst.
    /// </summary>
    public static IReadOnlyList<HistoryEntry> SelectForDeletion(IReadOnlyList<HistoryEntry> entries, int max, ISet<string>? protectedFiles = null)
    {
        max = Math.Max(1, max);
        var ordered = entries.OrderByDescending(e => e.CreatedAt).ToList();
        if (ordered.Count <= max) return Array.Empty<HistoryEntry>();

        bool IsProtected(HistoryEntry e) => protectedFiles?.Contains(e.FilePath) == true;
        var excess = ordered.Count - max;
        return ordered.Skip(1) // die neueste nie löschen
            .Where(e => !IsProtected(e))
            .OrderBy(e => e.Kind == SessionKind.Manual ? 1 : 0)
            .ThenBy(e => e.CreatedAt)
            .Take(excess)
            .ToList();
    }

    public static string FormatRelative(DateTimeOffset time, DateTimeOffset now)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        var local = time.ToLocalTime();
        var day = local.Date;
        var today = now.ToLocalTime().Date;
        var clock = local.ToString("HH:mm", de) + " Uhr";
        if (day == today) return "Heute, " + clock;
        if (day == today.AddDays(-1)) return "Gestern, " + clock;
        if ((today - day).TotalDays < 7) return local.ToString("dddd", de) + ", " + clock;
        return local.ToString("dd.MM.yyyy", de) + ", " + clock;
    }
}
