using System.Diagnostics;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Geometry;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Rules;

namespace DeskRecall.Core.Restore;

public interface ISessionRestorer
{
    Task<RestoreReport> RestoreAsync(SessionSnapshot snapshot, RestoreOptions options, IProgress<RestoreProgress>? progress = null, CancellationToken ct = default);
    IReadOnlyList<WindowRecord> FindExtraWindows(SessionSnapshot snapshot, RestoreOptions options);
    bool IsRunning { get; }
}

/// <summary>
/// Stellt eine Sitzung wieder her: Desktops abgleichen → laufende Fenster zuordnen → fehlende Programme
/// starten → auf Fenster warten → Position/Zustand setzen → per HWND auf den Ziel-Desktop verschieben
/// (ohne Desktop-Wechsel) → am Ende einmal den zuletzt aktiven Desktop aktivieren.
/// Fehler bei einzelnen Fenstern werden protokolliert und übersprungen.
/// </summary>
public sealed class SessionRestorer : ISessionRestorer
{
    private const string Src = "Wiederherstellung";
    private readonly IWindowManager _windows;
    private readonly IVirtualDesktopService _desktops;
    private readonly IProcessManager _processes;
    private readonly IMonitorManager _monitors;
    private readonly IRuleEngine _rules;
    private readonly ILogService _log;
    private readonly IClock _clock;
    private readonly IElevationConsent? _consent;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private int _running;

