namespace DeskRecall.Core.Models;

/// <summary>Klickverhalten einer App-Zeile: mehrere Fenster aufklappen, einzelnes fokussieren.</summary>
public static class AppRowInput
{
    public enum RowAction { None, ActivateApp, ToggleWindows, ActivateWindow }

    /// <summary>Klick auf die App-Zeile (nicht auf eine Unterzeile).</summary>
    public static RowAction OnAppRowClick(int windowCount, bool onSubRow = false)
    {
        if (onSubRow) return RowAction.ActivateWindow;
        return windowCount > 1 ? RowAction.ToggleWindows : RowAction.ActivateApp;
    }

    /// <summary>Aufgeklappter Zustand bleibt nur erhalten, solange noch mehrere Fenster da sind.</summary>
    public static bool ShouldKeepExpanded(int windowCount, bool wasExpanded) => wasExpanded && windowCount > 1;
}
