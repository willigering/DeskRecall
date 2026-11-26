using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Geometry;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Restore;
using DeskRecall.Core.Rules;
using Xunit;

namespace DeskRecall.Core.Tests;

/// <summary>Integrationstests der Wiederherstellung mit simuliertem Windows (Fakes).</summary>
public class SessionRestorerTests
{
    private static readonly RestoreOptions Fast = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        StableAfter = TimeSpan.FromMilliseconds(20),
        SplashGrace = TimeSpan.FromMilliseconds(400),
        SettleDelay = TimeSpan.FromMilliseconds(10),
        LaunchStagger = TimeSpan.Zero,
        RunningAppGrace = TimeSpan.FromMilliseconds(150),
        WindowTimeout = TimeSpan.FromSeconds(3),
    };

    private sealed class World
    {
        public FakeVirtualDesktops Vd = new("Kreativ", "Arbeit", "Gaming");
        public FakeWindows Win = new();
        public FakeMonitors Mon = new();
        public FakeProcesses Proc;
        public FakeClock Clock = new();
        public List<AppRule> Rules = new();
        public MemoryLogService Log = new();
        public World() { Proc = new FakeProcesses(Win); }
        public SessionRestorer Restorer() => new(Win, Vd, Proc, Mon, new RuleEngine(() => Rules), Log, Clock, directoryExists: _ => true, fileExists: _ => true);

        public SessionSnapshot Snapshot(params WindowRecord[] windows)
        {
            var s = new SessionSnapshot
            {
                Desktops = Vd.GetDesktops().ToList(),
                Monitors = Mon.Monitors.ToList(),
                ActiveDesktopIndex = 1,
            };
            s.ActiveDesktopId = s.Desktops[1].Id;
            foreach (var w in windows) { w.DesktopId = s.Desktops[w.DesktopIndex].Id; s.Windows.Add(w); }
            return s;
        }

        /// <summary>Simuliert: Programm erzeugt nach kurzer Zeit sein Fenster.</summary>
        public void AppearsAfterLaunch(string proc, string title, long handle, string cls = "Cls", int delayMs = 60)
            => Proc.OnLaunch[proc] = _ => Clock.Schedule(TimeSpan.FromMilliseconds(delayMs), () => Win.Add(T.W(proc, title, 0, cls: cls, handle: handle)));
    }

    [Fact]
    public async Task Full_restore_places_running_and_launched_apps_on_their_desktops()
    {
        var w = new World();
        // Firefox läuft schon (auf Desktop 1), Resolve und Steam fehlen
        w.Win.Add(T.W("firefox", "GitHub — Mozilla Firefox", 0, handle: 10, kind: AppKind.Browser));
        w.Proc.Running.Add(new RunningProcess(1, "firefox", null));
        w.AppearsAfterLaunch("Resolve", "DaVinci Resolve", 20);
        w.AppearsAfterLaunch("steam", "Steam", 30);

        var snap = w.Snapshot(
            T.W("Resolve", "DaVinci Resolve", 0, RectI.FromSize(0, 0, 1900, 1000), state: WindowShowState.Maximized),
            T.W("firefox", "GitHub — Mozilla Firefox", 1, RectI.FromSize(200, 100, 1200, 800), kind: AppKind.Browser),
            T.W("steam", "Steam", 2, RectI.FromSize(300, 200, 1000, 700), kind: AppKind.SingleInstance));

        var report = await w.Restorer().RestoreAsync(snap, Fast);

        Assert.Equal(3, report.PlacedCount);
        Assert.Equal(2, report.Launched);
        Assert.DoesNotContain(w.Proc.Launched, l => l.FileName.Contains("firefox"));      // läuft schon → kein Doppelstart
        Assert.Equal(w.Vd.Desktops[1].Id, w.Vd.WindowDesktop[10]);                          // Firefox → Desktop 2
        Assert.Equal(w.Vd.Desktops[2].Id, w.Vd.WindowDesktop[30]);                          // Steam → Desktop 3
        Assert.False(w.Vd.WindowDesktop.ContainsKey(20) && w.Vd.WindowDesktop[20] != w.Vd.Desktops[0].Id);
        Assert.Equal(WindowShowState.Maximized, w.Win.Applied[20].State);
        Assert.Equal(RectI.FromSize(200, 100, 1200, 800), w.Win.Applied[10].NormalBounds);
        Assert.Single(w.Vd.Calls, c => c.StartsWith("switch"));                             // genau ein Desktop-Wechsel am Ende
        Assert.Equal(w.Vd.Desktops[1].Id, w.Vd.Current);
    }

    [Fact]
    public async Task Rules_override_session_desktop_and_state()
    {
        var w = new World();
        w.Rules.Add(new AppRule { ProcessName = "Discord", TargetDesktop = 3, StartState = RuleStartState.Minimized });
        w.AppearsAfterLaunch("Discord", "Discord", 40);
        var snap = w.Snapshot(T.W("Discord", "Discord", 0, kind: AppKind.SingleInstance));
        var report = await w.Restorer().RestoreAsync(snap, Fast);
        Assert.Equal(1, report.PlacedCount);
        Assert.Equal(w.Vd.Desktops[2].Id, w.Vd.WindowDesktop[40]);
        Assert.Equal(WindowShowState.Minimized, w.Win.Applied[40].State);
        Assert.Equal("--start-minimized", w.Proc.Launched.Single().Arguments);
    }

    [Fact]
    public async Task Splash_screen_is_ignored_until_main_window_appears()
    {
        var w = new World();
        w.Proc.OnLaunch["Photoshop"] = _ =>
        {
            var splash = T.W("Photoshop", "Adobe Photoshop", 0, cls: "Photoshop", handle: 50);
            splash.HasCaption = false; splash.IsResizable = false;
            w.Win.Add(splash);
            w.Clock.Schedule(TimeSpan.FromMilliseconds(150), () => { w.Win.Remove(50); w.Win.Add(T.W("Photoshop", "Adobe Photoshop 2026", 0, cls: "Photoshop", handle: 51)); });
        };
        var snap = w.Snapshot(T.W("Photoshop", "Adobe Photoshop 2026", 1, cls: "Photoshop"));
        var report = await w.Restorer().RestoreAsync(snap, Fast);
        Assert.Equal(RestoreItemStatus.Restored, report.Items.Single().Status);
        Assert.Equal(51, report.Items.Single().AssignedHandle);
        Assert.False(w.Win.Applied.ContainsKey(50));
    }

    [Fact]
    public async Task Failures_are_isolated_and_logged_other_apps_still_restored()
    {
        var w = new World();
        w.Proc.Missing.Add("Uninstalled");
        w.Proc.NeedsAdmin.Add("AdminTool");
        w.AppearsAfterLaunch("code", "Projekt – Visual Studio Code", 60);
        // „Silent“ startet, aber zeigt nie ein Fenster
        var snap = w.Snapshot(T.W("Uninstalled", "x"), T.W("AdminTool", "y"), T.W("code", "Projekt – Visual Studio Code", 1), T.W("Silent", "z"));
        var report = await w.Restorer().RestoreAsync(snap, Fast with { WindowTimeout = TimeSpan.FromMilliseconds(300) });
        Assert.Equal(RestoreItemStatus.NotFound, report.Items.Single(i => i.Saved.ProcessName == "Uninstalled").Status);
        Assert.Equal(RestoreItemStatus.NeedsElevation, report.Items.Single(i => i.Saved.ProcessName == "AdminTool").Status);
        Assert.Equal(RestoreItemStatus.TimedOut, report.Items.Single(i => i.Saved.ProcessName == "Silent").Status);
        Assert.Equal(RestoreItemStatus.Restored, report.Items.Single(i => i.Saved.ProcessName == "code").Status);
        Assert.Contains(w.Log.Recent, e => e.Level == LogLevel.Warning && e.Message.Contains("Uninstalled"));
    }

    [Fact]
    public async Task Elevation_only_after_consent()
    {
        var w = new World();
        w.Proc.NeedsAdmin.Add("AdminTool");
        w.AppearsAfterLaunch("AdminTool", "Admin", 70);
        var consent = new Consent(true);
        var restorer = new SessionRestorer(w.Win, w.Vd, w.Proc, w.Mon, new RuleEngine(() => w.Rules), w.Log, w.Clock, consent, _ => true, _ => true);
        var snap = w.Snapshot(T.W("AdminTool", "Admin"));
        var report = await restorer.RestoreAsync(snap, Fast with { ElevationPolicy = ElevationPolicy.Ask });
        Assert.True(consent.Asked);
        Assert.Equal(RestoreItemStatus.Restored, report.Items.Single().Status);
    }

    private sealed class Consent(bool answer) : IElevationConsent
    {
        public bool Asked { get; private set; }
        public Task<bool> ConfirmAsync(string appName, CancellationToken ct) { Asked = true; return Task.FromResult(answer); }
    }

    [Fact]
    public async Task Missing_desktops_are_created_and_offscreen_windows_corrected()
    {
        var w = new World();
        var snap = w.Snapshot();
        snap.Desktops.Add(new DesktopInfo { Id = Guid.NewGuid(), Index = 3, Name = "Schule" });
        var win = T.W("code", "Hausaufgaben", 3, RectI.FromSize(4000, 3000, 800, 600), handle: 80);
        win.DesktopId = snap.Desktops[3].Id;
        snap.Windows.Add(win);
        w.Win.Add(T.W("code", "Hausaufgaben", 0, handle: 80));
        w.Proc.Running.Add(new RunningProcess(5, "code", null));

        var report = await w.Restorer().RestoreAsync(snap, Fast);
        Assert.Equal(4, w.Vd.Desktops.Count);
        Assert.Equal("Schule", w.Vd.Desktops[3].Name);
        Assert.Equal(w.Vd.Desktops[3].Id, w.Vd.WindowDesktop[80]);
        Assert.True(PlacementCorrector.IsReachable(w.Win.Applied[80].NormalBounds, w.Mon.Monitors));
        Assert.Equal(RestoreItemStatus.AlreadyRunning, report.Items.Single().Status);
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        var w = new World();
        w.Win.Add(T.W("code", "a", 0, handle: 90));
        w.Proc.Running.Add(new RunningProcess(5, "code", null));
        var snap = w.Snapshot(T.W("code", "a", 2), T.W("Resolve", "r", 0));
        var report = await w.Restorer().RestoreAsync(snap, Fast with { DryRun = true });
        Assert.Empty(w.Win.Applied);
        Assert.Empty(w.Proc.Launched);
        Assert.Empty(w.Vd.Calls);
        Assert.True(report.DryRun);
    }

    [Fact]
    public async Task Without_internal_desktop_api_windows_are_still_placed()
    {
        var w = new World();
        w.Vd.Manage = false;
        w.Win.Add(T.W("code", "a", 0, handle: 91));
        w.Proc.Running.Add(new RunningProcess(5, "code", null));
        var report = await w.Restorer().RestoreAsync(w.Snapshot(T.W("code", "a", 2)), Fast);
        Assert.Equal(1, report.PlacedCount);
        Assert.Empty(w.Vd.WindowDesktop);
        Assert.Contains(w.Log.Recent, e => e.Message.Contains("eingeschränkt"));
    }

    [Fact]
    public void Extra_windows_are_only_apps_not_in_profile()
    {
        var w = new World();
        w.Win.Add(T.W("code", "a", handle: 1));
        w.Win.Add(T.W("spotify", "Spotify", handle: 2));
        w.Win.Add(T.W("code", "b", handle: 3)); // weiteres Fenster eines Profil-Programms → nicht schließen
        var extra = w.Restorer().FindExtraWindows(w.Snapshot(T.W("code", "a")), Fast);
        Assert.Equal(2, Assert.Single(extra).Handle);
    }

    [Fact]
    public async Task Concurrent_restore_is_rejected()
    {
        var w = new World();
        w.AppearsAfterLaunch("slow", "s", 99, delayMs: 200);
        w.Clock.Pause();
        var r = w.Restorer();
        var first = r.RestoreAsync(w.Snapshot(T.W("slow", "s")), Fast);
        await w.Clock.WaitUntilPaused();
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.RestoreAsync(w.Snapshot(), Fast));
        w.Clock.Resume();
        await first;
    }

    [Fact]
    public async Task Two_browser_windows_restore_their_own_snap_and_desktop()
    {
        var w = new World();
        w.Proc.OnLaunch["chrome"] = _ => w.Clock.Schedule(TimeSpan.FromMilliseconds(60), () =>
        {
            w.Win.Add(T.W("chrome", "Links", cls: "Chrome_WidgetWin_1", handle: 11, kind: AppKind.Browser));
            w.Win.Add(T.W("chrome", "Rechts", cls: "Chrome_WidgetWin_1", handle: 12, kind: AppKind.Browser));
        });
        var left = T.W("chrome", "Links", 0, RectI.FromSize(10, 10, 400, 300), cls: "Chrome_WidgetWin_1", kind: AppKind.Browser);
        left.Snap = SnapLayout.LeftHalf;
        var right = T.W("chrome", "Rechts", 1, RectI.FromSize(50, 50, 400, 300), cls: "Chrome_WidgetWin_1", kind: AppKind.Browser);
        right.Snap = SnapLayout.RightHalf;

        var report = await w.Restorer().RestoreAsync(w.Snapshot(left, right), Fast);

        Assert.Equal(2, report.PlacedCount);
        Assert.Equal(1, report.Launched);
        Assert.Single(w.Proc.Launched);
        Assert.Equal(new RectI(0, 0, 960, 1032), w.Win.Applied[11].NormalBounds);
        Assert.Equal(new RectI(960, 0, 1920, 1032), w.Win.Applied[12].NormalBounds);
        Assert.Equal(WindowShowState.Normal, w.Win.Applied[11].State);
        Assert.Equal(w.Vd.Desktops[0].Id, w.Vd.WindowDesktop[11]);
        Assert.Equal(w.Vd.Desktops[1].Id, w.Vd.WindowDesktop[12]);
        Assert.All(report.Items, i => Assert.Equal(RestoreItemStatus.Restored, i.Status));
    }

    [Fact]
    public async Task Snap_half_uses_the_current_monitor_work_area()
    {
        var w = new World();
        var saved = T.W("code", "Editor", 1, RectI.FromSize(40, 40, 500, 400));
        saved.Snap = SnapLayout.LeftHalf;
        var snap = w.Snapshot(saved);
        snap.Monitors = new List<MonitorInfo> { T.Mon("M1", 0, 0, 1920, 1080, primary: true) };
        w.Mon.Monitors.Clear();
        w.Mon.Monitors.Add(T.Mon("M1", 0, 0, 2560, 1440, primary: true));
        w.Win.Add(T.W("code", "Editor", handle: 15));
        w.Proc.Running.Add(new RunningProcess(5, "code", null));

        var report = await w.Restorer().RestoreAsync(snap, Fast);

        Assert.Equal(RestoreItemStatus.AlreadyRunning, report.Items.Single().Status);
        Assert.Equal(new RectI(0, 0, 1280, 1392), w.Win.Applied[15].NormalBounds);
        Assert.Equal(WindowShowState.Normal, w.Win.Applied[15].State);
    }

    [Fact]
    public async Task Placement_that_drops_the_desktop_is_corrected_afterwards()
    {
        var w = new World();
        w.Win.Add(T.W("ms-teams", "Teams", handle: 7, kind: AppKind.SingleInstance));
        w.Proc.Running.Add(new RunningProcess(1, "ms-teams", null));
        w.Win.AfterApply = h => w.Vd.WindowDesktop.Remove(h);

        var report = await w.Restorer().RestoreAsync(w.Snapshot(T.W("ms-teams", "Teams", 2, kind: AppKind.SingleInstance)), Fast);

        Assert.Equal(RestoreItemStatus.AlreadyRunning, report.Items.Single().Status);
        Assert.Equal(w.Vd.Desktops[2].Id, w.Vd.WindowDesktop[7]);
    }

    [Fact]
    public async Task Running_app_without_a_window_is_woken_once_onto_its_desktop()
    {
        var w = new World();
        w.Proc.Running.Add(new RunningProcess(4, "ms-teams", null));
        DateTimeOffset? launchedAt = null;
        var start = w.Clock.Now;
        w.Proc.OnLaunch["ms-teams"] = _ =>
        {
            launchedAt = w.Clock.Now;
            w.Clock.Schedule(TimeSpan.FromMilliseconds(60), () => w.Win.Add(T.W("ms-teams", "Teams", handle: 8, kind: AppKind.SingleInstance)));
        };

        var report = await w.Restorer().RestoreAsync(w.Snapshot(T.W("ms-teams", "Teams", 2, kind: AppKind.SingleInstance)), Fast);

        Assert.Equal(RestoreItemStatus.Restored, report.Items.Single().Status);
        var launch = Assert.Single(w.Proc.Launched);
        Assert.Contains("ms-teams", launch.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(launchedAt);
        Assert.True(launchedAt!.Value - start >= Fast.RunningAppGrace);
        Assert.Equal(w.Vd.Desktops[2].Id, w.Vd.WindowDesktop[8]);
    }

    [Fact]
    public async Task Running_browser_without_a_window_is_not_started_again()
    {
        var w = new World();
        w.Proc.Running.Add(new RunningProcess(1, "chrome", null));
        var report = await w.Restorer().RestoreAsync(
            w.Snapshot(T.W("chrome", "Tab", 2, kind: AppKind.Browser)),
            Fast with { WindowTimeout = TimeSpan.FromMilliseconds(300) });

        Assert.Empty(w.Proc.Launched);
        Assert.Equal(RestoreItemStatus.Skipped, report.Items.Single().Status);
        Assert.Contains("läuft bereits", report.Items.Single().Message);
    }

    [Fact]
    public async Task Unknown_desktop_index_still_follows_the_saved_desktop_id()
    {
        var w = new World();
        var snap = w.Snapshot();
        var saved = T.W("code", "Notiz");
        saved.DesktopIndex = -1;
        saved.DesktopId = snap.Desktops[2].Id;
        snap.Windows.Add(saved);
        w.Win.Add(T.W("code", "Notiz", handle: 81));
        w.Proc.Running.Add(new RunningProcess(5, "code", null));

        var report = await w.Restorer().RestoreAsync(snap, Fast);

        Assert.Equal(RestoreItemStatus.AlreadyRunning, report.Items.Single().Status);
        Assert.Equal(w.Vd.Desktops[2].Id, w.Vd.WindowDesktop[81]);
    }
}
