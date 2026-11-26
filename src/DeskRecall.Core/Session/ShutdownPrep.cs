using DeskRecall.Core.Apps;
using DeskRecall.Core.Browsers;
using DeskRecall.Core.Models;

namespace DeskRecall.Core.Session;

/// <summary>
/// Welche Fenster vor dem Herunterfahren ein WM_CLOSE bekommen.
/// Systemprozesse und DeskRecall selbst bleiben außen vor: Windows beendet sie mit der Sitzung,
/// und ein Schließen von DeskRecall würde das Herunterfahren abbrechen, bevor es ausgelöst ist.
/// </summary>
public static class ShutdownPrep
{
    /// <summary>Zeit, in der Programme ihre Sitzung schreiben oder einen Speichern-Dialog zeigen können.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(12);

    public static bool ShouldClose(WindowRecord window)
    {
        if (window.Handle == 0) return false;
        if (AppIdentity.NormalizeProcessName(window.ProcessName).Length == 0) return false;
        if (AppCatalog.IsNeverLaunch(window.ProcessName)) return false;
        // Browser nicht Fenster für Fenster schließen: Chromium/Firefox würden die früh geschlossenen Fenster
        // als „vom Benutzer geschlossen“ werten und nach dem Neustart nur das letzte Fenster wiederherstellen.
        // Beim Herunterfahren von Windows sichern sie dagegen alle Fenster samt Tabs.
        if (BrowserCatalog.Detect(window.ProcessName, window.WindowClass) != BrowserFamily.None) return false;
        return true;
    }

    /// <summary>Jedes Fenster genau einmal, in der gegebenen Reihenfolge.</summary>
    public static IReadOnlyList<WindowRecord> Select(IEnumerable<WindowRecord> windows)
    {
        var seen = new HashSet<long>();
        var list = new List<WindowRecord>();
        foreach (var w in windows)
        {
            if (!ShouldClose(w) || !seen.Add(w.Handle)) continue;
            list.Add(w);
        }
        return list;
    }
}
