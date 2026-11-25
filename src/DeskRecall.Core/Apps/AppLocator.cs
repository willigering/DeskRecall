using DeskRecall.Core.Models;

namespace DeskRecall.Core.Apps;

public sealed record ResolvedLaunch(string FileName, string? Arguments, string? Note);

/// <summary>
/// Findet die Startdatei eines Programms auch dann, wenn sich der Pfad geändert hat
/// (z. B. Squirrel-Updates von Discord: …\Discord\app-1.0.9175\Discord.exe → app-1.0.9180).
/// </summary>
public sealed class AppLocator
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IEnumerable<string>> _listDirectories;
    private readonly Func<string, string?>? _appPathLookup;

    public AppLocator(Func<string, bool> fileExists, Func<string, IEnumerable<string>> listDirectories, Func<string, string?>? appPathLookup = null)
    {
        _fileExists = fileExists;
        _listDirectories = listDirectories;
        _appPathLookup = appPathLookup;
    }

    public static AppLocator CreateDefault(Func<string, string?>? appPathLookup = null) => new(
        File.Exists,
        d => Directory.Exists(d) ? Directory.EnumerateDirectories(d) : Array.Empty<string>(),
        appPathLookup);

    public ResolvedLaunch? Resolve(WindowRecord w)
    {
        var path = w.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path)) return LookupByName(w.ProcessName);

        var squirrel = TrySquirrel(path);
        if (squirrel is not null) return squirrel;
        if (_fileExists(path)) return new ResolvedLaunch(path, null, null);

        var versioned = TryVersionedSibling(path);
        if (versioned is not null) return new ResolvedLaunch(versioned, null, "Pfad geändert (neue Programmversion)");

        var byName = LookupByName(WinPath.GetFileName(path));
        return byName is null ? null : byName with { Note = "Pfad geändert (über Registrierung gefunden)" };
    }

    private ResolvedLaunch? LookupByName(string processOrFile)
    {
        if (_appPathLookup is null || string.IsNullOrWhiteSpace(processOrFile)) return null;
        var file = processOrFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processOrFile : processOrFile + ".exe";
        var found = _appPathLookup(file);
        return found is not null && _fileExists(found) ? new ResolvedLaunch(found, null, null) : null;
    }

    /// <summary>Squirrel-Apps (Discord, Teams classic, GitHub Desktop …) über Update.exe --processStart starten.</summary>
    private ResolvedLaunch? TrySquirrel(string exePath)
    {
        var dir = WinPath.GetDirectoryName(exePath);
        if (dir is null) return null;
        var dirName = WinPath.GetFileName(dir);
        if (!dirName.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) return null;
        var root = WinPath.GetDirectoryName(dir);
        if (root is null) return null;
        var update = WinPath.Combine(root, "Update.exe");
        if (!_fileExists(update)) return null;
        return new ResolvedLaunch(update, "--processStart " + CommandLine.Quote(WinPath.GetFileName(exePath)), "Squirrel-Start");
    }

    /// <summary>Sucht in versionierten Nachbarordnern (app-1.2.3, 1.2.3, v1.2.3 …) die neueste Version.</summary>
    internal string? TryVersionedSibling(string exePath)
    {
        var dir = WinPath.GetDirectoryName(exePath);
        var root = dir is null ? null : WinPath.GetDirectoryName(dir);
        if (dir is null || root is null) return null;
        var fileName = WinPath.GetFileName(exePath);
        if (ParseVersion(WinPath.GetFileName(dir)) is null) return null;

        string? best = null; Version? bestVer = null;
        foreach (var sub in _listDirectories(root))
        {
            var v = ParseVersion(WinPath.GetFileName(sub));
            if (v is null) continue;
            var candidate = WinPath.Combine(sub, fileName);
            if (!_fileExists(candidate)) continue;
            if (bestVer is null || v > bestVer) { best = candidate; bestVer = v; }
        }
        return best;
    }

    internal static Version? ParseVersion(string name)
    {
        var s = name;
        if (s.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        else if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].Trim('.');
        if (!s.Contains('.')) return null;
        return Version.TryParse(s, out var v) ? v : null;
    }
}
