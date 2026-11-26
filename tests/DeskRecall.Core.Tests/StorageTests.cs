using DeskRecall.Core.Geometry;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Storage;
using Xunit;

namespace DeskRecall.Core.Tests;

public class AtomicFileTests
{
    [Fact]
    public void Writes_new_file_and_replaces_existing_with_backup_without_leftover_temp_files()
    {
        var dir = T.TempDir();
        var path = Path.Combine(dir, "a.json");
        AtomicFile.WriteAllText(path, "eins");
        Assert.Equal("eins", File.ReadAllText(path));
        AtomicFile.WriteAllText(path, "zwei");
        Assert.Equal("zwei", File.ReadAllText(path));
        Assert.Equal("eins", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void ReadWithFallback_uses_backup_if_main_file_is_corrupt()
    {
        var dir = T.TempDir();
        var path = Path.Combine(dir, "s.json");
        AtomicFile.WriteAllText(path, Json.Serialize(new SessionSnapshot { Name = "gut" }));
        AtomicFile.WriteAllText(path, Json.Serialize(new SessionSnapshot { Name = "neu" }));
        File.WriteAllText(path, "{ kaputt"); // simulierter Absturz / Plattenfehler
        var s = AtomicFile.ReadWithFallback(path, Json.Deserialize<SessionSnapshot>);
        Assert.Equal("gut", s!.Name);
    }

    [Fact]
    public void CleanupTemp_keeps_fresh_files()
    {
        var dir = T.TempDir();
        var fresh = Path.Combine(dir, ".x.json.123.tmp");
        File.WriteAllText(fresh, "x");
        AtomicFile.CleanupTemp(dir);
        Assert.True(File.Exists(fresh));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddHours(-1));
        AtomicFile.CleanupTemp(dir);
        Assert.False(File.Exists(fresh));
    }
}

public class SessionSerializationTests
{
    [Fact]
    public void Snapshot_roundtrip_preserves_all_restore_relevant_fields()
    {
        var id = Guid.NewGuid();
        var s = new SessionSnapshot
        {
            Desktops = { new DesktopInfo { Id = id, Index = 0, Name = "Kreativ" } },
            ActiveDesktopId = id,
            Monitors = { T.Mon("M1", -1920, 0, 1920, 1080) },
            Windows =
            {
                T.W("Resolve", "DaVinci Resolve – Projekt", 0, RectI.FromSize(-1900, 20, 1800, 1000), state: WindowShowState.Maximized),
                T.W("explorer", "Downloads", 0, kind: AppKind.Explorer, folder: @"C:\Users\Willi\Downloads", cls: "CabinetWClass"),
            },
        };
        s.Windows[0].DesktopId = id;
        s.Windows[0].DocumentPath = @"D:\Projekte\Film.drp";
        var json = Json.Serialize(s);
        var back = Json.Deserialize<SessionSnapshot>(json)!;
        Assert.Contains("\"maximized\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RectI.FromSize(-1900, 20, 1800, 1000), back.Windows[0].NormalBounds);
        Assert.Equal(WindowShowState.Maximized, back.Windows[0].State);
        Assert.Equal(id, back.Windows[0].DesktopId);
        Assert.Equal(@"D:\Projekte\Film.drp", back.Windows[0].DocumentPath);
        Assert.Equal(AppKind.Explorer, back.Windows[1].Kind);
        Assert.Equal(@"C:\Users\Willi\Downloads", back.Windows[1].ExplorerFolderPath);
        Assert.Equal("Kreativ", back.Desktops[0].Name);
        Assert.Equal(-1920, back.Monitors[0].Bounds.Left);
    }

    [Fact]
    public void Hash_ignores_titles_handles_and_time_but_not_layout()
    {
        SessionSnapshot Make(string title, long handle, int x, int desk) => new()
        {
            CreatedAt = DateTimeOffset.Now.AddMinutes(handle),
            Windows = { T.W("firefox", title, desk, RectI.FromSize(x, 0, 800, 600), handle: handle) },
        };
        var a = SessionHasher.Compute(Make("Tab A", 1, 0, 0));
        Assert.Equal(a, SessionHasher.Compute(Make("Tab B", 2, 0, 0)));
        Assert.NotEqual(a, SessionHasher.Compute(Make("Tab A", 1, 50, 0)));
        Assert.NotEqual(a, SessionHasher.Compute(Make("Tab A", 1, 0, 1)));
    }

    [Fact]
    public void Hash_is_independent_of_window_enumeration_order()
    {
        var s1 = new SessionSnapshot { Windows = { T.W("a"), T.W("b", desktop: 1) } };
        var s2 = new SessionSnapshot { Windows = { T.W("b", desktop: 1), T.W("a") } };
        Assert.Equal(SessionHasher.Compute(s1), SessionHasher.Compute(s2));
    }
}

public class SessionStoreTests
{
    private static (SessionStore store, AppDataPaths paths) Create(int max = 5)
    {
        var paths = new AppDataPaths(T.TempDir());
        paths.EnsureCreated();
        return (new SessionStore(paths, new MemoryLogService(), max), paths);
    }

