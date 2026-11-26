using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Profiles;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Storage;
using DeskRecall.Platform.Services;
using DeskRecall.Platform.VirtualDesktops;
using DeskRecall.Services;

namespace DeskRecall.ViewModels;

// =====================================================================================
// Programmregeln
// =====================================================================================

public sealed class RuleItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private ImageSource? _icon;

    public RuleItemViewModel(AppRule rule, Action changed) { Rule = rule; _changed = changed; }
    public AppRule Rule { get; }

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
    {
        OnPropertyChanged(n);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
        _changed();
    }

    public string DisplayName => !string.IsNullOrWhiteSpace(Rule.Name) ? Rule.Name : (AppCatalog.FriendlyName(Rule.ProcessName) ?? (Rule.ProcessName.Length > 0 ? Rule.ProcessName : "Neue Regel"));

    /// <summary>Kurzbeschreibung wie in der Liste: „→ Desktop 3“, „Minimiert“, „Ausgeschlossen“ …</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Rule.Exclude) return "Ausgeschlossen";
            if (Rule.TargetDesktop is { } d) parts.Add($"→ Desktop {d}");
            switch (Rule.StartState)
            {
                case RuleStartState.Minimized: parts.Add("Minimiert"); break;
                case RuleStartState.Maximized: parts.Add("Maximiert"); break;
                case RuleStartState.Normal: parts.Add("Normal"); break;
            }
            if (Rule.TargetMonitorId is not null) parts.Add("fester Monitor");
            if (Rule.NeverAutoStart) parts.Add("nie automatisch starten");
            if (Rule.ApplyLive) parts.Add("live");
            return parts.Count == 0 ? "keine Änderung" : string.Join(" · ", parts);
        }
    }

    public bool Enabled { get => Rule.Enabled; set { Rule.Enabled = value; Changed(); } }
    public string Name { get => Rule.Name; set { Rule.Name = value ?? ""; Changed(); } }
    public string ProcessName { get => Rule.ProcessName; set { var v = (value ?? "").Trim(); if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = v[..^4]; Rule.ProcessName = v; Changed(); } }
    public string TitleContains { get => Rule.TitleContains ?? ""; set { Rule.TitleContains = string.IsNullOrWhiteSpace(value) ? null : value; Changed(); } }
    /// <summary>0 = kein fester Desktop, sonst 1-basiert.</summary>
    public int TargetDesktop { get => Rule.TargetDesktop ?? 0; set { Rule.TargetDesktop = value <= 0 ? null : value; Changed(); } }
    public string TargetMonitorId { get => Rule.TargetMonitorId ?? ""; set { Rule.TargetMonitorId = string.IsNullOrEmpty(value) ? null : value; Changed(); } }
    public RuleStartState StartState { get => Rule.StartState; set { Rule.StartState = value; Changed(); } }
    public bool NeverAutoStart { get => Rule.NeverAutoStart; set { Rule.NeverAutoStart = value; Changed(); } }
    public bool Exclude { get => Rule.Exclude; set { Rule.Exclude = value; Changed(); } }
    public bool ApplyLive { get => Rule.ApplyLive; set { Rule.ApplyLive = value; Changed(); } }
}

