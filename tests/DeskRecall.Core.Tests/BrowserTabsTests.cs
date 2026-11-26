using DeskRecall.Core.Browsers;
using DeskRecall.Core.Desktops;
using DeskRecall.Core.Models;
using Xunit;

namespace DeskRecall.Core.Tests;

public class BrowserTabsTests
{
    [Theory]
    [InlineData("chrome", "Chrome_WidgetWin_1", BrowserFamily.Chromium)]
    [InlineData("msedge.exe", "Chrome_WidgetWin_1", BrowserFamily.Chromium)]
    [InlineData("Brave", null, BrowserFamily.Chromium)]
    [InlineData("vivaldi", "Chrome_WidgetWin_1", BrowserFamily.Chromium)]
    [InlineData("firefox", "MozillaWindowClass", BrowserFamily.Firefox)]
    [InlineData("librewolf.exe", "MozillaWindowClass", BrowserFamily.Firefox)]
    [InlineData("chrome", "Chrome_WidgetWin_0", BrowserFamily.None)]      // z. B. versteckte Hilfsfenster
    [InlineData("firefox", "MozillaDialogClass", BrowserFamily.None)]
    [InlineData("notepad", "Notepad", BrowserFamily.None)]
    [InlineData("", null, BrowserFamily.None)]
    [InlineData(null, null, BrowserFamily.None)]
    public void Detects_browser_families(string? proc, string? cls, BrowserFamily expected)
        => Assert.Equal(expected, BrowserCatalog.Detect(proc, cls));

