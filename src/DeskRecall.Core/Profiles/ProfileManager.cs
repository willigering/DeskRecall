using System.Text;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Storage;

namespace DeskRecall.Core.Profiles;

public interface IProfileManager
{
    IReadOnlyList<SessionProfile> List();
    SessionProfile? Get(string name);
    SessionProfile Create(string name, SessionSnapshot snapshot);
    SessionProfile Update(string name, SessionSnapshot snapshot);
    SessionProfile Rename(string oldName, string newName);
    SessionProfile Duplicate(string name, string? newName = null);
    bool Delete(string name);
    void Export(string name, string targetFile);
    SessionProfile Import(string sourceFile);
    bool Exists(string name);
    event Action? Changed;
}

/// <summary>Benannte Sitzungsprofile (Arbeit, Gaming, …) als JSON-Dateien im Ordner „profiles“.</summary>
public sealed class ProfileManager : IProfileManager
{
    private readonly string _dir;
    private readonly ILogService _log;
    private readonly object _lock = new();

    public ProfileManager(string directory, ILogService log)
    {
        _dir = directory;
        _log = log;
        Directory.CreateDirectory(_dir);
        AtomicFile.CleanupTemp(_dir);
    }

    public event Action? Changed;

    public static string ValidateName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("Der Profilname darf nicht leer sein.");
        if (n.Length > 60) throw new ArgumentException("Der Profilname ist zu lang (max. 60 Zeichen).");
        if (n.Equals(SessionProfile.DefaultName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("„Standard“ ist die automatische Sitzung und kann nicht als Profilname verwendet werden.");
        return n;
    }

    internal static string FileNameFor(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_');
        var safe = sb.ToString().Trim();
        if (safe.Length == 0) safe = "profil";
        // Kurzer Hash vermeidet Kollisionen („A/B“ vs. „A_B“) und Probleme mit Groß-/Kleinschreibung.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant())))[..8];
        return $"{safe}.{hash}.profile.json";
    }

    private string PathFor(string name) => Path.Combine(_dir, FileNameFor(name));

    public IReadOnlyList<SessionProfile> List()
    {
        lock (_lock)
        {
            var list = new List<SessionProfile>();
            foreach (var f in Directory.EnumerateFiles(_dir, "*.profile.json"))
            {
                var p = AtomicFile.ReadWithFallback(f, Json.Deserialize<SessionProfile>);
                if (p is null || string.IsNullOrWhiteSpace(p.Name)) { _log.Warn("Profile", $"Profil unlesbar: {Path.GetFileName(f)}"); continue; }
                list.Add(p);
            }
            return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    public SessionProfile? Get(string name)
    {
        lock (_lock) return AtomicFile.ReadWithFallback(PathFor(name), Json.Deserialize<SessionProfile>);
    }

    public SessionProfile Create(string name, SessionSnapshot snapshot)
    {
        var n = ValidateName(name);
        SessionProfile p;
        lock (_lock)
        {
            if (Exists(n)) throw new InvalidOperationException($"Ein Profil „{n}“ existiert bereits.");
            p = new SessionProfile { Name = n, CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now, Snapshot = PrepareSnapshot(snapshot, n) };
            Write(p);
        }
        Changed?.Invoke();
        return p;
    }

    public SessionProfile Update(string name, SessionSnapshot snapshot)
    {
        SessionProfile p;
        lock (_lock)
        {
            p = Get(name) ?? throw new InvalidOperationException($"Profil „{name}“ nicht gefunden.");
            p.Snapshot = PrepareSnapshot(snapshot, p.Name);
            p.UpdatedAt = DateTimeOffset.Now;
            Write(p);
        }
        Changed?.Invoke();
        return p;
    }

    public SessionProfile Rename(string oldName, string newName)
    {
        var n = ValidateName(newName);
        SessionProfile p;
        lock (_lock)
        {
            p = Get(oldName) ?? throw new InvalidOperationException($"Profil „{oldName}“ nicht gefunden.");
            var oldPath = PathFor(p.Name);
            var newPath = PathFor(n);
            if (!string.Equals(oldPath, newPath, StringComparison.Ordinal) && File.Exists(newPath))
                throw new InvalidOperationException($"Ein Profil „{n}“ existiert bereits.");
            p.Name = n;
            p.UpdatedAt = DateTimeOffset.Now;
            if (p.Snapshot is not null) p.Snapshot.Name = n;
            Write(p);
            if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
                try { File.Delete(oldPath); File.Delete(oldPath + ".bak"); } catch { }
        }
        Changed?.Invoke();
        return p;
    }

    public SessionProfile Duplicate(string name, string? newName = null)
    {
        SessionProfile copy;
        lock (_lock)
        {
            var p = Get(name) ?? throw new InvalidOperationException($"Profil „{name}“ nicht gefunden.");
            var target = newName is null ? UniqueName(p.Name + " (Kopie)") : ValidateName(newName);
            if (Exists(target)) throw new InvalidOperationException($"Ein Profil „{target}“ existiert bereits.");
            copy = new SessionProfile
            {
                Name = target,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
                Snapshot = p.Snapshot is null ? null : Json.Deserialize<SessionSnapshot>(Json.Serialize(p.Snapshot)),
            };
            if (copy.Snapshot is not null) { copy.Snapshot.Id = Guid.NewGuid(); copy.Snapshot.Name = target; }
            Write(copy);
        }
        Changed?.Invoke();
        return copy;
    }

    public bool Delete(string name)
    {
        bool ok;
        lock (_lock)
        {
            var path = PathFor(name);
            ok = File.Exists(path);
            try { File.Delete(path); File.Delete(path + ".bak"); }
            catch (Exception ex) { _log.Warn("Profile", "Löschen fehlgeschlagen", ex); ok = false; }
        }
        if (ok) Changed?.Invoke();
        return ok;
    }

    public void Export(string name, string targetFile)
    {
        var p = Get(name) ?? throw new InvalidOperationException($"Profil „{name}“ nicht gefunden.");
        AtomicFile.WriteAllText(targetFile, Json.Serialize(p), keepBackup: false);
    }

    public SessionProfile Import(string sourceFile)
    {
        var text = File.ReadAllText(sourceFile);
        SessionProfile? p = null;
        try { p = Json.Deserialize<SessionProfile>(text); } catch (System.Text.Json.JsonException) { }
        if (p?.Snapshot is null)
        {
            // Auch eine nackte Sitzungsdatei (z. B. aus dem Verlauf) akzeptieren.
            SessionSnapshot? s = null;
            try { s = Json.Deserialize<SessionSnapshot>(text); } catch (System.Text.Json.JsonException) { }
            if (s?.Windows is null || s.Windows.Count == 0 && s.Desktops.Count == 0)
                throw new InvalidDataException("Die Datei enthält kein gültiges DeskRecall-Profil.");
            p = new SessionProfile { Name = Path.GetFileNameWithoutExtension(sourceFile).Replace(".profile", ""), Snapshot = s };
        }
        var baseName = string.IsNullOrWhiteSpace(p.Name) || p.Name.Equals(SessionProfile.DefaultName, StringComparison.OrdinalIgnoreCase) ? "Importiert" : p.Name.Trim();
        lock (_lock)
        {
            p.Name = UniqueName(baseName);
            p.UpdatedAt = DateTimeOffset.Now;
            p.Snapshot!.Kind = SessionKind.Imported;
            p.Snapshot.Name = p.Name;
            Write(p);
        }
        Changed?.Invoke();
        return p;
    }

    private string UniqueName(string baseName)
    {
        if (baseName.Length > 50) baseName = baseName[..50].Trim();
        if (!Exists(baseName) && !baseName.Equals(SessionProfile.DefaultName, StringComparison.OrdinalIgnoreCase)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!Exists(candidate)) return candidate;
        }
    }

    private static SessionSnapshot PrepareSnapshot(SessionSnapshot s, string name)
    {
        var copy = Json.Deserialize<SessionSnapshot>(Json.Serialize(s))!;
        copy.Kind = SessionKind.Profile;
        copy.Name = name;
        copy.ContentHash = SessionHasher.Compute(copy);
        return copy;
    }

    private void Write(SessionProfile p) => AtomicFile.WriteAllText(PathFor(p.Name), Json.Serialize(p));
}
