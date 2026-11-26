using System.Diagnostics;
using DeskRecall.Core.Browsers;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;

namespace DeskRecall.Services;

/// <summary>
/// Hält die Tabs der offenen Browserfenster aktuell: liest im Hintergrund (eigener Thread), gedrosselt
/// (höchstens alle 3 s pro Fenster bzw. sofort nach Titelwechsel), mit Rückzug bei Fehlern.
/// Die Oberfläche fragt nur den Zwischenspeicher ab.
/// </summary>
public sealed class TabService : IDisposable
{
    const string Src = "Tabs";
    readonly ITabReader _reader;
    readonly ILogService _log;
    readonly object _lock = new();
    readonly Dictionary<long, TabReadState> _states = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    List<(long Hwnd, BrowserFamily Family, string Title)> _observed = new();
    volatile bool _disposed;
    volatile bool _active = true;

    public TabService(ITabReader reader, ILogService log)
    {
        _reader = reader; _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "DeskRecall.Tabs", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Tabs haben sich geändert (aus dem Hintergrund-Thread).</summary>
    public event Action? Changed;
    public TimeSpan LastReadDuration { get; private set; }
    public int LastReadWindows { get; private set; }

    /// <summary>Nur lesen, solange das Widget sichtbar ist.</summary>
    public bool Active { get => _active; set { _active = value; if (value) _wake.Set(); } }

    public static BrowserFamily FamilyOf(WindowRecord w) => BrowserCatalog.Detect(w.ProcessName, w.WindowClass);

    /// <summary>Aktuelle Browserfenster melden (billig, vom UI-Thread).</summary>
    public void Observe(IEnumerable<WindowRecord> windows)
    {
        var list = new List<(long, BrowserFamily, string)>();
        foreach (var w in windows)
        {
            var f = FamilyOf(w);
            if (f != BrowserFamily.None) list.Add((w.Handle, f, w.Title ?? ""));
        }
        bool poke;
        lock (_lock)
        {
            poke = list.Count != _observed.Count || list.Where((x, i) => x != _observed[i]).Any();
            _observed = list;
        }
        if (poke) _wake.Set();
    }

    public IReadOnlyList<BrowserTab> Get(long hwnd)
    {
        lock (_lock) return _states.TryGetValue(hwnd, out var s) ? s.Tabs : Array.Empty<BrowserTab>();
    }

    public Task<bool> SelectAsync(long hwnd, BrowserFamily family, BrowserTab tab) => Task.Run(() =>
    {
        try { return _reader.SelectTab(hwnd, family, tab); }
        catch (Exception ex) { _log.Debug(Src, "Tab konnte nicht gewählt werden: " + ex.Message); return false; }
    });

    void Loop()
    {
        while (!_disposed)
        {
            _wake.WaitOne(TabReadPolicy.Interval);
            if (_disposed) break;
            if (!_active) continue;
            try { ReadDue(); }
            catch (Exception ex) { _log.Debug(Src, "Tabs lesen fehlgeschlagen: " + ex.Message); }
        }
    }

    void ReadDue()
    {
        List<(long Hwnd, BrowserFamily Family, string Title)> observed;
        lock (_lock)
        {
            observed = _observed;
            var alive = observed.Select(o => o.Hwnd).ToHashSet();
            foreach (var dead in _states.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _states.Remove(dead);
                if (_reader is UiaTabReader uia) uia.Forget(dead);
            }
        }

        var sw = Stopwatch.StartNew();
        var changed = false;
        var read = 0;
        foreach (var (hwnd, family, title) in observed)
        {
            if (_disposed || !_active) break;
            TabReadState? state;
            lock (_lock) _states.TryGetValue(hwnd, out state);
            var now = DateTimeOffset.Now;
            if (!TabReadPolicy.ShouldRead(state, title, now)) continue;
            state ??= new TabReadState();

            var tabs = _reader.ReadTabs(hwnd, family);
            read++;
            lock (_lock)
            {
                if (tabs is null) TabReadPolicy.RecordFailure(state, title, now);
                else
                {
                    if (!TabReadPolicy.SameTabs(state.Tabs, tabs)) changed = true;
                    TabReadPolicy.RecordSuccess(state, title, tabs, now);
                }
                _states[hwnd] = state;
            }
        }
        if (read > 0)
        {
            LastReadDuration = sw.Elapsed;
            LastReadWindows = read;
        }
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
    }
}
