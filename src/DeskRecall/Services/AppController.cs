using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Profiles;
using DeskRecall.Core.Recording;
using DeskRecall.Core.Restore;
using DeskRecall.Core.Session;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Storage;
using DeskRecall.Platform.Services;

namespace DeskRecall.Services;

/// <summary>Aktueller Zustand für die Oberfläche: Desktops, aktiver Desktop und alle Programmfenster.</summary>
public sealed record LiveState(IReadOnlyList<DesktopInfo> Desktops, Guid? CurrentDesktopId, IReadOnlyList<WindowRecord> Windows, DateTimeOffset Time);

/// <summary>
/// Steuert den Ablauf: Änderungserkennung, automatische Sicherung, Wiederherstellung beim Start,
/// Live-Regeln und Vollbilderkennung. Läuft dauerhaft im Hintergrund, ohne Eingaben abzufangen.
/// </summary>
public sealed class AppController : IDisposable
{
    private const string Src = "Steuerung";
    private readonly ISettingsService _settings;
    private readonly ISessionStore _store;
    private readonly IProfileManager _profiles;
    private readonly SessionRecorder _recorder;
    private readonly ISessionRestorer _restorer;
    private readonly IRuleEngine _rules;
    private readonly IWindowManager _windows;
    private readonly IVirtualDesktopService _vd;
    private readonly IDialogService _dialogs;
    private readonly ILogService _log;
    private readonly IconService _icons;
    private readonly Dispatcher _ui;
    private const int MinUiRefreshMs = 250;

    private WinEventMonitor? _events;
    private ChangeDebouncer? _saveDebouncer;
    private ChangeDebouncer? _uiDebouncer;
    private Timer? _saveTimer;
    private DispatcherTimer? _fullscreenTimer;
    private StartupGuard _guard = new(DateTimeOffset.Now, TimeSpan.Zero, active: false);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly HashSet<long> _liveRuleHandled = new();
    private int _refreshing;
    private bool _refreshPending;
    private int _quietUi;
    private bool _shutdownSaved;
    private bool _fullscreen;
    private long _lastRefreshTick;
    private long _lastFullscreenCheck;
    private CancellationTokenSource? _restoreCts;

    public AppController(ISettingsService settings, ISessionStore store, IProfileManager profiles, SessionRecorder recorder, ISessionRestorer restorer,
        IRuleEngine rules, IWindowManager windows, IVirtualDesktopService vd, IDialogService dialogs, ILogService log, IconService icons)
    {
        _settings = settings; _store = store; _profiles = profiles; _recorder = recorder; _restorer = restorer;
        _rules = rules; _windows = windows; _vd = vd; _dialogs = dialogs; _log = log; _icons = icons;
        _ui = Application.Current.Dispatcher;
        _settings.Changed += _ => _ui.BeginInvoke(ApplySettings);
    }

    public LiveState? Current { get; private set; }
    public event Action? LiveStateChanged;
    public event Action? StatusChanged;
    public event Action<bool>? FullscreenChanged;
    public event Action<string, string>? Notification;

    public bool IsRestoring => _restorer.IsRunning;
    public string? RestoreStatus { get; private set; }
    public RestoreReport? LastReport { get; private set; }
    /// <summary>Dauer der letzten Zustandserfassung inklusive etwaiger UI-Aktualisierung.</summary>
    public TimeSpan LastRefreshDuration { get; private set; }
    public int LastRefreshWindowCount { get; private set; }
    public bool LastRefreshSkippedUi { get; private set; }
    public DateTimeOffset? LastSavedAt => _store.LastSavedAt;
    public bool LastSaveFailed { get; private set; }
    public bool IsFullscreen => _fullscreen;
    /// <summary>Vom Widget gesetzt: nur wenn die Oberfläche sichtbar ist, wird sie laufend aktualisiert.</summary>
    public bool UiActive { get; set; } = true;
    public IntPtr WidgetHandle { get; set; }
    /// <summary>Apps, die in dieser Sitzung über DeskRecall geschlossen wurden. Kommen beim nächsten Start wieder.</summary>
    public SessionClosures Closures { get; } = new();

    // ---------------------------------------------------------------- Start

