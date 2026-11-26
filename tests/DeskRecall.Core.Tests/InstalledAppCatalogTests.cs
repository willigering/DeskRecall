using DeskRecall.Core.Apps;
using DeskRecall.Core.Geometry;
using DeskRecall.Core.Models;
using DeskRecall.Core.Rules;
using Xunit;

namespace DeskRecall.Core.Tests;

public class InstalledAppCatalogTests
{
    [Theory]
    [InlineData("Visual Studio Code.lnk", "Visual Studio Code")]
    [InlineData(@"C:\Users\Willi\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Chrome - Verknüpfung.lnk", "Chrome")]
    [InlineData("Notepad - Shortcut.lnk", "Notepad")]
    public void Display_name_strips_extension_and_shortcut_suffix(string path, string expected)
        => Assert.Equal(expected, InstalledAppCatalog.DisplayNameFromShortcut(path));

    [Theory]
    [InlineData("Uninstall Slack.lnk", true)]
    [InlineData("Slack deinstallieren.lnk", true)]
    [InlineData("Hilfe.lnk", true)]
    [InlineData("Release Notes.lnk", true)]
    [InlineData("Datenschutz.lnk", true)]
    [InlineData("Visual Studio Code.lnk", false)]
    [InlineData("Discord.lnk", false)]
    public void Ignores_unhelpful_start_menu_entries(string name, bool ignored)
        => Assert.Equal(ignored, InstalledAppCatalog.IsIgnoredShortcutName(name));

    [Theory]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsStore_8wekyb3d8bbwe!App", null, "Microsoft.WindowsStore_8wekyb3d8bbwe!App")]
    [InlineData(@"explorer.exe", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"C:\Program Files\App\app.exe", null, null)]
    public void Parses_aumid_from_appsfolder_targets(string? target, string? args, string? expected)
        => Assert.Equal(expected, InstalledAppCatalog.ParseAumidFromTarget(target, args));

