using DeskRecall.Core.Geometry;
using DeskRecall.Core.Models;
using DeskRecall.Core.Restore;
using Xunit;

namespace DeskRecall.Core.Tests;

public class WindowMatcherTests
{
    [Fact]
    public void Browser_windows_are_matched_by_title_similarity()
    {
        var saved = new[]
        {
            T.W("firefox", "GitHub – willigering/gering-digital — Mozilla Firefox", 1, cls: "MozillaWindowClass"),
            T.W("firefox", "YouTube — Mozilla Firefox", 2, cls: "MozillaWindowClass"),
        };
        var live = new[]
        {
            T.W("firefox", "YouTube — Mozilla Firefox", handle: 11, cls: "MozillaWindowClass"),
            T.W("firefox", "Pull requests · willigering/gering-digital · GitHub — Mozilla Firefox", handle: 12, cls: "MozillaWindowClass"),
        };
        var m = WindowMatcher.Match(saved, live).ToDictionary(x => x.SavedIndex, x => x.Live.Handle);
        Assert.Equal(12, m[0]);
        Assert.Equal(11, m[1]);
    }

    [Fact]
    public void Each_live_window_is_used_only_once()
    {
        var saved = new[] { T.W("code", "a"), T.W("code", "b"), T.W("code", "c") };
        var live = new[] { T.W("code", "a", handle: 1), T.W("code", "x", handle: 2) };
        var m = WindowMatcher.Match(saved, live);
        Assert.Equal(2, m.Count);
        Assert.Equal(2, m.Select(x => x.Live.Handle).Distinct().Count());
        Assert.Contains(m, x => x.SavedIndex == 0 && x.Live.Handle == 1);
    }

    [Fact]
    public void Different_process_never_matches()
    {
        Assert.Null(WindowMatcher.Score(T.W("chrome", "Neuer Tab"), T.W("msedge", "Neuer Tab")));
    }

