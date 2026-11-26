using DeskRecall.Core.Models;

namespace DeskRecall.Core.Rules;

public enum RuleStartState { Unchanged, Normal, Minimized, Maximized }

/// <summary>Feste Programmregel. Regeln haben bei Konflikten Vorrang vor der gespeicherten Sitzung.</summary>
public sealed class AppRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";

    // --- Bedingung ---
    /// <summary>Prozessname, z. B. "steam" oder "steam.exe".</summary>
    public string ProcessName { get; set; } = "";
    /// <summary>Optional: Fenstertitel muss diesen Text enthalten (ohne Groß-/Kleinschreibung).</summary>
    public string? TitleContains { get; set; }

    // --- Aktionen ---
    /// <summary>1-basierte Desktop-Nummer, null = unverändert.</summary>
    public int? TargetDesktop { get; set; }
    /// <summary>Monitor-Id, null = unverändert.</summary>
    public string? TargetMonitorId { get; set; }
    public RuleStartState StartState { get; set; } = RuleStartState.Unchanged;
    public bool NeverAutoStart { get; set; }
    /// <summary>Komplett ignorieren (weder sichern noch wiederherstellen).</summary>
    public bool Exclude { get; set; }
    /// <summary>Regel auch während der normalen Nutzung auf neue Fenster anwenden.</summary>
    public bool ApplyLive { get; set; }

    public string? IconPath { get; set; }
    /// <summary>Vollständiger Pfad der ausführbaren Datei (zum Starten angehefteter Programme).</summary>
    public string? ExecutablePath { get; set; }
    /// <summary>AUMID für UWP- oder Store-Apps.</summary>
    public string? AppUserModelId { get; set; }
    /// <summary>Vom Benutzer fest auf einen Desktop geheftet: dort starten und bei Bedarf hin verschieben.</summary>
    public bool IsPinnedEntry { get; set; }

    public AppRule Clone() => (AppRule)MemberwiseClone();

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Exclude) parts.Add("Ausgeschlossen");
            if (TargetDesktop is int d) parts.Add($"→ Desktop {d}");
            if (StartState == RuleStartState.Minimized) parts.Add("Minimiert");
            if (StartState == RuleStartState.Maximized) parts.Add("Maximiert");
            if (StartState == RuleStartState.Normal) parts.Add("Normal");
            if (!string.IsNullOrEmpty(TargetMonitorId)) parts.Add("Fester Monitor");
            if (NeverAutoStart) parts.Add("Nie automatisch starten");
            return parts.Count == 0 ? "Keine Aktion" : string.Join(" · ", parts);
        }
    }
}

/// <summary>Ergebnis der Regelauswertung für ein Fenster.</summary>
public sealed class RuleOutcome
{
    public static readonly RuleOutcome None = new();

    public int? TargetDesktopIndex { get; init; }
    public string? TargetMonitorId { get; init; }
    public WindowShowState? ForcedState { get; init; }
    public bool NeverAutoStart { get; init; }
    public bool Excluded { get; init; }
    public bool ApplyLive { get; init; }
    public IReadOnlyList<Guid> MatchedRules { get; init; } = Array.Empty<Guid>();
    public bool HasAny => MatchedRules.Count > 0;
}
