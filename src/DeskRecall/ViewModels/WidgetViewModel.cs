using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Profiles;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Storage;
using DeskRecall.Services;

namespace DeskRecall.ViewModels;

public sealed class AppRowViewModel : ObservableObject
{
    private ImageSource? _icon;
    private string _title = "";
    private int _windowCount = 1;

    public required string AppKey { get; init; }
    public required string AppName { get; init; }
    public string? ExecutablePath { get; init; }
    public List<WindowRecord> Windows { get; set; } = new();
    public long PrimaryHandle => Windows.FirstOrDefault()?.Handle ?? 0;
    public Guid DesktopId { get; init; }

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public int WindowCount { get => _windowCount; set { if (Set(ref _windowCount, value)) OnPropertyChanged(nameof(HasMultipleWindows)); } }
    public bool HasMultipleWindows => WindowCount > 1;
    public bool IsMinimized => Windows.All(w => w.State == WindowShowState.Minimized);
    public string ToolTipText => Windows.Count <= 1
        ? $"{AppName}\n{Title}"
        : $"{AppName} – {Windows.Count} Fenster\n" + string.Join("\n", Windows.Take(8).Select(w => "• " + (string.IsNullOrWhiteSpace(w.Title) ? "(ohne Titel)" : w.Title)));
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
    public void NotifyApps() { OnPropertyChanged(nameof(AppCount)); OnPropertyChanged(nameof(IsEmpty)); }
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

    private bool _isExpanded = true;
    private bool _isHistoryOpen, _isStatusOpen;
    private string _selectedProfile = SessionProfile.DefaultName;
    private bool _suppressProfileChange;

    public WidgetViewModel(AppController controller, DesktopManager desktops, ISettingsService settings, ISessionStore store,
        IProfileManager profiles, IDialogService dialogs, IconService icons, ILogService log)
    {
        _controller = controller; _desktops = desktops; _settings = settings; _store = store; _profiles = profiles;
        _dialogs = dialogs; _icons = icons; _log = log;

        _controller.LiveStateChanged += Rebuild;
        _controller.StatusChanged += () => { OnPropertyChanged(nameof(StatusTime)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsRestoring)); OnPropertyChanged(nameof(StatusOk)); OnPropertyChanged(nameof(StatusDetail)); };
        _store.HistoryChanged += () => Application.Current.Dispatcher.BeginInvoke(LoadHistory);
        _profiles.Changed += () => Application.Current.Dispatcher.BeginInvoke(LoadProfiles);
        _settings.Changed += _ => Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsPinned)));

        _selectedProfile = _settings.Current.ActiveProfile;
        LoadProfiles();
        LoadHistory();

        SwitchDesktopCommand = new AsyncRelayCommand(async p => { if (p is DesktopCardViewModel c) { await _desktops.SwitchAsync(c.Id); await _controller.RefreshAsync(); } });
        ToggleCardCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c) { c.IsExpanded = !c.IsExpanded; RememberCollapsed(); } });
        AddDesktopCommand = new AsyncRelayCommand(AddDesktopAsync, () => _desktops.CanManage);
        DuplicateDesktopCommand = new AsyncRelayCommand(DuplicateDesktopAsync, () => _desktops.CanManage && ActiveCard is not null);
        DeleteDesktopCommand = new AsyncRelayCommand(p => DeleteDesktopAsync(p as DesktopCardViewModel ?? ActiveCard), _ => _desktops.CanManage && Cards.Count > 1);
        BeginRenameCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c && _desktops.CanManage) { c.EditName = c.Name.Length > 0 ? c.Name : c.DisplayName; c.IsEditing = true; } });
        CommitRenameCommand = new AsyncRelayCommand(async p => { if (p is DesktopCardViewModel c) await CommitRenameAsync(c); });
        CancelRenameCommand = new RelayCommand(p => { if (p is DesktopCardViewModel c) c.IsEditing = false; });
        ActivateAppCommand = new AsyncRelayCommand(async p => { if (p is AppRowViewModel a) { await _desktops.BringToFrontAsync(a.PrimaryHandle, a.DesktopId); await _controller.RefreshAsync(); } });
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
            result.Add(row);
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

    private void RememberCollapsed() =>
        _settings.Update(s => s.Widget.CollapsedCards = Cards.Where(c => !c.IsExpanded).Select(c => c.Id).ToList());

    // ------------------------------------------------------------ Aktionen

    private async Task AddDesktopAsync()
    {
        var d = await _desktops.CreateAsync(null);
        await _controller.RefreshAsync();
        var card = d is null ? null : Cards.FirstOrDefault(c => c.Id == d.Id);
        if (card is not null) BeginRenameCommand.Execute(card);
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
