using DeskRecall.Core.Models;
using DeskRecall.Core.Rules;

namespace DeskRecall.Core.Apps;

/// <summary>
/// Angepinntes Programm auf einem virtuellen Desktop: wird beim Wiederherstellen dort gestartet
/// und bei einem späteren Start dorthin verschoben (Live-Regel).
/// </summary>
public static class PinnedApps
{
    public static AppRule Create(InstalledAppEntry app, int desktopNumber)
    {
        if (desktopNumber < 1) throw new ArgumentOutOfRangeException(nameof(desktopNumber));
        return new AppRule
        {
            Id = Guid.NewGuid(),
            Enabled = true,
            Name = string.IsNullOrWhiteSpace(app.Name) ? "Programm" : app.Name.Trim(),
            ProcessName = app.ProcessName,
            TargetDesktop = desktopNumber,
            ApplyLive = true,
            IsPinnedEntry = true,
            ExecutablePath = app.TargetPath,
            AppUserModelId = app.AppUserModelId,
            IconPath = app.IconPath ?? app.TargetPath ?? app.ShortcutPath,
        };
    }

    public static bool SameApp(AppRule rule, InstalledAppEntry app)
    {
        if (!string.IsNullOrWhiteSpace(rule.AppUserModelId) && !string.IsNullOrWhiteSpace(app.AppUserModelId)
            && string.Equals(rule.AppUserModelId, app.AppUserModelId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath) && !string.IsNullOrWhiteSpace(app.TargetPath)
            && string.Equals(InstalledAppCatalog.NormalizePath(rule.ExecutablePath), InstalledAppCatalog.NormalizePath(app.TargetPath), StringComparison.Ordinal))
            return true;
        var a = AppIdentity.NormalizeProcessName(rule.ProcessName);
        var b = app.ProcessName;
        return a.Length > 0 && a == b;
    }

    public static bool SameApp(AppRule rule, string processName, string? executablePath, string? aumid)
    {
        if (!string.IsNullOrWhiteSpace(rule.AppUserModelId) && !string.IsNullOrWhiteSpace(aumid)
            && string.Equals(rule.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath) && !string.IsNullOrWhiteSpace(executablePath)
            && string.Equals(InstalledAppCatalog.NormalizePath(rule.ExecutablePath), InstalledAppCatalog.NormalizePath(executablePath), StringComparison.Ordinal))
            return true;
        var a = AppIdentity.NormalizeProcessName(rule.ProcessName);
        var b = AppIdentity.NormalizeProcessName(processName);
        return a.Length > 0 && a == b;
    }

    public static AppRule? FindOnDesktop(IEnumerable<AppRule> rules, InstalledAppEntry app, int desktopNumber)
        => rules.FirstOrDefault(r => r.IsPinnedEntry && r.Enabled && r.TargetDesktop == desktopNumber && SameApp(r, app));

    /// <summary>
    /// Fügt die Anheftung hinzu oder verschiebt eine vorhandene auf den gewünschten Desktop.
    /// Liefert die gespeicherte Regel und, ob sie neu angelegt wurde.
    /// </summary>
    public static (List<AppRule> Rules, AppRule Rule, bool Created) AddOrMove(IList<AppRule> rules, InstalledAppEntry app, int desktopNumber)
    {
        var list = rules.ToList();
        var existing = list.FirstOrDefault(r => r.IsPinnedEntry && SameApp(r, app));
        if (existing is not null)
        {
            existing.TargetDesktop = desktopNumber;
            existing.Enabled = true;
            existing.ApplyLive = true;
            if (string.IsNullOrWhiteSpace(existing.ExecutablePath)) existing.ExecutablePath = app.TargetPath;
            if (string.IsNullOrWhiteSpace(existing.AppUserModelId)) existing.AppUserModelId = app.AppUserModelId;
            if (string.IsNullOrWhiteSpace(existing.IconPath)) existing.IconPath = app.IconPath ?? app.TargetPath;
            if (string.IsNullOrWhiteSpace(existing.Name)) existing.Name = app.Name;
            return (list, existing, false);
        }
        var created = Create(app, desktopNumber);
        list.Add(created);
        return (list, created, true);
    }

    public static List<AppRule> Remove(IList<AppRule> rules, Guid ruleId)
        => rules.Where(r => r.Id != ruleId).ToList();

    public static IReadOnlyList<AppRule> ForDesktop(IEnumerable<AppRule> rules, int desktopNumber)
        => rules.Where(r => r.IsPinnedEntry && r.Enabled && r.TargetDesktop == desktopNumber).ToList();

    /// <summary>
    /// Erzeugt Sitzungsfenster für angeheftete Programme, die in der letzten Sitzung (und unter den
    /// bereits laufenden Prozessen) fehlen, damit der Wiederhersteller sie starten kann.
    /// </summary>
    public static IReadOnlyList<WindowRecord> MissingLaunchRecords(
        IEnumerable<AppRule> rules,
        IEnumerable<WindowRecord> alreadyInSnapshot,
        IEnumerable<(string ProcessName, string? Path)>? running = null)
    {
        var known = new List<WindowRecord>(alreadyInSnapshot);
        var result = new List<WindowRecord>();
        foreach (var rule in rules.Where(r => r.IsPinnedEntry && r.Enabled && r.TargetDesktop is > 0 && !r.NeverAutoStart && !r.Exclude))
        {
            if (known.Any(w => SameApp(rule, w.ProcessName, w.ExecutablePath, w.AppUserModelId))) continue;
            if (running is not null && running.Any(p => SameApp(rule, p.ProcessName, p.Path, null))) continue;
            if (string.IsNullOrWhiteSpace(rule.ExecutablePath) && string.IsNullOrWhiteSpace(rule.AppUserModelId)) continue;

            var rec = new WindowRecord
            {
                ProcessName = string.IsNullOrWhiteSpace(rule.ProcessName) ? "app" : rule.ProcessName,
                ExecutablePath = rule.ExecutablePath,
                AppUserModelId = rule.AppUserModelId,
                DisplayName = string.IsNullOrWhiteSpace(rule.Name) ? rule.ProcessName : rule.Name,
                Title = "",
                DesktopIndex = rule.TargetDesktop!.Value - 1,
                Kind = string.IsNullOrWhiteSpace(rule.AppUserModelId) ? AppKind.Normal : AppKind.Uwp,
            };
            result.Add(rec);
            known.Add(rec);
        }
        return result;
    }
}