    public void Start(bool autostart)
    {
        _events = new WinEventMonitor(); // auf dem UI-Thread (Nachrichtenschleife)
        _events.WindowChanged += OnWindowChanged;
        _events.ForegroundChanged += _ => RequestFullscreenCheck();
        _events.WindowShown += OnWindowShown;
        _uiDebouncer = new ChangeDebouncer(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(250), () => _ = RefreshAsync());
        _fullscreenTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        _fullscreenTimer.Start();
        ApplySettings();
        _ = RefreshAsync();

        var s = _settings.Current;
        var last = _store.GetHistory().FirstOrDefault();
        var startupProfile = string.IsNullOrWhiteSpace(s.Restore.StartupProfile) ? null : _profiles.Get(s.Restore.StartupProfile)?.Snapshot;
        if (startupProfile is null && !string.IsNullOrWhiteSpace(s.Restore.StartupProfile))
            _log.Warn(Src, $"Startprofil „{s.Restore.StartupProfile}“ wurde nicht gefunden – es wird die letzte Sitzung verwendet.");
        var snapshot = StartupChoice.Select(startupProfile, _store.LoadLast());
        if (autostart && s.General.AutoRestoreOnStartup && snapshot is { Windows.Count: > 0 })
        {
            // Schutz: Bis die Wiederherstellung fertig ist, darf der fast leere Startzustand die Sitzung vor dem Neustart nicht überschreiben.
            var grace = TimeSpan.FromSeconds(s.Restore.StartupDelaySeconds + s.Restore.WindowTimeoutSeconds + 120);
            _guard = new StartupGuard(DateTimeOffset.Now, grace, active: true);
            _store.Protect(last?.FilePath);
            var label = startupProfile is not null && ReferenceEquals(snapshot, startupProfile) ? s.Restore.StartupProfile : "Letzte Sitzung";
            _ = AutoRestoreAsync(snapshot, TimeSpan.FromSeconds(s.Restore.StartupDelaySeconds), label);
        }
    }

    private async Task AutoRestoreAsync(SessionSnapshot snapshot, TimeSpan delay, string label)
    {
        try
        {
            RestoreStatus = $"Wiederherstellung startet in {delay.TotalSeconds:0} s …";
            StatusChanged?.Invoke();
            await Task.Delay(delay);
            await RestoreAsync(snapshot, label, offerCloseExtras: false);
        }
        catch (Exception ex) { _log.Error(Src, "Automatische Wiederherstellung fehlgeschlagen", ex); }
        finally { _guard.Release(); }
    }

    public void ApplySettings()
    {
        var s = _settings.Current;
        _store.MaxHistory = s.Backup.HistoryCount;
        _saveDebouncer?.Dispose();
        _saveDebouncer = s.Backup.Mode == SaveMode.OnChange
            ? new ChangeDebouncer(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(45), () => _ = SaveAsync(SessionKind.Auto))
            : null;
        _saveTimer?.Dispose();
        _saveTimer = s.Backup.Interval is { } interval
            ? new Timer(_ => _ = SaveAsync(SessionKind.Auto), null, interval, interval)
            : null;
        if (_log is LoggingService ls) ls.Verbose = s.Diagnostics.VerboseLogging;
    }

    // ---------------------------------------------------------------- Ereignisse

    private void OnWindowChanged(IntPtr hwnd, uint ev)
    {
        if (hwnd == WidgetHandle) return;
        if (UiActive && !_fullscreen) _uiDebouncer?.Signal(); else _refreshPending = true;
        if (ev != 0x0003 /* Vordergrund ändert die Sitzung nicht */) _saveDebouncer?.Signal();
    }