    [Fact]
    public void FromShortcuts_filters_deduplicates_and_prefers_exe_targets()
    {
        var entries = InstalledAppCatalog.FromShortcuts(new[]
        {
            new ShortcutRecord(@"C:\Start\Programs\Uninstall Foo.lnk", "Uninstall Foo", @"C:\Foo\uninstall.exe", null, null, null, null),
            new ShortcutRecord(@"C:\Start\Programs\Code.lnk", "Visual Studio Code", @"C:\P\Microsoft VS Code\Code.exe", null, @"C:\P\Microsoft VS Code", null, null),
            new ShortcutRecord(@"C:\Start\Programs\VS Code.lnk", "VS Code", @"C:\P\Microsoft VS Code\Code.exe", null, null, null, null),
            new ShortcutRecord(@"C:\Start\Programs\Notes.url", "Website", @"https://example.com", null, null, null, null),
            new ShortcutRecord(@"C:\Start\Programs\Rechner.lnk", "Rechner", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null, null, null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        });
        Assert.Equal(2, entries.Count);
        var code = entries.Single(e => e.ProcessName == "code");
        Assert.Equal("Visual Studio Code", code.Name); // erster Anzeigename bleibt
        Assert.Equal(@"C:\P\Microsoft VS Code\Code.exe", code.TargetPath);
        Assert.False(code.IsUwp);
        var calc = entries.Single(e => e.IsUwp);
        Assert.Equal("Rechner", calc.Name);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", calc.AppUserModelId);
    }

    [Fact]
    public void Search_requires_every_token()
    {
        var all = new[]
        {
            new InstalledAppEntry("Visual Studio Code", @"C:\P\Code.exe", null, null, null, null, null, false),
            new InstalledAppEntry("Visual Studio", @"C:\P\devenv.exe", null, null, null, null, null, false),
            new InstalledAppEntry("Discord", @"C:\U\Discord\Discord.exe", null, null, null, null, null, false),
        };
        Assert.Equal(2, InstalledAppCatalog.Search(all, "visual").Count);
        Assert.Single(InstalledAppCatalog.Search(all, "visual code"));
        Assert.Equal("Visual Studio Code", InstalledAppCatalog.Search(all, "visual code").Single().Name);
        Assert.Empty(InstalledAppCatalog.Search(all, "visual photoshop"));
        Assert.Equal(3, InstalledAppCatalog.Search(all, "  ").Count);
    }

    [Fact]
    public void Merge_unifies_start_menu_and_uwp_lists_by_aumid()
    {
        var start = InstalledAppCatalog.FromShortcuts(new[]
        {
            new ShortcutRecord(@"C:\Start\Calc.lnk", "Rechner", null, null, null, null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        });
        var uwp = InstalledAppCatalog.FromUwpPackages(new[]
        {
            ("Windows Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", @"C:\Windows\Calc.png"),
            ("Einstellungen", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", null),
        });
        var merged = InstalledAppCatalog.Merge(start, uwp);
        Assert.Equal(2, merged.Count);
        var calc = merged.Single(e => e.Name is "Rechner" or "Windows Calculator");
        Assert.Equal("Rechner", calc.Name);
        Assert.Equal(@"C:\Windows\Calc.png", calc.IconPath);
    }

    [Fact]
    public void FromDroppedPath_accepts_exe_and_resolved_lnk_and_rejects_uninstallers()
    {
        var exe = InstalledAppCatalog.FromDroppedPath(@"C:\Tools\Ripgrep\rg.exe");
        Assert.NotNull(exe);
        Assert.Equal("rg", exe!.Name);
        Assert.Equal("rg", exe.ProcessName);
        Assert.Equal(@"C:\Tools\Ripgrep\rg.exe", exe.TargetPath);
        Assert.Equal(@"C:\Tools\Ripgrep", exe.WorkingDirectory);

        Assert.Null(InstalledAppCatalog.FromDroppedPath(@"C:\Foo\uninstall.exe"));
        Assert.Null(InstalledAppCatalog.FromDroppedPath(@"C:\Foo\notes.txt"));

        var rec = new ShortcutRecord(@"D:\Desktop\Spotify.lnk", "Spotify", @"C:\P\Spotify\Spotify.exe", null, @"C:\P\Spotify", null, null);
        var dropped = InstalledAppCatalog.FromDroppedPath(@"D:\Desktop\Spotify.lnk", _ => rec);
        Assert.NotNull(dropped);
        Assert.Equal("Spotify", dropped!.Name);
        Assert.Equal(@"C:\P\Spotify\Spotify.exe", dropped.TargetPath);
    }

    [Fact]
    public void EnumerateShortcutFiles_walks_start_menu_trees_and_skips_duplicates()
    {
        var files = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs"] = new[]
            {
                @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Chrome.lnk",
                @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Uninstall Chrome.lnk",
                @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Readme.txt",
            },
            [@"C:\Users\Willi\AppData\Roaming\Microsoft\Windows\Start Menu\Programs"] = new[]
            {
                @"C:\Users\Willi\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Chrome.lnk",
                @"c:\programdata\microsoft\windows\start menu\programs\Chrome.lnk", // gleiches Ziel, andere Großschreibung
                @"C:\Users\Willi\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Tools\WinSCP.lnk",
            },
        };
        var found = InstalledAppCatalog.EnumerateShortcutFiles(files.Keys, root => files[root]);
        Assert.Equal(3, found.Count); // zwei Chrome-Verknüpfungen an unterschiedlichen Orten, WinSCP, eine Dublette entfernt
        Assert.Equal(2, found.Count(f => f.EndsWith("Chrome.lnk", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(found, f => f.EndsWith("WinSCP.lnk", StringComparison.OrdinalIgnoreCase));
    }
}

public class PinnedAppsTests
{
    static InstalledAppEntry Code() => new("Visual Studio Code", @"C:\P\Microsoft VS Code\Code.exe", null, null, @"C:\P\Microsoft VS Code", null, null, false);
    static InstalledAppEntry Discord() => new("Discord", @"C:\U\Discord\Discord.exe", null, null, null, null, null, false);
    static InstalledAppEntry Steam() => new("Steam", @"C:\P\Steam\steam.exe", null, null, null, null, null, false);

    [Fact]
    public void Create_rule_pins_to_desktop_and_is_applied_live()
    {
        var rule = PinnedApps.Create(Code(), 3);
        Assert.True(rule.IsPinnedEntry);
        Assert.True(rule.ApplyLive);
        Assert.True(rule.Enabled);
        Assert.Equal(3, rule.TargetDesktop);
        Assert.Equal("code", rule.ProcessName);
        Assert.Equal(@"C:\P\Microsoft VS Code\Code.exe", rule.ExecutablePath);
        Assert.Equal("Visual Studio Code", rule.Name);
    }

    [Fact]
    public void AddOrMove_updates_existing_pin_instead_of_duplicating()
    {
        var rules = new List<AppRule> { PinnedApps.Create(Code(), 1) };
        var (next, rule, created) = PinnedApps.AddOrMove(rules, Code(), 4);
        Assert.False(created);
        Assert.Single(next);
        Assert.Equal(4, rule.TargetDesktop);
        Assert.Equal(rules[0].Id, rule.Id);
    }

    [Fact]
    public void MissingLaunchRecords_skips_apps_already_in_the_snapshot_or_running()
    {
        var steam = PinnedApps.Create(Steam(), 3);
        steam.NeverAutoStart = true;
        var rules = new List<AppRule>
        {
            PinnedApps.Create(Code(), 2),
            PinnedApps.Create(Discord(), 1),
            steam,
        };
        var snapshot = new[]
        {
            new DeskRecall.Core.Models.WindowRecord { ProcessName = "Code", ExecutablePath = @"C:\P\Microsoft VS Code\Code.exe" },
        };
        (string ProcessName, string? Path)[] running = { ("Discord", @"C:\U\Discord\Discord.exe") };
        var missing = PinnedApps.MissingLaunchRecords(rules, snapshot, running);
        Assert.Empty(missing); // Code in Sitzung, Discord läuft, Steam darf nicht automatisch starten
    }

    [Fact]
    public void MissingLaunchRecords_emits_a_window_for_an_absent_pin()
    {
        var rules = new[] { PinnedApps.Create(Code(), 2) };
        var missing = PinnedApps.MissingLaunchRecords(rules, Array.Empty<DeskRecall.Core.Models.WindowRecord>());
        var rec = Assert.Single(missing);
        Assert.Equal("code", rec.ProcessName);
        Assert.Equal(1, rec.DesktopIndex);
        Assert.Equal(@"C:\P\Microsoft VS Code\Code.exe", rec.ExecutablePath);
        Assert.Equal("Visual Studio Code", rec.DisplayName);
    }

    [Fact]
    public void Remove_drops_only_the_named_rule()
    {
        var a = PinnedApps.Create(Code(), 1);
        var b = PinnedApps.Create(Discord(), 2);
        var next = PinnedApps.Remove(new[] { a, b }, a.Id);
        Assert.Equal(b.Id, Assert.Single(next).Id);
    }
}

public class DesktopCardInputTests
{
    [Fact]
    public void Creating_a_desktop_does_not_enter_rename_mode()
        => Assert.False(DesktopCardInput.EnterRenameAfterCreate);

    [Fact]
    public void Double_click_on_the_name_only_starts_rename()
        => Assert.Equal(DesktopCardInput.HeaderAction.BeginRename,
            DesktopCardInput.OnNameClick(isEditing: false, isDoubleClick: true, clickOnRenameBox: false, clickOnButton: false));

    [Fact]
    public void Single_click_on_the_name_is_a_delayed_switch()
        => Assert.Equal(DesktopCardInput.HeaderAction.Switch,
            DesktopCardInput.OnNameClick(isEditing: false, isDoubleClick: false, clickOnRenameBox: false, clickOnButton: false));

    [Fact]
    public void Click_on_the_name_while_renaming_commits_but_does_not_switch()
        => Assert.Equal(DesktopCardInput.HeaderAction.CommitRename,
            DesktopCardInput.OnNameClick(isEditing: true, isDoubleClick: false, clickOnRenameBox: false, clickOnButton: false));

    [Fact]
    public void Chrome_click_switches_unless_renaming()
    {
        Assert.Equal(DesktopCardInput.HeaderAction.Switch,
            DesktopCardInput.OnHeaderChromeClick(isEditing: false, clickOnRenameBox: false, clickOnButton: false));
        Assert.Equal(DesktopCardInput.HeaderAction.CommitRename,
            DesktopCardInput.OnHeaderChromeClick(isEditing: true, clickOnRenameBox: false, clickOnButton: false));
        Assert.Equal(DesktopCardInput.HeaderAction.None,
            DesktopCardInput.OnHeaderChromeClick(isEditing: false, clickOnRenameBox: false, clickOnButton: true));
        Assert.Equal(DesktopCardInput.HeaderAction.None,
            DesktopCardInput.OnHeaderChromeClick(isEditing: true, clickOnRenameBox: true, clickOnButton: false));
    }

    [Fact]
    public void Click_outside_the_rename_box_commits()
    {
        Assert.True(DesktopCardInput.ShouldCommitOnOutsideClick(true, false));
        Assert.False(DesktopCardInput.ShouldCommitOnOutsideClick(true, true));
        Assert.False(DesktopCardInput.ShouldCommitOnOutsideClick(false, false));
    }
}

public class AppRowInputTests
{
    [Fact]
    public void Single_window_row_click_activates_the_app()
        => Assert.Equal(AppRowInput.RowAction.ActivateApp, AppRowInput.OnAppRowClick(1));

    [Fact]
    public void Multi_window_row_click_activates_the_most_recent_window()
        => Assert.Equal(AppRowInput.RowAction.ActivateApp, AppRowInput.OnAppRowClick(3));

    [Fact]
    public void Chevron_or_badge_click_toggles_the_list()
    {
        Assert.Equal(AppRowInput.RowAction.ToggleWindows, AppRowInput.OnAppRowClick(3, onToggleChrome: true));
        Assert.True(AppRowInput.IsToggleChromeName("CountBadge"));
        Assert.True(AppRowInput.IsToggleChromeName("AppChevron"));
        Assert.False(AppRowInput.IsToggleChromeName("WinGlyph"));
        Assert.False(AppRowInput.IsToggleChromeName("Row"));
    }

    [Fact]
    public void Badge_click_on_a_single_window_row_still_activates()
        => Assert.Equal(AppRowInput.RowAction.ActivateApp, AppRowInput.OnAppRowClick(1, onToggleChrome: true));

    [Fact]
    public void Pinned_unused_row_click_activates_to_launch()
        => Assert.Equal(AppRowInput.RowAction.ActivateApp, AppRowInput.OnAppRowClick(0));

    [Fact]
    public void Sub_row_click_activates_that_window()
        => Assert.Equal(AppRowInput.RowAction.ActivateWindow, AppRowInput.OnAppRowClick(3, onSubRow: true));

    [Fact]
    public void Expanded_state_is_kept_only_while_multiple_windows_remain()
    {
        Assert.True(AppRowInput.ShouldKeepExpanded(3, true));
        Assert.False(AppRowInput.ShouldKeepExpanded(1, true));
        Assert.False(AppRowInput.ShouldKeepExpanded(4, false));
    }
}

public class ArrangementPreviewTests
{
    static MonitorInfo Mon(int l, int t, int w, int h, bool primary = false) => new()
    {
        Id = $"{l},{t}",
        Bounds = RectI.FromSize(l, t, w, h),
        WorkArea = RectI.FromSize(l, t, w, h),
        IsPrimary = primary,
    };

    static WindowRecord Win(string name, int l, int t, int w, int h, int z = 0, WindowShowState state = WindowShowState.Normal) => new()
    {
        ProcessName = name,
        DisplayName = name,
        NormalBounds = RectI.FromSize(l, t, w, h),
        Handle = name.GetHashCode(),
        ZOrder = z,
        State = state,
    };

    [Fact]
    public void Scales_windows_into_the_preview_and_keeps_relative_positions()
    {
        var layout = ArrangementPreview.Layout(
            new[] { Win("A", 0, 0, 960, 1080, 1), Win("B", 960, 0, 960, 1080, 2) },
            new[] { Mon(0, 0, 1920, 1080, true) },
            previewWidth: 200, previewHeight: 80, padding: 0);

        Assert.Single(layout.Monitors);
        Assert.Equal(2, layout.Windows.Count);
        var a = layout.Windows.Single(w => w.AppName == "A");
        var b = layout.Windows.Single(w => w.AppName == "B");
        Assert.True(a.Bounds.Left < b.Bounds.Left);
        Assert.True(a.Bounds.Right <= b.Bounds.Left + 2); // benachbart
        Assert.True(a.Bounds.Width > 8 && b.Bounds.Width > 8);
        Assert.Equal(b, ArrangementPreview.HitTest(layout, b.Bounds.Left + 1, b.Bounds.Top + 1));
        Assert.Equal(a, ArrangementPreview.HitTest(layout, a.Bounds.Left + 1, a.Bounds.Top + 1));
    }

    [Fact]
    public void Hit_test_prefers_the_topmost_window()
    {
        var layout = ArrangementPreview.Layout(
            new[] { Win("Hinten", 0, 0, 200, 200, z: 1), Win("Vorne", 50, 50, 50, 50, z: 5) },
            new[] { Mon(0, 0, 200, 200, true) },
            100, 100, padding: 0);
        var front = layout.Windows.Single(w => w.AppName == "Vorne");
        Assert.Equal(front, ArrangementPreview.HitTest(layout, front.Bounds.Left + 1, front.Bounds.Top + 1));
    }

    [Fact]
    public void Empty_input_yields_an_empty_layout()
    {
        var layout = ArrangementPreview.Layout(Array.Empty<WindowRecord>(), Array.Empty<MonitorInfo>(), 100, 60);
        Assert.Empty(layout.Windows);
        Assert.Empty(layout.Monitors);
    }
}