    [Fact]
    public void Explorer_windows_match_by_folder()
    {
        var saved = new[] { T.W("explorer", "Downloads", kind: AppKind.Explorer, folder: @"C:\Users\W\Downloads", cls: "CabinetWClass"),
                            T.W("explorer", "Bilder", kind: AppKind.Explorer, folder: @"C:\Users\W\Pictures", cls: "CabinetWClass") };
        var live = new[] { T.W("explorer", "Bilder", handle: 5, kind: AppKind.Explorer, folder: @"C:\Users\W\Pictures", cls: "CabinetWClass"),
                           T.W("explorer", "Downloads", handle: 6, kind: AppKind.Explorer, folder: @"C:\Users\W\Downloads\", cls: "CabinetWClass") };
        var m = WindowMatcher.Match(saved, live).ToDictionary(x => x.SavedIndex, x => x.Live.Handle);
        Assert.Equal(6, m[0]);
        Assert.Equal(5, m[1]);
    }

    [Fact]
    public void Class_mismatch_prevents_matching_splash_or_helper_windows()
    {
        var saved = T.W("Resolve", "DaVinci Resolve", cls: "Qt5QWindowIcon");
        var splash = T.W("Resolve", "DaVinci Resolve", cls: "SplashScreen", handle: 3);
        var main = T.W("Resolve", "DaVinci Resolve - Projekt", cls: "Qt5QWindowIcon", handle: 4);
        Assert.True(WindowMatcher.Score(saved, splash) < WindowMatcher.Score(saved, main));
        Assert.Equal(4, WindowMatcher.Match(new[] { saved }, new[] { splash, main }).Single().Live.Handle);
    }

    [Fact]
    public void Same_live_handle_and_pid_wins_without_reboot()
    {
        var saved = T.W("code", "a", handle: 42); saved.ProcessId = 7;
        var same = T.W("code", "b", handle: 42); same.ProcessId = 7;
        var other = T.W("code", "a", handle: 43); other.ProcessId = 7;
        var m = WindowMatcher.Match(new[] { saved }, new[] { other, same });
        Assert.Equal(42, m.Single().Live.Handle);
    }

    [Fact]
    public void Title_similarity_is_symmetric_and_bounded()
    {
        var s = WindowMatcher.TitleSimilarity("Projekt A – Visual Studio", "Projekt B – Visual Studio");
        Assert.InRange(s, 0.3, 0.99);
        Assert.Equal(s, WindowMatcher.TitleSimilarity("Projekt B – Visual Studio", "Projekt A – Visual Studio"));
        Assert.Equal(0, WindowMatcher.TitleSimilarity("", "abc"));
    }
}

public class PlacementCorrectorTests
{
    private static readonly MonitorInfo[] Two = { T.Mon("L", -1920, 0, 1920, 1080), T.Mon("P", 0, 0, 2560, 1440, primary: true) };

    [Fact]
    public void Reachable_windows_stay_untouched_even_on_negative_coordinates()
    {
        var r = RectI.FromSize(-1800, 50, 1200, 800);
        Assert.Equal(r, PlacementCorrector.Correct(r, Two));
    }

    [Fact]
    public void Offscreen_window_is_moved_into_work_area()
    {
        var r = RectI.FromSize(5000, 3000, 800, 600);
        var c = PlacementCorrector.Correct(r, Two);
        Assert.True(PlacementCorrector.IsReachable(c, Two));
        Assert.Equal(800, c.Width);
        Assert.Equal(600, c.Height);
    }

    [Fact]
    public void Window_with_title_bar_above_screen_is_pulled_down()
    {
        var r = RectI.FromSize(100, -400, 800, 600);
        var c = PlacementCorrector.Correct(r, Two);
        Assert.True(c.Top >= 0);
        Assert.True(PlacementCorrector.IsReachable(c, Two));
    }

    [Fact]
    public void Too_large_window_is_shrunk_to_work_area()
    {
        var single = new[] { T.Mon("P", 0, 0, 1920, 1080, primary: true) };
        var c = PlacementCorrector.Correct(RectI.FromSize(0, 0, 3000, 2000), single);
        Assert.True(c.Width <= 1920 && c.Height <= 1032);
    }

    [Fact]
    public void Preferred_monitor_is_used_for_correction()
    {
        var c = PlacementCorrector.Correct(RectI.FromSize(9000, 9000, 400, 300), Two, Two[0]);
        Assert.True(Two[0].WorkArea.Contains(c));
    }
}

public class MonitorRemapperTests
{
    [Fact]
    public void Finds_same_monitor_by_stable_id_even_if_gdi_name_changed()
    {
        var saved = T.Mon("LG-27", 0, 0, 2560, 1440, primary: true); saved.DeviceName = @"\\.\DISPLAY1";
        var now = T.Mon("LG-27", 0, 0, 2560, 1440, primary: true); now.DeviceName = @"\\.\DISPLAY3";
        Assert.Same(now, MonitorRemapper.FindCurrent(saved, new[] { T.Mon("X", 2560, 0, 1920, 1080), now }));
    }

    [Fact]
    public void Removed_monitor_maps_to_primary_by_default_or_nearest()
    {
        var saved = T.Mon("Beamer", 3840, 0, 1920, 1080);
        var primary = T.Mon("P", 0, 0, 1920, 1080, primary: true);
        var right = T.Mon("R", 1920, 0, 1920, 1080);
        Assert.Same(primary, MonitorRemapper.FindCurrent(saved, new[] { primary, right }));
        Assert.Same(right, MonitorRemapper.FindCurrent(saved, new[] { primary, right }, MonitorFallback.Nearest));
    }

    [Fact]
    public void Resolution_change_scales_window_proportionally()
    {
        var from = RectI.FromSize(0, 0, 1920, 1040);
        var to = RectI.FromSize(0, 0, 3840, 2080);
        var r = MonitorRemapper.Transform(RectI.FromSize(960, 0, 960, 1040), from, to);
        Assert.Equal(RectI.FromSize(1920, 0, 1920, 2080), r);
    }

    [Fact]
    public void Same_size_monitor_at_other_position_only_translates()
    {
        var r = MonitorRemapper.Transform(RectI.FromSize(100, 100, 800, 600), RectI.FromSize(0, 0, 1920, 1040), RectI.FromSize(-1920, 0, 1920, 1040));
        Assert.Equal(RectI.FromSize(-1820, 100, 800, 600), r);
    }

    [Fact]
    public void Scaling_can_be_disabled()
    {
        var r = MonitorRemapper.Transform(RectI.FromSize(100, 100, 800, 600), RectI.FromSize(0, 0, 1920, 1040), RectI.FromSize(0, 0, 1280, 680), scale: false);
        Assert.Equal(800, r.Width);
    }
}
