using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Restore;
using Xunit;

namespace DeskRecall.Core.Tests;

public class DesktopPlannerTests
{
    [Fact]
    public void Maps_by_guid_first_then_by_order_and_creates_missing()
    {
        var vd = new FakeVirtualDesktops("Kreativ", "Arbeit");
        var saved = new List<DesktopInfo>
        {
            new() { Id = vd.Desktops[0].Id, Index = 0, Name = "Kreativ" },
            new() { Id = Guid.NewGuid(), Index = 1, Name = "Arbeit" },
            new() { Id = Guid.NewGuid(), Index = 2, Name = "Gaming" },
        };
        var map = DesktopPlanner.Apply(vd, saved, createMissing: true, restoreNamesAndOrder: true, dryRun: false, new MemoryLogService());
        Assert.Equal(3, vd.Desktops.Count);
        Assert.Equal(1, map.Created);
        Assert.Equal(vd.Desktops[0].Id, map.SavedIndexToCurrent[0]);
        Assert.Equal(vd.Desktops[1].Id, map.SavedIndexToCurrent[1]);
        Assert.Equal("Gaming", vd.Desktops[2].Name);
    }

    [Fact]
    public void Restores_order_and_names()
    {
        var vd = new FakeVirtualDesktops("B", "A");
        var saved = new List<DesktopInfo>
        {
            new() { Id = vd.Desktops[1].Id, Index = 0, Name = "Arbeit" },
            new() { Id = vd.Desktops[0].Id, Index = 1, Name = "Gaming" },
        };
        var map = DesktopPlanner.Apply(vd, saved, true, true, false, new MemoryLogService());
        Assert.Equal(new[] { "Arbeit", "Gaming" }, vd.Desktops.Select(d => d.Name));
        Assert.Equal(1, map.Moved);
        Assert.Equal(2, map.Renamed);
    }

    [Fact]
    public void Does_not_create_when_disabled_or_in_dry_run()
    {
        var vd = new FakeVirtualDesktops("Eins");
        var saved = new List<DesktopInfo> { new() { Id = Guid.NewGuid(), Index = 0, Name = "x" }, new() { Id = Guid.NewGuid(), Index = 1, Name = "y" } };
        DesktopPlanner.Apply(vd, saved, false, true, false, new MemoryLogService());
        Assert.Single(vd.Desktops);
        DesktopPlanner.Apply(vd, saved, true, true, true, new MemoryLogService());
        Assert.Single(vd.Desktops);
    }
}

public class LaunchPlannerTests
{
    private static RestoreItem I(WindowRecord w, WindowShowState? state = null) => new() { Saved = w, TargetState = state ?? w.State };
    private static readonly RestoreOptions Opt = new();

    private static List<LaunchItem> Plan(IReadOnlyList<RestoreItem> items, IEnumerable<RunningProcess>? running = null, RestoreOptions? o = null, Func<string, bool>? dirExists = null, Func<string, bool>? fileExists = null)
        => LaunchPlanner.Plan(items, (running ?? Array.Empty<RunningProcess>()).ToList(), o ?? Opt,
            w => new ResolvedLaunch(w.ExecutablePath!, null, null), dirExists ?? (_ => true), fileExists ?? (_ => true));

    [Fact]
    public void Running_apps_are_never_started_again()
    {
        var items = new[] { I(T.W("steam", kind: AppKind.SingleInstance)), I(T.W("code")) };
        var plan = Plan(items, new[] { new RunningProcess(1, "steam", null), new RunningProcess(2, "Code", null) });
        Assert.Empty(plan);
        Assert.All(items, i => Assert.Equal(RestoreItemStatus.Pending, i.Status));
    }

    [Fact]
    public void Browser_with_multiple_windows_is_launched_once_without_arguments()
    {
        var items = new[] { I(T.W("firefox", "a", kind: AppKind.Browser)), I(T.W("firefox", "b", 1, kind: AppKind.Browser)), I(T.W("firefox", "c", 2, kind: AppKind.Browser)) };
        var plan = Plan(items);
        var launch = Assert.Single(plan);
        Assert.True(string.IsNullOrEmpty(launch.Request.Arguments));
        Assert.Equal(3, launch.Items.Count);
    }

    [Fact]
    public void Single_instance_apps_are_started_once_and_minimized_args_used()
    {
        var items = new[] { I(T.W("steam", kind: AppKind.SingleInstance), WindowShowState.Minimized), I(T.W("steam", "Freunde", kind: AppKind.SingleInstance), WindowShowState.Minimized) };
        var launch = Assert.Single(Plan(items));
        Assert.Equal("-silent", launch.Request.Arguments);
        Assert.True(launch.Request.StartMinimized);
    }

