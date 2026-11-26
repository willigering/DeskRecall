using System.Text.RegularExpressions;

namespace DeskRecall.Core.Browsers;

public enum BrowserFamily { None, Chromium, Firefox }

/// <summary>Ein Tab eines Browserfensters, wie ihn die Bedienungshilfen (UI Automation) melden.</summary>
public sealed record BrowserTab(int Index, string Title, bool IsSelected);

/// <summary>Liest und wählt Browser-Tabs. Implementierung über UI Automation; muss fehlertolerant sein.</summary>
public interface ITabReader
{
    /// <summary>Tabs des Fensters oder <c>null</c>, wenn sie nicht gelesen werden konnten.</summary>
    IReadOnlyList<BrowserTab>? ReadTabs(long hwnd, BrowserFamily family);
    /// <summary>Wählt den Tab aus (SelectionItem/Invoke). Liefert false, wenn das nicht möglich war.</summary>
    bool SelectTab(long hwnd, BrowserFamily family, BrowserTab tab);
}

/// <summary>Erkennt Browserfenster (Chromium-basiert und Firefox-basiert).</summary>
public static class BrowserCatalog
{
    static readonly HashSet<string> Chromium = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "opera_gx", "chromium", "thorium", "yandex", "arc", "sidekick", "whale", "iridium", "ungoogled-chromium",
    };
    static readonly HashSet<string> Firefox = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "waterfox", "librewolf", "floorp", "zen", "palemoon", "mullvadbrowser", "tor",
    };

    public static BrowserFamily Detect(string? processName, string? windowClass)
    {
        var p = Normalize(processName);
        if (p.Length > 0 && Chromium.Contains(p)) return windowClass is null or "Chrome_WidgetWin_1" ? BrowserFamily.Chromium : BrowserFamily.None;
        if (p.Length > 0 && Firefox.Contains(p)) return windowClass is null or "MozillaWindowClass" ? BrowserFamily.Firefox : BrowserFamily.None;
        return BrowserFamily.None;
    }

    static string Normalize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";
        var p = processName.Trim();
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        return p;
    }
}

/// <summary>Vereinheitlicht Tab-Titel für die Zuordnung von lokalen Namen.</summary>
public static class TabTitle
{
    static readonly Regex Counter = new(@"^\(\d+\+?\)\s*", RegexOptions.CultureInvariant);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    static readonly string[] Bullets = { "● ", "• ", "* " };

    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = Spaces.Replace(title.Trim(), " ");
        foreach (var b in Bullets)
            if (t.StartsWith(b, StringComparison.Ordinal)) t = t[b.Length..].TrimStart();
        t = Counter.Replace(t, "");
        return t.Trim();
    }

    public static string Display(string? title) => string.IsNullOrWhiteSpace(title) ? "(ohne Titel)" : title.Trim();
}

/// <summary>Wann die Tabs eines Fensters (erneut) gelesen werden – gedrosselt und mit Rückzug bei Fehlern.</summary>
public sealed class TabReadState
{
    public string WindowTitle { get; set; } = "";
    public DateTimeOffset ReadAt { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset BackoffUntil { get; set; }
    public IReadOnlyList<BrowserTab> Tabs { get; set; } = Array.Empty<BrowserTab>();
}

public static class TabReadPolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    public static bool ShouldRead(TabReadState? state, string windowTitle, DateTimeOffset now)
    {
        if (state is null) return true;
        if (now < state.BackoffUntil) return false;
        if (!string.Equals(state.WindowTitle, windowTitle, StringComparison.Ordinal)) return true;
        return now - state.ReadAt >= Interval;
    }

    public static void RecordSuccess(TabReadState state, string windowTitle, IReadOnlyList<BrowserTab> tabs, DateTimeOffset now)
    {
        state.WindowTitle = windowTitle;
        state.ReadAt = now;
        state.Tabs = tabs;
        state.Failures = 0;
        state.BackoffUntil = default;
    }

    public static void RecordFailure(TabReadState state, string windowTitle, DateTimeOffset now)
    {
        state.WindowTitle = windowTitle;
        state.ReadAt = now;
        state.Failures++;
        var seconds = Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(state.Failures, 6)));
        state.BackoffUntil = now + TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Gleiche Tabs (Reihenfolge, Titel, Auswahl)?</summary>
    public static bool SameTabs(IReadOnlyList<BrowserTab> a, IReadOnlyList<BrowserTab> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
