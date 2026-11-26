using DeskRecall.Core.Geometry;

namespace DeskRecall.Core.Models;

/// <summary>
/// Ein Fenster – entweder aus einer gespeicherten Sitzung oder live aus dem System.
/// Es werden ausschließlich Fensteranordnung und Startidentität erfasst, niemals Inhalte,
/// Passwörter, Cookies oder Zugangsdaten.
/// </summary>
public sealed class WindowRecord
{
    public string ProcessName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? AppUserModelId { get; set; }
    public string? DisplayName { get; set; }
    public string WindowClass { get; set; } = "";
    public string Title { get; set; } = "";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(ohne Titel)" : Title.Trim();

    /// <summary>HWND zum Zeitpunkt der Erfassung (nach Neustart ungültig, nur für Live-Abgleich).</summary>
    public long Handle { get; set; }
    public int ProcessId { get; set; }

    public Guid DesktopId { get; set; }
    public int DesktopIndex { get; set; } = -1;
    public bool IsPinned { get; set; }

    /// <summary>Normale (wiederhergestellte) Fensterposition in Bildschirmkoordinaten.</summary>
    public RectI NormalBounds { get; set; }
    public WindowShowState State { get; set; }
    /// <summary>Snap-Zone auf dem Monitor. Frei behält <see cref="NormalBounds"/>, Zonen werden beim Wiederherstellen neu aus dem Zielmonitor berechnet.</summary>
    public SnapLayout Snap { get; set; }
    public string? MonitorId { get; set; }
    public int ZOrder { get; set; }

    public bool HasCaption { get; set; } = true;
    public bool IsResizable { get; set; } = true;

    /// <summary>Unterstützter Dokument- oder Projektpfad (nur echte Dateipfade aus der Befehlszeile).</summary>
    public string? DocumentPath { get; set; }
    /// <summary>Geöffneter Ordner bei Explorer-Fenstern.</summary>
    public string? ExplorerFolderPath { get; set; }

    public AppKind Kind { get; set; }
    /// <summary>Prozess lief mit höheren Rechten (Zugriff verweigert) – wird nie ohne Zustimmung erhöht gestartet.</summary>
    public bool IsElevated { get; set; }

    public string AppKey => AppIdentity.KeyFor(this);
    public string FriendlyName => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : ProcessName;

    public WindowRecord Clone() => (WindowRecord)MemberwiseClone();
    public override string ToString() => $"{ProcessName} \"{Title}\" D{DesktopIndex + 1} {State} {NormalBounds}";
}

public static class AppIdentity
{
    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n.ToLowerInvariant();
    }

    public static string KeyFor(WindowRecord w)
    {
        if (!string.IsNullOrEmpty(w.AppUserModelId) && w.Kind == AppKind.Uwp) return "aumid:" + w.AppUserModelId!.ToLowerInvariant();
        return "proc:" + NormalizeProcessName(w.ProcessName);
    }
}
