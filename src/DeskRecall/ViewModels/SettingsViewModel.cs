using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Profiles;
using DeskRecall.Core.Restore;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Update;
using DeskRecall.Core.Storage;
using DeskRecall.Platform.Services;
using DeskRecall.Platform.VirtualDesktops;
using DeskRecall.Services;

namespace DeskRecall.ViewModels;

public sealed record Option<T>(T Value, string Label);

public sealed class NavItem
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Glyph { get; init; }
}

/// <summary>Einstellungsfenster: alle Kategorien. Änderungen werden sofort (gebündelt) gespeichert und angewendet.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly AutostartService _autostart;
    private readonly DispatcherTimer _commitTimer;
    private NavItem _selectedPage;

    public SettingsViewModel(ISettingsService settings, AutostartService autostart, RulesViewModel rules, ProfilesViewModel profiles,
        HistoryViewModel history, DiagnosticsViewModel diagnostics, AppController controller, IMonitorManager monitors, IVirtualDesktopService vd, ISessionStore store,
        UpdateController updates)
    {
        _settings = settings; _autostart = autostart; _store = store;
        Rules = rules; Profiles = profiles; History = history; Diagnostics = diagnostics;
        _controller = controller; _monitors = monitors; _vd = vd;
        Updates = updates;
        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _commitTimer.Tick += (_, _) => { _commitTimer.Stop(); _settings.Update(_ => { }); };
        Pages = new()
        {
            new NavItem { Key = "general", Title = "Allgemein", Glyph = "\uE80F" },
            new NavItem { Key = "backup", Title = "Sicherung", Glyph = "\uE753" },
            new NavItem { Key = "restore", Title = "Wiederherstellung", Glyph = "\uE777" },
            new NavItem { Key = "rules", Title = "Programmregeln", Glyph = "\uE8FD" },
            new NavItem { Key = "profiles", Title = "Profile", Glyph = "\uE81E" },
            new NavItem { Key = "appearance", Title = "Erscheinungsbild", Glyph = "\uE790" },
            new NavItem { Key = "desktops", Title = "Virtuelle Desktops", Glyph = "\uE7C4" },
            new NavItem { Key = "monitors", Title = "Monitore", Glyph = "\uE7F4" },
            new NavItem { Key = "exclusions", Title = "Ausnahmen", Glyph = "\uE71C" },
            new NavItem { Key = "diagnostics", Title = "Diagnose", Glyph = "\uE9D9" },
        };
        _selectedPage = Pages[0];
        foreach (var p in S.Exclusions.ProcessNames) ExcludedProcesses.Add(p);
        foreach (var t in S.Exclusions.TitleContains) ExcludedTitles.Add(t);

        SaveNowCommand = new AsyncRelayCommand(async () => { await _controller.SaveAsync(SessionKind.Manual); });
        RestoreNowCommand = new AsyncRelayCommand(async () => { await _controller.RestoreLastAsync(); }, () => !_controller.IsRestoring);
        DryRunCommand = new AsyncRelayCommand(DryRunAsync, () => !_controller.IsRestoring);
        AddExcludedProcessCommand = new RelayCommand(() => AddTo(ExcludedProcesses, NewExcludedProcess, v => NewExcludedProcess = v, list => S.Exclusions.ProcessNames = list));
        RemoveExcludedProcessCommand = new RelayCommand(p => { if (p is string s) { ExcludedProcesses.Remove(s); S.Exclusions.ProcessNames = ExcludedProcesses.ToList(); Commit(); } });
        AddExcludedTitleCommand = new RelayCommand(() => AddTo(ExcludedTitles, NewExcludedTitle, v => NewExcludedTitle = v, list => S.Exclusions.TitleContains = list));
        RemoveExcludedTitleCommand = new RelayCommand(p => { if (p is string s) { ExcludedTitles.Remove(s); S.Exclusions.TitleContains = ExcludedTitles.ToList(); Commit(); } });
        ReinitDesktopsCommand = new AsyncRelayCommand(async () => { await Task.Run(_vd.Reinitialize); OnPropertyChanged(nameof(DesktopBackend)); OnPropertyChanged(nameof(DesktopList)); Diagnostics.Refresh(); });
        RefreshMonitorsCommand = new RelayCommand(() => { (_monitors as MonitorManager)?.Invalidate(); OnPropertyChanged(nameof(MonitorList)); });
    }

    private readonly AppController _controller;
    private readonly ISessionStore _store;
    private readonly IMonitorManager _monitors;
    private readonly IVirtualDesktopService _vd;

    private AppSettings S => _settings.Current;

    private void Commit([System.Runtime.CompilerServices.CallerMemberName] string? name = null, bool preview = false)
    {
        OnPropertyChanged(name);
        if (preview) _settings.NotifyPreview();
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    /// <summary>Ausstehende Änderungen sofort speichern (z. B. beim Schließen des Fensters).</summary>
    public void Flush()
    {
        if (!_commitTimer.IsEnabled) return;
        _commitTimer.Stop();
        _settings.Update(_ => { });
    }

    public List<NavItem> Pages { get; }
    public NavItem SelectedPage { get => _selectedPage; set { if (value is not null && Set(ref _selectedPage, value)) { if (value.Key == "diagnostics") Diagnostics.Refresh(); if (value.Key == "backup") History.Load(); } } }

    public RulesViewModel Rules { get; }
    public ProfilesViewModel Profiles { get; }
    public HistoryViewModel History { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    public string VersionText => $"Version {AppInfo.Version} (Build {AppInfo.BuildNumber}) · © 2026 Willi Gering, Gering Digital";

    // ---------------------------------------------------------------- Optionen für Auswahllisten

    public List<Option<WidgetDock>> DockOptions { get; } = new() { new(WidgetDock.Right, "Rechts"), new(WidgetDock.Left, "Links") };
    public List<Option<WidgetDisplayMode>> ModeOptions { get; } = new()
    {
        new(WidgetDisplayMode.Collapsible, "Ausklappbar"),
        new(WidgetDisplayMode.AlwaysExpanded, "Immer sichtbar"),
        new(WidgetDisplayMode.AutoHide, "Automatisch ausblenden"),
        new(WidgetDisplayMode.TrayOnly, "Nur im Infobereich"),
    };
    public List<Option<FullscreenBehavior>> FullscreenOptions { get; } = new() { new(FullscreenBehavior.AutoHide, "Ausblenden"), new(FullscreenBehavior.KeepVisible, "Sichtbar lassen") };
    public List<Option<SaveMode>> SaveModeOptions { get; } = new()
    {
        new(SaveMode.OnChange, "Bei jeder Änderung"),
        new(SaveMode.Every30Seconds, "Alle 30 Sekunden"),
        new(SaveMode.Every1Minute, "Jede Minute"),
        new(SaveMode.Every5Minutes, "Alle 5 Minuten"),
        new(SaveMode.ManualOnly, "Nur manuell"),
    };
    public List<Option<ElevationPolicy>> ElevationOptions { get; } = new() { new(ElevationPolicy.Skip, "Überspringen"), new(ElevationPolicy.Ask, "Jedes Mal nachfragen") };
    public List<Option<MonitorFallback>> MonitorFallbackOptions { get; } = new() { new(MonitorFallback.Primary, "Hauptmonitor"), new(MonitorFallback.Nearest, "Nächstgelegener Monitor") };
    public List<Option<VirtualDesktopApiOverride>> ApiOptions { get; } = new()
    {
        new(VirtualDesktopApiOverride.Auto, "Automatisch (empfohlen)"),
        new(VirtualDesktopApiOverride.Win11_24H2, "Windows 11 24H2 / 25H2"),
        new(VirtualDesktopApiOverride.Win11_23H2, "Windows 11 23H2"),
        new(VirtualDesktopApiOverride.Win11_22H2, "Windows 11 22H2"),
        new(VirtualDesktopApiOverride.Win11Legacy, "Windows 11 21H2 / frühes 22H2"),
        new(VirtualDesktopApiOverride.PublicApiOnly, "Nur öffentliche API (eingeschränkt)"),
    };

    // ---------------------------------------------------------------- Allgemein

    public bool StartWithWindows
    {
        get => S.General.StartWithWindows;
        set { S.General.StartWithWindows = value; _autostart.SetEnabled(value); Commit(); }
    }
    public bool RunInBackground { get => S.General.RunInBackground; set { S.General.RunInBackground = value; Commit(); } }
    public bool AutoRestoreOnStartup { get => S.General.AutoRestoreOnStartup; set { S.General.AutoRestoreOnStartup = value; Commit(); } }
    public bool ShowTrayIcon { get => S.General.ShowTrayIcon; set { S.General.ShowTrayIcon = value; Commit(); } }

    public WidgetDock Dock { get => S.Widget.Dock; set { S.Widget.Dock = value; Commit(); } }
    public WidgetDisplayMode DisplayMode { get => S.Widget.Mode; set { S.Widget.Mode = value; Commit(); } }
    public WidgetWidthPreset WidthPreset
    {
        get => WidgetAppearance.FromPixels(S.Widget.ExpandedWidth);
        set
        {
            S.Widget.ExpandedWidth = WidgetAppearance.ToPixels(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(WidthNarrow));
            OnPropertyChanged(nameof(WidthNormal));
            OnPropertyChanged(nameof(WidthWide));
            Commit(nameof(WidthPreset), preview: true);
        }
    }
    public bool WidthNarrow { get => WidthPreset == WidgetWidthPreset.Narrow; set { if (value) WidthPreset = WidgetWidthPreset.Narrow; else OnPropertyChanged(); } }
    public bool WidthNormal { get => WidthPreset == WidgetWidthPreset.Normal; set { if (value) WidthPreset = WidgetWidthPreset.Normal; else OnPropertyChanged(); } }
    public bool WidthWide { get => WidthPreset == WidgetWidthPreset.Wide; set { if (value) WidthPreset = WidgetWidthPreset.Wide; else OnPropertyChanged(); } }
    public int Opacity { get => S.Widget.OpacityPercent; set { S.Widget.OpacityPercent = value; Commit(preview: true); } }
    public bool CheckUpdates { get => S.Update.CheckAutomatically; set { S.Update.CheckAutomatically = value; Commit(); } }
    public string FeedUrl
    {
        get => string.IsNullOrWhiteSpace(S.Update.FeedUrl) ? UpdateService.DefaultFeedUrl : S.Update.FeedUrl;
        set
        {
            var v = (value ?? "").Trim();
            S.Update.FeedUrl = string.Equals(v, UpdateService.DefaultFeedUrl, StringComparison.OrdinalIgnoreCase) ? "" : v;
            Commit();
        }
    }
    public UpdateController Updates { get; }
    public string DefaultFeedUrl => UpdateService.DefaultFeedUrl;
    public FullscreenBehavior Fullscreen { get => S.Widget.Fullscreen; set { S.Widget.Fullscreen = value; Commit(); } }
    public bool AlwaysOnTop { get => S.Widget.AlwaysOnTop; set { S.Widget.AlwaysOnTop = value; Commit(); } }

    // ---------------------------------------------------------------- Sicherung

    public SaveMode SaveMode { get => S.Backup.Mode; set { S.Backup.Mode = value; Commit(); } }
    public bool SaveOnShutdown { get => S.Backup.SaveOnShutdown; set { S.Backup.SaveOnShutdown = value; Commit(); } }
    public int HistoryCount { get => S.Backup.HistoryCount; set { S.Backup.HistoryCount = value; Commit(); } }
    public bool StoreWindowTitles { get => S.Backup.StoreWindowTitles; set { S.Backup.StoreWindowTitles = value; Commit(); } }
    public bool CaptureDocumentPaths { get => S.Backup.CaptureDocumentPaths; set { S.Backup.CaptureDocumentPaths = value; Commit(); } }
    public AsyncRelayCommand SaveNowCommand { get; }

    // ---------------------------------------------------------------- Wiederherstellung

    public int StartupDelay { get => S.Restore.StartupDelaySeconds; set { S.Restore.StartupDelaySeconds = value; Commit(); } }
    public bool LaunchMissingApps { get => S.Restore.LaunchMissingApps; set { S.Restore.LaunchMissingApps = value; Commit(); } }
    public int WindowTimeout { get => S.Restore.WindowTimeoutSeconds; set { S.Restore.WindowTimeoutSeconds = value; Commit(); } }
    public bool RestoreExplorerFolders { get => S.Restore.RestoreExplorerFolders; set { S.Restore.RestoreExplorerFolders = value; Commit(); } }
    public bool UseBrowserSessionRestore { get => S.Restore.UseBrowserSessionRestore; set { S.Restore.UseBrowserSessionRestore = value; Commit(); } }
    public bool ActivateLastDesktop { get => S.Restore.ActivateLastDesktop; set { S.Restore.ActivateLastDesktop = value; Commit(); } }
    public ElevationPolicy ElevatedApps { get => S.Restore.ElevatedApps; set { S.Restore.ElevatedApps = value; Commit(); } }
    public int LaunchStagger { get => S.Restore.LaunchStaggerMilliseconds; set { S.Restore.LaunchStaggerMilliseconds = value; Commit(); } }
    public AsyncRelayCommand RestoreNowCommand { get; }
    public AsyncRelayCommand DryRunCommand { get; }

    private string? _dryRunResult;
    public string? DryRunResult { get => _dryRunResult; private set => Set(ref _dryRunResult, value); }

    private async Task DryRunAsync()
    {
        var snap = _store.LoadLast();
        if (snap is null) { DryRunResult = "Es gibt noch keine gespeicherte Sitzung."; return; }
        DryRunResult = "Probelauf läuft …";
        var report = await _controller.RestoreAsync(snap, "Probelauf", offerCloseExtras: false, dryRun: true);
        if (report is null) { DryRunResult = "Probelauf nicht möglich (siehe Diagnose)."; return; }
        var lines = report.Items.Select(i => $"• {i.Saved.FriendlyName}: {SessionRestorer.StatusText(i.Status)}{(string.IsNullOrEmpty(i.Message) ? "" : " – " + i.Message)}");
        DryRunResult = report.Summary + "\n" + string.Join("\n", lines.Take(40));
    }

    // ---------------------------------------------------------------- Erscheinungsbild

    public bool Acrylic { get => S.Widget.Acrylic; set { S.Widget.Acrylic = value; Commit(preview: true); } }
    public bool Animations { get => S.Widget.Animations; set { S.Widget.Animations = value; Commit(); } }
    public int Scale { get => S.Widget.ScalePercent; set { S.Widget.ScalePercent = value; Commit(preview: true); } }

    // ---------------------------------------------------------------- Virtuelle Desktops

    public bool CreateMissingDesktops { get => S.Desktops.CreateMissingDesktops; set { S.Desktops.CreateMissingDesktops = value; Commit(); } }
    public bool RestoreNamesAndOrder { get => S.Desktops.RestoreNamesAndOrder; set { S.Desktops.RestoreNamesAndOrder = value; Commit(); } }
    public VirtualDesktopApiOverride ApiOverride
    {
        get => S.Desktops.ApiOverride;
        set { S.Desktops.ApiOverride = value; Commit(); ReinitDesktopsCommand.Execute(null); }
    }
    public string DesktopBackend => _vd.BackendDescription + (_vd.LastError is null ? "" : $"\nLetzter Fehler: {_vd.LastError}");
    public IEnumerable<string> DesktopList
    {
        get
        {
            try
            {
                var cur = _vd.GetCurrentDesktopId();
                return _vd.GetDesktops().Select(d => $"{d.Index + 1:00}  {d.DisplayName}{(d.Id == cur ? "   (aktiv)" : "")}").ToList();
            }
            catch { return Array.Empty<string>(); }
        }
    }
    public AsyncRelayCommand ReinitDesktopsCommand { get; }

    // ---------------------------------------------------------------- Monitore

    public bool ScaleOnResolutionChange { get => S.Monitors.ScaleOnResolutionChange; set { S.Monitors.ScaleOnResolutionChange = value; Commit(); } }
    public bool CorrectOffscreenWindows { get => S.Monitors.CorrectOffscreenWindows; set { S.Monitors.CorrectOffscreenWindows = value; Commit(); } }
    public MonitorFallback MonitorFallback { get => S.Monitors.Fallback; set { S.Monitors.Fallback = value; Commit(); } }
    public IEnumerable<string> MonitorList
    {
        get
        {
            try
            {
                return _monitors.GetMonitors().Select((m, i) =>
                    $"{i + 1}. {(string.IsNullOrEmpty(m.FriendlyName) ? m.DeviceName : m.FriendlyName)}{(m.IsPrimary ? " (Hauptmonitor)" : "")}\n    {m.Bounds.Width} × {m.Bounds.Height} bei ({m.Bounds.Left}, {m.Bounds.Top}) · Skalierung {m.ScalePercent} %").ToList();
            }
            catch { return Array.Empty<string>(); }
        }
    }
    public RelayCommand RefreshMonitorsCommand { get; }

    // ---------------------------------------------------------------- Ausnahmen

    public ObservableCollection<string> ExcludedProcesses { get; } = new();
    public ObservableCollection<string> ExcludedTitles { get; } = new();
    private string _newExcludedProcess = "", _newExcludedTitle = "";
    public string NewExcludedProcess { get => _newExcludedProcess; set => Set(ref _newExcludedProcess, value); }
    public string NewExcludedTitle { get => _newExcludedTitle; set => Set(ref _newExcludedTitle, value); }
    public RelayCommand AddExcludedProcessCommand { get; }
    public RelayCommand RemoveExcludedProcessCommand { get; }
    public RelayCommand AddExcludedTitleCommand { get; }
    public RelayCommand RemoveExcludedTitleCommand { get; }

    private void AddTo(ObservableCollection<string> list, string value, Action<string> clear, Action<List<string>> store)
    {
        var v = (value ?? "").Trim();
        if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && list == ExcludedProcesses) v = v[..^4];
        if (v.Length == 0 || list.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) { clear(""); return; }
        list.Add(v);
        store(list.ToList());
        clear("");
        Commit(nameof(ExcludedProcesses));
    }
}
