using DeskRecall.Core.Geometry;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Recording;
using DeskRecall.Core.Restore;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Session;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Storage;
using Xunit;

namespace DeskRecall.Core.Tests;

public class SnapLayoutMathTests
{
    private static readonly MonitorInfo Monitor = T.Mon("M1", 0, 0, 1920, 1080, primary: true);

    [Fact]
    public void Classifies_halves_quarters_free_fullscreen_and_maximized()
    {
        Assert.Equal(SnapLayout.LeftHalf, SnapLayoutMath.Classify(RectI.FromSize(0, 0, 960, 1032), WindowShowState.Normal, Monitor));
        Assert.Equal(SnapLayout.RightHalf, SnapLayoutMath.Classify(RectI.FromSize(960, 0, 960, 1032), WindowShowState.Normal, Monitor));
        Assert.Equal(SnapLayout.TopLeft, SnapLayoutMath.Classify(new RectI(0, 0, 960, 516), WindowShowState.Normal, Monitor));
        Assert.Equal(SnapLayout.TopRight, SnapLayoutMath.Classify(new RectI(960, 0, 1920, 516), WindowShowState.Normal, Monitor));
        Assert.Equal(SnapLayout.BottomLeft, SnapLayoutMath.Classify(new RectI(0, 516, 960, 1032), WindowShowState.Normal, Monitor));
        Assert.Equal(SnapLayout.BottomRight, SnapLayoutMath.Classify(new RectI(960, 516, 1920, 1032), WindowShowState.Normal, Monitor));
        Assert.Equal(SnapLayout.Free, SnapLayoutMath.Classify(RectI.FromSize(100, 100, 800, 600), WindowShowState.Normal, Monitor));
        Assert.Equal(SnapLayout.FullScreen, SnapLayoutMath.Classify(Monitor.Bounds, WindowShowState.Normal, Monitor));
        Assert.Equal(SnapLayout.Maximized, SnapLayoutMath.Classify(RectI.FromSize(100, 100, 800, 600), WindowShowState.Maximized, Monitor));
        Assert.Equal(SnapLayout.Maximized, SnapLayoutMath.Classify(Monitor.WorkArea, WindowShowState.Normal, Monitor));
    }

    [Fact]
    public void Bounds_follow_the_work_area()
    {
        Assert.Equal(new RectI(0, 0, 960, 1032), SnapLayoutMath.BoundsFor(SnapLayout.LeftHalf, Monitor));
        Assert.Equal(new RectI(960, 0, 1920, 1032), SnapLayoutMath.BoundsFor(SnapLayout.RightHalf, Monitor));
        Assert.Equal(Monitor.Bounds, SnapLayoutMath.BoundsFor(SnapLayout.FullScreen, Monitor));
    }
}

public class SessionClosureTests
{
    [Fact]
    public void Merge_adds_closed_windows_once_and_a_new_handle_clears_the_closure()
    {
        var id = Guid.NewGuid();
        var closures = new SessionClosures();
        var closed = T.W("code", "a", 1, handle: 9);
        closed.DesktopId = id;
        closures.Remember(new SessionClosure
        {
            AppKey = closed.AppKey,
            AppName = "code",
            DesktopId = id,
            DesktopIndex = 1,
            Windows = new List<WindowRecord> { closed.Clone() },
        });

        var empty = new SessionSnapshot();
        closures.MergeInto(empty);
        var added = Assert.Single(empty.Windows);
        Assert.Equal(0, added.Handle);
        Assert.Equal(0, added.ProcessId);
        Assert.Equal("code", added.ProcessName);

        var present = new SessionSnapshot { Windows = { T.W("code", "noch da", 1, handle: 3) } };
        present.Windows[0].DesktopId = id;
        closures.MergeInto(present);
        var kept = Assert.Single(present.Windows);
        Assert.Equal(3, kept.Handle);

        closures.ForgetRunning(new[] { closed });
        Assert.Single(closures.ForDesktop(id, 1));

        var fresh = closed.Clone();
        fresh.Handle = 99;
        closures.ForgetRunning(new[] { fresh });
        Assert.Empty(closures.ForDesktop(id, 1));
    }
}

public class StartupChoiceTests
{
    [Fact]
    public void Named_snapshot_wins_when_it_has_content()
    {
        var last = new SessionSnapshot { Windows = { T.W("teams") } };
        var profile = new SessionSnapshot { Windows = { T.W("code"), T.W("chrome", kind: AppKind.Browser) } };
        Assert.Same(profile, StartupChoice.Select(profile, last));

        var desksOnly = new SessionSnapshot { Desktops = { new DesktopInfo { Name = "Arbeit" } } };
        Assert.Same(desksOnly, StartupChoice.Select(desksOnly, last));

        Assert.Same(last, StartupChoice.Select(null, last));
        Assert.Same(last, StartupChoice.Select(new SessionSnapshot(), last));
    }
}

public class SnapshotCaptureTests
{
    [Fact]
    public void Recorder_stores_the_snap_zone()
    {
        var vd = new FakeVirtualDesktops("Eins");
        var win = new FakeWindows();
        var half = T.W("code", "Editor", bounds: RectI.FromSize(0, 0, 960, 1032), handle: 4);
        half.DesktopId = vd.Desktops[0].Id;
        win.Add(half);
        var rec = new SessionRecorder(win, vd, new FakeMonitors(), new RuleEngine(Array.Empty<AppRule>()), () => AppSettings.CreateDefault(), new MemoryLogService());
        var snap = rec.Capture(SessionKind.Manual);
        Assert.Equal(SnapLayout.LeftHalf, Assert.Single(snap.Windows).Snap);
    }

    [Fact]
    public void Hash_changes_when_only_the_snap_zone_changes()
    {
        var s = new SessionSnapshot { Windows = { T.W("a") } };
        var hash = SessionHasher.Compute(s);
        s.Windows[0].Snap = SnapLayout.LeftHalf;
        Assert.NotEqual(hash, SessionHasher.Compute(s));
    }
}