    [Theory]
    [InlineData("  Posteingang   -  Gmail ", "Posteingang - Gmail")]
    [InlineData("(3) WhatsApp", "WhatsApp")]
    [InlineData("(99+) Facebook", "Facebook")]
    [InlineData("● Ungespeichert", "Ungespeichert")]
    [InlineData("• Musik", "Musik")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalizes_tab_titles(string? input, string expected) => Assert.Equal(expected, TabTitle.Normalize(input));

    [Fact]
    public void Display_title_falls_back_for_empty_tabs()
    {
        Assert.Equal("(ohne Titel)", TabTitle.Display("  "));
        Assert.Equal("Neuer Tab", TabTitle.Display(" Neuer Tab "));
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Policy_reads_new_windows_and_throttles_repeat_reads()
    {
        Assert.True(TabReadPolicy.ShouldRead(null, "A", T0));
        var s = new TabReadState();
        TabReadPolicy.RecordSuccess(s, "A", new[] { new BrowserTab(0, "A", true) }, T0);
        Assert.False(TabReadPolicy.ShouldRead(s, "A", T0.AddSeconds(1)));
        Assert.True(TabReadPolicy.ShouldRead(s, "A", T0 + TabReadPolicy.Interval));
    }

    [Fact]
    public void Policy_rereads_immediately_after_a_title_change()
    {
        var s = new TabReadState();
        TabReadPolicy.RecordSuccess(s, "A", Array.Empty<BrowserTab>(), T0);
        Assert.True(TabReadPolicy.ShouldRead(s, "B", T0.AddMilliseconds(100)));
    }

    [Fact]
    public void Policy_backs_off_exponentially_and_caps()
    {
        var s = new TabReadState();
        TabReadPolicy.RecordFailure(s, "A", T0);
        Assert.Equal(T0.AddSeconds(2), s.BackoffUntil);
        Assert.False(TabReadPolicy.ShouldRead(s, "B", T0.AddSeconds(1)));   // auch Titelwechsel wartet den Rückzug ab
        TabReadPolicy.RecordFailure(s, "A", T0);
        Assert.Equal(T0.AddSeconds(4), s.BackoffUntil);
        for (var i = 0; i < 10; i++) TabReadPolicy.RecordFailure(s, "A", T0);
        Assert.Equal(T0 + TabReadPolicy.MaxBackoff, s.BackoffUntil);
        TabReadPolicy.RecordSuccess(s, "A", Array.Empty<BrowserTab>(), T0.AddMinutes(2));
        Assert.Equal(0, s.Failures);
        Assert.Equal(default, s.BackoffUntil);
    }

    [Fact]
    public void SameTabs_compares_order_title_and_selection()
    {
        var a = new[] { new BrowserTab(0, "A", true), new BrowserTab(1, "B", false) };
        Assert.True(TabReadPolicy.SameTabs(a, new[] { new BrowserTab(0, "A", true), new BrowserTab(1, "B", false) }));
        Assert.False(TabReadPolicy.SameTabs(a, new[] { new BrowserTab(0, "A", false), new BrowserTab(1, "B", true) }));
        Assert.False(TabReadPolicy.SameTabs(a, new[] { new BrowserTab(0, "A", true) }));
    }
}

public class TabAliasStoreTests
{
    static string NewPath() => Path.Combine(T.TempDir(), "tab-names.json");

    [Fact]
    public void Set_find_and_reset_an_alias()
    {
        var store = new TabAliasStore(NewPath());
        Assert.Null(store.Find("chrome", "Posteingang - Gmail"));
        Assert.True(store.Set("chrome.exe", "Posteingang - Gmail", "  Mail  "));
        Assert.Equal("Mail", store.Find("Chrome", "Posteingang - Gmail"));
        Assert.Equal("Mail", store.Find("chrome", "(4) Posteingang  -  gmail"));   // Zähler/Leerraum/Großschreibung egal
        Assert.Null(store.Find("msedge", "Posteingang - Gmail"));                 // pro Browser
        Assert.True(store.Reset("chrome", "Posteingang - Gmail"));
        Assert.Null(store.Find("chrome", "Posteingang - Gmail"));
    }

    [Fact]
    public void Empty_or_original_name_resets_the_alias()
    {
        var store = new TabAliasStore(NewPath());
        store.Set("firefox", "Wetter", "Wetterbericht");
        store.Set("firefox", "Wetter", "   ");
        Assert.Null(store.Find("firefox", "Wetter"));
        store.Set("firefox", "Wetter", "Wetterbericht");
        store.Set("firefox", "Wetter", "Wetter");
        Assert.Null(store.Find("firefox", "Wetter"));
    }

    [Fact]
    public void Aliases_are_limited_and_persisted()
    {
        var path = NewPath();
        var store = new TabAliasStore(path);
        store.Set("chrome", "Lang", new string('x', 200));
        Assert.Equal(TabAliasStore.MaxAliasLength, store.Find("chrome", "Lang")!.Length);
        store.Set("chrome", "Doku", "Handbuch");
        var reloaded = new TabAliasStore(path);
        Assert.Equal("Handbuch", reloaded.Find("chrome", "Doku"));
        Assert.Equal(2, reloaded.Count);
    }

    [Fact]
    public void Url_matches_before_title_when_known()
    {
        var store = new TabAliasStore(NewPath());
        store.Set("chrome", "Seite A", "Mein Projekt", url: "https://example.org/p");
        Assert.Equal("Mein Projekt", store.Find("chrome", "Anderer Titel", "https://example.org/p"));
    }

    [Fact]
    public void Corrupt_file_is_ignored()
    {
        var path = NewPath();
        File.WriteAllText(path, "{ kaputt");
        var store = new TabAliasStore(path);
        Assert.Equal(0, store.Count);
        Assert.True(store.Set("chrome", "A", "B"));
        Assert.Equal("B", new TabAliasStore(path).Find("chrome", "A"));
    }

    [Fact]
    public void Changed_fires_on_set_and_reset()
    {
        var store = new TabAliasStore(NewPath());
        var n = 0;
        store.Changed += () => n++;
        store.Set("chrome", "A", "B");
        store.Reset("chrome", "A");
        Assert.Equal(2, n);
    }
}

public class TabRowInputTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 5, true)]    // ein Browserfenster mit Tabs: aufklappbar
    [InlineData(2, 0, true)]
    public void CanExpand_counts_windows_and_tabs(int windows, int tabs, bool expected)
        => Assert.Equal(expected, AppRowInput.CanExpand(windows, tabs));

    [Fact]
    public void Single_browser_window_toggles_tabs_on_the_badge_and_activates_elsewhere()
    {
        Assert.Equal(AppRowInput.RowAction.ToggleWindows, AppRowInput.OnAppRowClick(1, onToggleChrome: true, tabCount: 4));
        Assert.Equal(AppRowInput.RowAction.ActivateApp, AppRowInput.OnAppRowClick(1, onToggleChrome: false, tabCount: 4));
        Assert.Equal(AppRowInput.RowAction.ActivateApp, AppRowInput.OnAppRowClick(1, onToggleChrome: true, tabCount: 1));
    }

    [Fact]
    public void Window_row_toggles_tabs_only_on_its_chevron()
    {
        Assert.Equal(AppRowInput.WindowRowAction.ToggleTabs, AppRowInput.OnWindowRowClick(3, onToggleChrome: true));
        Assert.Equal(AppRowInput.WindowRowAction.ActivateWindow, AppRowInput.OnWindowRowClick(3, onToggleChrome: false));
        Assert.Equal(AppRowInput.WindowRowAction.ActivateWindow, AppRowInput.OnWindowRowClick(1, onToggleChrome: true));
    }

    [Theory]
    [InlineData("TabBadge")]
    [InlineData("WinChevron")]
    [InlineData("CountBadge")]
    [InlineData("AppChevron")]
    public void Toggle_chrome_names(string name) => Assert.True(AppRowInput.IsToggleChromeName(name));

    [Fact]
    public void Expanded_state_survives_only_while_expandable()
    {
        Assert.True(AppRowInput.ShouldKeepExpanded(1, true, 3));
        Assert.False(AppRowInput.ShouldKeepExpanded(1, true, 1));
    }

    [Fact]
    public void Name_click_on_other_cards_switches_without_delay()
    {
        Assert.True(DesktopCardInput.NameClickSwitchesImmediately(cardIsActive: false));
        Assert.False(DesktopCardInput.NameClickSwitchesImmediately(cardIsActive: true));
    }
}

