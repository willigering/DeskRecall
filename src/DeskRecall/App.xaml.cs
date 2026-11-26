using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Browsers;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Profiles;
using DeskRecall.Core.Recording;
using DeskRecall.Core.Restore;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Storage;
using DeskRecall.Platform.Services;
using DeskRecall.Platform.VirtualDesktops;
using DeskRecall.Services;
using DeskRecall.ViewModels;
using DeskRecall.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DeskRecall;

/// <summary>Einstiegspunkt: Einzelinstanz, Dependency Injection, Tray, Widget, Sicherung beim Herunterfahren.</summary>
public partial class App : Application
{
    private const string MutexName = @"Local\GeringDigital.DeskRecall.SingleInstance";
    private const string ShowEventName = @"Local\GeringDigital.DeskRecall.Show";
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private ServiceProvider? _services;
    private ILogService? _log;
    private WidgetWindow? _widget;
    private SettingsWindow? _settingsWindow;
    private TrayService? _tray;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Läuft bereits → vorhandene Instanz bitten, das Widget zu zeigen
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        var autostart = e.Args.Any(a => string.Equals(a, AutostartService.AutostartArgument, StringComparison.OrdinalIgnoreCase));
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var paths = AppDataPaths.Resolve(exeDir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        paths.EnsureCreated();
        AppInfo.Paths = paths;
        AppInfo.IsPortable = File.Exists(Path.Combine(exeDir, "portable.flag"));

        var log = new LoggingService(paths.Logs);
        _log = log;
        DispatcherUnhandledException += (_, ex) => { log.Error("App", "Unerwarteter Fehler (abgefangen)", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => log.Error("App", "Schwerer Fehler", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { log.Warn("App", "Unbeobachteter Task-Fehler", ex.Exception); ex.SetObserved(); };
        AsyncRelayCommand.ErrorHandler = ex => log.Error("UI", "Aktion fehlgeschlagen", ex);
        log.Info("App", $"DeskRecall {AppInfo.Version} (Build {AppInfo.BuildNumber}) gestartet{(autostart ? " (Autostart)" : "")}");

        try { UiNative.SetProcessShutdownParameters(0x3FF, 0); } catch { } // früh über das Herunterfahren informiert werden

        _services = ConfigureServices(paths, log);
        var settings = _services.GetRequiredService<ISettingsService>();
        log.Verbose = settings.Current.Diagnostics.VerboseLogging;
        var compat = _services.GetRequiredService<CompatibilityService>();
        log.Info("App", $"System: {compat.Description}");

        SyncAutostart(settings.Current, _services.GetRequiredService<AutostartService>());

        _controller = _services.GetRequiredService<AppController>();
        _widget = _services.GetRequiredService<WidgetWindow>();
        _widget.SettingsRequested += () => OpenSettings();
        _tray = _services.GetRequiredService<TrayService>();
        _tray.Initialize();
        _tray.LeftClick += () => _widget.ToggleFromTray();
        _tray.MenuRequested += BuildTrayMenu;
        _tray.QueryEndSession += () => _controller.SaveOnShutdown();
        _controller.Notification += (title, text) => _tray.ShowBalloon(title, text);
        _controller.StatusChanged += () => _tray.SetToolTip("DeskRecall – " + (_controller.RestoreStatus ?? (_controller.LastSavedAt is { } t ? $"gesichert {t.ToLocalTime():HH:mm}" : "bereit")));
        settings.Changed += s => Dispatcher.BeginInvoke(() => ApplyGlobalSettings(s));
        ApplyGlobalSettings(settings.Current);

        if (settings.Current.Widget.Mode != WidgetDisplayMode.TrayOnly) _widget.ShowWidget();
        _controller.Start(autostart);

        var updates = _services.GetRequiredService<UpdateController>();
        updates.PrepareRelaunch = () =>
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _mutex = null;
        };
        updates.ShutdownApp = () => Dispatcher.BeginInvoke(() => Shutdown());
        updates.TrayNotice += (title, text) => _tray?.ShowBalloon(title, text);
        updates.Start(e.Args);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => { _widget.ShowWidget(); if (!_widget.IsVisible) _widget.ToggleFromTray(); }), null, -1, false);
    }

    private void ApplyGlobalSettings(AppSettings s)
    {
        _tray?.SetVisible(s.General.ShowTrayIcon || s.Widget.Mode == WidgetDisplayMode.TrayOnly);
        if (_widget is null) return;
        if (s.Widget.Mode == WidgetDisplayMode.TrayOnly) { if (_widget.IsVisible) { } }
        else if (!_widget.IsVisible && !(_controller?.IsFullscreen ?? false)) _widget.ShowWidget();
    }

    private static void SyncAutostart(AppSettings s, AutostartService autostart)
    {
        if (s.General.StartWithWindows) autostart.SetEnabled(true); // aktualisiert auch einen geänderten EXE-Pfad
        else if (autostart.IsEnabled) autostart.SetEnabled(false);
    }

