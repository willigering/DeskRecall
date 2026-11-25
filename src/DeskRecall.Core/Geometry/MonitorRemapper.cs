using DeskRecall.Core.Models;

namespace DeskRecall.Core.Geometry;

/// <summary>
/// Ordnet gespeicherte Monitore den aktuell angeschlossenen zu (Monitor entfernt, Reihenfolge geändert,
/// Auflösung geändert) und rechnet Fensterpositionen um.
/// </summary>
public static class MonitorRemapper
{
    public static MonitorInfo? FindCurrent(MonitorInfo? saved, IReadOnlyList<MonitorInfo> current, MonitorFallback fallback = MonitorFallback.Primary)
    {
        if (current.Count == 0) return null;
        var primary = current.FirstOrDefault(m => m.IsPrimary) ?? current[0];
        if (saved is null) return primary;

        // 1. Gleiche stabile Geräte-Id
        var byId = current.FirstOrDefault(m => !string.IsNullOrEmpty(saved.Id) && string.Equals(m.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        // 2. Gleicher GDI-Name und gleiche Auflösung
        var byName = current.FirstOrDefault(m => string.Equals(m.DeviceName, saved.DeviceName, StringComparison.OrdinalIgnoreCase)
                                                 && m.Bounds.Width == saved.Bounds.Width && m.Bounds.Height == saved.Bounds.Height);
        if (byName is not null) return byName;
        // 3. Exakt gleiche Lage im virtuellen Bildschirm
        var byBounds = current.FirstOrDefault(m => m.Bounds == saved.Bounds);
        if (byBounds is not null) return byBounds;
        // 4. Primär bleibt primär
        if (saved.IsPrimary) return primary;
        // 5. Gleicher Name (Auflösung geändert)
        var byNameOnly = current.FirstOrDefault(m => string.Equals(m.DeviceName, saved.DeviceName, StringComparison.OrdinalIgnoreCase) && !m.IsPrimary);
        if (byNameOnly is not null) return byNameOnly;

        if (fallback == MonitorFallback.Nearest)
            return current.OrderBy(m => m.Bounds.DistanceTo(saved.Bounds.CenterX, saved.Bounds.CenterY)).First();
        return primary;
    }

    /// <summary>
    /// Überträgt ein Fensterrechteck vom Arbeitsbereich des alten Monitors in den des neuen.
    /// Gleiche Größe → reine Verschiebung; andere Größe → proportionale Skalierung (optional).
    /// </summary>
    public static RectI Transform(RectI rect, RectI fromWork, RectI toWork, bool scale = true)
    {
        if (fromWork.IsEmpty || toWork.IsEmpty) return rect;
        if (fromWork == toWork) return rect;
        if (!scale || (fromWork.Width == toWork.Width && fromWork.Height == toWork.Height))
            return rect.Offset(toWork.Left - fromWork.Left, toWork.Top - fromWork.Top);

        var sx = (double)toWork.Width / fromWork.Width;
        var sy = (double)toWork.Height / fromWork.Height;
        var left = toWork.Left + (int)Math.Round((rect.Left - fromWork.Left) * sx);
        var top = toWork.Top + (int)Math.Round((rect.Top - fromWork.Top) * sy);
        var width = Math.Max(160, (int)Math.Round(rect.Width * sx));
        var height = Math.Max(120, (int)Math.Round(rect.Height * sy));
        return RectI.FromSize(left, top, width, height);
    }

    public static MonitorInfo? MonitorForRect(RectI rect, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return null;
        var best = monitors.OrderByDescending(m => m.Bounds.Intersect(rect).Area).First();
        if (best.Bounds.Intersect(rect).Area > 0) return best;
        return monitors.OrderBy(m => m.Bounds.DistanceTo(rect.CenterX, rect.CenterY)).First();
    }
}
