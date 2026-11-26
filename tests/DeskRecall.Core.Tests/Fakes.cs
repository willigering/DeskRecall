using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Geometry;
using DeskRecall.Core.Models;

namespace DeskRecall.Core.Tests;

internal static class T
{
    public static WindowRecord W(string proc, string title = "", int desktop = 0, RectI? bounds = null, string cls = "Cls", long handle = 0,
        AppKind kind = AppKind.Normal, string? path = null, WindowShowState state = WindowShowState.Normal, string? folder = null, string? monitor = "M1")
        => new()
        {
            ProcessName = proc, Title = title, DesktopIndex = desktop, NormalBounds = bounds ?? RectI.FromSize(100, 100, 800, 600),
            WindowClass = cls, Handle = handle, Kind = kind, ExecutablePath = path ?? $@"C:\Apps\{proc}\{proc}.exe", State = state,
            ExplorerFolderPath = folder, MonitorId = monitor, DisplayName = proc,
        };

    public static MonitorInfo Mon(string id, int x, int y, int w, int h, bool primary = false, int taskbar = 48) => new()
    {
        Id = id, DeviceName = @"\\.\DISPLAY" + id, Bounds = RectI.FromSize(x, y, w, h), WorkArea = RectI.FromSize(x, y, w, h - taskbar), IsPrimary = primary,
    };

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "deskrecall-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

internal sealed class FakeVirtualDesktops : IVirtualDesktopService
{
    public List<DesktopInfo> Desktops { get; } = new();
    public Guid Current { get; set; }
    public Dictionary<long, Guid> WindowDesktop { get; } = new();
    public HashSet<long> Pinned { get; } = new();
    public List<string> Calls { get; } = new();
    public bool Manage { get; set; } = true;

    public FakeVirtualDesktops(params string[] names)
    {
        foreach (var n in names) Desktops.Add(new DesktopInfo { Id = Guid.NewGuid(), Name = n });
        Reindex();
        if (Desktops.Count > 0) Current = Desktops[0].Id;
    }

    private void Reindex() { for (var i = 0; i < Desktops.Count; i++) Desktops[i].Index = i; }