    public SessionRestorer(IWindowManager windows, IVirtualDesktopService desktops, IProcessManager processes, IMonitorManager monitors,
        IRuleEngine rules, ILogService log, IClock? clock = null, IElevationConsent? consent = null,
        Func<string, bool>? directoryExists = null, Func<string, bool>? fileExists = null)
    {
        _windows = windows; _desktops = desktops; _processes = processes; _monitors = monitors;
        _rules = rules; _log = log; _clock = clock ?? SystemClock.Instance; _consent = consent;
        _directoryExists = directoryExists ?? Directory.Exists;
        _fileExists = fileExists ?? File.Exists;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task<RestoreReport> RestoreAsync(SessionSnapshot snapshot, RestoreOptions options, IProgress<RestoreProgress>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            throw new InvalidOperationException("Es läuft bereits eine Wiederherstellung.");
        var sw = Stopwatch.StartNew();
        var report = new RestoreReport { DryRun = options.DryRun };
        try
        {
            await RestoreCoreAsync(snapshot, options, report, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            _log.Warn(Src, "Abgebrochen.");
        }
        finally
        {
            report.Duration = sw.Elapsed;
            Volatile.Write(ref _running, 0);
        }

        foreach (var item in report.Items.Where(i => i.Status is not (RestoreItemStatus.Restored or RestoreItemStatus.AlreadyRunning)))
            _log.Log(item.Status == RestoreItemStatus.Skipped ? LogLevel.Info : LogLevel.Warning, Src, $"{item.Saved.FriendlyName} „{Short(item.Saved.Title)}“: {StatusText(item.Status)} – {item.Message}");
        _log.Info(Src, report.Summary + $" ({report.Duration.TotalSeconds:0.0} s)");
        return report;
    }

    private async Task RestoreCoreAsync(SessionSnapshot snapshot, RestoreOptions o, RestoreReport report, IProgress<RestoreProgress>? progress, CancellationToken ct)
    {
        _log.Info(Src, $"Starte{(o.DryRun ? " Testlauf" : "")}: Sitzung vom {snapshot.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm} mit {snapshot.Windows.Count} Fenstern auf {snapshot.Desktops.Count} Desktops.");
        var monitors = _monitors.GetMonitors();

        // 1. Virtuelle Desktops
        progress?.Report(new RestoreProgress("Desktops werden abgeglichen …", 0, snapshot.Windows.Count));
        DesktopMapping mapping;
        try
        {
            mapping = _desktops.IsAvailable
                ? DesktopPlanner.Apply(_desktops, snapshot.Desktops, o.CreateMissingDesktops, o.RestoreDesktopNamesAndOrder, o.DryRun, _log)
                : new DesktopMapping(new Dictionary<int, Guid>(), 0, 0, 0);
        }
        catch (Exception ex)
        {
            _log.Error(Src, "Desktop-Abgleich fehlgeschlagen – Fenster werden ohne Desktop-Zuordnung wiederhergestellt.", ex);
            mapping = new DesktopMapping(new Dictionary<int, Guid>(), 0, 0, 0);
        }
        report.Desktops = mapping;
        if (!_desktops.CanManage)
            _log.Warn(Src, $"Virtuelle Desktops nur eingeschränkt verfügbar ({_desktops.BackendDescription}). Fenster werden nicht zwischen Desktops verschoben.");

        // 2. Ziele berechnen (Regeln haben Vorrang vor der Sitzung)
        foreach (var w in snapshot.Windows.OrderBy(w => w.ZOrder))
        {
            if (IsExcluded(w, o)) continue;
            var rule = _rules.Evaluate(w);
            if (rule.Excluded) continue;
            var item = new RestoreItem { Saved = w, NeverAutoStart = rule.NeverAutoStart };
            ComputeTarget(item, rule, snapshot, monitors, o, mapping);
            report.Items.Add(item);
        }

        // Angeheftete Programme, die in der letzten Sitzung fehlen, ebenfalls starten.
        if (o.PinnedEntries.Count > 0)
        {
            var missing = PinnedApps.MissingLaunchRecords(o.PinnedEntries, snapshot.Windows);
            foreach (var w in missing)
            {
                if (IsExcluded(w, o)) continue;
                var rule = _rules.Evaluate(w);
                if (rule.Excluded || rule.NeverAutoStart) continue;
                var item = new RestoreItem { Saved = w, NeverAutoStart = false };
                ComputeTarget(item, rule, snapshot, monitors, o, mapping);
                report.Items.Add(item);
                _log.Info(Src, $"Angeheftet: {w.FriendlyName} → Desktop {w.DesktopIndex + 1}");
            }
        }
        var total = report.Items.Count;

        // 3. Bereits laufende Fenster zuordnen
        var assigned = new HashSet<long>();
        var live = SafeEnumerate();
        MatchAndApply(report.Items, live, assigned, o, firstSeen: null, report, progress, total);

        // 4. Fehlende Programme starten
        var pending = report.Items.Where(i => i.Status == RestoreItemStatus.Pending).ToList();
        if (pending.Count > 0)
        {
            var running = SafeRunning();
            var launches = LaunchPlanner.Plan(pending, running, o, w => _processes.Resolve(w), _directoryExists, _fileExists);
            var now = _clock.Now;
            foreach (var i in pending.Where(i => i.Status == RestoreItemStatus.Pending))
                i.WaitDeadline = now + o.RunningAppGrace;

            foreach (var launch in launches)
            {
                ct.ThrowIfCancellationRequested();
                if (o.DryRun)
                {
                    _log.Info(Src, $"[Testlauf] würde starten: {launch.Request.FileName} {launch.Request.Arguments}".TrimEnd());
                    foreach (var i in launch.Items) { i.Status = RestoreItemStatus.Skipped; i.Message = "Testlauf – nicht gestartet"; }
                    continue;
                }
                progress?.Report(new RestoreProgress($"Starte {launch.DisplayName} …", report.PlacedCount, total));
                var result = SafeLaunch(launch.Request);
                if (result.Status == LaunchStatus.RequiresElevation && o.ElevationPolicy == ElevationPolicy.Ask && _consent is not null)
                {
                    if (await _consent.ConfirmAsync(launch.DisplayName, ct).ConfigureAwait(false))
                        result = SafeLaunch(launch.Request with { AllowElevation = true });
                    else result = new LaunchResult(LaunchStatus.RequiresElevation, null, "Start mit Administratorrechten abgelehnt");
                }
                switch (result.Status)
                {
                    case LaunchStatus.Started:
                        report.Launched++;
                        _log.Info(Src, $"Gestartet: {launch.DisplayName}{(launch.Note is null ? "" : $" ({launch.Note})")}");
                        foreach (var i in launch.Items) { i.WasLaunched = true; i.WaitDeadline = _clock.Now + o.WindowTimeout; i.Message = "gestartet – warte auf Fenster"; }
                        break;
                    case LaunchStatus.RequiresElevation:
                        foreach (var i in launch.Items) { i.Status = RestoreItemStatus.NeedsElevation; i.Message = result.Message ?? "benötigt Administratorrechte – übersprungen"; }
                        break;
                    case LaunchStatus.NotFound:
                        foreach (var i in launch.Items) { i.Status = RestoreItemStatus.NotFound; i.Message = result.Message ?? "Programm nicht gefunden"; }
                        break;
                    default:
                        foreach (var i in launch.Items) { i.Status = RestoreItemStatus.Failed; i.Message = result.Message ?? "Start fehlgeschlagen"; }
                        break;
                }
                if (o.LaunchStagger > TimeSpan.Zero) await _clock.Delay(o.LaunchStagger, ct).ConfigureAwait(false);
            }

            // 5. Auf Fenster warten und zuordnen
            var firstSeen = new Dictionary<long, DateTimeOffset>();
            foreach (var w in SafeEnumerate()) firstSeen[w.Handle] = _clock.Now - o.StableAfter; // vorhandene gelten als stabil
            while (!o.DryRun)
            {
                var waiting = report.Items.Where(i => i.Status == RestoreItemStatus.Pending).ToList();
                if (waiting.Count == 0) break;
                var t = _clock.Now;
                foreach (var i in waiting.Where(i => i.WaitDeadline is { } d && t > d))
                {
                    i.Status = i.WasLaunched ? RestoreItemStatus.TimedOut : RestoreItemStatus.Skipped;
                    i.Message = i.WasLaunched
                        ? $"kein passendes Fenster nach {o.WindowTimeout.TotalSeconds:0} s (Programm unterstützt evtl. keine exakte Wiederherstellung)"
                        : "läuft bereits, aber ohne passendes Fenster (z. B. nur im Infobereich)";
                }
                if (report.Items.All(i => i.Status != RestoreItemStatus.Pending)) break;

                await _clock.Delay(o.PollInterval, ct).ConfigureAwait(false);
                live = SafeEnumerate();
                var now2 = _clock.Now;
                foreach (var w in live) firstSeen.TryAdd(w.Handle, now2);
                // verschwundene Fenster (z. B. Splashscreen geschlossen) wieder freigeben
                foreach (var i in report.Items.Where(i => i.AssignedHandle is long h && i.Status == RestoreItemStatus.Restored && !_windows.IsAlive(h) && i.WasLaunched))
                {
                    assigned.Remove(i.AssignedHandle!.Value);
                    i.AssignedHandle = null;
                    i.Status = RestoreItemStatus.Pending;
                    i.Message = "Fenster wurde geschlossen (Splashscreen?) – warte auf Hauptfenster";
                }
                MatchAndApply(report.Items, live, assigned, o, firstSeen, report, progress, total);
            }
            if (o.DryRun)
                foreach (var i in report.Items.Where(i => i.Status == RestoreItemStatus.Pending))
                { i.Status = RestoreItemStatus.Skipped; i.Message = "[Testlauf] läuft bereits, Fenster würde abgewartet"; }
        }

        // 6. Nachkontrolle: manche Programme verändern ihre Größe nach dem Laden
        var placed = report.Items.Where(i => i.Status == RestoreItemStatus.Restored && i.WasLaunched && i.AssignedHandle is not null).ToList();
        if (!o.DryRun && placed.Count > 0 && o.SettleDelay > TimeSpan.Zero)
        {
            await _clock.Delay(o.SettleDelay, ct).ConfigureAwait(false);
            foreach (var i in placed)
            {
                var h = i.AssignedHandle!.Value;
                if (!_windows.IsAlive(h) || i.TargetState != WindowShowState.Normal) continue;
                var b = _windows.GetWindowBounds(h);
                if (b is { } r && (Math.Abs(r.Left - i.TargetBounds.Left) > 10 || Math.Abs(r.Top - i.TargetBounds.Top) > 10 || Math.Abs(r.Width - i.TargetBounds.Width) > 10 || Math.Abs(r.Height - i.TargetBounds.Height) > 10))
                {
                    _log.Debug(Src, $"Position von {i.Saved.FriendlyName} nachkorrigiert.");
                    Apply(i, h, o);
                }
            }
        }

        // 7. Zuletzt aktiven Desktop aktivieren – genau ein Wechsel am Ende
        if (!o.DryRun && o.ActivateLastDesktop && _desktops.CanManage)
        {
            var target = mapping.SavedIndexToCurrent.TryGetValue(snapshot.ActiveDesktopIndex, out var id) ? id : (Guid?)null;
            if (target is Guid g && _desktops.GetCurrentDesktopId() != g)
            {
                if (_desktops.SwitchToDesktop(g)) _log.Info(Src, $"Zuletzt aktiver Desktop {snapshot.ActiveDesktopIndex + 1} aktiviert.");
            }
        }
        progress?.Report(new RestoreProgress(report.Summary, report.PlacedCount, total));
    }

    private void MatchAndApply(List<RestoreItem> items, IReadOnlyList<WindowRecord> live, HashSet<long> assigned, RestoreOptions o,
        Dictionary<long, DateTimeOffset>? firstSeen, RestoreReport report, IProgress<RestoreProgress>? progress, int total)
    {
        var pending = items.Where(i => i.Status == RestoreItemStatus.Pending).ToList();
        if (pending.Count == 0) return;
        var now = _clock.Now;
        var candidates = live.Where(w => !assigned.Contains(w.Handle)).Where(w =>
        {
            if (firstSeen is null) return true;
            if (!firstSeen.TryGetValue(w.Handle, out var seen)) return false;
            var age = now - seen;
            if (age < o.StableAfter) return false;
            // Splashscreens (ohne Titelleiste) erst nach einer Weile akzeptieren
            if (!w.HasCaption && !w.IsResizable && age < o.SplashGrace) return false;
            return true;
        }).ToList();
        if (candidates.Count == 0) return;

        var matches = WindowMatcher.Match(pending.Select(p => p.Saved).ToList(), candidates);
        foreach (var (idx, w, _) in matches)
        {
            var item = pending[idx];
            if (!w.HasCaption && item.Saved.HasCaption && firstSeen is not null && now - firstSeen[w.Handle] < o.SplashGrace) continue;
            assigned.Add(w.Handle);
            item.AssignedHandle = w.Handle;
            if (o.DryRun)
            {
                item.Status = RestoreItemStatus.AlreadyRunning;
                item.Message = $"[Testlauf] würde nach Desktop {item.TargetDesktopIndex + 1}, {item.TargetState}, {item.TargetBounds} verschoben";
                _log.Info(Src, $"{item.Saved.FriendlyName}: {item.Message}");
                continue;
            }
            var ok = Apply(item, w.Handle, o, w);
            item.Status = ok ? (item.WasLaunched ? RestoreItemStatus.Restored : RestoreItemStatus.AlreadyRunning) : RestoreItemStatus.Failed;
            if (!ok) item.Message ??= "Fensterposition konnte nicht gesetzt werden (Fenster mit höheren Rechten?)";
            else item.Message = null;
            progress?.Report(new RestoreProgress($"{item.Saved.FriendlyName} platziert", report.PlacedCount, total));
        }
    }

    private bool Apply(RestoreItem item, long hwnd, RestoreOptions o, WindowRecord? live = null)
    {
        try
        {
            var placementOk = _windows.ApplyPlacement(hwnd, new PlacementTarget(item.TargetBounds, item.TargetState, item.TargetMonitor, Activate: false));
            if (_desktops.CanManage)
            {
                if (item.Saved.IsPinned)
                {
                    if (!_desktops.IsWindowPinned(hwnd)) _desktops.SetWindowPinned(hwnd, true);
                }
                else if (item.TargetDesktopId is Guid target)
                {
                    var currentDesk = live?.DesktopId is { } ld && ld != Guid.Empty ? ld : _desktops.GetWindowDesktopId(hwnd);
                    if (currentDesk != target && !_desktops.MoveWindowToDesktop(hwnd, target))
                    {
                        item.Message = $"konnte nicht auf Desktop {item.TargetDesktopIndex + 1} verschoben werden";
                        _log.Warn(Src, $"{item.Saved.FriendlyName}: {item.Message}");
                    }
                }
            }
            return placementOk;
        }
        catch (Exception ex)
        {
            item.Message = "Fehler: " + ex.Message;
            _log.Warn(Src, $"{item.Saved.FriendlyName}: Platzieren fehlgeschlagen", ex);
            return false;
        }
    }

    private void ComputeTarget(RestoreItem item, RuleOutcome rule, SessionSnapshot snapshot, IReadOnlyList<MonitorInfo> monitors, RestoreOptions o, DesktopMapping mapping)
    {
        var w = item.Saved;
        item.TargetDesktopIndex = rule.TargetDesktopIndex ?? w.DesktopIndex;
        item.TargetState = rule.ForcedState ?? w.State;
        if (item.TargetDesktopIndex >= 0)
        {
            if (mapping.SavedIndexToCurrent.TryGetValue(item.TargetDesktopIndex, out var id)) item.TargetDesktopId = id;
            else
            {
                // Regel-Desktop, der in der Sitzung nicht vorkam: direkt über aktuelle Desktops auflösen
                var cur = _desktops.IsAvailable ? _desktops.GetDesktops() : Array.Empty<DesktopInfo>();
                if (item.TargetDesktopIndex < cur.Count) item.TargetDesktopId = cur[item.TargetDesktopIndex].Id;
            }
        }

        var savedMonitor = snapshot.Monitors.FirstOrDefault(m => m.Id == w.MonitorId)
                           ?? (snapshot.Monitors.Count > 0 ? MonitorRemapper.MonitorForRect(w.NormalBounds, snapshot.Monitors) : null);
        MonitorInfo? target = null;
        if (rule.TargetMonitorId is not null)
            target = monitors.FirstOrDefault(m => string.Equals(m.Id, rule.TargetMonitorId, StringComparison.OrdinalIgnoreCase));
        target ??= savedMonitor is not null ? MonitorRemapper.FindCurrent(savedMonitor, monitors, o.MonitorFallback) : MonitorRemapper.MonitorForRect(w.NormalBounds, monitors);

        var bounds = w.NormalBounds;
        if (savedMonitor is not null && target is not null)
            bounds = MonitorRemapper.Transform(bounds, savedMonitor.WorkArea, target.WorkArea, o.ScaleOnResolutionChange);
        if (o.CorrectOffscreenWindows && monitors.Count > 0)
        {
            var corrected = PlacementCorrector.Correct(bounds, monitors, target);
            if (corrected != bounds) _log.Debug(Src, $"{w.FriendlyName}: Position {bounds} → {corrected} (außerhalb des sichtbaren Bereichs)");
            bounds = corrected;
        }
        item.TargetBounds = bounds;
        item.TargetMonitor = target;
    }

    private static bool IsExcluded(WindowRecord w, RestoreOptions o)
    {
        var p = AppIdentity.NormalizeProcessName(w.ProcessName);
        if (o.ExcludedProcesses.Any(x => AppIdentity.NormalizeProcessName(x) == p)) return true;
        if (!string.IsNullOrEmpty(w.Title) && o.ExcludedTitles.Any(t => !string.IsNullOrWhiteSpace(t) && w.Title.Contains(t, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    public IReadOnlyList<WindowRecord> FindExtraWindows(SessionSnapshot snapshot, RestoreOptions options)
    {
        var live = SafeEnumerate();
        var matches = WindowMatcher.Match(snapshot.Windows, live);
        var matched = matches.Select(m => m.Live.Handle).ToHashSet();
        var savedApps = snapshot.Windows.Select(w => w.AppKey).ToHashSet();
        // „Zusätzlich“ = Programme, die im Profil gar nicht vorkommen (keine Fenster bekannter Programme schließen)
        return live.Where(w => !matched.Contains(w.Handle) && !savedApps.Contains(w.AppKey) && !IsExcluded(w, options)
                               && !Apps.AppCatalog.IsNeverLaunch(w.ProcessName)).ToList();
    }

    private IReadOnlyList<WindowRecord> SafeEnumerate()
    {
        try { return _windows.EnumerateAppWindows(); }
        catch (Exception ex) { _log.Error(Src, "Fenster konnten nicht ermittelt werden", ex); return Array.Empty<WindowRecord>(); }
    }

    private IReadOnlyList<RunningProcess> SafeRunning()
    {
        try { return _processes.GetRunningProcesses(); }
        catch (Exception ex) { _log.Error(Src, "Prozessliste nicht verfügbar", ex); return Array.Empty<RunningProcess>(); }
    }

    private LaunchResult SafeLaunch(LaunchRequest r)
    {
        try { return _processes.Launch(r); }
        catch (Exception ex) { return new LaunchResult(LaunchStatus.Failed, null, ex.Message); }
    }

    private static string Short(string s) => s.Length > 60 ? s[..57] + "…" : s;

    public static string StatusText(RestoreItemStatus s) => s switch
    {
        RestoreItemStatus.Restored => "wiederhergestellt",
        RestoreItemStatus.AlreadyRunning => "lief bereits, platziert",
        RestoreItemStatus.Skipped => "übersprungen",
        RestoreItemStatus.NotFound => "nicht gefunden",
        RestoreItemStatus.NeedsElevation => "benötigt Administratorrechte",
        RestoreItemStatus.TimedOut => "Zeitüberschreitung",
        RestoreItemStatus.Failed => "fehlgeschlagen",
        _ => "ausstehend",
    };
}