public sealed class RulesViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IconService _icons;
    private readonly AppController _controller;
    private readonly IMonitorManager _monitors;
    private readonly IDialogService _dialogs;
    private RuleItemViewModel? _selected;
    private readonly System.Windows.Threading.DispatcherTimer _commit = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public RulesViewModel(ISettingsService settings, IconService icons, AppController controller, IMonitorManager monitors, IDialogService dialogs)
    {
        _settings = settings; _icons = icons; _controller = controller; _monitors = monitors; _dialogs = dialogs;
        _commit.Tick += (_, _) => { _commit.Stop(); _settings.Update(s => s.Rules = Items.Select(i => i.Rule).ToList()); };
        foreach (var r in _settings.Current.Rules) Items.Add(Wrap(r));
        Selected = Items.FirstOrDefault();

        AddCommand = new RelayCommand(() => { var vm = Wrap(new AppRule { Enabled = true }); Items.Add(vm); Selected = vm; Changed(); });
        AddFromRunningCommand = new RelayCommand(p =>
        {
            if (p is not RunningAppOption app) return;
            if (Items.FirstOrDefault(i => string.Equals(i.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase)) is { } existing) { Selected = existing; return; }
            var vm = Wrap(new AppRule { Enabled = true, ProcessName = app.ProcessName, Name = app.Name });
            Items.Add(vm); Selected = vm; Changed();
        });
        RemoveCommand = new RelayCommand(() =>
        {
            if (Selected is null) return;
            if (!_dialogs.Confirm("Regel löschen", $"Regel „{Selected.DisplayName}“ löschen?", "Löschen", "Abbrechen", danger: true)) return;
            var i = Items.IndexOf(Selected);
            Items.Remove(Selected);
            Selected = Items.Count == 0 ? null : Items[Math.Min(i, Items.Count - 1)];
            Changed();
        }, () => Selected is not null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => Selected is not null && Items.IndexOf(Selected) > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => Selected is not null && Items.IndexOf(Selected) < Items.Count - 1);
    }

    public ObservableCollection<RuleItemViewModel> Items { get; } = new();
    public RuleItemViewModel? Selected { get => _selected; set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); } }
    public bool HasSelection => Selected is not null;

    public RelayCommand AddCommand { get; }
    public RelayCommand AddFromRunningCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    public List<Option<int>> DesktopOptions { get; } =
        new[] { new Option<int>(0, "Wie in der Sitzung") }.Concat(Enumerable.Range(1, 12).Select(i => new Option<int>(i, $"Desktop {i}"))).ToList();
    public List<Option<RuleStartState>> StateOptions { get; } = new()
    {
        new(RuleStartState.Unchanged, "Wie in der Sitzung"), new(RuleStartState.Normal, "Normal"),
        new(RuleStartState.Minimized, "Minimiert"), new(RuleStartState.Maximized, "Maximiert"),
    };
    public List<Option<string>> MonitorOptions
    {
        get
        {
            var list = new List<Option<string>> { new("", "Wie in der Sitzung") };
            try { list.AddRange(_monitors.GetMonitors().Select((m, i) => new Option<string>(m.Id, $"{i + 1}. {(string.IsNullOrEmpty(m.FriendlyName) ? m.DeviceName : m.FriendlyName)}{(m.IsPrimary ? " (Haupt)" : "")}"))); } catch { }
            return list;
        }
    }

    public sealed record RunningAppOption(string ProcessName, string Name, ImageSource? Icon);

    /// <summary>Laufende Programme zur schnellen Auswahl.</summary>
    public List<RunningAppOption> RunningApps =>
        (_controller.Current?.Windows ?? Array.Empty<WindowRecord>())
            .GroupBy(w => AppIdentity.NormalizeProcessName(w.ProcessName))
            .Select(g => g.First())
            .Select(w => new RunningAppOption(w.ProcessName, AppCatalog.FriendlyName(w.ProcessName) ?? w.FriendlyName, _icons.ForPath(w.ExecutablePath)))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private RuleItemViewModel Wrap(AppRule r)
    {
        var vm = new RuleItemViewModel(r, Changed);
        var path = _controller.Current?.Windows.FirstOrDefault(w => AppIdentity.NormalizeProcessName(w.ProcessName) == AppIdentity.NormalizeProcessName(r.ProcessName))?.ExecutablePath;
        vm.Icon = _icons.ForPath(path);
        return vm;
    }

    private void Move(int delta)
    {
        if (Selected is null) return;
        var i = Items.IndexOf(Selected);
        var j = i + delta;
        if (j < 0 || j >= Items.Count) return;
        Items.Move(i, j);
        Changed();
    }

    private void Changed() { _commit.Stop(); _commit.Start(); }
    public void Flush() { if (_commit.IsEnabled) { _commit.Stop(); _settings.Update(s => s.Rules = Items.Select(i => i.Rule).ToList()); } }
}