    private static SessionSnapshot Snap(int x, SessionKind kind = SessionKind.Auto, DateTimeOffset? at = null) => new()
    {
        Kind = kind, CreatedAt = at ?? DateTimeOffset.Now,
        Windows = { T.W("code", "x", 0, RectI.FromSize(x, 0, 500, 500)) },
    };

    [Fact]
    public void Save_and_load_last_roundtrip()
    {
        var (store, _) = Create();
        Assert.Equal(SaveOutcome.Saved, store.Save(Snap(10)));
        var last = store.LoadLast();
        Assert.NotNull(last);
        Assert.Equal(10, last!.Windows[0].NormalBounds.Left);
        Assert.Single(store.GetHistory());
    }

    [Fact]
    public void Unchanged_automatic_snapshots_are_not_duplicated()
    {
        var (store, _) = Create();
        Assert.Equal(SaveOutcome.Saved, store.Save(Snap(10)));
        var again = Snap(10); again.Windows[0].Title = "anderer Titel"; again.Windows[0].Handle = 999;
        Assert.Equal(SaveOutcome.Unchanged, store.Save(again));
        Assert.Single(store.GetHistory());
        Assert.Equal(SaveOutcome.Saved, store.Save(Snap(20)));
        Assert.Equal(2, store.GetHistory().Count);
    }

    [Fact]
    public void Manual_snapshots_are_always_saved()
    {
        var (store, _) = Create();
        store.Save(Snap(10));
        Assert.Equal(SaveOutcome.Saved, store.Save(Snap(10, SessionKind.Manual, DateTimeOffset.Now.AddSeconds(1))));
        Assert.Equal(2, store.GetHistory().Count);
    }

    [Fact]
    public void Dedupe_survives_restart_of_the_store()
    {
        var (store, paths) = Create();
        store.Save(Snap(10));
        var store2 = new SessionStore(paths, new MemoryLogService(), 5);
        Assert.Equal(SaveOutcome.Unchanged, store2.Save(Snap(10)));
    }

    [Fact]
    public void History_is_limited_to_max_entries_newest_first()
    {
        var (store, _) = Create(max: 3);
        var t0 = DateTimeOffset.Now.AddHours(-1);
        for (var i = 0; i < 6; i++) store.Save(Snap(i * 10, at: t0.AddMinutes(i)));
        var h = store.GetHistory();
        Assert.Equal(3, h.Count);
        Assert.True(h[0].CreatedAt > h[1].CreatedAt && h[1].CreatedAt > h[2].CreatedAt);
        Assert.Equal(50, store.LoadHistory(h[0].FilePath)!.Windows[0].NormalBounds.Left);
    }

    [Fact]
    public void Corrupt_last_session_falls_back_to_backup_or_history()
    {
        var (store, paths) = Create();
        store.Save(Snap(10, at: DateTimeOffset.Now.AddMinutes(-2)));
        store.Save(Snap(20, at: DateTimeOffset.Now.AddMinutes(-1)));
        File.WriteAllText(paths.LastSessionFile, "{\"schemaVersion\": 1, \"windows\": [ {\"processName\": ");
        Assert.Equal(10, new SessionStore(paths, new MemoryLogService()).LoadLast()!.Windows[0].NormalBounds.Left); // .bak
        File.Delete(paths.LastSessionFile + ".bak");
        Assert.Equal(20, new SessionStore(paths, new MemoryLogService()).LoadLast()!.Windows[0].NormalBounds.Left); // Verlauf
    }