    public bool IsAvailable => true;
    public bool CanManage => Manage;
    public string BackendDescription => "Fake";
    public string? LastError => null;
    public IReadOnlyList<DesktopInfo> GetDesktops() => Desktops.Select(d => new DesktopInfo { Id = d.Id, Index = d.Index, Name = d.Name }).ToList();
    public Guid? GetCurrentDesktopId() => Current;
    public Guid? GetWindowDesktopId(long hwnd) => WindowDesktop.TryGetValue(hwnd, out var g) ? g : null;
    public bool IsWindowPinned(long hwnd) => Pinned.Contains(hwnd);
    public bool MoveWindowToDesktop(long hwnd, Guid desktopId) { Calls.Add($"move {hwnd}->{Desktops.First(d => d.Id == desktopId).Index}"); WindowDesktop[hwnd] = desktopId; return true; }
    public bool SetWindowPinned(long hwnd, bool pinned) { if (pinned) Pinned.Add(hwnd); else Pinned.Remove(hwnd); return true; }
    public bool SwitchToDesktop(Guid desktopId) { Calls.Add($"switch {Desktops.First(d => d.Id == desktopId).Index}"); Current = desktopId; return true; }
    public DesktopInfo? CreateDesktop(string? name = null) { var d = new DesktopInfo { Id = Guid.NewGuid(), Name = name ?? "" }; Desktops.Add(d); Reindex(); Calls.Add("create " + name); return d; }
    public bool RemoveDesktop(Guid desktopId) { Desktops.RemoveAll(d => d.Id == desktopId); Reindex(); return true; }
    public bool RenameDesktop(Guid desktopId, string name) { Desktops.First(d => d.Id == desktopId).Name = name; Calls.Add("rename " + name); return true; }
    public bool MoveDesktop(Guid desktopId, int newIndex)
    {
        var d = Desktops.First(x => x.Id == desktopId); Desktops.Remove(d); Desktops.Insert(newIndex, d); Reindex(); Calls.Add($"movedesk {d.Name}->{newIndex}"); return true;
    }
    public void Reinitialize() { }
}

internal sealed class FakeWindows : IWindowManager
{
    public List<WindowRecord> Live { get; } = new();
    public Dictionary<long, PlacementTarget> Applied { get; } = new();
    public List<long> Closed { get; } = new();
    public IReadOnlyList<WindowRecord> EnumerateAppWindows() { lock (Live) return Live.Select(w => w.Clone()).ToList(); }
    public bool IsAlive(long hwnd) { lock (Live) return Live.Any(w => w.Handle == hwnd); }
    public RectI? GetWindowBounds(long hwnd) => Applied.TryGetValue(hwnd, out var t) ? t.NormalBounds : null;
    public WindowShowState? GetState(long hwnd) => Applied.TryGetValue(hwnd, out var t) ? t.State : WindowShowState.Normal;
    public bool ApplyPlacement(long hwnd, PlacementTarget target) { Applied[hwnd] = target; return true; }
    public bool BringToFront(long hwnd) => true;
    public bool Minimize(long hwnd) => true;
    public bool Close(long hwnd) { Closed.Add(hwnd); return true; }
    public void Add(WindowRecord w) { lock (Live) Live.Add(w); }
    public void Remove(long hwnd) { lock (Live) Live.RemoveAll(w => w.Handle == hwnd); }
}

internal sealed class FakeProcesses : IProcessManager
{
    private readonly FakeWindows _windows;
    public List<RunningProcess> Running { get; } = new();
    public List<LaunchRequest> Launched { get; } = new();
    /// <summary>Was beim Start eines Programms passiert (z. B. Fenster erscheinen verzögert).</summary>
    public Dictionary<string, Action<LaunchRequest>> OnLaunch { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> NeedsAdmin { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeProcesses(FakeWindows w) => _windows = w;
    public IReadOnlyList<RunningProcess> GetRunningProcesses() => Running.ToList();
    public ResolvedLaunch? Resolve(WindowRecord record) => Missing.Contains(record.ProcessName) ? null : new ResolvedLaunch(record.ExecutablePath ?? record.ProcessName + ".exe", null, null);
    public LaunchResult Launch(LaunchRequest request)
    {
        var name = WinPath.GetFileNameWithoutExtension(request.FileName);
        if (NeedsAdmin.Contains(name) && !request.AllowElevation) return new LaunchResult(LaunchStatus.RequiresElevation);
        Launched.Add(request);
        Running.Add(new RunningProcess(1000 + Launched.Count, name, request.FileName));
        if (OnLaunch.TryGetValue(name, out var a)) a(request);
        return new LaunchResult(LaunchStatus.Started, 1000 + Launched.Count);
    }
}

internal sealed class FakeMonitors : IMonitorManager
{
    public List<MonitorInfo> Monitors { get; } = new() { T.Mon("M1", 0, 0, 1920, 1080, primary: true) };
    public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors;
}

/// <summary>Virtuelle Uhr: Delay schaltet die Zeit weiter und führt fällige Termine aus, ohne echte Wartezeit.</summary>
internal sealed class FakeClock : DeskRecall.Core.Abstractions.IClock
{
    readonly List<Work> _scheduled = new();
    TaskCompletionSource? _paused;
    TaskCompletionSource? _resume;

    public DateTimeOffset Now { get; private set; } = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(1));

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var work = new Work(Now + delay, action);
        _scheduled.Add(work);
        return work;
    }

    sealed class Work(DateTimeOffset due, Action action) : IDisposable
    {
        public DateTimeOffset Due { get; } = due;
        public Action Action { get; } = action;
        public bool Canceled { get; private set; }
        public void Dispose() => Canceled = true;
    }

    /// <summary>Nächstes Delay hält an, bis <see cref="Resume"/> kommt – für Tests auf gleichzeitige Aufrufe.</summary>
    public void Pause()
    {
        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitUntilPaused() => _paused?.Task ?? Task.CompletedTask;

    public void Resume()
    {
        var resume = _resume;
        _resume = null;
        _paused = null;
        resume?.TrySetResult();
    }

    public async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Advance(delay);
        var paused = _paused;
        var resume = _resume;
        if (paused is not null && resume is not null)
        {
            paused.TrySetResult();
            await resume.Task.WaitAsync(ct);
        }
        else
            await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }

    public void Advance(TimeSpan delay)
    {
        var target = Now + delay;
        _scheduled.RemoveAll(w => w.Canceled);
        while (_scheduled.Count > 0)
        {
            var next = 0;
            for (var i = 1; i < _scheduled.Count; i++)
                if (_scheduled[i].Due < _scheduled[next].Due) next = i;
            if (_scheduled[next].Due > target) break;
            var item = _scheduled[next];
            _scheduled.RemoveAt(next);
            if (item.Canceled) continue;
            Now = item.Due;
            item.Action();
        }
        Now = target;
    }
}
