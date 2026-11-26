using DeskRecall.Core.Abstractions;

namespace DeskRecall.Core.Desktops;

/// <summary>
/// Wechselt per COM auf den Desktop eines Fensters, wartet bis der Wechsel sichtbar ist
/// (höchstens 500 ms) und holt das Fenster danach in den Vordergrund.
/// </summary>
public static class WindowActivation
{
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Desktop des Fensters (COM), sonst der übergebene – leere Guid bedeutet „nicht wechseln“.</summary>
    public static Guid ResolveDesktop(IVirtualDesktopService vd, long hwnd, Guid? desktopId)
    {
        if (hwnd != 0)
        {
            var fromWindow = vd.GetWindowDesktopId(hwnd);
            if (fromWindow is { } id && id != Guid.Empty) return id;
        }
        return desktopId is { } given && given != Guid.Empty ? given : Guid.Empty;
    }

    public static async Task<bool> SwitchToDesktopAsync(IVirtualDesktopService vd, Guid desktopId, IClock clock, CancellationToken ct = default)
    {
        if (desktopId == Guid.Empty) return true;
        if (vd.GetCurrentDesktopId() == desktopId) return true;
        if (!vd.SwitchToDesktop(desktopId)) return false;

        var deadline = clock.Now + SwitchTimeout;
        while (vd.GetCurrentDesktopId() != desktopId && clock.Now < deadline)
            await clock.Delay(PollInterval, ct);
        return vd.GetCurrentDesktopId() is not { } cur || cur == desktopId;
    }

    public static async Task<bool> ActivateAsync(
        IVirtualDesktopService vd, IWindowManager windows, long hwnd, Guid? desktopId, IClock clock, CancellationToken ct = default)
    {
        if (hwnd == 0) return false;
        var target = ResolveDesktop(vd, hwnd, desktopId);
        if (target != Guid.Empty)
            await SwitchToDesktopAsync(vd, target, clock, ct);
        return windows.BringToFront(hwnd);
    }
}
