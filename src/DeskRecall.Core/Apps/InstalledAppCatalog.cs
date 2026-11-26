using DeskRecall.Core.Models;

namespace DeskRecall.Core.Apps;

/// <summary>Ein installiertes Programm aus dem Startmenü, einer UWP-Liste oder einer abgelegten Datei.</summary>
public sealed record InstalledAppEntry(
    string Name,
    string? TargetPath,
    string? ShortcutPath,
    string? AppUserModelId,
    string? WorkingDirectory,
    string? Arguments,
    string? IconPath,
    bool IsUwp)
{
    public string ProcessName => InstalledAppCatalog.ProcessNameOf(this);
    public string Key => InstalledAppCatalog.KeyOf(this);
}

/// <summary>Aufgelöste Verknüpfung (.lnk), bereitgestellt von der Plattformschicht.</summary>
public sealed record ShortcutRecord(
    string ShortcutPath,
    string Name,
    string? TargetPath,
    string? Arguments,
    string? WorkingDirectory,
    string? IconPath,
    string? AppUserModelId);

/// <summary>
/// Reine Auswertelogik für den Programm-Wähler: Startmenü-Verknüpfungen filtern,
/// UWP-AUMIDs erkennen, suchen, zusammenführen und Dateiablagen auswerten.
/// Keine Windows-COM-Aufrufe – die Auflösung von .lnk erfolgt über einen Rückruf.
/// </summary>
public static class InstalledAppCatalog
{
    static readonly string[] IgnoredNameTokens =
    {
        "uninstall", "unins000", "deinstall", "deinstallieren", "entfernen", "remove",
        "hilfe", "help", "readme", "release notes", "website", "homepage", "support",
        "documentation", "dokumentation", "lizenz", "license", "eula", "getting started",
        "einführung", "einfuehrung", "changelog", "what's new", "whats new",
        "buy", "kaufen", "register", "registrieren", "visit", "online help",
        "feedback", "privacy", "datenschutz", "donate", "spenden",
    };

    static readonly string[] IgnoredTargetExtensions =
    { ".url", ".chm", ".txt", ".html", ".htm", ".pdf", ".rtf", ".doc", ".docx", ".msi", ".bat", ".cmd" };