    [Fact]
    public void Protected_entries_survive_retention()
    {
        var (store, _) = Create(max: 2);
        var t0 = DateTimeOffset.Now.AddHours(-1);
        store.Save(Snap(1, at: t0));
        var first = store.GetHistory()[0].FilePath;
        store.Protect(first);
        for (var i = 1; i < 5; i++) store.Save(Snap(i * 10, at: t0.AddMinutes(i)));
        Assert.Contains(store.GetHistory(), e => e.FilePath == first);
    }
}

public class HistoryRetentionTests
{
    private static HistoryEntry E(int minutesAgo, SessionKind kind) =>
        new($"/h/{minutesAgo}.json", DateTimeOffset.Now.AddMinutes(-minutesAgo), kind, 1, 1, 1, null);

    [Fact]
    public void Deletes_oldest_automatic_first_and_keeps_manual()
    {
        var entries = new[] { E(1, SessionKind.Auto), E(2, SessionKind.Manual), E(3, SessionKind.Auto), E(4, SessionKind.Auto), E(5, SessionKind.Manual) };
        var del = HistoryRetention.SelectForDeletion(entries, 3);
        Assert.Equal(2, del.Count);
        Assert.All(del, e => Assert.Equal(SessionKind.Auto, e.Kind));
        Assert.Contains(del, e => e.FilePath == "/h/4.json");
        Assert.Contains(del, e => e.FilePath == "/h/3.json");
    }

    [Fact]
    public void Never_deletes_newest_entry()
    {
        var entries = new[] { E(1, SessionKind.Auto), E(2, SessionKind.Manual) };
        var del = HistoryRetention.SelectForDeletion(entries, 1);
        Assert.DoesNotContain(del, e => e.FilePath == "/h/1.json");
    }

    [Fact]
    public void Formats_relative_german_labels()
    {
        var now = new DateTimeOffset(2026, 11, 25, 21, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal("Heute, 20:45 Uhr", HistoryRetention.FormatRelative(now.AddMinutes(-15), now));
        Assert.Equal("Gestern, 22:15 Uhr", HistoryRetention.FormatRelative(new DateTimeOffset(2026, 11, 24, 22, 15, 0, TimeSpan.FromHours(1)), now));
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void Unknown_properties_survive_load_and_save()
    {
        var dir = T.TempDir();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ \"schemaVersion\": 1, \"zukunftsFeature\": { \"a\": 1 }, \"widget\": { \"expandedWidth\": 300, \"neueOption\": true } }");
        var svc = new SettingsService(path, new MemoryLogService());
        Assert.Equal(300, svc.Current.Widget.ExpandedWidth);
        svc.Update(s => s.Widget.OpacityPercent = 70);
        var text = File.ReadAllText(path);
        Assert.Contains("zukunftsFeature", text);
        Assert.Contains("neueOption", text);
        Assert.Equal(70, new SettingsService(path, new MemoryLogService()).Current.Widget.OpacityPercent);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults_and_keep_copy()
    {
        var dir = T.TempDir();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "<<<kaputt");
        var svc = new SettingsService(path, new MemoryLogService());
        Assert.Equal(SaveMode.OnChange, svc.Current.Backup.Mode);
        Assert.True(File.Exists(path + ".defekt"));
    }

    [Fact]
    public void Values_are_clamped_to_sane_ranges()
    {
        var s = new AppSettings();
        s.Widget.ExpandedWidth = 5000; s.Backup.HistoryCount = 0; s.Widget.OpacityPercent = 3;
        SettingsService.Normalize(s);
        Assert.Equal(320, s.Widget.ExpandedWidth);
        Assert.Equal(3, s.Backup.HistoryCount);
        Assert.Equal(30, s.Widget.OpacityPercent);
    }

    [Fact]
    public void Enums_are_stored_as_readable_strings()
    {
        var json = Json.Serialize(new AppSettings());
        Assert.Contains("\"onChange\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