// =====================================================================================
// Profile
// =====================================================================================

public sealed class ProfileItemViewModel
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public bool IsDefault { get; init; }
    public bool IsActive { get; init; }
}

public sealed class ProfilesViewModel : ObservableObject
{
    private readonly IProfileManager _profiles;
    private readonly ISettingsService _settings;
    private readonly AppController _controller;
    private readonly IDialogService _dialogs;
    private readonly ILogService _log;
    private ProfileItemViewModel? _selected;

    public ProfilesViewModel(IProfileManager profiles, ISettingsService settings, AppController controller, IDialogService dialogs, ILogService log)
    {
        _profiles = profiles; _settings = settings; _controller = controller; _dialogs = dialogs; _log = log;
        _profiles.Changed += () => Application.Current.Dispatcher.BeginInvoke(Load);
        _settings.Changed += _ => Application.Current.Dispatcher.BeginInvoke(Load);
        Load();

        bool IsCustom() => Selected is { IsDefault: false };
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        LoadCommand = new AsyncRelayCommand(async () =>
        {
            if (Selected is null) return;
            if (!_dialogs.Confirm("Profil laden", $"Profil „{Selected.Name}“ wiederherstellen?\n\nFehlende Programme werden gestartet. Geöffnete Programme werden nur nach Rückfrage geschlossen.", "Laden", "Abbrechen")) return;
            _settings.Update(s => s.ActiveProfile = Selected.Name);
            await _controller.RestoreProfileAsync(Selected.Name);
        }, () => Selected is not null && !_controller.IsRestoring);
        UpdateCommand = new AsyncRelayCommand(async () =>
        {
            if (!IsCustom()) return;
            if (!_dialogs.Confirm("Profil aktualisieren", $"Profil „{Selected!.Name}“ mit dem aktuellen Zustand überschreiben?", "Aktualisieren", "Abbrechen")) return;
            await Guard(async () => _profiles.Update(Selected.Name, await Task.Run(_controller.CaptureForProfile)));
        }, IsCustom);
        RenameCommand = new AsyncRelayCommand(async () =>
        {
            if (!IsCustom()) return;
            var old = Selected!.Name;
            var n = _dialogs.Prompt("Profil umbenennen", "Neuer Name", old);
            if (string.IsNullOrWhiteSpace(n) || n == old) return;
            await Guard(() => { _profiles.Rename(old, n); if (_settings.Current.ActiveProfile == old) _settings.Update(s => s.ActiveProfile = n.Trim()); return Task.CompletedTask; });
        }, IsCustom);
        DuplicateCommand = new AsyncRelayCommand(() => Guard(() => { _profiles.Duplicate(Selected!.Name); return Task.CompletedTask; }), IsCustom);
        DeleteCommand = new AsyncRelayCommand(async () =>
        {
            if (!IsCustom()) return;
            var name = Selected!.Name;
            if (!_dialogs.Confirm("Profil löschen", $"Profil „{name}“ endgültig löschen?\n\nProgramme und Fenster bleiben unverändert.", "Löschen", "Abbrechen", danger: true)) return;
            await Guard(() => { _profiles.Delete(name); if (_settings.Current.ActiveProfile == name) _settings.Update(s => s.ActiveProfile = SessionProfile.DefaultName); return Task.CompletedTask; });
        }, IsCustom);
        ExportCommand = new AsyncRelayCommand(async () =>
        {
            if (!IsCustom()) return;
            var file = _dialogs.SaveFile("Profil exportieren", "DeskRecall-Profil (*.json)|*.json", Selected!.Name + ".deskrecall.json");
            if (file is null) return;
            await Guard(() => { _profiles.Export(Selected.Name, file); return Task.CompletedTask; });
        }, IsCustom);
        ImportCommand = new AsyncRelayCommand(async () =>
        {
            var file = _dialogs.OpenFile("Profil importieren", "DeskRecall-Profil oder Sitzung (*.json)|*.json|Alle Dateien (*.*)|*.*");
            if (file is null) return;
            await Guard(() => { var p = _profiles.Import(file); _dialogs.Info("Profil importiert", $"Profil „{p.Name}“ wurde importiert."); return Task.CompletedTask; });
        });
        SetActiveCommand = new RelayCommand(() => { if (Selected is not null) _settings.Update(s => s.ActiveProfile = Selected.Name); }, () => Selected is not null);
    }

