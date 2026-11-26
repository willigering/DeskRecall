using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Geometry;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Profiles;
using DeskRecall.Core.Restore;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Storage;
using DeskRecall.Services;

namespace DeskRecall.ViewModels;

public sealed class AppRowViewModel : ObservableObject
{
    private ImageSource? _icon;
    private string _title = "";
    private int _windowCount = 1;
    private bool _isWindowsExpanded;

    public required string AppKey { get; init; }
    public required string AppName { get; init; }
    public string? ExecutablePath { get; init; }
    public string? AppUserModelId { get; init; }
    public List<WindowRecord> Windows { get; set; } = new();
    public ObservableCollection<WindowRecord> WindowItems { get; } = new();
    public long PrimaryHandle => Windows.FirstOrDefault()?.Handle ?? 0;
    public bool IsWindowsExpanded
    {
        get => _isWindowsExpanded;
        set => Set(ref _isWindowsExpanded, AppRowInput.ShouldKeepExpanded(WindowCount, value));
    }
    public Guid DesktopId { get; init; }
    public bool IsPinnedEntry { get; set; }
    public Guid? PinRuleId { get; set; }
    public bool IsRunning => Windows.Count > 0 && PrimaryHandle != 0;

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public int WindowCount { get => _windowCount; set { if (Set(ref _windowCount, value)) OnPropertyChanged(nameof(HasMultipleWindows)); } }
    public bool HasMultipleWindows => WindowCount > 1;
    public bool IsMinimized => IsRunning && Windows.All(w => w.State == WindowShowState.Minimized);
    public string ToolTipText =>
        IsPinnedEntry && !IsRunning ? $"{AppName}\nAngepinnt – klicken zum Starten auf diesem Desktop"
        : Windows.Count <= 1 ? $"{AppName}\n{Title}"
        : $"{AppName} – {Windows.Count} Fenster\n" + string.Join("\n", Windows.Take(8).Select(w => "• " + w.DisplayTitle));
    public void SyncWindowItems()
    {
        for (var i = 0; i < Windows.Count; i++)
        {
            if (i < WindowItems.Count) WindowItems[i] = Windows[i];
            else WindowItems.Add(Windows[i]);
        }
        while (WindowItems.Count > Windows.Count) WindowItems.RemoveAt(WindowItems.Count - 1);
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsMinimized));
        OnPropertyChanged(nameof(ToolTipText));
        OnPropertyChanged(nameof(PrimaryHandle));
        IsWindowsExpanded = AppRowInput.ShouldKeepExpanded(WindowCount, IsWindowsExpanded);
    }
}

public sealed class ArrangementTileViewModel
{
    public required ArrangementWindowTile Tile { get; init; }
    public ImageSource? Icon { get; init; }
    public WindowRecord? Window { get; init; }
    public double Left => Tile.Bounds.Left;
    public double Top => Tile.Bounds.Top;
    public double Width => Tile.Bounds.Width;
    public double Height => Tile.Bounds.Height;
    public string AppName => Tile.AppName;
    public long Handle => Tile.Handle;
    public bool IsMinimized => Tile.IsMinimized;
}

public sealed class DesktopCardViewModel : ObservableObject
{
    private string _name = "";
    private int _index;
    private bool _isActive, _isExpanded = true, _isEditing, _isDropTarget;
    private string _editName = "";

    public required Guid Id { get; init; }
    public int Index { get => _index; set { if (Set(ref _index, value)) { OnPropertyChanged(nameof(Number)); OnPropertyChanged(nameof(ShortNumber)); OnPropertyChanged(nameof(DisplayName)); } } }
    public string Number => (Index + 1).ToString("00");
    public string ShortNumber => (Index + 1).ToString();
    public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Desktop {Index + 1}" : Name;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public ObservableCollection<AppRowViewModel> Apps { get; } = new();
    public ObservableCollection<ArrangementTileViewModel> PreviewTiles { get; } = new();
    public ObservableCollection<ArrangementMonitorTile> PreviewMonitors { get; } = new();
    public int AppCount => Apps.Count;
    public bool IsEmpty => Apps.Count == 0;
    public bool HasArrangement => PreviewTiles.Count > 0;
    public int PreviewWidth { get; set; } = ArrangementPreview.DefaultWidth;
    public int PreviewHeight { get; set; } = ArrangementPreview.DefaultHeight;
    public void NotifyApps() { OnPropertyChanged(nameof(AppCount)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HasArrangement)); OnPropertyChanged(nameof(PreviewWidth)); OnPropertyChanged(nameof(PreviewHeight)); }
}