public class ActivationTimingTests
{
    [Fact]
    public async Task Desktop_switch_is_confirmed_without_polling_when_com_is_immediate()
    {
        var vd = new FakeVirtualDesktops("Eins", "Zwei");
        vd.Current = vd.Desktops[0].Id;
        var t = await WindowActivation.SwitchDesktopAsync(vd, vd.Desktops[1].Id, new FakeClock());
        Assert.True(t.Switched);
        Assert.True(t.Focused);
        Assert.Equal(0, t.Polls);
        Assert.Equal(TimeSpan.Zero, t.Confirm);
    }

    [Fact]
    public async Task Slow_switch_polls_in_short_steps()
    {
        var vd = new FakeVirtualDesktops("Eins", "Zwei") { SwitchSettleAfterGets = 4 };
        vd.Current = vd.Desktops[0].Id;
        var t = await WindowActivation.SwitchDesktopAsync(vd, vd.Desktops[1].Id, new FakeClock());
        Assert.True(t.Focused);
        Assert.InRange(t.Polls, 1, 4);
        Assert.True(t.Confirm <= TimeSpan.FromMilliseconds(5 * 4));
        Assert.Contains("Desktop:", t.Describe());
        Assert.Contains("Bestätigung", t.Describe());
    }

    [Fact]
    public async Task Same_desktop_needs_no_switch()
    {
        var vd = new FakeVirtualDesktops("Eins", "Zwei");
        vd.Current = vd.Desktops[0].Id;
        var t = await WindowActivation.SwitchDesktopAsync(vd, vd.Desktops[0].Id, new FakeClock());
        Assert.False(t.Switched);
        Assert.True(t.Focused);
        Assert.Contains("kein Wechsel nötig", t.Describe());
    }

    [Fact]
    public async Task Window_activation_reports_focus()
    {
        var vd = new FakeVirtualDesktops("Eins", "Zwei");
        vd.Current = vd.Desktops[0].Id;
        var win = new FakeWindows();
        var t = await WindowActivation.ActivateMeasuredAsync(vd, win, 7, vd.Desktops[1].Id, new FakeClock());
        Assert.True(t.Focused);
        Assert.Equal(new long[] { 7 }, win.Focused);
        Assert.StartsWith("Fenster:", t.Describe());
        Assert.Contains("Fokus", t.Describe());
    }
}
