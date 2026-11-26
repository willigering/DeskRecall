using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Models;

namespace DeskRecall.Core.Restore;

public sealed class LaunchItem
{
    public required string DisplayName { get; init; }
    public required LaunchRequest Request { get; init; }
    public required List<RestoreItem> Items { get; init; }
    public string? Note { get; init; }
    public override string ToString() => $"{DisplayName}: {Request.FileName} {Request.Arguments}";
}

/// <summary>
/// Entscheidet, welche Programme gestartet werden müssen – und vor allem, welche NICHT:
/// laufende Programme, Single-Instance-Apps und Browser werden nie doppelt gestartet.
/// </summary>
public static class LaunchPlanner
{
    public static List<LaunchItem> Plan(
        IReadOnlyList<RestoreItem> pending,
        IReadOnlyList<RunningProcess> running,
        RestoreOptions options,
        Func<WindowRecord, ResolvedLaunch?> resolve,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        bool wake = false)
    {
        var launches = new List<LaunchItem>();
        var runningNames = new HashSet<string>(running.Select(p => AppIdentity.NormalizeProcessName(p.Name)));
        var runningPaths = new HashSet<string>(running.Where(p => p.Path is not null).Select(p => p.Path!), StringComparer.OrdinalIgnoreCase);

        // Explorer-Ordner: je fehlendem Ordner ein Fenster (Explorer selbst läuft immer)
        foreach (var it in pending.Where(i => i.Saved.Kind == AppKind.Explorer))
        {
            var folder = it.Saved.ExplorerFolderPath;
            if (!options.RestoreExplorerFolders || string.IsNullOrWhiteSpace(folder))
            { Skip(it, "Explorer-Ordner werden nicht wiederhergestellt"); continue; }
            var isShellPath = folder.StartsWith("::", StringComparison.Ordinal) || folder.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            if (!isShellPath && !directoryExists(folder))
            { it.Status = RestoreItemStatus.NotFound; it.Message = $"Ordner existiert nicht mehr: {folder}"; continue; }
            launches.Add(new LaunchItem
            {
                DisplayName = "Explorer: " + folder,
                Request = new LaunchRequest("explorer.exe", CommandLine.Quote(isShellPath && folder.StartsWith("::") ? "shell:" + folder : folder), null, null, false, false, "Explorer"),
                Items = new List<RestoreItem> { it },
            });
        }

        foreach (var group in pending.Where(i => i.Saved.Kind != AppKind.Explorer && i.Status == RestoreItemStatus.Pending)
                                     .GroupBy(i => i.Saved.AppKey))
        {
            var items = group.ToList();
            var first = items[0].Saved;
            var name = first.FriendlyName;
            var proc = AppIdentity.NormalizeProcessName(first.ProcessName);

            if (items.Any(i => i.NeverAutoStart)) { foreach (var i in items) Skip(i, "Regel: nie automatisch starten"); continue; }
            if (AppCatalog.IsNeverLaunch(first.ProcessName) && first.Kind != AppKind.Uwp) { foreach (var i in items) Skip(i, "Systemprozess – wird nicht gestartet"); continue; }

            var isRunning = runningNames.Contains(proc) || (first.ExecutablePath is not null && runningPaths.Contains(first.ExecutablePath));

            // Beim normalen Plan nicht doppelt starten. „wake“ startet trotzdem einmal,
            // wenn der Prozess läuft, aber kein Fenster erschienen ist (z. B. Teams nur im Hintergrund).
            if (isRunning && !wake)
            {
                foreach (var i in items) i.Message = "läuft bereits – warte auf Fenster";
                continue;
            }
            if (!options.LaunchMissingApps) { foreach (var i in items) Skip(i, "Starten fehlender Programme ist deaktiviert"); continue; }

            if (items.All(i => i.Saved.IsElevated) && options.ElevationPolicy == ElevationPolicy.Skip)
            {
                foreach (var i in items) { i.Status = RestoreItemStatus.NeedsElevation; i.Message = "lief mit Administratorrechten – übersprungen (Einstellung „Wiederherstellung“)"; }
                continue;
            }

            var minimized = items.All(i => i.TargetState == WindowShowState.Minimized);
            var minArgs = minimized ? AppCatalog.MinimizedStartArguments(first.ProcessName) : null;

            // UWP / Store-Apps über ihre AppUserModelID
            if (first.Kind == AppKind.Uwp || (string.IsNullOrEmpty(first.ExecutablePath) && !string.IsNullOrEmpty(first.AppUserModelId)))
            {
                if (string.IsNullOrEmpty(first.AppUserModelId)) { foreach (var i in items) { i.Status = RestoreItemStatus.NotFound; i.Message = "Store-App ohne Startidentität"; } continue; }
                launches.Add(new LaunchItem
                {
                    DisplayName = name,
                    Request = new LaunchRequest("explorer.exe", CommandLine.Quote("shell:AppsFolder\\" + first.AppUserModelId), null, first.AppUserModelId, minimized, false, name),
                    Items = items,
                });
                continue;
            }

            var resolved = resolve(first);
            if (resolved is null)
            {
                foreach (var i in items) { i.Status = RestoreItemStatus.NotFound; i.Message = $"Programm nicht gefunden (deinstalliert oder verschoben?): {first.ExecutablePath ?? first.ProcessName}"; }
                continue;
            }
            var workDir = WinPath.GetDirectoryName(resolved.FileName);
            const bool allowElevation = false; // Erhöhung nur nach ausdrücklicher Zustimmung (SessionRestorer fragt bei ERROR_ELEVATION_REQUIRED)

            var oneShot = first.Kind is AppKind.Browser or AppKind.SingleInstance or AppKind.NativeSession
                          || AppCatalog.IsSingleInstance(first.ProcessName);
            if (first.Kind == AppKind.Browser && !options.UseBrowserSessionRestore) oneShot = true; // Browser trotzdem nur einmal starten, nie Tabs doppelt

            if (oneShot)
            {
                launches.Add(new LaunchItem
                {
                    DisplayName = name,
                    Request = new LaunchRequest(resolved.FileName, Join(resolved.Arguments, minArgs), workDir, null, minimized, allowElevation, name),
                    Items = items,
                    Note = first.Kind == AppKind.Browser ? "Browser öffnet seine Fenster über die eigene Sitzungswiederherstellung" : resolved.Note,
                });
                continue;
            }

            // Mehrfach-Instanz-Programme: pro unterschiedlichem Dokument ein Start, sonst genau einer.
            var docs = items.Where(i => !string.IsNullOrEmpty(i.Saved.DocumentPath) && fileExists(i.Saved.DocumentPath!))
                            .GroupBy(i => i.Saved.DocumentPath!, StringComparer.OrdinalIgnoreCase).ToList();
            var withoutDoc = items.Except(docs.SelectMany(d => d)).ToList();
            if (docs.Count == 0)
            {
                launches.Add(new LaunchItem
                {
                    DisplayName = name,
                    Request = new LaunchRequest(resolved.FileName, Join(resolved.Arguments, minArgs), workDir, null, minimized, allowElevation, name),
                    Items = items,
                    Note = resolved.Note,
                });
                continue;
            }
            for (var d = 0; d < docs.Count; d++)
            {
                var docItems = docs[d].ToList();
                if (d == 0) docItems.AddRange(withoutDoc); // Fenster ohne Dokument teilen sich den ersten Start
                launches.Add(new LaunchItem
                {
                    DisplayName = $"{name} ({WinPath.GetFileName(docs[d].Key)})",
                    Request = new LaunchRequest(resolved.FileName, Join(resolved.Arguments, CommandLine.Quote(docs[d].Key)), workDir, null, minimized, allowElevation, name),
                    Items = docItems,
                    Note = resolved.Note,
                });
            }
        }
        return launches;
    }

    private static string? Join(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : a + " " + b;

    private static void Skip(RestoreItem i, string reason) { i.Status = RestoreItemStatus.Skipped; i.Message = reason; }
}