    public ObservableCollection<ProfileItemViewModel> Items { get; } = new();
    public ProfileItemViewModel? Selected { get => _selected; set => Set(ref _selected, value); }

    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand SetActiveCommand { get; }

    public void Load()
    {
        var selectedName = Selected?.Name;
        var active = _settings.Current.ActiveProfile;
        Items.Clear();
        Items.Add(new ProfileItemViewModel { Name = SessionProfile.DefaultName, Detail = "Automatisch gesicherte letzte Sitzung", IsDefault = true, IsActive = active == SessionProfile.DefaultName });
        try
        {
            foreach (var p in _profiles.List())
                Items.Add(new ProfileItemViewModel
                {
                    Name = p.Name,
                    Detail = p.Snapshot is null ? "leer" : $"{p.Snapshot.DesktopCount} Desktops · {p.Snapshot.AppCount} Apps · geändert {HistoryRetention.FormatRelative(p.UpdatedAt, DateTimeOffset.Now)}",
                    IsActive = active == p.Name,
                });
        }
        catch (Exception ex) { _log.Warn("Profile", "Profile konnten nicht gelesen werden", ex); }
        Selected = Items.FirstOrDefault(i => i.Name == selectedName) ?? Items.FirstOrDefault(i => i.IsActive) ?? Items[0];
    }

    private async Task CreateAsync()
    {
        var name = _dialogs.Prompt("Neues Profil", "Name des Profils (z. B. Arbeit, Gaming, Videobearbeitung)", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Guard(async () =>
        {
            var snap = await Task.Run(_controller.CaptureForProfile);
            _profiles.Create(name, snap);
        });
        Selected = Items.FirstOrDefault(i => i.Name == name.Trim()) ?? Selected;
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Info("Profile", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("Profile", "Profilaktion fehlgeschlagen", ex);
            _dialogs.Info("Profile", "Die Aktion ist fehlgeschlagen: " + ex.Message);
        }
    }
}

// =====================================================================================
// Sitzungsverlauf
// =====================================================================================

public sealed class HistoryViewModel : ObservableObject
{
    private readonly ISessionStore _store;
    private readonly AppController _controller;
    private readonly IDialogService _dialogs;

    public HistoryViewModel(ISessionStore store, AppController controller, IDialogService dialogs)
    {
        _store = store; _controller = controller; _dialogs = dialogs;
        _store.HistoryChanged += () => Application.Current.Dispatcher.BeginInvoke(Load);
        RestoreCommand = new AsyncRelayCommand(async p => { if (p is HistoryItemViewModel h) await _controller.RestoreHistoryAsync(h.Entry.FilePath); }, _ => !_controller.IsRestoring);
        DeleteCommand = new RelayCommand(p => { if (p is HistoryItemViewModel h) { _store.DeleteHistory(h.Entry.FilePath); Load(); } });
        ClearCommand = new RelayCommand(() =>
        {
            if (!_dialogs.Confirm("Verlauf leeren", "Alle älteren Sicherungen löschen? Die letzte Sitzung bleibt erhalten.", "Leeren", "Abbrechen", danger: true)) return;
            _store.ClearHistory(); Load();
        });
        Load();
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ClearCommand { get; }

    public void Load()
    {
        Items.Clear();
        var now = DateTimeOffset.Now;
        foreach (var e in _store.GetHistory())
            Items.Add(new HistoryItemViewModel
            {
                Entry = e,
                When = HistoryRetention.FormatRelative(e.CreatedAt, now),
                Summary = $"{e.DesktopCount} {(e.DesktopCount == 1 ? "Desktop" : "Desktops")} · {e.AppCount} {(e.AppCount == 1 ? "App" : "Apps")} · {e.WindowCount} Fenster",
            });
    }
}

// =====================================================================================
// Diagnose
// =====================================================================================

public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly CompatibilityService _compat;
    private readonly IVirtualDesktopService _vd;
    private readonly AppController _controller;
    private readonly ISettingsService _settings;

