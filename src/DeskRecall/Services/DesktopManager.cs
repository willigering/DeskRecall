using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;

namespace DeskRecall.Services;

/// <summary>Fassade für Desktop- und Fensteraktionen aus der Oberfläche (Wechseln, Anlegen, Umbenennen, Verschieben …).</summary>
public sealed class DesktopManager
{
    private readonly IVirtualDesktopService _vd;
    private readonly IWindowManager _windows;
    private readonly ILogService _log;

    public DesktopManager(IVirtualDesktopService vd, IWindowManager windows, ILogService log)
    {
        _vd = vd; _windows = windows; _log = log;
    }

    public bool CanManage => _vd.CanManage;
    public string Backend => _vd.BackendDescription;

    public Task<bool> SwitchAsync(Guid id) => Run("Desktop wechseln", () => _vd.SwitchToDesktop(id));

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

    /// <summary>Holt ein Fenster nach vorne; liegt es auf einem anderen Desktop, wird zuerst dorthin gewechselt.</summary>
    public Task<bool> BringToFrontAsync(long hwnd, Guid? desktopId) => Run("Fenster aktivieren", () =>
    {
        if (desktopId is { } d && d != Guid.Empty && _vd.GetCurrentDesktopId() != d) _vd.SwitchToDesktop(d);
        return _windows.BringToFront(hwnd);
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