    [Fact]
    public void Multi_instance_apps_launch_once_per_document()
    {
        var a = T.W("Photoshop"); a.DocumentPath = @"D:\Bilder\a.psd";
        var b = T.W("Photoshop"); b.DocumentPath = @"D:\Bilder\b.psd";
        var c = T.W("Photoshop");
        var plan = Plan(new[] { I(a), I(b), I(c) });
        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, p => p.Request.Arguments!.Contains("a.psd"));
        Assert.Contains(plan, p => p.Request.Arguments!.Contains("b.psd"));
        Assert.Equal(3, plan.Sum(p => p.Items.Count));
    }

    [Fact]
    public void Missing_documents_are_not_passed()
    {
        var a = T.W("notepadx"); a.DocumentPath = @"D:\weg.txt";
        var launch = Assert.Single(Plan(new[] { I(a) }, fileExists: _ => false));
        Assert.True(string.IsNullOrEmpty(launch.Request.Arguments));
    }

    [Fact]
    public void Never_autostart_rule_skips_app()
    {
        var item = I(T.W("EpicGamesLauncher")); item.NeverAutoStart = true;
        Assert.Empty(Plan(new[] { item }));
        Assert.Equal(RestoreItemStatus.Skipped, item.Status);
    }

    [Fact]
    public void Elevated_apps_are_skipped_by_default_and_never_auto_elevated()
    {
        var w = T.W("HWiNFO64"); w.IsElevated = true;
        var item = I(w);
        Assert.Empty(Plan(new[] { item }));
        Assert.Equal(RestoreItemStatus.NeedsElevation, item.Status);

        var item2 = I(w.Clone());
        var plan = Plan(new[] { item2 }, o: new RestoreOptions { ElevationPolicy = ElevationPolicy.Ask });
        Assert.False(Assert.Single(plan).Request.AllowElevation); // erst nach Zustimmung im Restorer
    }

    [Fact]
    public void Uninstalled_apps_are_reported_not_found()
    {
        var item = I(T.W("Uninstalled"));
        var plan = LaunchPlanner.Plan(new[] { item }, Array.Empty<RunningProcess>(), Opt, _ => null, _ => true, _ => true);
        Assert.Empty(plan);
        Assert.Equal(RestoreItemStatus.NotFound, item.Status);
    }

    [Fact]
    public void Explorer_folders_open_individually_missing_folders_reported()
    {
        var a = I(T.W("explorer", "Downloads", kind: AppKind.Explorer, folder: @"C:\Downloads", cls: "CabinetWClass"));
        var b = I(T.W("explorer", "Alt", kind: AppKind.Explorer, folder: @"C:\Gibtsnicht", cls: "CabinetWClass"));
        var c = I(T.W("explorer", "Dieser PC", kind: AppKind.Explorer, folder: "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", cls: "CabinetWClass"));
        var plan = Plan(new[] { a, b, c }, new[] { new RunningProcess(1, "explorer", null) }, dirExists: d => d == @"C:\Downloads");
        Assert.Equal(2, plan.Count);
        Assert.All(plan, p => Assert.Equal("explorer.exe", p.Request.FileName));
        Assert.Contains(plan, p => p.Request.Arguments == "\"shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\"" || p.Request.Arguments == "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        Assert.Equal(RestoreItemStatus.NotFound, b.Status);
    }

    [Fact]
    public void Launching_can_be_disabled()
    {
        var item = I(T.W("code"));
        Assert.Empty(Plan(new[] { item }, o: new RestoreOptions { LaunchMissingApps = false }));
        Assert.Equal(RestoreItemStatus.Skipped, item.Status);
    }

    [Fact]
    public void Wake_starts_a_running_process_that_showed_no_window()
    {
        var running = new[] { new RunningProcess(1, "ms-teams", null) };
        var quiet = I(T.W("ms-teams", kind: AppKind.SingleInstance));
        Assert.Empty(Plan(new[] { quiet }, running));
        Assert.Equal(RestoreItemStatus.Pending, quiet.Status);

        var woken = I(T.W("ms-teams", kind: AppKind.SingleInstance));
        var plan = LaunchPlanner.Plan(new[] { woken }, running, Opt,
            w => new ResolvedLaunch(w.ExecutablePath!, null, null), _ => true, _ => true, wake: true);
        var launch = Assert.Single(plan);
        Assert.Contains("ms-teams", launch.Request.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Store_apps_launch_via_aumid()
    {
        var w = T.W("CalculatorApp", kind: AppKind.Uwp); w.AppUserModelId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        var launch = Assert.Single(Plan(new[] { I(w) }));
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", launch.Request.AppUserModelId);
    }
}
