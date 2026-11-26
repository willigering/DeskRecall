using System.Text.Json;
using System.Text.Json.Serialization;
using DeskRecall.Core.Storage;

namespace DeskRecall.Core.Browsers;

/// <summary>Ein eigener Name für einen Browser-Tab – nur in DeskRecall, der Browser bleibt unverändert.</summary>
public sealed class TabAlias
{
    public string Browser { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string Alias { get; set; } = "";
    public DateTimeOffset Updated { get; set; }
}

/// <summary>
/// Lokale Tab-Namen in <c>tab-names.json</c>. Schlüssel: Browser (Prozessname) + vereinheitlichter Tab-Titel,
/// zusätzlich die Adresse, falls bekannt. Defekte Dateien werden ignoriert, nie überschrieben ohne Sicherung.
/// </summary>
public sealed class TabAliasStore
{
    sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<TabAlias> Aliases { get; set; } = new();
    }

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const int MaxAliasLength = 80;
    public const int MaxEntries = 2000;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now;
    private List<TabAlias> _items = new();

    public TabAliasStore(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.Now);
        Load();
    }

    public event Action? Changed;

    public int Count { get { lock (_lock) return _items.Count; } }

    public static string BrowserKey(string? processName)
    {
        var p = (processName ?? "").Trim();
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        return p.ToLowerInvariant();
    }

    public string? Find(string? processName, string? title, string? url = null)
    {
        var b = BrowserKey(processName);
        var t = TabTitle.Normalize(title);
        if (b.Length == 0 || (t.Length == 0 && string.IsNullOrEmpty(url))) return null;
        lock (_lock) return Match(b, t, url)?.Alias;
    }

    /// <summary>Setzt den Namen; leer oder gleich dem Original setzt ihn zurück. Liefert true bei einer Änderung.</summary>
    public bool Set(string? processName, string? title, string? alias, string? url = null)
    {
        var b = BrowserKey(processName);
        var t = TabTitle.Normalize(title);
        if (b.Length == 0 || t.Length == 0) return false;
        var a = (alias ?? "").Trim();
        if (a.Length > MaxAliasLength) a = a[..MaxAliasLength];
        if (a.Length == 0 || string.Equals(TabTitle.Normalize(a), t, StringComparison.Ordinal)) return Reset(processName, title, url);

        lock (_lock)
        {
            var existing = Match(b, t, url);
            if (existing is not null && existing.Alias == a) return false;
            if (existing is null)
            {
                existing = new TabAlias { Browser = b, Title = t, Url = string.IsNullOrWhiteSpace(url) ? null : url };
                _items.Add(existing);
                if (_items.Count > MaxEntries)
                    _items = _items.OrderByDescending(x => x.Updated).Take(MaxEntries).ToList();
            }
            existing.Alias = a;
            existing.Updated = _now();
            SaveLocked();
        }
        Changed?.Invoke();
        return true;
    }

    public bool Reset(string? processName, string? title, string? url = null)
    {
        var b = BrowserKey(processName);
        var t = TabTitle.Normalize(title);
        bool removed;
        lock (_lock)
        {
            var hit = Match(b, t, url);
            removed = hit is not null && _items.Remove(hit);
            if (removed) SaveLocked();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    TabAlias? Match(string browser, string title, string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            var byUrl = _items.FirstOrDefault(x => x.Browser == browser && x.Url is not null && string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
            if (byUrl is not null) return byUrl;
        }
        return _items.FirstOrDefault(x => x.Browser == browser && string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Options);
            _items = model?.Aliases?
                .Where(x => !string.IsNullOrWhiteSpace(x.Browser) && !string.IsNullOrWhiteSpace(x.Title) && !string.IsNullOrWhiteSpace(x.Alias))
                .ToList() ?? new List<TabAlias>();
        }
        catch
        {
            _items = new List<TabAlias>(); // defekte Datei: ignorieren, beim nächsten Speichern bleibt eine .bak
        }
    }

    void SaveLocked()
    {
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new FileModel { Aliases = _items }, Options)); }
        catch { /* lokale Namen sind Komfort – Fehler nicht hochreichen */ }
    }
}
