using DeskRecall.Core.Logging;
using DeskRecall.Core.Storage;

namespace DeskRecall.Core.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }
    event Action<AppSettings>? Changed;
    /// <summary>Sofortige Vorschau (z. B. Transparenz), ohne erst die Datei zu schreiben.</summary>
    event Action<AppSettings>? Previewed;
    void Save();
    void Update(Action<AppSettings> change);
    void NotifyPreview();
    AppSettings Reload();
}

/// <summary>Lädt/speichert settings.json atomar. Unbekannte Felder bleiben erhalten, damit Updates nichts verlieren.</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string _path;
    private readonly ILogService _log;
    private readonly object _lock = new();

    public SettingsService(string path, ILogService log)
    {
        _path = path;
        _log = log;
        Current = Load();
    }

    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? Changed;
    public event Action<AppSettings>? Previewed;

    private AppSettings Load()
    {
        var s = AtomicFile.ReadWithFallback(_path, Json.Deserialize<AppSettings>);
        if (s is null)
        {
            if (File.Exists(_path))
            {
                _log.Warn("Einstellungen", "settings.json war unlesbar – Standardwerte werden verwendet (alte Datei bleibt als .defekt erhalten).");
                try { File.Copy(_path, _path + ".defekt", true); } catch { }
            }
            s = AppSettings.CreateDefault();
        }
        Normalize(s);
        return s;
    }

    public static void Normalize(AppSettings s)
    {
        s.General ??= new(); s.Backup ??= new(); s.Restore ??= new(); s.Widget ??= new();
        s.Desktops ??= new(); s.Monitors ??= new(); s.Exclusions ??= new(); s.Diagnostics ??= new(); s.Update ??= new();
        s.Rules ??= new(); s.Exclusions.ProcessNames ??= new(); s.Exclusions.TitleContains ??= new();
        s.Widget.CollapsedCards ??= new();
        s.Backup.HistoryCount = Math.Clamp(s.Backup.HistoryCount, 3, 200);
        s.Widget.ExpandedWidth = WidgetAppearance.ToPixels(WidgetAppearance.FromPixels(s.Widget.ExpandedWidth));
        s.Widget.OpacityPercent = Math.Clamp(s.Widget.OpacityPercent, WidgetAppearance.MinOpacity, WidgetAppearance.MaxOpacity);
        s.Widget.ScalePercent = Math.Clamp(s.Widget.ScalePercent, 80, 140);
        s.Restore.WindowTimeoutSeconds = Math.Clamp(s.Restore.WindowTimeoutSeconds, 5, 300);
        s.Restore.StartupDelaySeconds = Math.Clamp(s.Restore.StartupDelaySeconds, 0, 120);
        s.Restore.LaunchStaggerMilliseconds = Math.Clamp(s.Restore.LaunchStaggerMilliseconds, 0, 5000);
        if (string.IsNullOrWhiteSpace(s.ActiveProfile)) s.ActiveProfile = Models.SessionProfile.DefaultName;
        s.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    public void Save()
    {
        lock (_lock)
        {
            try { AtomicFile.WriteAllText(_path, Json.Serialize(Current)); }
            catch (Exception ex) { _log.Error("Einstellungen", "Speichern fehlgeschlagen", ex); }
        }
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_lock) { change(Current); Normalize(Current); }
        Save();
        Changed?.Invoke(Current);
    }

    public void NotifyPreview() => Previewed?.Invoke(Current);

    public AppSettings Reload()
    {
        Current = Load();
        Changed?.Invoke(Current);
        return Current;
    }
}