    /// <summary>Anzeigename einer .lnk-Datei (Dateiname ohne Endung, getrimmte Leerzeichen).</summary>
    public static string DisplayNameFromShortcut(string fileNameOrPath)
    {
        var name = WinPath.GetFileNameWithoutExtension(fileNameOrPath) ?? fileNameOrPath;
        name = name.Replace(" - Verknüpfung", "", StringComparison.OrdinalIgnoreCase)
                   .Replace(" - Shortcut", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();
        return name.Length == 0 ? "Programm" : name;
    }

    public static bool IsIgnoredShortcutName(string fileNameOrPath)
    {
        var name = (WinPath.GetFileNameWithoutExtension(fileNameOrPath) ?? fileNameOrPath).Trim();
        if (name.Length == 0) return true;
        foreach (var token in IgnoredNameTokens)
        {
            if (name.Equals(token, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)
                && (token.Length >= 6 || name.StartsWith(token, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>AUMID aus „shell:AppsFolder\Publisher.App_id!Entry“ oder einem Explorer-Aufruf damit.</summary>
    public static string? ParseAumidFromTarget(string? target, string? arguments = null)
    {
        foreach (var raw in new[] { target, arguments })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim().Trim('"');
            const string prefix = "shell:AppsFolder\\";
            var idx = s.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var rest = s[(idx + prefix.Length)..].Trim().Trim('"');
            var cut = rest.IndexOfAny([' ', '"', '\t']);
            if (cut > 0) rest = rest[..cut];
            rest = rest.TrimEnd('\\', '/');
            if (rest.Length > 0 && rest.Contains('!', StringComparison.Ordinal) || rest.Contains('_', StringComparison.Ordinal))
                return rest;
            if (rest.Length > 0) return rest;
        }
        return null;
    }

    public static bool IsLaunchableTarget(string? targetPath, string? aumid)
    {
        if (!string.IsNullOrWhiteSpace(aumid)) return true;
        if (string.IsNullOrWhiteSpace(targetPath)) return false;
        var ext = WinPath.GetExtension(targetPath);
        if (ext.Length == 0) return false;
        if (IgnoredTargetExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase))) return false;
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".com", StringComparison.OrdinalIgnoreCase))
        {
            var file = WinPath.GetFileNameWithoutExtension(targetPath) ?? "";
            if (file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) return false;
            if (file.Equals("uninstall", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        return false;
    }

    public static IReadOnlyList<InstalledAppEntry> FromShortcuts(IEnumerable<ShortcutRecord> shortcuts)
    {
        var list = new List<InstalledAppEntry>();
        foreach (var s in shortcuts)
        {
            if (IsIgnoredShortcutName(s.Name) || IsIgnoredShortcutName(s.ShortcutPath)) continue;
            var aumid = string.IsNullOrWhiteSpace(s.AppUserModelId) ? ParseAumidFromTarget(s.TargetPath, s.Arguments) : s.AppUserModelId;
            var isUwp = !string.IsNullOrWhiteSpace(aumid);
            if (!IsLaunchableTarget(s.TargetPath, aumid)) continue;
            var name = string.IsNullOrWhiteSpace(s.Name) ? DisplayNameFromShortcut(s.ShortcutPath) : s.Name.Trim();
            list.Add(new InstalledAppEntry(name, s.TargetPath, s.ShortcutPath, aumid, s.WorkingDirectory, s.Arguments, s.IconPath, isUwp));
        }
        return Deduplicate(list);
    }

    public static IReadOnlyList<InstalledAppEntry> FromUwpPackages(IEnumerable<(string Name, string Aumid, string? LogoPath)> packages)
    {
        var list = new List<InstalledAppEntry>();
        foreach (var p in packages)
        {
            if (string.IsNullOrWhiteSpace(p.Aumid) || string.IsNullOrWhiteSpace(p.Name)) continue;
            if (IsIgnoredShortcutName(p.Name)) continue;
            list.Add(new InstalledAppEntry(p.Name.Trim(), null, null, p.Aumid.Trim(), null, null, p.LogoPath, true));
        }
        return Deduplicate(list);
    }

    /// <summary>Führt Startmenü- und UWP-Einträge zusammen (gleiche AUMID oder gleiche Zieldatei → ein Eintrag, Anzeigename bleibt der erste).</summary>
    public static IReadOnlyList<InstalledAppEntry> Merge(params IEnumerable<InstalledAppEntry>[] sources)
    {
        var all = new List<InstalledAppEntry>();
        foreach (var s in sources) all.AddRange(s);
        return Deduplicate(all);
    }

    public static IReadOnlyList<InstalledAppEntry> Deduplicate(IEnumerable<InstalledAppEntry> entries)
    {
        var byKey = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var key = KeyOf(e);
            if (byKey.TryGetValue(key, out var existing))
            {
                // Vorhandenen Eintrag um fehlende Felder ergänzen; .exe-Ziel bevorzugen
                byKey[key] = existing with
                {
                    TargetPath = PreferTarget(existing.TargetPath, e.TargetPath),
                    ShortcutPath = existing.ShortcutPath ?? e.ShortcutPath,
                    AppUserModelId = existing.AppUserModelId ?? e.AppUserModelId,
                    WorkingDirectory = existing.WorkingDirectory ?? e.WorkingDirectory,
                    Arguments = existing.Arguments ?? e.Arguments,
                    IconPath = existing.IconPath ?? e.IconPath,
                    IsUwp = existing.IsUwp || e.IsUwp,
                };
            }
            else byKey[key] = e;
        }
        return byKey.Values
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.TargetPath ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string? PreferTarget(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a)) return b;
        if (string.IsNullOrWhiteSpace(b)) return a;
        var ea = WinPath.GetExtension(a);
        var eb = WinPath.GetExtension(b);
        if (!ea.Equals(".exe", StringComparison.OrdinalIgnoreCase) && eb.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return b;
        return a;
    }

    public static string KeyOf(InstalledAppEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.AppUserModelId))
            return "aumid:" + e.AppUserModelId.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(e.TargetPath))
            return "path:" + NormalizePath(e.TargetPath);
        if (!string.IsNullOrWhiteSpace(e.ShortcutPath))
            return "lnk:" + NormalizePath(e.ShortcutPath);
        return "name:" + e.Name.Trim().ToLowerInvariant();
    }

