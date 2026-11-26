using DeskRecall.Core.Apps;
using DeskRecall.Core.Compatibility;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Profiles;
using DeskRecall.Core.Recording;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Settings;
using Xunit;

namespace DeskRecall.Core.Tests;

public class CompatibilityTests
{
    [Theory]
    [InlineData(19045, 0, VirtualDesktopApiSet.PublicOnly)]
    [InlineData(22000, 2538, VirtualDesktopApiSet.Win11Legacy)]
    [InlineData(22621, 1702, VirtualDesktopApiSet.Win11Legacy)]
    [InlineData(22621, 2215, VirtualDesktopApiSet.Win11_22H2)]
    [InlineData(22631, 2861, VirtualDesktopApiSet.Win11_22H2)]
    [InlineData(22631, 3085, VirtualDesktopApiSet.Win11_23H2)]
    [InlineData(22631, 4460, VirtualDesktopApiSet.Win11_23H2)]
    [InlineData(26100, 1742, VirtualDesktopApiSet.Win11_24H2)]
    [InlineData(26200, 6584, VirtualDesktopApiSet.Win11_24H2)]
    public void Selects_interface_set_by_build_and_ubr(int build, int ubr, VirtualDesktopApiSet expected)
        => Assert.Equal(expected, VirtualDesktopApiResolver.Resolve(new WindowsBuild(10, 0, build, ubr)));

    [Fact]
    public void Override_wins()
        => Assert.Equal(VirtualDesktopApiSet.PublicOnly, VirtualDesktopApiResolver.Resolve(new WindowsBuild(10, 0, 26100, 1), VirtualDesktopApiOverride.PublicApiOnly));

    [Fact]
    public void Fallbacks_never_mix_sets_with_identical_iid_but_different_layout()
    {
        Assert.DoesNotContain(VirtualDesktopApiSet.Win11_23H2, VirtualDesktopApiResolver.FallbackOrder(VirtualDesktopApiSet.Win11_24H2));
        Assert.DoesNotContain(VirtualDesktopApiSet.Win11_24H2, VirtualDesktopApiResolver.FallbackOrder(VirtualDesktopApiSet.Win11_23H2));
    }
}

public class CommandLineTests
{
    [Fact]
    public void Splits_like_windows()
    {
        var a = CommandLine.Split("\"C:\\Program Files\\App\\app.exe\" --flag \"D:\\Meine Projekte\\x.sln\" plain");
        Assert.Equal(new[] { @"C:\Program Files\App\app.exe", "--flag", @"D:\Meine Projekte\x.sln", "plain" }, a);
        Assert.Equal(new[] { "a\"b", "c" }, CommandLine.Split("a\\\"b c"));
        Assert.Empty(CommandLine.Split("   "));
    }

    [Fact]
    public void Quote_roundtrips()
    {
        foreach (var s in new[] { @"C:\Pfad mit Leer\x.txt", "simple", @"ends\with\", "has\"quote" })
            Assert.Equal(s, CommandLine.Split(CommandLine.Quote(s)).Single());
    }

    [Fact]
    public void Extracts_only_existing_document_paths()
    {
        var cmd = "\"C:\\VS\\devenv.exe\" /rootsuffix Exp \"D:\\Code\\GeringDigital.sln\" --token=abc123";
        Assert.Equal(@"D:\Code\GeringDigital.sln", DocumentPathExtractor.Extract(cmd, "devenv", _ => true));
        Assert.Null(DocumentPathExtractor.Extract(cmd, "devenv", _ => false));
        Assert.Null(DocumentPathExtractor.Extract("firefox.exe \"C:\\x\\page.html\"", "firefox", _ => true)); // Browser nie
        Assert.Null(DocumentPathExtractor.Extract("app.exe https://example.com/?session=geheim", "app", _ => true));
        Assert.Null(DocumentPathExtractor.Extract("app.exe C:\\Tools\\helper.dll", "app", _ => true));
    }
}

public class AppLocatorTests
{
    [Fact]
    public void Squirrel_apps_start_via_update_exe()
    {
        var files = new HashSet<string> { @"C:\U\AppData\Local\Discord\Update.exe" };
        var loc = new AppLocator(files.Contains, _ => Array.Empty<string>());
        var r = loc.Resolve(T.W("Discord", path: @"C:\U\AppData\Local\Discord\app-1.0.9175\Discord.exe"))!;
        Assert.Equal(@"C:\U\AppData\Local\Discord\Update.exe", r.FileName);
        Assert.Equal("--processStart Discord.exe", r.Arguments);
    }

