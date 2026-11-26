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

    public async Task<bool> SwitchAsync(Guid id)
    {
        try
        {
            var ok = await WindowActivation.SwitchToDesktopAsync(_vd, id, _clock);
            if (!ok) _log.Warn("Desktops", $"Desktop wechseln war nicht möglich{(_vd.LastError is null ? "" : $": {_vd.LastError}")}");
            return ok;
        }
        catch (Exception ex) { _log.Warn("Desktops", "Desktop wechseln fehlgeschlagen", ex); return false; }
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

    /// <summary>Wechselt bei Bedarf auf den Desktop des Fensters, wartet auf den Wechsel und holt es dann in den Vordergrund.</summary>
    public async Task<bool> BringToFrontAsync(long hwnd, Guid? desktopId)
    {
        try
        {
            var ok = await WindowActivation.ActivateAsync(_vd, _windows, hwnd, desktopId, _clock);
            if (!ok) _log.Warn("Desktops", $"Fenster aktivieren war nicht möglich{(_vd.LastError is null ? "" : $": {_vd.LastError}")}");
            return ok;
        }
        catch (Exception ex) { _log.Warn("Desktops", "Fenster aktivieren fehlgeschlagen", ex); return false; }
    }

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