    public static string ProcessNameOf(InstalledAppEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.TargetPath))
        {
            var n = WinPath.GetFileNameWithoutExtension(e.TargetPath);
            if (!string.IsNullOrWhiteSpace(n)) return AppIdentity.NormalizeProcessName(n);
        }
        if (!string.IsNullOrWhiteSpace(e.AppUserModelId))
        {
            var a = e.AppUserModelId;
            var bang = a.LastIndexOf('!');
            var id = bang > 0 ? a[..bang] : a;
            var dot = id.LastIndexOf('.');
            return AppIdentity.NormalizeProcessName(dot >= 0 ? id[(dot + 1)..] : id);
        }
        return AppIdentity.NormalizeProcessName(e.Name);
    }

    public static string NormalizePath(string path)
        => path.Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    /// <summary>Alle Suchwörter müssen in Name, Dateiname oder AUMID vorkommen (ohne Groß-/Kleinschreibung).</summary>
    public static IReadOnlyList<InstalledAppEntry> Search(IEnumerable<InstalledAppEntry> apps, string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return apps.ToList();
        var tokens = q.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return apps.ToList();
        return apps.Where(a =>
        {
            var hay = $"{a.Name}\n{a.TargetPath}\n{a.ShortcutPath}\n{a.AppUserModelId}";
            return tokens.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    /// <summary>Wertet eine per Drag &amp; Drop oder „Durchsuchen“ gewählte Datei aus (.exe oder .lnk).</summary>
    public static InstalledAppEntry? FromDroppedPath(string path, Func<string, ShortcutRecord?>? resolveShortcut = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var ext = WinPath.GetExtension(path);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var rec = resolveShortcut?.Invoke(path);
            if (rec is not null)
            {
                var list = FromShortcuts(new[] { rec });
                return list.Count > 0 ? list[0] : new InstalledAppEntry(DisplayNameFromShortcut(path), rec.TargetPath, path, rec.AppUserModelId ?? ParseAumidFromTarget(rec.TargetPath, rec.Arguments), rec.WorkingDirectory, rec.Arguments, rec.IconPath, rec.AppUserModelId is not null);
            }
            return new InstalledAppEntry(DisplayNameFromShortcut(path), null, path, null, null, null, path, false);
        }
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".com", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLaunchableTarget(path, null)) return null;
            var name = WinPath.GetFileNameWithoutExtension(path) ?? "Programm";
            return new InstalledAppEntry(name, path, null, null, WinPath.GetDirectoryName(path), null, path, false);
        }
        return null;
    }

    /// <summary>Sammelt .lnk-Dateien unterhalb der angegebenen Startmenü-Ordner (rekursiv, ohne aufgelöste Ziele).</summary>
    public static IReadOnlyList<string> EnumerateShortcutFiles(IEnumerable<string> startMenuRoots, Func<string, IEnumerable<string>> enumerateFiles)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in startMenuRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            IEnumerable<string> files;
            try { files = enumerateFiles(root); }
            catch { continue; }
            foreach (var f in files)
            {
                if (!WinPath.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsIgnoredShortcutName(f)) continue;
                var key = NormalizePath(f);
                if (!seen.Add(key)) continue;
                found.Add(f);
            }
        }
        return found;
    }
}