    [Fact]
    public void Finds_newest_version_folder_after_update()
    {
        var files = new HashSet<string> { @"C:\Apps\Tool\2.1.0\tool.exe", @"C:\Apps\Tool\2.3.1\tool.exe" };
        var loc = new AppLocator(files.Contains, d => d == @"C:\Apps\Tool" ? new[] { @"C:\Apps\Tool\2.1.0", @"C:\Apps\Tool\2.3.1", @"C:\Apps\Tool\logs" } : Array.Empty<string>());
        var r = loc.Resolve(T.W("tool", path: @"C:\Apps\Tool\2.0.0\tool.exe"))!;
        Assert.Equal(@"C:\Apps\Tool\2.3.1\tool.exe", r.FileName);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void Falls_back_to_app_paths_lookup_and_null_if_unknown()
    {
        var loc = new AppLocator(p => p == @"D:\Neu\resolve.exe", _ => Array.Empty<string>(), n => n.Equals("Resolve.exe", StringComparison.OrdinalIgnoreCase) ? @"D:\Neu\resolve.exe" : null);
        Assert.Equal(@"D:\Neu\resolve.exe", loc.Resolve(T.W("Resolve", path: @"C:\Alt\Resolve.exe"))!.FileName);
        Assert.Null(loc.Resolve(T.W("weg", path: @"C:\Alt\weg.exe")));
    }

    [Fact]
    public void Classifies_known_apps()
    {
        Assert.Equal(AppKind.Browser, AppCatalog.Classify("vivaldi", "Chrome_WidgetWin_1", null));
        Assert.Equal(AppKind.Explorer, AppCatalog.Classify("explorer", "CabinetWClass", null));
        Assert.Equal(AppKind.SingleInstance, AppCatalog.Classify("steam", "SDL_app", null));
        Assert.Equal(AppKind.Normal, AppCatalog.Classify("discordbot", "x", null));
        foreach (var b in new[] { "firefox", "chrome", "msedge", "brave", "vivaldi", "opera" }) Assert.True(AppCatalog.IsBrowser(b + ".exe"));
    }
}

public class ProfileManagerTests
{
    private static ProfileManager Create() => new(T.TempDir(), new MemoryLogService());
    private static SessionSnapshot Snap(params string[] procs) { var s = new SessionSnapshot(); foreach (var p in procs) s.Windows.Add(T.W(p)); return s; }

    [Fact]
    public void Full_lifecycle_create_update_rename_duplicate_delete()
    {
        var pm = Create();
        pm.Create("Arbeit", Snap("firefox", "devenv"));
        pm.Create("Gaming", Snap("steam"));
        Assert.Equal(new[] { "Arbeit", "Gaming" }, pm.List().Select(p => p.Name));
        pm.Update("Arbeit", Snap("firefox", "devenv", "discord"));
        Assert.Equal(3, pm.Get("arbeit")!.Snapshot!.Windows.Count);
        pm.Rename("Arbeit", "Programmierung");
        Assert.Null(pm.Get("Arbeit"));
        Assert.NotNull(pm.Get("Programmierung"));
        var copy = pm.Duplicate("Gaming");
        Assert.Equal("Gaming (Kopie)", copy.Name);
        Assert.True(pm.Delete("Gaming"));
        Assert.Equal(new[] { "Gaming (Kopie)", "Programmierung" }, pm.List().Select(p => p.Name));
    }

    [Fact]
    public void Rejects_duplicates_reserved_and_empty_names()
    {
        var pm = Create();
        pm.Create("Schule", Snap("a"));
        Assert.Throws<InvalidOperationException>(() => pm.Create("schule", Snap("b")));
        Assert.Throws<ArgumentException>(() => pm.Create("Standard", Snap("b")));
        Assert.Throws<ArgumentException>(() => pm.Create("  ", Snap("b")));
    }

