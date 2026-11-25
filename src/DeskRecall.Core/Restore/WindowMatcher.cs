using DeskRecall.Core.Models;

namespace DeskRecall.Core.Restore;

/// <summary>
/// Ordnet gespeicherte Fenster laufenden Fenstern zu. Wichtig bei Browsern und Programmen mit mehreren
/// Fenstern: Titel-Ähnlichkeit, Fensterklasse, Ordner-/Dokumentpfad und Größe entscheiden.
/// </summary>
public static class WindowMatcher
{
    public const double MinScore = 0;

    public static double? Score(WindowRecord saved, WindowRecord live)
    {
        var sameProcess = AppIdentity.NormalizeProcessName(saved.ProcessName) == AppIdentity.NormalizeProcessName(live.ProcessName);
        var sameAumid = !string.IsNullOrEmpty(saved.AppUserModelId) && string.Equals(saved.AppUserModelId, live.AppUserModelId, StringComparison.OrdinalIgnoreCase);
        if (!sameProcess && !sameAumid) return null;

        double score = 10;
        if (saved.Handle != 0 && saved.Handle == live.Handle && saved.ProcessId == live.ProcessId) score += 1000;

        if (!string.IsNullOrEmpty(saved.WindowClass) && !string.IsNullOrEmpty(live.WindowClass))
            score += string.Equals(saved.WindowClass, live.WindowClass, StringComparison.Ordinal) ? 25 : -30;

        if (!string.IsNullOrEmpty(saved.ExplorerFolderPath) || !string.IsNullOrEmpty(live.ExplorerFolderPath))
        {
            if (string.Equals(NormalizePath(saved.ExplorerFolderPath), NormalizePath(live.ExplorerFolderPath), StringComparison.OrdinalIgnoreCase)) score += 80;
            else score -= 40;
        }

        if (!string.IsNullOrEmpty(saved.DocumentPath) && string.Equals(saved.DocumentPath, live.DocumentPath, StringComparison.OrdinalIgnoreCase)) score += 60;
        if (sameAumid) score += 15;

        if (!string.IsNullOrEmpty(saved.Title) && !string.IsNullOrEmpty(live.Title))
        {
            if (string.Equals(saved.Title, live.Title, StringComparison.Ordinal)) score += 40;
            else score += TitleSimilarity(saved.Title, live.Title) * 35;
        }

        if (saved.MonitorId is not null && saved.MonitorId == live.MonitorId) score += 3;
        if (Math.Abs(saved.NormalBounds.Width - live.NormalBounds.Width) <= 8 && Math.Abs(saved.NormalBounds.Height - live.NormalBounds.Height) <= 8) score += 8;
        if (Math.Abs(saved.NormalBounds.Left - live.NormalBounds.Left) <= 8 && Math.Abs(saved.NormalBounds.Top - live.NormalBounds.Top) <= 8) score += 4;
        if (saved.DesktopId != Guid.Empty && saved.DesktopId == live.DesktopId) score += 2;
        if (saved.State == live.State) score += 1;
        return score;
    }

    /// <summary>Wort-basierte Jaccard-Ähnlichkeit (0…1), robust gegen wechselnde Tab-Titel-Präfixe.</summary>
    public static double TitleSimilarity(string a, string b)
    {
        var ta = Tokens(a); var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var inter = ta.Count(tb.Contains);
        var union = ta.Count + tb.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }

    private static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder();
        foreach (var c in s + " ")
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); continue; }
            if (sb.Length >= 1) set.Add(sb.ToString());
            sb.Clear();
        }
        return set;
    }

    private static string NormalizePath(string? p) => (p ?? "").TrimEnd('\\', '/');

    /// <summary>
    /// Eindeutige Zuordnung (jedes Live-Fenster höchstens einmal) per Greedy nach absteigender Punktzahl.
    /// Liefert Paare (Index in <paramref name="saved"/>, Live-Fenster).
    /// </summary>
    public static IReadOnlyList<(int SavedIndex, WindowRecord Live, double Score)> Match(
        IReadOnlyList<WindowRecord> saved, IReadOnlyList<WindowRecord> live, ISet<long>? excludedHandles = null, double minScore = MinScore)
    {
        var pairs = new List<(int s, int l, double score)>();
        for (var i = 0; i < saved.Count; i++)
            for (var j = 0; j < live.Count; j++)
            {
                if (excludedHandles?.Contains(live[j].Handle) == true) continue;
                var sc = Score(saved[i], live[j]);
                if (sc is double v && v >= minScore) pairs.Add((i, j, v));
            }

        var usedS = new HashSet<int>(); var usedL = new HashSet<int>();
        var result = new List<(int, WindowRecord, double)>();
        foreach (var p in pairs.OrderByDescending(p => p.score).ThenBy(p => p.s).ThenBy(p => live[p.l].ZOrder))
        {
            if (usedS.Contains(p.s) || usedL.Contains(p.l)) continue;
            usedS.Add(p.s); usedL.Add(p.l);
            result.Add((p.s, live[p.l], p.score));
        }
        return result;
    }
}
