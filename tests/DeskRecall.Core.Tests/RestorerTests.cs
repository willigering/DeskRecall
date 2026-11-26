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
}