    private static ServiceProvider ConfigureServices(AppDataPaths paths, LoggingService log)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ILogService>(log);
        sc.AddSingleton(paths);
        sc.AddSingleton<ISettingsService>(sp => new SettingsService(paths.SettingsFile, log));
        sc.AddSingleton<CompatibilityService>();
        sc.AddSingleton(sp => new WindowsVirtualDesktopService(log, sp.GetRequiredService<CompatibilityService>(),
            () => sp.GetRequiredService<ISettingsService>().Current.Desktops.ApiOverride));
        sc.AddSingleton<IVirtualDesktopService>(sp => sp.GetRequiredService<WindowsVirtualDesktopService>());
        sc.AddSingleton<MonitorManager>();
        sc.AddSingleton<IMonitorManager>(sp => sp.GetRequiredService<MonitorManager>());
        sc.AddSingleton<ExplorerFolderService>();
        sc.AddSingleton<IWindowManager>(sp => new WindowManager(sp.GetRequiredService<WindowsVirtualDesktopService>(), sp.GetRequiredService<MonitorManager>(),
            sp.GetRequiredService<ExplorerFolderService>(), log, () => sp.GetRequiredService<ISettingsService>().Current.Backup.CaptureDocumentPaths));
        sc.AddSingleton<IProcessManager, ProcessManager>();
        sc.AddSingleton<AutostartService>();
        sc.AddSingleton<IRuleEngine>(sp => new RuleEngine(() => sp.GetRequiredService<ISettingsService>().Current.Rules));
        sc.AddSingleton<ISessionStore>(sp => new SessionStore(paths, log, sp.GetRequiredService<ISettingsService>().Current.Backup.HistoryCount));
        sc.AddSingleton<IProfileManager>(sp => new ProfileManager(paths.Profiles, log));
        sc.AddSingleton(sp => new SessionRecorder(sp.GetRequiredService<IWindowManager>(), sp.GetRequiredService<IVirtualDesktopService>(),
            sp.GetRequiredService<IMonitorManager>(), sp.GetRequiredService<IRuleEngine>(), () => sp.GetRequiredService<ISettingsService>().Current, log,
            sp.GetRequiredService<CompatibilityService>().Description));
        sc.AddSingleton<DialogService>();
        sc.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        sc.AddSingleton<ISessionRestorer>(sp => new SessionRestorer(sp.GetRequiredService<IWindowManager>(), sp.GetRequiredService<IVirtualDesktopService>(),
            sp.GetRequiredService<IProcessManager>(), sp.GetRequiredService<IMonitorManager>(), sp.GetRequiredService<IRuleEngine>(), log,
            consent: sp.GetRequiredService<DialogService>()));
        sc.AddSingleton<IInstalledAppSource, StartMenuAppSource>();
        sc.AddSingleton<UpdateController>();
        sc.AddSingleton<IconService>();
        sc.AddSingleton<TrayService>();
        sc.AddSingleton<DesktopManager>();
        sc.AddSingleton(sp => new TabAliasStore(Path.Combine(paths.Root, "tab-names.json")));
        sc.AddSingleton<ITabReader, UiaTabReader>();
        sc.AddSingleton<TabService>();
        sc.AddSingleton<AppController>();
        sc.AddSingleton<WidgetViewModel>();
        sc.AddSingleton<WidgetWindow>();
        sc.AddTransient<RulesViewModel>();
        sc.AddTransient<ProfilesViewModel>();
        sc.AddTransient<HistoryViewModel>();
        sc.AddTransient<DiagnosticsViewModel>();
        sc.AddTransient<SettingsViewModel>();
        sc.AddTransient<SettingsWindow>();
        return sc.BuildServiceProvider();
    }

    public void OpenSettings(string? page = null)
    {
        if (_services is null) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = _services.GetRequiredService<SettingsWindow>();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (page is not null) _settingsWindow.ShowPage(page);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string glyph, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, Icon = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 12 } };
            mi.Click += (_, _) => a();
            return mi;
        }
        var c = _controller!;
        menu.Items.Add(Item(_widget!.IsVisible ? "Widget ausblenden" : "Widget anzeigen", "\uE7C4", () => { if (_widget.IsVisible) _widget.Hide(); else _widget.ShowWidget(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Jetzt sichern", "\uE74E", () => _ = c.SaveAsync(SessionKind.Manual)));
        menu.Items.Add(Item("Letzte Sitzung wiederherstellen", "\uE777", () => _ = c.RestoreLastAsync(), !c.IsRestoring));
        var profiles = new MenuItem { Header = "Profil laden", Icon = new TextBlock { Text = "\uE81E", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 12 } };
        try
        {
            foreach (var p in _services!.GetRequiredService<IProfileManager>().List())
            {
                var name = p.Name;
                profiles.Items.Add(Item(name, "\uE81E", () => _ = c.RestoreProfileAsync(name), !c.IsRestoring));
            }
        }
        catch { }
        profiles.IsEnabled = profiles.Items.Count > 0;
        menu.Items.Add(profiles);
        if (c.IsRestoring) menu.Items.Add(Item("Wiederherstellung abbrechen", "\uE711", c.CancelRestore));
        menu.Items.Add(new Separator());
        var updates = _services!.GetRequiredService<UpdateController>();
        if (updates.HasUpdate)
            menu.Items.Add(Item(updates.NoticeText ?? "Aktualisierung installieren", "\uE895", () => updates.InstallCommand.Execute(null)));
        menu.Items.Add(Item("Einstellungen", "\uE713", () => OpenSettings()));
        menu.Items.Add(Item("Beenden", "\uE7E8", () => Shutdown()));
        return menu;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _controller?.SaveOnShutdown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_controller is not null && _services is not null)
            {
                // Beim normalen Beenden den aktuellen Stand sichern (unverändert → keine Datei)
                var ctl = _controller; Task.Run(() => ctl.SaveAsync(SessionKind.Auto)).Wait(TimeSpan.FromSeconds(3)); // nicht auf dem UI-Kontext warten (Deadlock)
                _controller.Dispose();
            }
            _tray?.Dispose();
            _services?.Dispose();
            _log?.Info("App", "DeskRecall beendet");
            (_log as IDisposable)?.Dispose();
        }
        catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
