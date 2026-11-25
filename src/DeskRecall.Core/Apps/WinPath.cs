namespace DeskRecall.Core.Apps;

/// <summary>
/// Pfad-Helfer für Windows-Pfade, die unabhängig vom Host-Betriebssystem funktionieren
/// (damit die Kernlogik auch unter Linux-CI testbar ist). Trennt an '\' und '/'.
/// </summary>
public static class WinPath
{
    private static int LastSep(string p) => Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));

    public static string GetFileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var i = LastSep(path);
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string GetFileNameWithoutExtension(string? path)
    {
        var f = GetFileName(path);
        var dot = f.LastIndexOf('.');
        return dot <= 0 ? f : f[..dot];
    }

    public static string? GetDirectoryName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var t = path.TrimEnd('\\', '/');
        var i = LastSep(t);
        if (i < 0) return null;
        var dir = t[..i];
        if (dir.Length == 2 && dir[1] == ':') return dir + "\\"; // "C:" → "C:\"
        return dir.Length == 0 ? null : dir;
    }

    public static string Combine(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) return name;
        var sep = dir.Contains('/') && !dir.Contains('\\') ? '/' : '\\';
        return dir.EndsWith('\\') || dir.EndsWith('/') ? dir + name : dir + sep + name;
    }
}
