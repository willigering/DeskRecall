using DeskRecall.Core.Models;

namespace DeskRecall.Core.Session;

/// <summary>Eine App, die in DeskRecall nur für die laufende Sitzung geschlossen wurde.</summary>
public sealed class SessionClosure
{
    public required string AppKey { get; init; }
    public required string AppName { get; init; }
    public Guid DesktopId { get; init; }
    public int DesktopIndex { get; init; }
    public string? ExecutablePath { get; init; }
    public string? AppUserModelId { get; init; }
    public List<WindowRecord> Windows { get; init; } = new();
}

/// <summary>
/// Merkt sich manuell geschlossene Apps, bis sie wieder laufen oder DeskRecall beendet wird.
/// Sicherungen der letzten Sitzung bekommen die Fenster wieder angehängt, damit der nächste Start sie öffnet.
/// Ein Snapshot (der benannte Startzustand) tut das nicht: der zählt nur, was gerade wirklich offen ist.
/// </summary>
public sealed class SessionClosures
{
    private readonly object _lock = new();
    private readonly List<SessionClosure> _items = new();
    private readonly HashSet<long> _closedHandles = new();

    public void Remember(SessionClosure closure)
    {
        lock (_lock)
        {
            _items.RemoveAll(c => c.AppKey == closure.AppKey && c.DesktopId == closure.DesktopId);
            _items.Add(closure);
            foreach (var w in closure.Windows)
                if (w.Handle != 0) _closedHandles.Add(w.Handle);
        }
    }

    /// <summary>Ein neues Fenster derselben App auf demselben Desktop hebt das Schließen auf. Das gerade geschlossene Handle zählt nicht.</summary>
    public void ForgetRunning(IEnumerable<WindowRecord> live)
    {
        var list = live as IReadOnlyList<WindowRecord> ?? live.ToList();
        lock (_lock)
        {
            var fresh = list.Where(w => w.Handle != 0 && !_closedHandles.Contains(w.Handle))
                .Select(w => (w.AppKey, w.DesktopId)).ToHashSet();
            _items.RemoveAll(c => fresh.Contains((c.AppKey, c.DesktopId)));
            var alive = list.Select(w => w.Handle).ToHashSet();
            _closedHandles.RemoveWhere(h => !alive.Contains(h));
        }
    }

    public IReadOnlyList<SessionClosure> ForDesktop(Guid desktopId, int desktopIndex)
    {
        lock (_lock)
        {
            return _items.Where(c => c.DesktopId == desktopId || (c.DesktopId == Guid.Empty && c.DesktopIndex == desktopIndex)).ToList();
        }
    }

    public void MergeInto(SessionSnapshot snapshot)
    {
        lock (_lock)
        {
            foreach (var c in _items)
            {
                if (snapshot.Windows.Any(w => w.AppKey == c.AppKey && SameDesktop(w, c))) continue;
                foreach (var src in c.Windows)
                {
                    var w = src.Clone();
                    w.Handle = 0;
                    w.ProcessId = 0;
                    snapshot.Windows.Add(w);
                }
            }
        }
    }

    private static bool SameDesktop(WindowRecord w, SessionClosure c)
    {
        if (c.DesktopId != Guid.Empty && w.DesktopId == c.DesktopId) return true;
        return c.DesktopIndex >= 0 && w.DesktopIndex == c.DesktopIndex;
    }
}