    private void OnWindowShown(IntPtr hwnd)
    {
        var rules = _settings.Current.Rules;
        if (!rules.Any(r => r.Enabled && r.ApplyLive) || IsRestoring) return;
        var h = (long)hwnd;
        lock (_liveRuleHandled) { if (!_liveRuleHandled.Add(h)) return; if (_liveRuleHandled.Count > 4000) _liveRuleHandled.Clear(); }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200); // Fenster erst fertig aufbauen lassen
                var w = _windows.EnumerateAppWindows().FirstOrDefault(x => x.Handle == h);
                if (w is null) return;
                var outcome = _rules.Evaluate(w, liveOnly: true);
                if (!outcome.HasAny || outcome.Excluded) return;
                if (outcome.TargetDesktopIndex is { } idx && _vd.CanManage)
                {
                    var desktops = _vd.GetDesktops();
                    while (desktops.Count <= idx && _settings.Current.Desktops.CreateMissingDesktops && _vd.CreateDesktop() is not null) desktops = _vd.GetDesktops();
                    if (idx < desktops.Count && w.DesktopId != desktops[idx].Id)
                    {
                        _vd.MoveWindowToDesktop(h, desktops[idx].Id);
                        _log.Info("Regeln", $"Live-Regel: {w.FriendlyName} → {desktops[idx].DisplayName}");
                    }
                }
                if (outcome.ForcedState == WindowShowState.Minimized) _windows.Minimize(h);
                else if (outcome.ForcedState is { } st && st != w.State)
                    _windows.ApplyPlacement(h, new PlacementTarget(w.NormalBounds, st, null));
            }
            catch (Exception ex) { _log.Warn("Regeln", "Live-Regel konnte nicht angewendet werden", ex); }
        });
    }

    private void RequestFullscreenCheck()
    {
        var now = Environment.TickCount64;
        if (now - _lastFullscreenCheck < MinUiRefreshMs) return;
        _lastFullscreenCheck = now;
        _ui.BeginInvoke(CheckFullscreen, DispatcherPriority.Background);
    }

    private void CheckFullscreen()
    {
        bool fs;
        try { fs = FullscreenDetector.IsFullscreenActive(WidgetHandle, out _); } catch { fs = false; }
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        FullscreenChanged?.Invoke(fs);
        if (!fs && _refreshPending) _ = RefreshAsync();
    }

    /// <summary>
    /// Unterdrückt UI-Aktualisierungen während eines Desktop-Wechsels, damit die Karten
    /// nicht mitten in der Animation neu aufgebaut werden. Nach <paramref name="hold"/>
    /// wird eine ausstehende Aktualisierung nachgeholt.
    /// </summary>
    public IDisposable QuietUi(TimeSpan hold)
    {
        Interlocked.Increment(ref _quietUi);
        return new QuietScope(this, hold);
    }

    private sealed class QuietScope : IDisposable
    {
        private readonly AppController _owner;
        private readonly TimeSpan _hold;
        private int _disposed;
        public QuietScope(AppController owner, TimeSpan hold) { _owner = owner; _hold = hold; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _ = _owner.ReleaseQuietAsync(_hold);
        }
    }

    private async Task ReleaseQuietAsync(TimeSpan hold)
    {
        try { if (hold > TimeSpan.Zero) await Task.Delay(hold); }
        catch { }
        if (Interlocked.Decrement(ref _quietUi) == 0)
        {
            _refreshPending = true;
            if (UiActive && !_fullscreen) SchedulePendingRefresh();
        }
    }

    // ---------------------------------------------------------------- Live-Zustand

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) { _refreshPending = true; return; }
        try
        {
            if (Volatile.Read(ref _quietUi) > 0)
            {
                _refreshPending = true;
                return;
            }
            var wait = _lastRefreshTick == 0 ? 0 : MinUiRefreshMs - (Environment.TickCount64 - _lastRefreshTick);
            if (wait > 0)
            {
                _refreshPending = true;
                return;
            }

            _refreshPending = false;
            var previous = Current;
            var sw = Stopwatch.StartNew();
            var state = await Task.Run(() =>
            {
                var desktops = _vd.IsAvailable ? _vd.GetDesktops() : Array.Empty<DesktopInfo>();
                var current = _vd.IsAvailable ? _vd.GetCurrentDesktopId() : null;
                var excluded = _settings.Current.Exclusions.ProcessNames.Select(AppIdentity.NormalizeProcessName).ToHashSet();
                var windows = _windows.EnumerateAppWindows(detailed: false)
                    .Where(w => !excluded.Contains(AppIdentity.NormalizeProcessName(w.ProcessName))).ToList();
                var indexById = new Dictionary<Guid, int>(desktops.Count);
                for (var i = 0; i < desktops.Count; i++) indexById[desktops[i].Id] = i;
                foreach (var w in windows)
                    w.DesktopIndex = w.IsPinned ? -1 : (indexById.TryGetValue(w.DesktopId, out var idx) ? idx : -1);

                if (!UnchangedForUi(previous, desktops, current, windows))
                {
                    foreach (var w in windows)
                        _icons.Prefetch(w.Handle, w.ExecutablePath, w.AppUserModelId);
                }
                return new LiveState(desktops, current, windows, DateTimeOffset.Now);
            });
            Closures.ForgetRunning(state.Windows);

            var skipUi = UnchangedForUi(previous, state.Desktops, state.CurrentDesktopId, state.Windows);
            Current = state;
            LastRefreshWindowCount = state.Windows.Count;
            LastRefreshSkippedUi = skipUi;
            if (!skipUi)
                await _ui.InvokeAsync(() => LiveStateChanged?.Invoke(), DispatcherPriority.Background);
            LastRefreshDuration = sw.Elapsed;
            _lastRefreshTick = Environment.TickCount64;
            _log.Debug(Src, $"Aktualisierung {LastRefreshDuration.TotalMilliseconds:0} ms, {state.Windows.Count} Fenster{(skipUi ? ", unverändert" : "")}");
        }
        catch (Exception ex) { _log.Warn(Src, "Aktualisierung fehlgeschlagen", ex); }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
            if (_refreshPending && UiActive && !_fullscreen && Volatile.Read(ref _quietUi) == 0) SchedulePendingRefresh();
        }
    }

    private void SchedulePendingRefresh()
    {
        var wait = MinUiRefreshMs - (Environment.TickCount64 - _lastRefreshTick);
        if (wait <= 0)
        {
            _refreshPending = false;
            _ = RefreshAsync();
            return;
        }
        var delay = (int)Math.Clamp(wait, 1, MinUiRefreshMs);
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            if (_refreshPending && UiActive && !_fullscreen)
            {
                _refreshPending = false;
                _ = RefreshAsync();
            }
        });
    }

    private static bool UnchangedForUi(LiveState? previous, IReadOnlyList<DesktopInfo> desktops, Guid? current, IReadOnlyList<WindowRecord> windows)
    {
        if (previous is null) return false;
        if (previous.CurrentDesktopId != current || previous.Desktops.Count != desktops.Count || previous.Windows.Count != windows.Count)
            return false;
        for (var i = 0; i < desktops.Count; i++)
        {
            var a = previous.Desktops[i]; var b = desktops[i];
            if (a.Id != b.Id || a.Name != b.Name) return false;
        }
        for (var i = 0; i < windows.Count; i++)
        {
            var a = previous.Windows[i]; var b = windows[i];
            if (a.Handle != b.Handle || a.Title != b.Title || a.DesktopId != b.DesktopId
                || a.State != b.State || a.IsPinned != b.IsPinned || a.ZOrder != b.ZOrder
                || a.ProcessName != b.ProcessName)
                return false;
        }
        return true;
    }

    public void MarkDirty() { if (_refreshPending) _ = RefreshAsync(); }

    // ---------------------------------------------------------------- Sichern

    public async Task<SaveOutcome> SaveAsync(SessionKind kind)
    {
        if (kind is SessionKind.Auto)
        {
            if (!_guard.IsReleased(DateTimeOffset.Now)) { _log.Debug(Src, "Automatische Sicherung pausiert (Start-Schutz aktiv)"); return SaveOutcome.Rejected; }
            if (IsRestoring) return SaveOutcome.Rejected;
            if (_settings.Current.Backup.Mode == SaveMode.ManualOnly) return SaveOutcome.Rejected;
        }
        if (!await _saveLock.WaitAsync(kind == SessionKind.Manual ? 10000 : 0)) return SaveOutcome.Rejected;
        try
        {
            var outcome = await Task.Run(() =>
            {
                var snap = _recorder.Capture(kind);
                if (kind != SessionKind.Profile) Closures.MergeInto(snap);
                return _store.Save(snap, force: kind == SessionKind.Manual);
            });
            LastSaveFailed = outcome == SaveOutcome.Failed;
            if (kind == SessionKind.Manual && outcome == SaveOutcome.Saved) _guard.Release();
            _ = _ui.BeginInvoke(() => StatusChanged?.Invoke());
            return outcome;
        }
        catch (Exception ex)
        {
            LastSaveFailed = true;
            _log.Error(Src, "Sicherung fehlgeschlagen", ex);
            _ = _ui.BeginInvoke(() => StatusChanged?.Invoke());
            return SaveOutcome.Failed;
        }
        finally { _saveLock.Release(); }
    }

    /// <summary>Synchron (WM_QUERYENDSESSION / SessionEnding): schnell sichern, Herunterfahren nie blockieren.</summary>
    public void SaveOnShutdown()
    {
        if (_shutdownSaved || !_settings.Current.Backup.SaveOnShutdown) return;
        _shutdownSaved = true;
        try
        {
            if (!_guard.IsReleased(DateTimeOffset.Now)) { _log.Info(Src, "Herunterfahren während des Start-Schutzes – vorherige Sitzung bleibt erhalten."); return; }
            if (IsRestoring) { _restoreCts?.Cancel(); _log.Info(Src, "Herunterfahren während der Wiederherstellung – keine neue Sicherung."); return; }
            var snap = _recorder.Capture(SessionKind.Shutdown);
            Closures.MergeInto(snap);
            var prev = _store.LoadLast();
            if (!ShutdownSnapshotPolicy.ShouldAccept(prev, snap))
            {
                _log.Info(Src, $"Sicherung beim Herunterfahren verworfen ({snap.Windows.Count} statt {prev?.Windows.Count} Fenster – Programme schlossen bereits).");
                return;
            }
            var r = _store.Save(snap);
            _log.Info(Src, $"Sicherung beim Herunterfahren: {r}");
        }
        catch (Exception ex) { _log.Error(Src, "Sicherung beim Herunterfahren fehlgeschlagen", ex); }
    }

    // ---------------------------------------------------------------- Wiederherstellen

    public async Task<RestoreReport?> RestoreAsync(SessionSnapshot snapshot, string label, bool offerCloseExtras, bool dryRun = false)
    {
        if (IsRestoring) { Notification?.Invoke("DeskRecall", "Es läuft bereits eine Wiederherstellung."); return null; }
        var options = RestoreOptions.FromSettings(_settings.Current, dryRun);
        _restoreCts = new CancellationTokenSource();
        var progress = new Progress<RestoreProgress>(p => { RestoreStatus = p.Message; StatusChanged?.Invoke(); });
        try
        {
            _log.Info(Src, $"Wiederherstellung „{label}“ gestartet ({snapshot.Windows.Count} Fenster, {snapshot.Desktops.Count} Desktops){(dryRun ? " – Probelauf" : "")}");
            RestoreStatus = "Wiederherstellung läuft …";
            StatusChanged?.Invoke();
            var report = await Task.Run(() => _restorer.RestoreAsync(snapshot, options, progress, _restoreCts.Token));
            LastReport = report;
            _log.Info(Src, report.Summary);
            Notification?.Invoke(dryRun ? "Probelauf abgeschlossen" : "Sitzung wiederhergestellt", report.Summary);

            if (offerCloseExtras && !dryRun)
            {
                var extra = await Task.Run(() => _restorer.FindExtraWindows(snapshot, options));
                if (extra.Count > 0 && _dialogs.Confirm("Weitere Programme geöffnet",
                        $"{extra.Count} geöffnete Fenster gehören nicht zum Profil „{label}“. Sollen sie geschlossen werden?\n\nDie Programme werden höflich gebeten, sich zu schließen – nicht gespeicherte Änderungen fragen sie selbst nach.",
                        "Schließen", "Offen lassen", danger: true, details: extra.Select(w => $"{w.FriendlyName} – {w.Title}")))
                {
                    foreach (var w in extra) _windows.Close(w.Handle);
                    _log.Info(Src, $"{extra.Count} Fenster außerhalb des Profils geschlossen (vom Benutzer bestätigt)");
                }
            }
            return report;
        }
        catch (InvalidOperationException ex) { _log.Warn(Src, ex.Message); return null; }
        catch (Exception ex)
        {
            _log.Error(Src, "Wiederherstellung fehlgeschlagen", ex);
            Notification?.Invoke("Wiederherstellung fehlgeschlagen", ex.Message);
            return null;
        }
        finally
        {
            RestoreStatus = null;
            _restoreCts?.Dispose(); _restoreCts = null;
            _guard.Release();
            StatusChanged?.Invoke();
            _ = RefreshAsync();
        }
    }

    public void CancelRestore() => _restoreCts?.Cancel();

    public Task<RestoreReport?> RestoreLastAsync()
    {
        var snap = _store.LoadLast();
        if (snap is null) { Notification?.Invoke("DeskRecall", "Es gibt noch keine gespeicherte Sitzung."); return Task.FromResult<RestoreReport?>(null); }
        return RestoreAsync(snap, "Letzte Sitzung", offerCloseExtras: false);
    }

    public Task<RestoreReport?> RestoreProfileAsync(string name)
    {
        if (name == SessionProfile.DefaultName) return RestoreLastAsync();
        var p = _profiles.Get(name);
        if (p?.Snapshot is null) { Notification?.Invoke("DeskRecall", $"Profil „{name}“ wurde nicht gefunden."); return Task.FromResult<RestoreReport?>(null); }
        return RestoreAsync(p.Snapshot, name, offerCloseExtras: true);
    }

    public Task<RestoreReport?> RestoreHistoryAsync(string file)
    {
        var snap = _store.LoadHistory(file);
        if (snap is null) { Notification?.Invoke("DeskRecall", "Diese Sicherung ist nicht mehr lesbar."); return Task.FromResult<RestoreReport?>(null); }
        return RestoreAsync(snap, "Sicherung vom " + snap.CreatedAt.ToString("dd.MM.yyyy HH:mm"), offerCloseExtras: false);
    }

    public SessionSnapshot CaptureForProfile() => _recorder.Capture(SessionKind.Profile);

    /// <summary>
    /// Speichert genau den gerade offenen Zustand als Profil und lädt dieses Profil beim nächsten Systemstart.
    /// In dieser Sitzung geschlossene Apps gehören nicht dazu.
    /// </summary>
    public async Task<bool> TakeSnapshotAsync()
    {
        var current = _settings.Current.Restore.StartupProfile;
        var suggested = string.IsNullOrWhiteSpace(current) ? "Arbeitsplatz" : current;
        var raw = _dialogs.Prompt(
            "Snapshot",
            "Name für diesen Zustand. Es wird gespeichert, was gerade geöffnet ist – Monitor, Position, Größe, Snap und jedes Fenster.\nAusgegraute, in dieser Sitzung geschlossene Apps gehören nicht dazu.\nGenau dieses Profil wird beim nächsten Systemstart geladen.",
            suggested,
            "Snapshot speichern");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string name;
        try { name = ProfileManager.ValidateName(raw); }
        catch (ArgumentException ex) { _dialogs.Info("Snapshot", ex.Message); return false; }

        if (_profiles.Exists(name) && !string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
        {
            if (!_dialogs.Confirm("Profil ersetzen", $"Profil „{name}“ gibt es schon. Mit diesem Snapshot ersetzen und beim Systemstart laden?", "Ersetzen", "Abbrechen"))
                return false;
        }

        SessionSnapshot snap;
        try { snap = await Task.Run(() => _recorder.Capture(SessionKind.Profile)); }
        catch (Exception ex)
        {
            _log.Error(Src, "Snapshot fehlgeschlagen", ex);
            _dialogs.Info("Snapshot", "Der Zustand konnte nicht erfasst werden: " + ex.Message);
            return false;
        }
        if (snap.Windows.Count == 0 && !_dialogs.Confirm("Leerer Snapshot", "Es ist kein Programmfenster geöffnet. Diesen leeren Zustand beim Systemstart laden?", "Trotzdem speichern", "Abbrechen"))
            return false;

        try
        {
            if (_profiles.Exists(name)) _profiles.Update(name, snap);
            else _profiles.Create(name, snap);
        }
        catch (Exception ex)
        {
            _log.Error(Src, "Snapshot konnte nicht gespeichert werden", ex);
            _dialogs.Info("Snapshot", ex.Message);
            return false;
        }
        _settings.Update(s => { s.Restore.StartupProfile = name; s.ActiveProfile = name; });
        _log.Info(Src, $"Snapshot „{name}“: {snap.Windows.Count} Fenster auf {snap.Desktops.Count} Desktops, wird beim Systemstart geladen.");
        Notification?.Invoke("Snapshot gespeichert", $"„{name}“ wird beim Systemstart geladen ({snap.Windows.Count} Fenster, {snap.Desktops.Count} Desktops).");
        StatusChanged?.Invoke();
        return true;
    }

    public void Dispose()
    {
        _events?.Dispose();
        _saveDebouncer?.Dispose();
        _uiDebouncer?.Dispose();
        _saveTimer?.Dispose();
        _fullscreenTimer?.Stop();
    }
}