public sealed class HistoryItemViewModel
{
    public required HistoryEntry Entry { get; init; }
    public required string When { get; init; }
    public required string Summary { get; init; }
    public string KindText => Entry.Kind switch
    {
        SessionKind.Manual => "Manuell",
        SessionKind.Shutdown => "Beim Herunterfahren",
        SessionKind.Profile => "Profil",
        SessionKind.Imported => "Importiert",
        _ => "Automatisch",
    };
}

/// <summary>Daten und Befehle des Widgets am Bildschirmrand.</summary>
public sealed class WidgetViewModel : ObservableObject
{
    private readonly AppController _controller;
    private readonly DesktopManager _desktops;
    private readonly ISettingsService _settings;
    private readonly ISessionStore _store;
    private readonly IProfileManager _profiles;
    private readonly IDialogService _dialogs;
    private readonly IconService _icons;
    private readonly ILogService _log;
    private readonly IProcessManager _processes;
    private readonly IInstalledAppSource _apps;
    private readonly IMonitorManager _monitors;
    private readonly HashSet<string> _expandedAppRows = new();

    private bool _isExpanded = true;
    private bool _isHistoryOpen, _isStatusOpen;
    private string _selectedProfile = SessionProfile.DefaultName;
    private bool _suppressProfileChange;

    public WidgetViewModel(AppController controller, DesktopManager desktops, ISettingsService settings, ISessionStore store,
        IProfileManager profiles, IDialogService dialogs, IconService icons, ILogService log,
        IProcessManager processes, IInstalledAppSource apps, UpdateController updates, IMonitorManager monitors)
    {
        _controller = controller; _desktops = desktops; _settings = settings; _store = store; _profiles = profiles;
        _dialogs = dialogs; _icons = icons; _log = log;
        _processes = processes; _apps = apps; Updates = updates; _monitors = monitors;

        _controller.LiveStateChanged += Rebuild;
        _controller.StatusChanged += () => { OnPropertyChanged(nameof(StatusTime)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsRestoring)); OnPropertyChanged(nameof(StatusOk)); OnPropertyChanged(nameof(StatusDetail)); };
        _store.HistoryChanged += () => Application.Current.Dispatcher.BeginInvoke(LoadHistory);
        _profiles.Changed += () => Application.Current.Dispatcher.BeginInvoke(LoadProfiles);
        _settings.Changed += _ => Application.Current.Dispatcher.BeginInvoke(() => { OnPropertyChanged(nameof(IsPinned)); Rebuild(); });

        _selectedProfile = _settings.Current.ActiveProfile;
        LoadProfiles();
        LoadHistory();