    [Fact]
    public void Export_and_import_with_unique_name()
    {
        var pm = Create();
        pm.Create("Videobearbeitung", Snap("Resolve", "Photoshop"));
        var file = Path.Combine(T.TempDir(), "export.json");
        pm.Export("Videobearbeitung", file);
        var imported = pm.Import(file);
        Assert.Equal("Videobearbeitung 2", imported.Name);
        Assert.Equal(2, imported.Snapshot!.Windows.Count);
        Assert.Throws<InvalidDataException>(() => { var bad = Path.Combine(T.TempDir(), "x.json"); File.WriteAllText(bad, "{}"); pm.Import(bad); });
    }

    [Fact]
    public void Names_with_special_characters_are_stored_safely()
    {
        var pm = Create();
        pm.Create("A/B: C*", Snap("x"));
        pm.Create("A_B_ C_", Snap("y"));
        Assert.Equal(2, pm.List().Count);
        Assert.Equal("x", pm.Get("A/B: C*")!.Snapshot!.Windows[0].ProcessName);
    }
}

public class RecordingTests
{
    [Fact]
    public void Shutdown_snapshot_with_far_fewer_windows_is_rejected()
    {
        var prev = new SessionSnapshot(); for (var i = 0; i < 10; i++) prev.Windows.Add(T.W("a" + i));
        var partial = new SessionSnapshot(); for (var i = 0; i < 3; i++) partial.Windows.Add(T.W("a" + i));
        var almost = new SessionSnapshot(); for (var i = 0; i < 8; i++) almost.Windows.Add(T.W("a" + i));
        Assert.False(ShutdownSnapshotPolicy.ShouldAccept(prev, partial));
        Assert.True(ShutdownSnapshotPolicy.ShouldAccept(prev, almost));
        Assert.True(ShutdownSnapshotPolicy.ShouldAccept(null, partial));
    }

    [Fact]
    public void Startup_guard_blocks_until_released_or_grace_elapsed()
    {
        var t0 = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(1));
        var g = new StartupGuard(t0, TimeSpan.FromMinutes(3), active: true);
        Assert.False(g.IsReleased(t0.AddMinutes(1)));
        Assert.True(g.IsReleased(t0.AddMinutes(4)));
        g.Release();
        Assert.True(g.IsReleased(t0));
        Assert.True(new StartupGuard(t0, TimeSpan.FromMinutes(3), active: false).IsReleased(t0));
    }

    [Fact]
    public void Debouncer_coalesces_bursts()
    {
        var clock = new FakeClock();
        var count = 0;
        using var d = new ChangeDebouncer(TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(5), () => count++, clock);
        for (var i = 0; i < 20; i++)
        {
            d.Signal();
            clock.Advance(TimeSpan.FromMilliseconds(5));
        }
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, count);
    }

    [Fact]
    public void Debouncer_fires_at_max_delay_under_constant_signals()
    {
        var clock = new FakeClock();
        var count = 0;
        using var d = new ChangeDebouncer(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), () => count++, clock);
        for (var t = 0; t < 600; t += 20)
        {
            d.Signal();
            clock.Advance(TimeSpan.FromMilliseconds(20));
        }
        Assert.True(count >= 1);
        Assert.Equal(2, count); // nach 250 ms und erneut nach 500 ms
    }

    [Fact]
    public void Recorder_applies_exclusions_rules_and_privacy_settings()
    {
        var vd = new FakeVirtualDesktops("Eins", "Zwei");
        var win = new FakeWindows();
        var a = T.W("code", "Geheim.txt – VS Code", handle: 1); a.DesktopId = vd.Desktops[1].Id; a.DocumentPath = @"C:\Geheim.txt";
        win.Add(a);
        win.Add(T.W("TextInputHost", "x", handle: 2));
        win.Add(T.W("KeePassXC", "Datenbank", handle: 3));
        var settings = AppSettings.CreateDefault();
        settings.Backup.StoreWindowTitles = false;
        settings.Backup.CaptureDocumentPaths = false;
        var rules = new RuleEngine(new[] { new AppRule { ProcessName = "KeePassXC", Exclude = true } });
        var rec = new SessionRecorder(win, vd, new FakeMonitors(), rules, () => settings, new MemoryLogService());
        var snap = rec.Capture(SessionKind.Manual);
        var only = Assert.Single(snap.Windows);
        Assert.Equal("code", only.ProcessName);
        Assert.Equal(1, only.DesktopIndex);
        Assert.Equal("", only.Title);
        Assert.Null(only.DocumentPath);
        Assert.Equal(2, snap.Desktops.Count);
    }
}
