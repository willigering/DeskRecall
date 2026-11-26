using System.Diagnostics;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Desktops;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;

namespace DeskRecall.Services;

/// <summary>Fassade für Desktop- und Fensteraktionen aus der Oberfläche (Wechseln, Anlegen, Umbenennen, Verschieben …).</summary>
public sealed class DesktopManager
{
    private readonly IVirtualDesktopService _vd;
    private readonly IWindowManager _windows;
    private readonly ILogService _log;
    private readonly IClock _clock;

    public DesktopManager(IVirtualDesktopService vd, IWindowManager windows, ILogService log, IClock? clock = null)
    {
        _vd = vd; _windows = windows; _log = log;
        _clock = clock ?? SystemClock.Instance;
    }

    public bool CanManage => _vd.CanManage;
    public string Backend => _vd.BackendDescription;

    /// <summary>Letzte Messung Klick → Wechsel → Fokus (für Einstellungen → Diagnose).</summary>
    public ActivationTiming? LastTiming { get; private set; }
    /// <summary>Zeit vom Klick bis zum Start des Wechsels im Hintergrund (Oberfläche + Warteschlange).</summary>
    public TimeSpan LastClickLatency { get; private set; }

    /// <summary>Wechselt den Desktop im Hintergrund (nie auf dem UI-Thread), ohne feste Wartezeit.</summary>
    public Task<bool> SwitchAsync(Guid id, long clickTimestamp = 0) => Task.Run(async () =>
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var timing = await WindowActivation.SwitchDesktopAsync(_vd, id, _clock);
            Record(timing, clickTimestamp, started);
            if (!timing.Focused) _log.Warn("Desktops", $"Desktop wechseln war nicht möglich{(_vd.LastError is null ? "" : $": {_vd.LastError}")}");
            return timing.Focused;
        }
        catch (Exception ex) { _log.Warn("Desktops", "Desktop wechseln fehlgeschlagen", ex); return false; }
    });

    void Record(ActivationTiming timing, long clickTimestamp, long started)
    {
        LastTiming = timing;
        LastClickLatency = clickTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(clickTimestamp, started);
        _log.Debug("Desktops", $"{timing.Describe()}{(clickTimestamp == 0 ? "" : $", ab Klick {Stopwatch.GetElapsedTime(clickTimestamp).TotalMilliseconds:0} ms")}");
    }

    public Task<DesktopInfo?> CreateAsync(string? name) => Task.Run(() =>
    {
        try { return _vd.CreateDesktop(string.IsNullOrWhiteSpace(name) ? null : name.Trim()); }
        catch (Exception ex) { _log.Warn("Desktops", "Desktop konnte nicht erstellt werden", ex); return null; }
    });

    public Task<bool> RenameAsync(Guid id, string name) => Run("Desktop umbenennen", () => _vd.RenameDesktop(id, name.Trim()));

    /// <summary>Schließt einen Desktop. Windows verschiebt seine Fenster dabei auf den benachbarten Desktop – es wird nichts beendet.</summary>
    public Task<bool> RemoveAsync(Guid id) => Run("Desktop schließen", () => _vd.RemoveDesktop(id));

    public Task<bool> MoveDesktopAsync(Guid id, int newIndex) => Run("Desktop verschieben", () => _vd.MoveDesktop(id, newIndex));

    public Task<bool> MoveWindowAsync(long hwnd, Guid desktopId) => Run("Fenster verschieben", () => _vd.MoveWindowToDesktop(hwnd, desktopId));

    public Task<bool> PinWindowAsync(long hwnd, bool pin) => Run("Fenster anheften", () => _vd.SetWindowPinned(hwnd, pin));

    /// <summary>Wechselt bei Bedarf auf den Desktop des Fensters, bestätigt den Wechsel und holt es dann in den Vordergrund – im Hintergrund.</summary>
    public Task<bool> BringToFrontAsync(long hwnd, Guid? desktopId, long clickTimestamp = 0, Func<Task>? beforeFocus = null) => Task.Run(async () =>
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            if (beforeFocus is not null) { try { await beforeFocus(); } catch { } }
            var timing = await WindowActivation.ActivateMeasuredAsync(_vd, _windows, hwnd, desktopId, _clock);
            Record(timing, clickTimestamp, started);
            if (!timing.Focused) _log.Debug("Desktops", "Fenster ließ sich nicht sicher in den Vordergrund holen.");
            return timing.Focused;
        }
        catch (Exception ex) { _log.Warn("Desktops", "Fenster aktivieren fehlgeschlagen", ex); return false; }
    });

    public Task<bool> MinimizeAsync(long hwnd) => Run("Fenster minimieren", () => _windows.Minimize(hwnd));

    public Task<bool> CloseWindowAsync(long hwnd) => Run("Fenster schließen", () => _windows.Close(hwnd));

    public Task<bool> ApplyPlacementAsync(long hwnd, PlacementTarget target) => Run("Fenster anordnen", () => _windows.ApplyPlacement(hwnd, target));

    private Task<bool> Run(string what, Func<bool> action) => Task.Run(() =>
    {
        try
        {
            var ok = action();
            if (!ok) _log.Warn("Desktops", $"{what} war nicht möglich{(_vd.LastError is null ? "" : $": {_vd.LastError}")}");
            return ok;
        }
        catch (Exception ex) { _log.Warn("Desktops", $"{what} fehlgeschlagen", ex); return false; }
    });
}
