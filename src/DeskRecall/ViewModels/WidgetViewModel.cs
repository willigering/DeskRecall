using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Browsers;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Profiles;
using DeskRecall.Core.Restore;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Session;
using DeskRecall.Core.Storage;
using DeskRecall.Services;

namespace DeskRecall.ViewModels;

public sealed class AppRowViewModel : ObservableObject
{
    private ImageSource? _icon;
    private string _title = "";
    private int _windowCount = 1;
    private int _tabCount;
    private bool _isWindowsExpanded;
    private bool _lastRunning, _lastMinimized;
    private long _lastPrimaryHandle;

    public required string AppKey { get; init; }
    public required string AppName { get; init; }
    public string? ExecutablePath { get; set; }
    public string? AppUserModelId { get; set; }
    private bool _sessionClosed;
    public List<WindowRecord> Windows { get; set; } = new();
    public ObservableCollection<WindowItemViewModel> WindowItems { get; } = new();
    public long PrimaryHandle => AppRowInput.MostRecent(Windows)?.Handle ?? 0;
    public bool IsWindowsExpanded
    {
        get => _isWindowsExpanded;
        set => Set(ref _isWindowsExpanded, AppRowInput.ShouldKeepExpanded(WindowCount, value, TabCount));
    }
    public Guid DesktopId { get; init; }
    public bool IsPinnedEntry { get; set; }
    public Guid? PinRuleId { get; set; }
    public bool IsSessionClosed
    {
        get => _sessionClosed;
        set { if (Set(ref _sessionClosed, value)) { OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(ToolTipText)); } }
    }
    public bool IsRunning => !IsSessionClosed && Windows.Count > 0 && PrimaryHandle != 0;

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public int WindowCount { get => _windowCount; set { if (Set(ref _windowCount, value)) NotifyExpandable(); } }
    /// <summary>Tabs, wenn genau ein Browserfenster da ist (sonst stehen die Tabs unter den Fensterzeilen).</summary>
    public int TabCount { get => _tabCount; private set { if (Set(ref _tabCount, value)) NotifyExpandable(); } }
    public bool HasMultipleWindows => WindowCount > 1;
    public bool CanExpand => AppRowInput.CanExpand(WindowCount, TabCount);
    public string BadgeText => WindowCount > 1 ? WindowCount.ToString() : TabCount.ToString();
    public string BadgeToolTip => WindowCount > 1 ? $"{WindowCount} Fenster – auf- oder zuklappen" : $"{TabCount} Tabs – auf- oder zuklappen";
    public bool IsMinimized => IsRunning && Windows.All(w => w.State == WindowShowState.Minimized);
    public string ToolTipText =>
        IsSessionClosed ? $"{AppName}\nIn dieser Sitzung geschlossen – beim nächsten Systemstart wieder da"
        : IsPinnedEntry && !IsRunning ? $"{AppName}\nAngepinnt – klicken zum Starten auf diesem Desktop"
        : Windows.Count <= 1 ? $"{AppName}\n{Title}"
        : $"{AppName} – {Windows.Count} Fenster\n" + string.Join("\n", Windows.Take(8).Select(w => "• " + w.DisplayTitle));

    void NotifyExpandable()
    {
        OnPropertyChanged(nameof(HasMultipleWindows));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BadgeToolTip));
    }

    public void RefreshTabCount() => TabCount = WindowItems.Count == 1 ? WindowItems[0].Tabs.Count : 0;

    public void SyncWindowItems()
    {
        var listChanged = false;
        for (var i = 0; i < Windows.Count; i++)
        {
            var w = Windows[i];
            if (i < WindowItems.Count)
            {
                var item = WindowItems[i];
                if (item.Handle == w.Handle) { if (item.Record.Title != w.Title || item.Record.State != w.State) listChanged = true; item.Update(w); }
                else
                {
                    var existing = WindowItems.Skip(i + 1).FirstOrDefault(x => x.Handle == w.Handle);
                    if (existing is not null) { WindowItems.Move(WindowItems.IndexOf(existing), i); existing.Update(w); }
                    else WindowItems.Insert(i, new WindowItemViewModel(w));
                    listChanged = true;
                }
            }
            else { WindowItems.Add(new WindowItemViewModel(w)); listChanged = true; }
        }
        while (WindowItems.Count > Windows.Count) { WindowItems.RemoveAt(WindowItems.Count - 1); listChanged = true; }
        var multi = WindowItems.Count > 1;
        foreach (var item in WindowItems) item.ShowHeader = multi;

        var running = IsRunning;
        var minimized = IsMinimized;
        var handle = PrimaryHandle;
        if (running != _lastRunning) { _lastRunning = running; OnPropertyChanged(nameof(IsRunning)); }
        if (minimized != _lastMinimized) { _lastMinimized = minimized; OnPropertyChanged(nameof(IsMinimized)); }
        if (handle != _lastPrimaryHandle) { _lastPrimaryHandle = handle; OnPropertyChanged(nameof(PrimaryHandle)); }
        if (listChanged) OnPropertyChanged(nameof(ToolTipText));
        RefreshTabCount();
        IsWindowsExpanded = AppRowInput.ShouldKeepExpanded(WindowCount, IsWindowsExpanded, TabCount);
    }
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
    public int AppCount => Apps.Count;
    public bool IsEmpty => Apps.Count == 0;
    public bool HasArrangement => Apps.Any(a => a.IsRunning);
    private int _notifiedAppCount = int.MinValue;
    private bool _notifiedEmpty = true, _notifiedArrangement;
    public void NotifyApps()
    {
        var count = Apps.Count;
        var empty = count == 0;
        var has = Apps.Any(a => a.IsRunning);
        if (count != _notifiedAppCount) { _notifiedAppCount = count; OnPropertyChanged(nameof(AppCount)); }
        if (empty != _notifiedEmpty) { _notifiedEmpty = empty; OnPropertyChanged(nameof(IsEmpty)); }
        if (has != _notifiedArrangement) { _notifiedArrangement = has; OnPropertyChanged(nameof(HasArrangement)); }
    }
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
    private readonly HashSet<string> _expandedAppRows = new();
    private readonly HashSet<long> _expandedWindowTabs = new();
    private readonly TabService _tabs;
    private readonly TabAliasStore _aliases;
    private bool _tabRefreshQueued;

    private bool _isExpanded = true;
    private bool _isHistoryOpen, _isStatusOpen;
    private string _selectedProfile = SessionProfile.DefaultName;
    private bool _suppressProfileChange;

    public WidgetViewModel(AppController controller, DesktopManager desktops, ISettingsService settings, ISessionStore store,
        IProfileManager profiles, IDialogService dialogs, IconService icons, ILogService log,
        IProcessManager processes, IInstalledAppSource apps, UpdateController updates, TabService tabs, TabAliasStore aliases)
    {
        _tabs = tabs; _aliases = aliases;
        _tabs.Changed += QueueTabRefresh;
        _aliases.Changed += QueueTabRefresh;
        _controller = controller; _desktops = desktops; _settings = settings; _store = store; _profiles = profiles;
        _dialogs = dialogs; _icons = icons; _log = log;
        _processes = processes; _apps = apps; Updates = updates;

        _controller.LiveStateChanged += Rebuild;
        _controller.StatusChanged += () => { OnPropertyChanged(nameof(StatusTime)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsRestoring)); OnPropertyChanged(nameof(StatusOk)); OnPropertyChanged(nameof(StatusDetail)); };
        _store.HistoryChanged += () => Application.Current.Dispatcher.BeginInvoke(LoadHistory);
        _profiles.Changed += () => Application.Current.Dispatcher.BeginInvoke(LoadProfiles);
        _settings.Changed += _ => Application.Current.Dispatcher.BeginInvoke(() => { OnPropertyChanged(nameof(IsPinned)); OnPropertyChanged(nameof(StartupHint)); Rebuild(); });

        _selectedProfile = _settings.Current.ActiveProfile;
        LoadProfiles();
        LoadHistory();

        SwitchDesktopCommand = new AsyncRelayCommand(async p => { if (p is DesktopCardViewModel c) await SwitchToCardAsync(c); });
        ToggleCardCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c) { c.IsExpanded = !c.IsExpanded; RememberCollapsed(); } });
        AddDesktopCommand = new AsyncRelayCommand(AddDesktopAsync, () => _desktops.CanManage);
        AddAppCommand = new RelayCommand(p => AddApp(p as DesktopCardViewModel ?? ActiveCard));
        RestoreArrangementCommand = new AsyncRelayCommand(p => RestoreArrangementAsync(p as DesktopCardViewModel ?? ActiveCard));
        ToggleAppWindowsCommand = new RelayCommand(p => ToggleAppWindows(p as AppRowViewModel));
        RemovePinnedAppCommand = new RelayCommand(p => { if (p is AppRowViewModel a) RemovePinned(a); });
        DuplicateDesktopCommand = new AsyncRelayCommand(p => DuplicateDesktopAsync(p as DesktopCardViewModel ?? ActiveCard), _ => _desktops.CanManage && Cards.Count > 0);
        DeleteDesktopCommand = new AsyncRelayCommand(p => DeleteDesktopAsync(p as DesktopCardViewModel ?? ActiveCard), _ => _desktops.CanManage && Cards.Count > 1);
        BeginRenameCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c && _desktops.CanManage) { c.EditName = c.Name.Length > 0 ? c.Name : c.DisplayName; c.IsEditing = true; } });
        CommitRenameCommand = new AsyncRelayCommand(async p => { if (p is DesktopCardViewModel c) await CommitRenameAsync(c); });
        CancelRenameCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c) c.IsEditing = false; });
        ActivateAppCommand = new AsyncRelayCommand(ActivateOrLaunchAsync);
        ActivateWindowCommand = new AsyncRelayCommand(async p =>
        {
            if (p is WindowItemViewModel w) await ActivateWindowQuietAsync(w.Handle, w.DesktopId);
            else if (p is WindowRecord r) await ActivateWindowQuietAsync(r.Handle, r.DesktopId);
        });
        ActivateTabCommand = new AsyncRelayCommand(async p => { if (p is TabRowViewModel t) await ActivateTabAsync(t); });
        ToggleWindowTabsCommand = new RelayCommand(p => ToggleWindowTabs(p as WindowItemViewModel));
        BeginTabRenameCommand = new RelayCommand(p => { if (p is TabRowViewModel t) BeginTabRename(t); });
        CommitTabRenameCommand = new RelayCommand(p => { if (p is TabRowViewModel t) CommitTabRename(t); });
        CancelTabRenameCommand = new RelayCommand(p => { if (p is TabRowViewModel t) t.IsEditing = false; });
        ResetTabAliasCommand = new RelayCommand(p => { if (p is TabRowViewModel t) ResetTabAlias(t); });
        MinimizeAppCommand = new AsyncRelayCommand(async p => { if (p is AppRowViewModel a) foreach (var w in a.Windows) await _desktops.MinimizeAsync(w.Handle); });
        CloseAppCommand = new AsyncRelayCommand(CloseAppAsync);
        SnapshotCommand = new AsyncRelayCommand(() => _controller.TakeSnapshotAsync(), () => !_controller.IsRestoring);
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
    public AsyncRelayCommand ActivateTabCommand { get; }
    public RelayCommand ToggleWindowTabsCommand { get; }
    public RelayCommand BeginTabRenameCommand { get; }
    public RelayCommand CommitTabRenameCommand { get; }
    public RelayCommand CancelTabRenameCommand { get; }
    public RelayCommand ResetTabAliasCommand { get; }
    /// <summary>Läuft gerade eine Tab-Umbenennung (Tastaturmodus des Widgets).</summary>
    public bool TabReadingActive { get => _tabs.Active; set => _tabs.Active = value; }
    public bool IsEditingAnyTab => AllTabs().Any(t => t.IsEditing);
    public AsyncRelayCommand MinimizeAppCommand { get; }
    public AsyncRelayCommand CloseAppCommand { get; }
    public AsyncRelayCommand SnapshotCommand { get; }
    public string StartupHint
    {
        get
        {
            var name = _settings.Current.Restore.StartupProfile;
            return string.IsNullOrWhiteSpace(name)
                ? "Beim Systemstart wird die letzte Sitzung geladen."
                : $"Beim Systemstart wird „{name}“ geladen.";
        }
    }
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
        _tabs.Observe(state.Windows);
        var collapsed = _settings.Current.Widget.CollapsedCards.ToHashSet();
        var desktops = state.Desktops.Count > 0 ? state.Desktops : new[] { new DesktopInfo { Id = Guid.Empty, Index = 0, Name = "Desktop 1" } };
        var byDesktop = desktops.ToDictionary(d => d.Id, _ => new List<WindowRecord>());
        var activeId = state.CurrentDesktopId ?? desktops[0].Id;
        foreach (var w in state.Windows)
        {
            var id = state.Desktops.Count == 0 ? desktops[0].Id : (w.IsPinned ? activeId : w.DesktopId);
            if (byDesktop.TryGetValue(id, out var list)) list.Add(w);
        }

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
            card.IsActive = d.Id == activeId;
            SyncApps(card, byDesktop[d.Id]);
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
                    AppUserModelId = first.AppUserModelId,
                    DesktopId = card.Id,
                };
                row.Icon = _icons.ForWindow(first.Handle, first.ExecutablePath, first.AppUserModelId);
            }
            row.IsSessionClosed = false;
            row.ExecutablePath ??= first.ExecutablePath;
            row.AppUserModelId ??= first.AppUserModelId;
            row.Windows = g;
            row.Title = string.IsNullOrWhiteSpace(first.Title) ? row.AppName : first.Title;
            row.WindowCount = g.Count;
            row.Icon ??= _icons.ForWindow(first.Handle, first.ExecutablePath, first.AppUserModelId);
            row.SyncWindowItems();
            SyncTabs(row);
            ApplyExpandedState(row, card.Id);
            result.Add(row);
        }
        foreach (var closed in _controller.Closures.ForDesktop(card.Id, card.Index))
        {
            if (result.Any(r => r.AppKey == closed.AppKey)) continue;
            if (!existing.TryGetValue(closed.AppKey, out var row) || !row.IsSessionClosed)
            {
                row = new AppRowViewModel
                {
                    AppKey = closed.AppKey,
                    AppName = closed.AppName,
                    ExecutablePath = closed.ExecutablePath,
                    AppUserModelId = closed.AppUserModelId,
                    DesktopId = card.Id,
                    IsSessionClosed = true,
                };
                row.Icon = _icons.ForPath(closed.ExecutablePath);
            }
            row.IsSessionClosed = true;
            row.ExecutablePath ??= closed.ExecutablePath;
            row.AppUserModelId ??= closed.AppUserModelId;
            row.Windows = new List<WindowRecord>();
            row.WindowCount = 0;
            row.Title = closed.Windows.Count > 1
                ? $"In dieser Sitzung geschlossen · {closed.Windows.Count} Fenster"
                : "In dieser Sitzung geschlossen";
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
        card.NotifyApps();
    }

    static string AppExpandKey(Guid desktopId, string appKey) => desktopId.ToString("N") + ":" + appKey;

    private void ApplyExpandedState(AppRowViewModel row, Guid desktopId)
    {
        var keep = _expandedAppRows.Contains(AppExpandKey(desktopId, row.AppKey));
        row.IsWindowsExpanded = AppRowInput.ShouldKeepExpanded(row.WindowCount, keep, row.TabCount);
    }

    public void ToggleAppWindows(AppRowViewModel? row)
    {
        if (row is null || !row.CanExpand) return;
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
        var picked = DeskRecall.Views.AppPickerWindow.Pick(owner, _apps, _dialogs, _icons, card.DisplayName);
        if (picked is not { } p) return;
        PinEntry(p.Entry, card);
        if (p.Launch) _ = LaunchPickedAsync(p.Entry, card);
    }

    /// <summary>„Hinzufügen und starten“: auf den Desktop der Karte wechseln und das Programm dort starten.</summary>
    private Task LaunchPickedAsync(InstalledAppEntry entry, DesktopCardViewModel card)
    {
        var uwp = entry.IsUwp && !string.IsNullOrWhiteSpace(entry.AppUserModelId);
        var req = new LaunchRequest(
            uwp ? "explorer.exe" : (entry.TargetPath ?? entry.ShortcutPath ?? entry.Name),
            uwp ? CommandLine.Quote("shell:AppsFolder\\" + entry.AppUserModelId) : entry.Arguments,
            uwp ? null : (entry.WorkingDirectory ?? (entry.TargetPath is null ? null : WinPath.GetDirectoryName(entry.TargetPath))),
            entry.AppUserModelId,
            false,
            false,
            entry.Name);
        return LaunchOnDesktopAsync(card.Id, req);
    }

    private async Task LaunchOnDesktopAsync(Guid desktopId, LaunchRequest req)
    {
        try
        {
            using (_controller.QuietUi(TimeSpan.FromMilliseconds(400)))
            {
                if (_desktops.CanManage && desktopId != Guid.Empty) { MarkActive(desktopId); await _desktops.SwitchAsync(desktopId, Stopwatch.GetTimestamp()); }
                var result = _processes.Launch(req);
                if (result.Status is LaunchStatus.NotFound or LaunchStatus.Failed)
                    _dialogs.Info("Start fehlgeschlagen", result.Message ?? "Das Programm konnte nicht gestartet werden.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Apps", "Programm konnte nicht gestartet werden", ex);
            _dialogs.Info("Start fehlgeschlagen", ex.Message);
        }
    }

    public InstalledAppEntry? ResolveDropped(string path) => _apps.ResolveShortcut(path) ?? InstalledAppCatalog.FromDroppedPath(path);

    public async Task ActivateWindowByHandleAsync(long handle)
    {
        var win = Cards.SelectMany(c => c.Apps).SelectMany(a => a.Windows).FirstOrDefault(w => w.Handle == handle);
        if (win is not null) await ActivateWindowQuietAsync(win.Handle, win.DesktopId);
        else await ActivateWindowQuietAsync(handle, null);
    }

    private async Task ActivateWindowQuietAsync(long handle, Guid? desktopId, Func<Task>? beforeFocus = null)
    {
        var click = Stopwatch.GetTimestamp();
        if (desktopId is Guid d && d != Guid.Empty) MarkActive(d);
        using (_controller.QuietUi(TimeSpan.FromMilliseconds(400)))
            await _desktops.BringToFrontAsync(handle, desktopId, click, beforeFocus);
    }

    /// <summary>Desktop-Kopf geklickt: Karte sofort als aktiv zeigen, im Hintergrund wechseln, währenddessen kein Neuaufbau.</summary>
    public async Task SwitchToCardAsync(DesktopCardViewModel card)
    {
        var click = Stopwatch.GetTimestamp();
        if (card.IsActive) return;
        MarkActive(card.Id);
        using (_controller.QuietUi(TimeSpan.FromMilliseconds(400)))
            await _desktops.SwitchAsync(card.Id, click);
    }

    /// <summary>Optimistisch: nur IsActive umschalten (billig), der eigentliche Abgleich folgt nach dem Wechsel.</summary>
    private void MarkActive(Guid desktopId)
    {
        if (Cards.All(c => c.Id != desktopId)) return;
        foreach (var c in Cards) c.IsActive = c.Id == desktopId;
        OnPropertyChanged(nameof(ActiveCard));
    }

    // ------------------------------------------------------------ Browser-Tabs

    private IEnumerable<TabRowViewModel> AllTabs() =>
        Cards.SelectMany(c => c.Apps).SelectMany(a => a.WindowItems).SelectMany(w => w.Tabs);

    private void SyncTabs(AppRowViewModel row)
    {
        foreach (var item in row.WindowItems)
        {
            item.Family = TabService.FamilyOf(item.Record);
            if (item.Family == BrowserFamily.None) { if (item.Tabs.Count > 0) item.SyncTabs(Array.Empty<BrowserTab>(), "", _ => null); continue; }
            var proc = item.Record.ProcessName ?? "";
            item.SyncTabs(_tabs.Get(item.Handle), proc, title => _aliases.Find(proc, title));
            item.IsTabsExpanded = _expandedWindowTabs.Contains(item.Handle);
        }
        row.RefreshTabCount();
    }

    private void QueueTabRefresh()
    {
        if (_tabRefreshQueued) return;
        _tabRefreshQueued = true;
        Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshTabs);
    }

    private void RefreshTabs()
    {
        _tabRefreshQueued = false;
        foreach (var card in Cards)
            foreach (var row in card.Apps)
            {
                SyncTabs(row);
                row.IsWindowsExpanded = AppRowInput.ShouldKeepExpanded(row.WindowCount, _expandedAppRows.Contains(AppExpandKey(card.Id, row.AppKey)), row.TabCount);
            }
    }

    public void ToggleWindowTabs(WindowItemViewModel? item)
    {
        if (item is null || !item.HasTabs) return;
        item.IsTabsExpanded = !item.IsTabsExpanded;
        if (item.IsTabsExpanded) _expandedWindowTabs.Add(item.Handle);
        else _expandedWindowTabs.Remove(item.Handle);
    }

    /// <summary>Tab-Klick: Desktop wechseln, Tab per UI Automation wählen, Fenster in den Vordergrund.</summary>
    public async Task ActivateTabAsync(TabRowViewModel tab)
    {
        var w = tab.Window;
        var selected = false;
        await ActivateWindowQuietAsync(w.Handle, w.DesktopId, async () => selected = await _tabs.SelectAsync(w.Handle, w.Family, tab.Tab));
        // manche Browser wählen Tabs erst, wenn ihr Fenster im Vordergrund ist
        if (!selected) await _tabs.SelectAsync(w.Handle, w.Family, tab.Tab);
    }

    public void BeginTabRename(TabRowViewModel tab)
    {
        foreach (var t in AllTabs()) if (t.IsEditing && t != tab) CommitTabRename(t);
        tab.EditName = tab.DisplayTitle;
        tab.IsEditing = true;
    }

    public void CommitTabRename(TabRowViewModel tab)
    {
        if (!tab.IsEditing) return;
        tab.IsEditing = false;
        _aliases.Set(tab.ProcessName, tab.Tab.Title, tab.EditName);
        tab.SetAlias(_aliases.Find(tab.ProcessName, tab.Tab.Title));
    }

    public void ResetTabAlias(TabRowViewModel tab)
    {
        tab.IsEditing = false;
        _aliases.Reset(tab.ProcessName, tab.Tab.Title);
        tab.SetAlias(null);
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
            var handle = AppRowInput.MostRecent(a.Windows)?.Handle ?? a.PrimaryHandle;
            await ActivateWindowQuietAsync(handle, a.DesktopId);
            return;
        }
        if (!a.IsPinnedEntry && !a.IsSessionClosed) return;
        await LaunchOnDesktopAsync(a.DesktopId, new LaunchRequest(
            a.AppUserModelId is null ? (a.ExecutablePath ?? a.AppName) : "explorer.exe",
            a.AppUserModelId is null ? null : CommandLine.Quote("shell:AppsFolder\\" + a.AppUserModelId),
            a.ExecutablePath is null ? null : WinPath.GetDirectoryName(a.ExecutablePath),
            a.AppUserModelId,
            false,
            false,
            a.AppName));
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
    private async Task DuplicateDesktopAsync(DesktopCardViewModel? src)
    {
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
        if (p is not AppRowViewModel a || !a.IsRunning) return;
        var n = a.Windows.Count;
        var msg = n <= 1
            ? $"„{a.AppName}“ nur für diese Sitzung schließen?\n\nDie Zeile bleibt ausgegraut. Beim nächsten Systemstart wird die App wieder geöffnet. Das Startprofil ändert sich nicht."
            : $"Alle {n} Fenster von {a.AppName} nur für diese Sitzung schließen?\n\nDie Zeile bleibt ausgegraut. Beim nächsten Systemstart sind sie wieder da. Das Startprofil ändert sich nicht.";
        if (!_dialogs.Confirm("Für diese Sitzung schließen", msg, "Schließen", "Abbrechen", danger: true)) return;
        _controller.Closures.Remember(new SessionClosure
        {
            AppKey = a.AppKey,
            AppName = a.AppName,
            DesktopId = a.DesktopId,
            DesktopIndex = a.Windows.FirstOrDefault()?.DesktopIndex ?? -1,
            ExecutablePath = a.ExecutablePath ?? a.Windows.FirstOrDefault()?.ExecutablePath,
            AppUserModelId = a.AppUserModelId ?? a.Windows.FirstOrDefault()?.AppUserModelId,
            Windows = a.Windows.Select(w => w.Clone()).ToList(),
        });
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
