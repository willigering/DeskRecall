using System.Text.RegularExpressions;
using DeskRecall.Core.Models;

namespace DeskRecall.Core.Apps;

/// <summary>
/// Teams öffnet beim Start nur das Hauptfenster. Herausgelöster Chat und Besprechungsfenster
/// kommen nicht von allein zurück und dürfen die Wiederherstellung nicht aufhalten.
/// </summary>
public static class TeamsWindows
{
    static readonly Regex Badge = new(@"^\(\d+\+?\)\s*", RegexOptions.CultureInvariant);
    static readonly Regex PreviewSuffix = new(@"^Microsoft Teams\s*\([^)]*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsTeamsProcess(string? processName)
    {
        var n = AppIdentity.NormalizeProcessName(processName);
        return n is "ms-teams" or "teams";
    }

    /// <summary>Fenstertitel des Teams-Hauptfensters, inklusive Hinweis-Zähler und Vorschau-Zusatz.</summary>
    public static bool IsMainTitle(string? title)
    {
        var t = NormalizeTitle(title);
        if (t.Length == 0) return false;
        if (t.Equals("Teams", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase)) return true;
        return PreviewSuffix.IsMatch(t);
    }

    /// <summary>Das Hauptfenster. Anruf- und Chatfenster zählen nicht, auch wenn sie größer sind.</summary>
    public static WindowRecord? ChooseMain(IReadOnlyList<WindowRecord> windows)
    {
        var titled = windows.Where(w => IsTeamsProcess(w.ProcessName) && IsMainTitle(w.Title)).ToList();
        if (titled.Count == 0) return null;
        return titled.OrderByDescending(w => w.NormalBounds.Area).ThenBy(w => w.ZOrder).First();
    }

    /// <summary>Nur Fenster, die ein späterer Start wieder öffnen kann. Teams-Anrufe und -Chats fallen weg.</summary>
    public static (List<WindowRecord> Kept, List<WindowRecord> Omitted) OmitUnrestorable(IReadOnlyList<WindowRecord> windows)
    {
        var main = ChooseMain(windows);
        var kept = new List<WindowRecord>(windows.Count);
        var omitted = new List<WindowRecord>();
        foreach (var w in windows)
        {
            if (!IsTeamsProcess(w.ProcessName) || ReferenceEquals(w, main)) kept.Add(w);
            else omitted.Add(w);
        }
        return (kept, omitted);
    }

    static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.Trim().Trim('\u200e', '\u200f', '\u202a', '\u202c');
        t = Badge.Replace(t, "");
        return t.TrimStart('●', '•', '*', ' ', '\u200e').Trim();
    }
}
