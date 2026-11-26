namespace DeskRecall.Core.Models;

/// <summary>
/// Klickverhalten einer App-Zeile: die Zeile selbst aktiviert das zuletzt verwendete Fenster
/// (bzw. startet eine angeheftete, nicht laufende App). Chevron und Fensterzahl klappen nur die Liste auf.
/// </summary>
public static class AppRowInput
{
    public enum RowAction { None, ActivateApp, ToggleWindows, ActivateWindow }

    /// <summary>Namen der Elemente, die nur die Fensterliste umschalten (nicht die App aktivieren).</summary>
    public static bool IsToggleChromeName(string? name) => name is "CountBadge" or "AppChevron" or "WinChevron" or "TabBadge";

    /// <summary>Aufklappbar bei mehreren Fenstern – oder bei einem Browserfenster mit mehreren Tabs.</summary>
    public static bool CanExpand(int windowCount, int tabCount = 0) => windowCount > 1 || (windowCount == 1 && tabCount > 1);

    /// <summary>Klick auf die App-Zeile oder eine Unterzeile.</summary>
    public static RowAction OnAppRowClick(int windowCount, bool onSubRow = false, bool onToggleChrome = false, int tabCount = 0)
    {
        if (onSubRow) return RowAction.ActivateWindow;
        if (onToggleChrome && CanExpand(windowCount, tabCount)) return RowAction.ToggleWindows;
        return RowAction.ActivateApp;
    }

    public enum WindowRowAction { ActivateWindow, ToggleTabs }

    /// <summary>Klick auf eine Fenster-Unterzeile: der Pfeil klappt die Tabs auf, sonst wird das Fenster geholt.</summary>
    public static WindowRowAction OnWindowRowClick(int tabCount, bool onToggleChrome)
        => onToggleChrome && tabCount > 1 ? WindowRowAction.ToggleTabs : WindowRowAction.ActivateWindow;

    /// <summary>Das zuletzt in den Vordergrund geholte Fenster (kleinste Z-Ordnung, oben in der EnumWindows-Reihenfolge).</summary>
    public static WindowRecord? MostRecent(IEnumerable<WindowRecord> windows)
    {
        WindowRecord? best = null;
        foreach (var w in windows)
            if (best is null || w.ZOrder < best.ZOrder) best = w;
        return best;
    }

    /// <summary>Aufgeklappter Zustand bleibt nur erhalten, solange noch mehrere Fenster da sind.</summary>
    public static bool ShouldKeepExpanded(int windowCount, bool wasExpanded, int tabCount = 0) => wasExpanded && CanExpand(windowCount, tabCount);
}