    public DiagnosticsViewModel(ILogService log, CompatibilityService compat, IVirtualDesktopService vd, AppController controller, ISettingsService settings)
    {
        Log = log; _compat = compat; _vd = vd; _controller = controller; _settings = settings;
        Log.EntryAdded += e => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Entries.Insert(0, e);
            while (Entries.Count > 600) Entries.RemoveAt(Entries.Count - 1);
        });
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(AppInfo.Paths.Logs));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppInfo.Paths.Root));
        CopyLogCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(Report + "\n\n" + string.Join("\n", Log.Recent.Reverse().Select(e => e.Format()))); } catch { }
        });
        Refresh();
    }

    public ILogService Log { get; }
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand CopyLogCommand { get; }

    public bool VerboseLogging
    {
        get => _settings.Current.Diagnostics.VerboseLogging;
        set { _settings.Update(s => s.Diagnostics.VerboseLogging = value); OnPropertyChanged(); }
    }

    public string Report { get; private set; } = "";
    public string LastRestore { get; private set; } = "";

    public void Refresh()
    {
        var b = _compat.Build;
        var lines = new List<string>
        {
            $"DeskRecall {AppInfo.Version} (Build {AppInfo.BuildNumber}), .NET {Environment.Version}, {(Environment.Is64BitProcess ? "64-Bit" : "32-Bit")}",
            $"Windows: {_compat.Description}",
            $"Virtuelle Desktops: {_vd.BackendDescription}",
            $"Schnittstellensatz: {(_vd is WindowsVirtualDesktopService w ? w.ActiveSet.ToString() : "–")} (Einstellung: {_settings.Current.Desktops.ApiOverride})",
            $"Desktops verwalten: {(_vd.CanManage ? "ja" : "nein")}",
            $"Läuft als Administrator: {(_compat.IsElevated ? "ja (nicht nötig)" : "nein")}",
            $"Datenordner: {AppInfo.Paths.Root}{(AppInfo.IsPortable ? " (portabel)" : "")}",
        };
        if (_vd.LastError is not null) lines.Add($"Letzter Desktop-Fehler: {_vd.LastError}");
        if (!_compat.IsSupported) lines.Add("Hinweis: Diese Windows-Version wird nicht offiziell unterstützt (Windows 11 erforderlich).");
        if (_controller.LastRefreshDuration > TimeSpan.Zero || _controller.LastRefreshWindowCount > 0)
        {
            var ms = _controller.LastRefreshDuration.TotalMilliseconds;
            var skip = _controller.LastRefreshSkippedUi ? ", Oberfläche unverändert" : "";
            lines.Add($"Letzte Aktualisierung: {ms:0} ms, {_controller.LastRefreshWindowCount} Fenster{skip}");
        }
        Report = string.Join("\n", lines);

        var r = _controller.LastReport;
        LastRestore = r is null ? "Seit dem Start wurde noch nichts wiederhergestellt."
            : r.Summary + "\n" + string.Join("\n", r.Items.Select(i => $"• {i.Saved.FriendlyName}: {Core.Restore.SessionRestorer.StatusText(i.Status)}{(string.IsNullOrEmpty(i.Message) ? "" : " – " + i.Message)}"));

        Entries.Clear();
        foreach (var e in Log.Recent.Reverse()) Entries.Add(e);
        OnPropertyChanged(nameof(Report));
        OnPropertyChanged(nameof(LastRestore));
    }

    private static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false }); } catch { }
    }
}
