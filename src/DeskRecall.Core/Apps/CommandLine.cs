namespace DeskRecall.Core.Apps;

public static class CommandLine
{
    /// <summary>Zerlegt eine Windows-Befehlszeile nach den Regeln von CommandLineToArgvW (vereinfacht, aber kompatibel).</summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return args;
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { slashes++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) { current.Append('"'); }
                    else { inQuotes = !inQuotes; }
                    hasToken = true;
                }
                else
                {
                    current.Append('\\', slashes);
                    hasToken = true;
                    i--;
                }
                continue;
            }
            if (c == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) { args.Add(current.ToString()); current.Clear(); hasToken = false; }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken) args.Add(current.ToString());
        return args;
    }

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c); slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}

/// <summary>
/// Ermittelt aus der Befehlszeile eines Prozesses ausschließlich einen geöffneten Dokument-/Projektpfad.
/// Alles andere (Schalter, URLs, Tokens) wird verworfen und nie gespeichert.
/// </summary>
public static class DocumentPathExtractor
{
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".lnk", ".url", ".tmp", ".log", ".pak", ".asar", ".dat",
    };

    public static string? Extract(string? commandLine, string processName, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        if (AppCatalog.IsBrowser(processName)) return null; // Browser: native Sitzungswiederherstellung, keine URLs speichern
        var n = Models.AppIdentity.NormalizeProcessName(processName);
        if (n is "explorer" or "applicationframehost" or "steam" or "discord") return null;

        var args = Split(commandLine);
        foreach (var a in args.Skip(1))
        {
            if (a.Length < 4 || a.StartsWith('-') || a.StartsWith('/')) continue;
            if (a.Contains("://", StringComparison.Ordinal)) continue;
            if (!IsRootedWindowsPath(a)) continue;
            var ext = GetExtension(a);
            if (string.IsNullOrEmpty(ext) || IgnoredExtensions.Contains(ext)) continue;
            if (!fileExists(a)) continue;
            return a;
        }
        return null;
    }

    private static IReadOnlyList<string> Split(string cmd) => CommandLine.Split(cmd);

    public static bool IsRootedWindowsPath(string p) =>
        (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) || p.StartsWith(@"\\", StringComparison.Ordinal);

    private static string GetExtension(string p)
    {
        var slash = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        var dot = p.LastIndexOf('.');
        return dot > slash ? p[dot..] : "";
    }
}
