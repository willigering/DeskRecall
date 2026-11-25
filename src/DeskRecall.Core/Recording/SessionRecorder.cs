using System.Reflection;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Core.Rules;
using DeskRecall.Core.Settings;

namespace DeskRecall.Core.Recording;

public interface ISessionRecorder
{
    SessionSnapshot Capture(SessionKind kind);
}

/// <summary>Erfasst den aktuellen Zustand der Windows-Sitzung (Desktops, Monitore, Fenster).</summary>
public sealed class SessionRecorder : ISessionRecorder
{
    private readonly IWindowManager _windows;
    private readonly IVirtualDesktopService _desktops;
    private readonly IMonitorManager _monitors;
    private readonly IRuleEngine _rules;
    private readonly Func<AppSettings> _settings;
    private readonly ILogService _log;
    private readonly string? _windowsVersion;

    public SessionRecorder(IWindowManager windows, IVirtualDesktopService desktops, IMonitorManager monitors, IRuleEngine rules,
        Func<AppSettings> settings, ILogService log, string? windowsVersion = null)
    {
        _windows = windows; _desktops = desktops; _monitors = monitors; _rules = rules; _settings = settings; _log = log;
        _windowsVersion = windowsVersion;
    }

    public SessionSnapshot Capture(SessionKind kind)
    {
        var s = _settings();
        var snapshot = new SessionSnapshot
        {
            Kind = kind,
            CreatedAt = DateTimeOffset.Now,
            WindowsVersion = _windowsVersion,
            AppVersion = typeof(SessionRecorder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        };

        try { snapshot.Monitors = _monitors.GetMonitors().ToList(); }
        catch (Exception ex) { _log.Warn("Sicherung", "Monitore konnten nicht ermittelt werden", ex); }

        var desktops = new List<DesktopInfo>();
        Guid? current = null;
        if (_desktops.IsAvailable)
        {
            try
            {
                desktops = _desktops.GetDesktops().ToList();
                current = _desktops.GetCurrentDesktopId();
            }
            catch (Exception ex) { _log.Warn("Sicherung", "Virtuelle Desktops konnten nicht gelesen werden", ex); }
        }
        snapshot.Desktops = desktops;
        snapshot.ActiveDesktopId = current;
        snapshot.ActiveDesktopIndex = Math.Max(0, desktops.FindIndex(d => d.Id == current));

        IReadOnlyList<WindowRecord> windows;
        try { windows = _windows.EnumerateAppWindows(); }
        catch (Exception ex) { _log.Error("Sicherung", "Fenster konnten nicht ermittelt werden", ex); windows = Array.Empty<WindowRecord>(); }

        var excludedProcs = s.Exclusions.ProcessNames.Select(AppIdentity.NormalizeProcessName).ToHashSet();
        foreach (var w0 in windows)
        {
            if (excludedProcs.Contains(AppIdentity.NormalizeProcessName(w0.ProcessName))) continue;
            if (!string.IsNullOrEmpty(w0.Title) && s.Exclusions.TitleContains.Any(t => !string.IsNullOrWhiteSpace(t) && w0.Title.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            if (_rules.Evaluate(w0).Excluded) continue;

            var w = w0.Clone();
            w.DesktopIndex = w.IsPinned ? -1 : desktops.FindIndex(d => d.Id == w.DesktopId);
            if (!s.Backup.StoreWindowTitles) w.Title = "";
            if (!s.Backup.CaptureDocumentPaths) w.DocumentPath = null;
            snapshot.Windows.Add(w);
        }
        return snapshot;
    }
}

/// <summary>
/// Beim Herunterfahren schließen manche Programme ihre Fenster schon während WM_QUERYENDSESSION.
/// Eine deutlich kleinere „letzte“ Sicherung würde die gute periodische Sicherung überschreiben – das verhindern wir.
/// </summary>
public static class ShutdownSnapshotPolicy
{
    public static bool ShouldAccept(SessionSnapshot? previous, SessionSnapshot shutdown, double minRatio = 0.6)
    {
        if (previous is null || previous.Windows.Count == 0) return true;
        if (shutdown.Windows.Count >= previous.Windows.Count) return true;
        return shutdown.Windows.Count >= Math.Ceiling(previous.Windows.Count * minRatio);
    }
}

/// <summary>
/// Startschutz: Direkt nach dem Hochfahren ist der Desktop (fast) leer. Würde DeskRecall jetzt automatisch
/// sichern, wäre die Sitzung vor dem Neustart verloren. Automatische Sicherungen werden daher erst nach
/// erfolgter Wiederherstellung bzw. einer Schonfrist freigegeben.
/// </summary>
public sealed class StartupGuard
{
    private readonly DateTimeOffset _started;
    private readonly TimeSpan _grace;
    private bool _released;

    public StartupGuard(DateTimeOffset started, TimeSpan grace, bool active)
    {
        _started = started; _grace = grace; _released = !active;
    }

    public void Release() => _released = true;
    public bool IsReleased(DateTimeOffset now) => _released || now - _started >= _grace;
}

/// <summary>Bündelt viele Änderungsereignisse (Fenster verschoben, geöffnet …) zu einem Aufruf.</summary>
public sealed class ChangeDebouncer : IDisposable
{
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _maxDelay;
    private readonly Action _callback;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private DateTimeOffset? _firstSignal;
    private bool _disposed;

    public ChangeDebouncer(TimeSpan quiet, TimeSpan maxDelay, Action callback)
    {
        _quiet = quiet; _maxDelay = maxDelay; _callback = callback;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Signal()
    {
        lock (_lock)
        {
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            _firstSignal ??= now;
            var due = _quiet;
            var forced = _firstSignal.Value + _maxDelay - now;
            if (forced < due) due = forced < TimeSpan.Zero ? TimeSpan.Zero : forced;
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_lock) { if (_disposed) return; _firstSignal = null; }
        try { _callback(); } catch { }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; }
        _timer.Dispose();
    }
}
