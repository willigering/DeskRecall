using DeskRecall.Core.Desktops;
using DeskRecall.Core.Models;
using Xunit;

namespace DeskRecall.Core.Tests;

public class WindowActivationTests
{
    static (FakeVirtualDesktops vd, FakeWindows windows, FakeClock clock, Guid d1, Guid d2) Setup()
    {
        var vd = new FakeVirtualDesktops("Eins", "Zwei");
        var d1 = vd.Desktops[0].Id;
        var d2 = vd.Desktops[1].Id;
        vd.Current = d1;
        var windows = new FakeWindows();
        var clock = new FakeClock();
        return (vd, windows, clock, d1, d2);
    }

    [Fact]
    public void ResolveDesktop_uses_the_known_desktop_without_a_com_call()
    {
        var (vd, _, _, d1, d2) = Setup();
        vd.WindowDesktop[42] = d2;
        Assert.Equal(d1, WindowActivation.ResolveDesktop(vd, 42, d1));
    }

    [Fact]
    public void ResolveDesktop_asks_com_only_when_the_desktop_is_unknown()
    {
        var (vd, _, _, _, d2) = Setup();
        vd.WindowDesktop[42] = d2;
        Assert.Equal(d2, WindowActivation.ResolveDesktop(vd, 42, null));
        Assert.Equal(d2, WindowActivation.ResolveDesktop(vd, 42, Guid.Empty));
    }

    [Fact]
    public void ResolveDesktop_falls_back_to_the_given_desktop()
    {
        var (vd, _, _, _, d2) = Setup();
        Assert.Equal(d2, WindowActivation.ResolveDesktop(vd, 42, d2));
        Assert.Equal(Guid.Empty, WindowActivation.ResolveDesktop(vd, 0, null));
        Assert.Equal(Guid.Empty, WindowActivation.ResolveDesktop(vd, 99, null));
    }

    [Fact]
    public async Task Already_on_the_target_desktop_only_brings_the_window_to_front()
    {
        var (vd, windows, clock, d1, _) = Setup();
        var ok = await WindowActivation.ActivateAsync(vd, windows, 7, d1, clock);
        Assert.True(ok);
        Assert.Empty(vd.Calls);
        Assert.Equal(new[] { 7L }, windows.Focused);
    }

    [Fact]
    public async Task Switches_then_focuses_after_the_desktop_has_changed()
    {
        var (vd, windows, clock, _, d2) = Setup();
        vd.SwitchSettleAfterGets = 3; // first get after switch still old, third get settles
        var start = clock.Now;
        var ok = await WindowActivation.ActivateAsync(vd, windows, 11, d2, clock);
        Assert.True(ok);
        Assert.Equal(new[] { "switch 1" }, vd.Calls);
        Assert.Equal(d2, vd.Current);
        Assert.Equal(new[] { 11L }, windows.Focused);
        Assert.True(clock.Now < start + WindowActivation.SwitchTimeout);
        // BringToFront erst nach dem Wechsel, nicht davor
        Assert.Equal("switch 1", vd.Calls[0]);
    }

    [Fact]
    public async Task After_timeout_still_tries_to_focus()
    {
        var (vd, windows, clock, d1, d2) = Setup();
        vd.SwitchSettleAfterGets = 10_000; // nie
        var start = clock.Now;
        var ok = await WindowActivation.ActivateAsync(vd, windows, 11, d2, clock);
        Assert.True(ok);
        Assert.Equal(d1, vd.Current); // Wechsel noch nicht sichtbar
        Assert.Equal(new[] { 11L }, windows.Focused);
        Assert.True(clock.Now >= start + WindowActivation.SwitchTimeout);
    }

    [Fact]
    public async Task SwitchToDesktop_is_a_no_op_when_already_there()
    {
        var (vd, _, clock, d1, _) = Setup();
        Assert.True(await WindowActivation.SwitchToDesktopAsync(vd, d1, clock));
        Assert.Empty(vd.Calls);
    }

    [Fact]
    public async Task SwitchToDesktop_waits_until_current_matches()
    {
        var (vd, _, clock, _, d2) = Setup();
        vd.SwitchSettleAfterGets = 2;
        Assert.True(await WindowActivation.SwitchToDesktopAsync(vd, d2, clock));
        Assert.Equal(d2, vd.Current);
        Assert.Equal(new[] { "switch 1" }, vd.Calls);
    }
}

public class AppRowMostRecentTests
{
    static WindowRecord Win(long h, int z) => new() { Handle = h, ZOrder = z, ProcessName = "app", Title = h.ToString() };

    [Fact]
    public void MostRecent_is_the_lowest_z_order()
    {
        var windows = new[] { Win(1, 8), Win(2, 2), Win(3, 5) };
        Assert.Equal(2, AppRowInput.MostRecent(windows)?.Handle);
    }

    [Fact]
    public void MostRecent_is_null_when_empty()
        => Assert.Null(AppRowInput.MostRecent(Array.Empty<WindowRecord>()));
}
