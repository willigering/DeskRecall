using DeskRecall.Core.Models;

namespace DeskRecall.Core.Geometry;

public sealed record ArrangementMonitorTile(RectI Bounds, bool IsPrimary);

public sealed record ArrangementWindowTile(
    string AppKey,
    string AppName,
    long Handle,
    RectI Bounds,
    bool IsMinimized,
    bool IsMaximized,
    int ZOrder);

public sealed record ArrangementLayout(
    IReadOnlyList<ArrangementMonitorTile> Monitors,
    IReadOnlyList<ArrangementWindowTile> Windows,
    int Width,
    int Height);

/// <summary>
/// Skaliert Monitor- und Fensterrechtecke in eine kleine Vorschaufläche (Mini-Karte),
/// unter Beibehaltung des Seitenverhältnisses.
/// </summary>
public static class ArrangementPreview
{
    public const int DefaultWidth = 252;
    public const int DefaultHeight = 72;

    public static ArrangementLayout Layout(
        IReadOnlyList<WindowRecord> windows,
        IReadOnlyList<MonitorInfo>? monitors,
        int previewWidth,
        int previewHeight,
        int padding = 3)
    {
        var width = Math.Max(16, previewWidth);
        var height = Math.Max(16, previewHeight);
        var pad = Math.Clamp(padding, 0, Math.Min(width, height) / 4);

        var world = WorldBounds(windows, monitors);
        if (world.IsEmpty)
            return new ArrangementLayout(Array.Empty<ArrangementMonitorTile>(), Array.Empty<ArrangementWindowTile>(), width, height);

        var innerW = width - 2 * pad;
        var innerH = height - 2 * pad;
        var scale = Math.Min(innerW / (double)world.Width, innerH / (double)world.Height);
        if (scale <= 0) scale = 1;
        var contentW = world.Width * scale;
        var contentH = world.Height * scale;
        var ox = pad + (innerW - contentW) / 2.0;
        var oy = pad + (innerH - contentH) / 2.0;

        RectI Map(RectI r)
        {
            var l = (int)Math.Round(ox + (r.Left - world.Left) * scale);
            var t = (int)Math.Round(oy + (r.Top - world.Top) * scale);
            var w = Math.Max(8, (int)Math.Round(Math.Max(r.Width, 1) * scale));
            var h = Math.Max(8, (int)Math.Round(Math.Max(r.Height, 1) * scale));
            var mapped = RectI.FromSize(l, t, w, h);
            // leicht in die Vorschau einklemmen
            var l2 = Math.Clamp(mapped.Left, 0, Math.Max(0, width - 4));
            var t2 = Math.Clamp(mapped.Top, 0, Math.Max(0, height - 4));
            var r2 = Math.Clamp(mapped.Right, l2 + 4, width);
            var b2 = Math.Clamp(mapped.Bottom, t2 + 4, height);
            return new RectI(l2, t2, r2, b2);
        }

        var monTiles = new List<ArrangementMonitorTile>();
        if (monitors is not null)
        {
            foreach (var m in monitors)
            {
                if (m.Bounds.IsEmpty) continue;
                monTiles.Add(new ArrangementMonitorTile(Map(m.Bounds), m.IsPrimary));
            }
        }

        var winTiles = new List<ArrangementWindowTile>();
        foreach (var w in windows.OrderBy(w => w.ZOrder))
        {
            var bounds = w.NormalBounds;
            if (bounds.IsEmpty) continue;
            winTiles.Add(new ArrangementWindowTile(
                w.AppKey,
                string.IsNullOrWhiteSpace(w.DisplayName) ? (string.IsNullOrWhiteSpace(w.ProcessName) ? "Fenster" : w.ProcessName) : w.DisplayName!,
                w.Handle,
                Map(bounds),
                w.State == WindowShowState.Minimized,
                w.State == WindowShowState.Maximized,
                w.ZOrder));
        }

        return new ArrangementLayout(monTiles, winTiles, width, height);
    }

    public static ArrangementWindowTile? HitTest(ArrangementLayout layout, int x, int y)
    {
        for (var i = layout.Windows.Count - 1; i >= 0; i--)
            if (layout.Windows[i].Bounds.Contains(x, y)) return layout.Windows[i];
        return null;
    }

    static RectI WorldBounds(IReadOnlyList<WindowRecord> windows, IReadOnlyList<MonitorInfo>? monitors)
    {
        var has = false;
        int l = 0, t = 0, r = 0, b = 0;
        void Acc(RectI rect)
        {
            if (rect.IsEmpty) return;
            if (!has) { l = rect.Left; t = rect.Top; r = rect.Right; b = rect.Bottom; has = true; }
            else { l = Math.Min(l, rect.Left); t = Math.Min(t, rect.Top); r = Math.Max(r, rect.Right); b = Math.Max(b, rect.Bottom); }
        }
        if (monitors is not null)
            foreach (var m in monitors) Acc(m.Bounds);
        if (!has)
            foreach (var w in windows) Acc(w.NormalBounds);
        return has && r > l && b > t ? new RectI(l, t, r, b) : default;
    }
}
