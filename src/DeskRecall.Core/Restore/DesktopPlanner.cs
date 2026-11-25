using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;

namespace DeskRecall.Core.Restore;

public sealed record DesktopMapping(IReadOnlyDictionary<int, Guid> SavedIndexToCurrent, int Created, int Renamed, int Moved);

/// <summary>
/// Stellt Anzahl, Namen und Reihenfolge der virtuellen Desktops her und liefert die Zuordnung
/// gespeicherter Desktop-Positionen zu den aktuellen Desktop-GUIDs.
/// </summary>
public static class DesktopPlanner
{
    /// <summary>Reine Planung (ohne Änderungen): gleiche GUID zuerst, danach Reihenfolge.</summary>
    public static (Dictionary<int, Guid> Map, List<int> MissingSavedIndexes) Plan(IReadOnlyList<DesktopInfo> saved, IReadOnlyList<DesktopInfo> current)
    {
        var map = new Dictionary<int, Guid>();
        var usedCurrent = new HashSet<Guid>();
        var savedOrdered = saved.OrderBy(d => d.Index).ToList();

        foreach (var s in savedOrdered)
        {
            var same = current.FirstOrDefault(c => c.Id == s.Id && s.Id != Guid.Empty);
            if (same is null) continue;
            map[s.Index] = same.Id;
            usedCurrent.Add(same.Id);
        }

        var freeCurrent = new Queue<DesktopInfo>(current.OrderBy(c => c.Index).Where(c => !usedCurrent.Contains(c.Id)));
        var missing = new List<int>();
        foreach (var s in savedOrdered.Where(s => !map.ContainsKey(s.Index)))
        {
            if (freeCurrent.Count > 0) map[s.Index] = freeCurrent.Dequeue().Id;
            else missing.Add(s.Index);
        }
        return (map, missing);
    }

    public static DesktopMapping Apply(IVirtualDesktopService vd, IReadOnlyList<DesktopInfo> saved, bool createMissing, bool restoreNamesAndOrder, bool dryRun, ILogService log)
    {
        var current = vd.GetDesktops();
        if (saved.Count == 0 || current.Count == 0)
            return new DesktopMapping(new Dictionary<int, Guid>(), 0, 0, 0);

        var (map, missing) = Plan(saved, current);
        int created = 0, renamed = 0, moved = 0;

        if (missing.Count > 0)
        {
            if (createMissing && vd.CanManage)
            {
                foreach (var idx in missing)
                {
                    var name = saved.First(s => s.Index == idx).Name;
                    if (dryRun) { log.Info("Desktops", $"[Testlauf] Desktop {idx + 1} „{name}“ würde angelegt."); continue; }
                    var d = vd.CreateDesktop(restoreNamesAndOrder ? name : null);
                    if (d is null) { log.Warn("Desktops", $"Desktop {idx + 1} konnte nicht angelegt werden."); continue; }
                    map[idx] = d.Id;
                    created++;
                    log.Info("Desktops", $"Desktop {idx + 1} „{name}“ angelegt.");
                }
            }
            else
            {
                log.Warn("Desktops", $"{missing.Count} gespeicherte Desktops fehlen und werden nicht angelegt – Fenster bleiben auf dem aktuellen Desktop.");
            }
        }

        if (restoreNamesAndOrder && vd.CanManage && !dryRun)
        {
            // Reihenfolge: gespeicherte Position i ← zugeordneter Desktop (wie Selection-Sort, je ein MoveDesktop)
            var targetOrder = map.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            for (var i = 0; i < targetOrder.Count; i++)
            {
                var now = vd.GetDesktops();
                var pos = now.ToList().FindIndex(d => d.Id == targetOrder[i]);
                if (pos >= 0 && pos != i && i < now.Count)
                {
                    if (vd.MoveDesktop(targetOrder[i], i)) moved++;
                    else log.Warn("Desktops", $"Reihenfolge von Desktop {i + 1} konnte nicht gesetzt werden.");
                }
            }

            var after = vd.GetDesktops();
            foreach (var (savedIndex, id) in map)
            {
                var want = saved.First(s => s.Index == savedIndex).Name;
                if (string.IsNullOrWhiteSpace(want)) continue;
                var cur = after.FirstOrDefault(d => d.Id == id);
                if (cur is null || cur.Name == want) continue;
                if (vd.RenameDesktop(id, want)) renamed++;
            }
        }

        if (created + renamed + moved > 0)
            log.Info("Desktops", $"Desktops abgeglichen: {created} angelegt, {renamed} umbenannt, {moved} verschoben.");
        return new DesktopMapping(map, created, renamed, moved);
    }
}
