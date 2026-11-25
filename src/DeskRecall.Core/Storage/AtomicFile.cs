using System.Text;

namespace DeskRecall.Core.Storage;

/// <summary>
/// Atomares Schreiben: Inhalt in eine temporäre Datei im selben Ordner schreiben, auf die Platte
/// durchschreiben (Flush) und dann per File.Replace (mit .bak-Sicherung) bzw. File.Move einsetzen.
/// Ein Absturz oder Stromausfall hinterlässt so entweder die alte oder die neue Datei – nie eine halbe.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, bool keepBackup = true)
        => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content), keepBackup);

    public static void WriteAllBytes(string path, byte[] bytes, bool keepBackup = true)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                var backup = keepBackup ? full + ".bak" : null;
                try
                {
                    File.Replace(tmp, full, backup, ignoreMetadataErrors: true);
                }
                catch (Exception) when (File.Exists(tmp))
                {
                    // Fallback, z. B. wenn das Dateisystem Replace nicht unterstützt (Netzlaufwerk, FAT).
                    if (backup is not null) File.Copy(full, backup, overwrite: true);
                    File.Move(tmp, full, overwrite: true);
                }
            }
            else
            {
                File.Move(tmp, full);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Liest die Datei; ist sie beschädigt/fehlt, wird die .bak-Sicherung versucht.</summary>
    public static T? ReadWithFallback<T>(string path, Func<string, T?> parse) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var text = File.ReadAllText(candidate, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var value = parse(text);
                if (value is not null) return value;
            }
            catch { /* nächste Quelle versuchen */ }
        }
        return null;
    }

    /// <summary>Entfernt liegengebliebene temporäre Dateien (z. B. nach Absturz während des Schreibens).</summary>
    public static void CleanupTemp(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var f in Directory.EnumerateFiles(directory, ".*.tmp"))
                try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-5)) File.Delete(f); } catch { }
        }
        catch { }
    }
}