        SwitchDesktopCommand = new AsyncRelayCommand(async p => { if (p is DesktopCardViewModel c) { await _desktops.SwitchAsync(c.Id); await _controller.RefreshAsync(); } });
        ToggleCardCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c) { c.IsExpanded = !c.IsExpanded; RememberCollapsed(); } });
        AddDesktopCommand = new AsyncRelayCommand(AddDesktopAsync, () => _desktops.CanManage);
        AddAppCommand = new RelayCommand(p => AddApp(p as DesktopCardViewModel ?? ActiveCard));
        RestoreArrangementCommand = new AsyncRelayCommand(p => RestoreArrangementAsync(p as DesktopCardViewModel ?? ActiveCard));
        ToggleAppWindowsCommand = new RelayCommand(p => ToggleAppWindows(p as AppRowViewModel));
        RemovePinnedAppCommand = new RelayCommand(p => { if (p is AppRowViewModel a) RemovePinned(a); });
        DuplicateDesktopCommand = new AsyncRelayCommand(DuplicateDesktopAsync, () => _desktops.CanManage && ActiveCard is not null);
        DeleteDesktopCommand = new AsyncRelayCommand(p => DeleteDesktopAsync(p as DesktopCardViewModel ?? ActiveCard), _ => _desktops.CanManage && Cards.Count > 1);
        BeginRenameCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c && _desktops.CanManage) { c.EditName = c.Name.Length > 0 ? c.Name : c.DisplayName; c.IsEditing = true; } });
        CommitRenameCommand = new AsyncRelayCommand(async p => { if (p is DesktopCardViewModel c) await CommitRenameAsync(c); });
        CancelRenameCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c) c.IsEditing = false; });
        ActivateAppCommand = new AsyncRelayCommand(ActivateOrLaunchAsync);
        ActivateWindowCommand = new AsyncRelayCommand(async p => { if (p is WindowRecord w) { await _desktops.BringToFrontAsync(w.Handle, w.DesktopId); await _controller.RefreshAsync(); } });
        MinimizeAppCommand = new AsyncRelayCommand(async p => { if (p is AppRowViewModel a) foreach (var w in a.Windows) await _desktops.MinimizeAsync(w.Handle); });
        CloseAppCommand = new AsyncRelayCommand(CloseAppAsync);
        PinAppCommand = new AsyncRelayCommand(async p => { if (p is AppRowViewModel a) { foreach (var w in a.Windows) await _desktops.PinWindowAsync(w.Handle, true); await _controller.RefreshAsync(); } }, _ => _desktops.CanManage);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !_controller.IsRestoring);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RestoreHistoryCommand = new AsyncRelayCommand(async p => { if (p is HistoryItemViewModel h) { IsHistoryOpen = false; await _controller.RestoreHistoryAsync(h.Entry.FilePath); } }, _ => !_controller.IsRestoring);
        CancelRestoreCommand = new RelayCommand(() => _controller.CancelRestore(), () => _controller.IsRestoring);
        ToggleHistoryCommand = new RelayCommand(() => { if (!IsHistoryOpen) LoadHistory(); IsHistoryOpen = !IsHistoryOpen; });
        ToggleStatusCommand = new RelayCommand(() => IsStatusOpen = !IsStatusOpen);
        TogglePinCommand = new RelayCommand(() => _settings.Update(s => s.Widget.AlwaysOnTop = !s.Widget.AlwaysOnTop));
        RefreshCommand = new AsyncRelayCommand(() => _controller.RefreshAsync());
    }

    public ObservableCollection<DesktopCardViewModel> Cards { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<HistoryItemViewModel> History { get; } = new();

    public DesktopCardViewModel? ActiveCard => Cards.FirstOrDefault(c => c.IsActive);
    public bool CanManageDesktops => _desktops.CanManage;
    public string BackendText => _desktops.Backend;

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsHistoryOpen { get => _isHistoryOpen; set => Set(ref _isHistoryOpen, value); }
    public bool IsStatusOpen { get => _isStatusOpen; set => Set(ref _isStatusOpen, value); }
    public bool IsPinned => _settings.Current.Widget.AlwaysOnTop;
    public bool IsRestoring => _controller.IsRestoring;

    public bool StatusOk => !_controller.LastSaveFailed;
    public string StatusText => _controller.RestoreStatus ?? (_controller.LastSaveFailed ? "Sicherung fehlgeschlagen"
        : _settings.Current.Backup.Mode == SaveMode.ManualOnly ? "Manuelle Sicherung" : "Automatische Sicherung");
    public string StatusTime
    {
        get
        {
            var t = _controller.LastSavedAt ?? History.FirstOrDefault()?.Entry.CreatedAt;
            return t is null ? "–" : t.Value.ToLocalTime().ToString(t.Value.ToLocalTime().Date == DateTime.Today ? "HH:mm" : "dd.MM. HH:mm");
        }
    }
    public string StatusDetail
    {
        get
        {
            var s = _settings.Current.Backup;
            var mode = s.Mode switch
            {
                SaveMode.OnChange => "bei jeder Änderung",
                SaveMode.Every30Seconds => "alle 30 Sekunden",
                SaveMode.Every1Minute => "jede Minute",
                SaveMode.Every5Minutes => "alle 5 Minuten",
                _ => "nur manuell",
            };
            var last = History.FirstOrDefault();
            return $"Sicherung: {mode}\nLetzte Sicherung: {(last is null ? "noch keine" : HistoryRetention.FormatRelative(last.Entry.CreatedAt, DateTimeOffset.Now))}"
                 + (last is null ? "" : $"\n{last.Summary}")
                 + $"\nVerlauf: {History.Count} von {s.HistoryCount} Einträgen\nDesktops: {_desktops.Backend}";
        }
    }

    public string SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null || !Set(ref _selectedProfile, value) || _suppressProfileChange) return;
            _settings.Update(s => s.ActiveProfile = value);
        }
    }

    public RelayCommand ToggleCardCommand { get; }
    public RelayCommand BeginRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public RelayCommand ToggleHistoryCommand { get; }
    public RelayCommand ToggleStatusCommand { get; }
    public RelayCommand TogglePinCommand { get; }
    public RelayCommand CancelRestoreCommand { get; }
    public AsyncRelayCommand SwitchDesktopCommand { get; }
    public AsyncRelayCommand AddDesktopCommand { get; }
    public RelayCommand AddAppCommand { get; }
    public AsyncRelayCommand RestoreArrangementCommand { get; }
    public RelayCommand ToggleAppWindowsCommand { get; }
    public RelayCommand RemovePinnedAppCommand { get; }
    public UpdateController Updates { get; }
    public AsyncRelayCommand DuplicateDesktopCommand { get; }
    public AsyncRelayCommand DeleteDesktopCommand { get; }
    public AsyncRelayCommand CommitRenameCommand { get; }
    public AsyncRelayCommand ActivateAppCommand { get; }
    public AsyncRelayCommand ActivateWindowCommand { get; }
    public AsyncRelayCommand MinimizeAppCommand { get; }
    public AsyncRelayCommand CloseAppCommand { get; }
    public AsyncRelayCommand PinAppCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RestoreHistoryCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    // ------------------------------------------------------------ Aufbau

    private void Rebuild()
    {
        var state = _controller.Current;
        if (state is null) return;
        var collapsed = _settings.Current.Widget.CollapsedCards.ToHashSet();
        var desktops = state.Desktops.Count > 0 ? state.Desktops : new[] { new DesktopInfo { Id = Guid.Empty, Index = 0, Name = "Desktop 1" } };

        // Karten anhand der Desktop-ID abgleichen (Aufklapp-/Bearbeitungszustand bleibt erhalten)
        for (var i = 0; i < desktops.Count; i++)
        {
            var d = desktops[i];
            var card = Cards.FirstOrDefault(c => c.Id == d.Id);
            if (card is null)
            {
                card = new DesktopCardViewModel { Id = d.Id, IsExpanded = !collapsed.Contains(d.Id) };
                Cards.Insert(Math.Min(i, Cards.Count), card);
            }
            else
            {
                var at = Cards.IndexOf(card);
                if (at != i) Cards.Move(at, i);
            }
            card.Index = i;
            if (!card.IsEditing) card.Name = d.Name;
            card.IsActive = state.CurrentDesktopId is { } cur ? d.Id == cur : i == 0;
            var wins = state.Windows.Where(w => state.Desktops.Count == 0 || (w.IsPinned ? card.IsActive : w.DesktopId == d.Id)).ToList(); // angeheftete Fenster beim aktiven Desktop zeigen
            SyncApps(card, wins);
        }
        while (Cards.Count > desktops.Count) Cards.RemoveAt(Cards.Count - 1);
        OnPropertyChanged(nameof(ActiveCard));
        OnPropertyChanged(nameof(CanManageDesktops));
    }

    private void SyncApps(DesktopCardViewModel card, List<WindowRecord> windows)
    {
        var groups = windows
            .OrderBy(w => w.ZOrder)
            .GroupBy(w => w.AppKey)
            .Select(g => g.ToList())
            .ToList();
        // Reihenfolge stabil nach erstem Auftreten in der vorhandenen Liste, neue hinten
        var existing = card.Apps.ToDictionary(a => a.AppKey);
        var result = new List<AppRowViewModel>();
        foreach (var g in groups)
        {
            var first = g[0];
            if (!existing.TryGetValue(first.AppKey, out var row))
            {
                row = new AppRowViewModel
                {
                    AppKey = first.AppKey,
                    AppName = AppCatalog.FriendlyName(first.ProcessName) ?? first.FriendlyName,
                    ExecutablePath = first.ExecutablePath,
                    DesktopId = card.Id,
                };
                row.Icon = _icons.ForWindow(first.Handle, first.ExecutablePath, first.AppUserModelId);
            }
            row.Windows = g;
            row.Title = string.IsNullOrWhiteSpace(first.Title) ? row.AppName : first.Title;
            row.WindowCount = g.Count;
            row.Icon ??= _icons.ForWindow(first.Handle, first.ExecutablePath, first.AppUserModelId);
            ApplyExpandedState(row, card.Id);
            row.SyncWindowItems();
            result.Add(row);
        }
        foreach (var rule in PinnedApps.ForDesktop(_settings.Current.Rules, card.Index + 1))
        {
            var key = !string.IsNullOrWhiteSpace(rule.AppUserModelId) ? "aumid:" + rule.AppUserModelId.ToLowerInvariant()
                : "proc:" + AppIdentity.NormalizeProcessName(rule.ProcessName);
            if (result.Any(r => r.AppKey == key || (r.PinRuleId is Guid id && id == rule.Id)))
            {
                var row = result.First(r => r.AppKey == key || r.PinRuleId == rule.Id);
                row.IsPinnedEntry = true;
                row.PinRuleId = rule.Id;
                continue;
            }
            if (!existing.TryGetValue(key, out var pin) || !pin.IsPinnedEntry)
            {
                pin = new AppRowViewModel
                {
                    AppKey = key,
                    AppName = string.IsNullOrWhiteSpace(rule.Name) ? rule.ProcessName : rule.Name,
                    ExecutablePath = rule.ExecutablePath,
                    AppUserModelId = rule.AppUserModelId,
                    DesktopId = card.Id,
                    IsPinnedEntry = true,
                    PinRuleId = rule.Id,
                    Title = "Angepinnt",
                };
                pin.Icon = _icons.ForPath(rule.IconPath ?? rule.ExecutablePath);
                pin.Windows = new List<WindowRecord>();
                pin.WindowCount = 0;
                pin.SyncWindowItems();
            }
            else
            {
                pin.IsPinnedEntry = true;
                pin.PinRuleId = rule.Id;
                pin.Windows = new List<WindowRecord>();
                pin.WindowCount = 0;
                pin.Title = "Angepinnt";
                pin.SyncWindowItems();
            }
            result.Add(pin);
        }

        var ordered = result.OrderBy(r => { var i = card.Apps.IndexOf(r); return i < 0 ? int.MaxValue : i; }).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var at = card.Apps.IndexOf(ordered[i]);
            if (at < 0) card.Apps.Insert(i, ordered[i]);
            else if (at != i) card.Apps.Move(at, i);
        }
        while (card.Apps.Count > ordered.Count) card.Apps.RemoveAt(card.Apps.Count - 1);
        UpdateArrangement(card, windows);
        card.NotifyApps();
    }

    private void UpdateArrangement(DesktopCardViewModel card, List<WindowRecord> windows)
    {
        var pw = Math.Max(160, _settings.Current.Widget.ExpandedWidth - 48);
        var ph = ArrangementPreview.DefaultHeight;
        IReadOnlyList<MonitorInfo> monitors;
        try { monitors = _monitors.GetMonitors(); }
        catch { monitors = Array.Empty<MonitorInfo>(); }
        var layout = ArrangementPreview.Layout(windows, monitors, pw, ph);
        card.PreviewWidth = layout.Width;
        card.PreviewHeight = layout.Height;
        card.PreviewMonitors.Clear();
        foreach (var m in layout.Monitors) card.PreviewMonitors.Add(m);
        var byKey = card.Apps.ToLookup(a => a.AppKey);
        card.PreviewTiles.Clear();
        foreach (var tile in layout.Windows)
        {
            var row = byKey[tile.AppKey].FirstOrDefault();
            var win = row?.Windows.FirstOrDefault(w => w.Handle == tile.Handle) ?? windows.FirstOrDefault(w => w.Handle == tile.Handle);
            card.PreviewTiles.Add(new ArrangementTileViewModel
            {
                Tile = tile,
                Icon = row?.Icon ?? _icons.ForWindow(tile.Handle, win?.ExecutablePath, win?.AppUserModelId),
                Window = win,
            });
        }
    }


    static string AppExpandKey(Guid desktopId, string appKey) => desktopId.ToString("N") + ":" + appKey;

    private void ApplyExpandedState(AppRowViewModel row, Guid desktopId)
    {
        var keep = _expandedAppRows.Contains(AppExpandKey(desktopId, row.AppKey));
        row.IsWindowsExpanded = AppRowInput.ShouldKeepExpanded(row.WindowCount, keep);
    }

    public void ToggleAppWindows(AppRowViewModel? row)
    {
        if (row is null || !row.HasMultipleWindows) return;
        row.IsWindowsExpanded = !row.IsWindowsExpanded;
        var key = AppExpandKey(row.DesktopId, row.AppKey);
        if (row.IsWindowsExpanded) _expandedAppRows.Add(key);
        else _expandedAppRows.Remove(key);
    }

    public async Task RestoreArrangementAsync(DesktopCardViewModel? card)
    {
        card ??= ActiveCard;
        if (card is null) return;
        var snap = _store.LoadLast();
        if (snap is null)
        {
            _dialogs.Info("Keine Sicherung", "Es liegt noch keine gespeicherte Sitzung vor, aus der die Anordnung gelesen werden könnte.");
            return;
        }
        var saved = snap.Windows.Where(w => w.DesktopId == card.Id || w.DesktopIndex == card.Index).ToList();
        var live = card.Apps.SelectMany(a => a.Windows).ToList();
        if (saved.Count == 0 || live.Count == 0)
        {
            _dialogs.Info("Keine Anordnung", "Für diesen Desktop sind keine gespeicherten Fenster vorhanden, oder es ist derzeit keines geöffnet.");
            return;
        }
        var matches = WindowMatcher.Match(saved, live);
        var n = 0;
        foreach (var m in matches)
        {
            var rec = saved[m.SavedIndex];
            if (await _desktops.ApplyPlacementAsync(m.Live.Handle, new PlacementTarget(rec.NormalBounds, rec.State, null))) n++;
        }
        await _controller.RefreshAsync();
        if (n == 0) _dialogs.Info("Anordnung", "Die gespeicherten Fenster konnten den geöffneten nicht eindeutig zugeordnet werden.");
    }

    private void LoadProfiles()
    {
        _suppressProfileChange = true;
        try
        {
            var names = new List<string> { SessionProfile.DefaultName };
            try { names.AddRange(_profiles.List().Select(p => p.Name)); } catch (Exception ex) { _log.Warn("Profile", "Profile konnten nicht gelesen werden", ex); }
            Profiles.Clear();
            foreach (var n in names) Profiles.Add(n);
            var active = _settings.Current.ActiveProfile;
            _selectedProfile = names.Contains(active) ? active : SessionProfile.DefaultName;
            OnPropertyChanged(nameof(SelectedProfile));
        }
        finally { _suppressProfileChange = false; }
    }

    private void LoadHistory()
    {
        History.Clear();
        var now = DateTimeOffset.Now;
        foreach (var e in _store.GetHistory().Take(30))
            History.Add(new HistoryItemViewModel
            {
                Entry = e,
                When = HistoryRetention.FormatRelative(e.CreatedAt, now),
                Summary = $"{e.DesktopCount} {(e.DesktopCount == 1 ? "Desktop" : "Desktops")} · {e.AppCount} {(e.AppCount == 1 ? "App" : "Apps")}",
            });
        OnPropertyChanged(nameof(StatusTime));
        OnPropertyChanged(nameof(StatusDetail));
    }


    public void AddApp(DesktopCardViewModel? card)
    {
        card ??= ActiveCard;
        if (card is null) return;
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        var entry = DeskRecall.Views.AppPickerWindow.Pick(owner, _apps, _dialogs, _icons, card.DisplayName);
        if (entry is null) return;
        PinEntry(entry, card);
    }

    public InstalledAppEntry? ResolveDropped(string path) => _apps.ResolveShortcut(path) ?? InstalledAppCatalog.FromDroppedPath(path);

    public async Task ActivateWindowByHandleAsync(long handle)
    {
        var win = Cards.SelectMany(c => c.Apps).SelectMany(a => a.Windows).FirstOrDefault(w => w.Handle == handle);
        if (win is not null) await _desktops.BringToFrontAsync(win.Handle, win.DesktopId);
        else await _desktops.BringToFrontAsync(handle, null);
        await _controller.RefreshAsync();
    }

    public void PinEntry(InstalledAppEntry entry, DesktopCardViewModel card)
    {
        _settings.Update(s =>
        {
            var (rules, _, _) = PinnedApps.AddOrMove(s.Rules, entry, card.Index + 1);
            s.Rules = rules;
        });
        Rebuild();
    }

    public void RemovePinned(AppRowViewModel row)
    {
        if (row.PinRuleId is not Guid id) return;
        _settings.Update(s => s.Rules = PinnedApps.Remove(s.Rules, id));
        Rebuild();
    }

    private async Task ActivateOrLaunchAsync(object? p)
    {
        if (p is not AppRowViewModel a) return;
        if (a.IsRunning)
        {
            await _desktops.BringToFrontAsync(a.PrimaryHandle, a.DesktopId);
            await _controller.RefreshAsync();
            return;
        }
        if (!a.IsPinnedEntry) return;
        try
        {
            if (_desktops.CanManage) await _desktops.SwitchAsync(a.DesktopId);
            var req = new LaunchRequest(
                a.AppUserModelId is null ? (a.ExecutablePath ?? a.AppName) : "explorer.exe",
                a.AppUserModelId is null ? null : CommandLine.Quote("shell:AppsFolder\\" + a.AppUserModelId),
                a.ExecutablePath is null ? null : WinPath.GetDirectoryName(a.ExecutablePath),
                a.AppUserModelId,
                false,
                false,
                a.AppName);
            // Fix the explorer args – I'll set this properly below
            var result = _processes.Launch(req);
            if (result.Status is LaunchStatus.NotFound or LaunchStatus.Failed)
                _dialogs.Info("Start fehlgeschlagen", result.Message ?? "Das Programm konnte nicht gestartet werden.");
            else
                await Task.Delay(400);
            await _controller.RefreshAsync();
        }
        catch (Exception ex)
        {
            _log.Warn("Apps", "Angeheftetes Programm konnte nicht gestartet werden", ex);
            _dialogs.Info("Start fehlgeschlagen", ex.Message);
        }
    }

    private void RememberCollapsed() =>
        _settings.Update(s => s.Widget.CollapsedCards = Cards.Where(c => !c.IsExpanded).Select(c => c.Id).ToList());

    // ------------------------------------------------------------ Aktionen

    private async Task AddDesktopAsync()
    {
        await _desktops.CreateAsync(null);
        await _controller.RefreshAsync();
        // Nicht automatisch umbenennen: das Widget nimmt keinen Fokus, das Textfeld würde sonst offen bleiben.
        // Umbenennen per Doppelklick auf den Kartenkopf oder über das Kontextmenü.
    }

    /// <summary>Legt direkt hinter dem aktiven Desktop eine Kopie an (Name und Explorer-Ordner). Programme selbst lassen sich nicht klonen.</summary>
    private async Task DuplicateDesktopAsync()
    {
        var src = ActiveCard;
        if (src is null) return;
        var d = await _desktops.CreateAsync(src.DisplayName + " (Kopie)");
        if (d is null) return;
        await _desktops.MoveDesktopAsync(d.Id, src.Index + 1);
        var folders = src.Apps.SelectMany(a => a.Windows).Where(w => !string.IsNullOrEmpty(w.ExplorerFolderPath)).Select(w => w.ExplorerFolderPath!).Distinct().ToList();
        if (folders.Count > 0)
        {
            await _desktops.SwitchAsync(d.Id);
            foreach (var f in folders)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", CommandLine.Quote(f.StartsWith("::") ? "shell:" + f : f)) { UseShellExecute = false }); }
                catch (Exception ex) { _log.Warn("Desktops", "Ordner konnte nicht geöffnet werden", ex); }
            }
        }
        await _controller.RefreshAsync();
    }

    private async Task DeleteDesktopAsync(DesktopCardViewModel? card)
    {
        if (card is null) return;
        var msg = card.AppCount == 0
            ? $"Desktop „{card.DisplayName}“ schließen?"
            : $"Desktop „{card.DisplayName}“ schließen?\n\nSeine {card.Apps.Sum(a => a.WindowCount)} Fenster werden dabei auf den benachbarten Desktop verschoben – es wird nichts beendet.";
        if (!_dialogs.Confirm("Desktop schließen", msg, "Schließen", "Abbrechen", danger: true)) return;
        await _desktops.RemoveAsync(card.Id);
        await _controller.RefreshAsync();
    }

    private async Task CommitRenameAsync(DesktopCardViewModel c)
    {
        if (!c.IsEditing) return;
        c.IsEditing = false;
        var name = (c.EditName ?? "").Trim();
        if (name == c.Name || name == c.DisplayName && c.Name.Length == 0) return;
        c.Name = name;
        await _desktops.RenameAsync(c.Id, name);
        await _controller.RefreshAsync();
    }

    public async Task MoveAppAsync(AppRowViewModel app, DesktopCardViewModel target)
    {
        if (app.DesktopId == target.Id || !_desktops.CanManage) return;
        foreach (var w in app.Windows) await _desktops.MoveWindowAsync(w.Handle, target.Id);
        await _controller.RefreshAsync();
    }

    public Task MoveWindowAsync(WindowRecord w, DesktopCardViewModel target) => Task.Run(async () =>
    {
        await _desktops.MoveWindowAsync(w.Handle, target.Id);
        await _controller.RefreshAsync();
    });

    public async Task CreateDesktopAndMoveAsync(AppRowViewModel app)
    {
        var d = await _desktops.CreateAsync(null);
        if (d is null) return;
        foreach (var w in app.Windows) await _desktops.MoveWindowAsync(w.Handle, d.Id);
        await _controller.RefreshAsync();
    }

    private async Task CloseAppAsync(object? p)
    {
        if (p is not AppRowViewModel a) return;
        var n = a.Windows.Count;
        if (!_dialogs.Confirm("Fenster schließen", n == 1 ? $"„{a.Title}“ schließen?" : $"Alle {n} Fenster von {a.AppName} schließen?",
                "Schließen", "Abbrechen", danger: true)) return;
        foreach (var w in a.Windows) await _desktops.CloseWindowAsync(w.Handle);
        await Task.Delay(400);
        await _controller.RefreshAsync();
    }

    private async Task RestoreAsync()
    {
        var name = SelectedProfile;
        if (name != SessionProfile.DefaultName && !_dialogs.Confirm("Profil laden",
                $"Profil „{name}“ wiederherstellen?\n\nFehlende Programme werden gestartet und alle Fenster auf ihre Desktops verteilt. Es wird nichts geschlossen, ohne dass Sie gefragt werden.",
                "Wiederherstellen", "Abbrechen")) return;
        await _controller.RestoreProfileAsync(name);
    }

    private async Task SaveAsync()
    {
        var name = SelectedProfile;
        if (name != SessionProfile.DefaultName)
        {
            if (_dialogs.Confirm("Profil aktualisieren", $"Den aktuellen Zustand im Profil „{name}“ speichern?\n\nDie bisherige Version des Profils wird ersetzt.", "Profil speichern", "Nur Sitzung sichern"))
            {
                try { _profiles.Update(name, await Task.Run(_controller.CaptureForProfile)); }
                catch (Exception ex) { _dialogs.Info("Profil", "Das Profil konnte nicht gespeichert werden: " + ex.Message); }
            }
        }
        var r = await _controller.SaveAsync(SessionKind.Manual);
        if (r == SaveOutcome.Failed) _dialogs.Info("Sichern", "Die Sitzung konnte nicht gesichert werden. Details stehen in der Diagnose.");
    }
}
