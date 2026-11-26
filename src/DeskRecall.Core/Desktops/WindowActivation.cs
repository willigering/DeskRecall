using DeskRecall.Core.Abstractions;

namespace DeskRecall.Core.Desktops;

/// <summary>Messwerte eines Klicks: Desktop-Wechsel, Bestätigung, Fokus (für die Diagnose).</summary>
public sealed record ActivationTiming(
    string Kind, TimeSpan Switch, TimeSpan Confirm, TimeSpan Focus, TimeSpan Total, bool Switched, bool Focused, int Polls)
{
    public string Describe() =>
        $"{Kind}: {Total.TotalMilliseconds:0} ms" +
        (Switched ? $" (Wechsel {Switch.TotalMilliseconds:0} ms, Bestätigung {Confirm.TotalMilliseconds:0} ms/{Polls}×" : " (kein Wechsel nötig") +
        (Kind == "Desktop" ? ")" : $", Fokus {Focus.TotalMilliseconds:0} ms{(Focused ? "" : ", nicht bestätigt")})");
}

/// <summary>
/// Wechselt per COM auf den Desktop eines Fensters, bestätigt den Wechsel mit sehr kurzen Abfragen
/// (meist sofort, höchstens 300 ms) und holt das Fenster danach in den Vordergrund.
/// Keine festen Wartezeiten; keine überflüssigen COM-Aufrufe.
/// </summary>
public static class WindowActivation
{
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Ziel-Desktop: der bekannte Desktop aus dem Live-Zustand; nur wenn er fehlt, wird COM gefragt.
    /// Leere Guid bedeutet „nicht wechseln“.
    /// </summary>
    public static Guid ResolveDesktop(IVirtualDesktopService vd, long hwnd, Guid? desktopId)
    {
        if (desktopId is { } given && given != Guid.Empty) return given;
        if (hwnd != 0 && vd.GetWindowDesktopId(hwnd) is { } id && id != Guid.Empty) return id;
        return Guid.Empty;
    }

    public static async Task<bool> SwitchToDesktopAsync(IVirtualDesktopService vd, Guid desktopId, IClock clock, CancellationToken ct = default)
        => (await SwitchMeasuredAsync(vd, desktopId, clock, ct)).Switched || vd.GetCurrentDesktopId() == desktopId;

    internal sealed record SwitchResult(bool Needed, bool Switched, TimeSpan Switch, TimeSpan Confirm, int Polls);

    internal static async Task<SwitchResult> SwitchMeasuredAsync(IVirtualDesktopService vd, Guid desktopId, IClock clock, CancellationToken ct, Guid? knownCurrent = null)
    {
        if (desktopId == Guid.Empty) return new(false, false, TimeSpan.Zero, TimeSpan.Zero, 0);
        var current = knownCurrent ?? vd.GetCurrentDesktopId();
        if (current == desktopId) return new(false, false, TimeSpan.Zero, TimeSpan.Zero, 0);

        var t0 = clock.Now;
        if (!vd.SwitchToDesktop(desktopId)) return new(true, false, clock.Now - t0, TimeSpan.Zero, 0);
        var t1 = clock.Now;

        var polls = 0;
        var deadline = t1 + SwitchTimeout;
        var confirmed = vd.GetCurrentDesktopId() is not { } cur || cur == desktopId;
        while (!confirmed && clock.Now < deadline)
        {
            await clock.Delay(PollInterval, ct);
            polls++;
            confirmed = vd.GetCurrentDesktopId() is not { } c || c == desktopId;
        }
        return new(true, confirmed, t1 - t0, clock.Now - t1, polls);
    }

    public static async Task<ActivationTiming> SwitchDesktopAsync(IVirtualDesktopService vd, Guid desktopId, IClock clock, CancellationToken ct = default)
    {
        var start = clock.Now;
        var r = await SwitchMeasuredAsync(vd, desktopId, clock, ct);
        return new ActivationTiming("Desktop", r.Switch, r.Confirm, TimeSpan.Zero, clock.Now - start, r.Needed, r.Switched || !r.Needed, r.Polls);
    }

    public static async Task<bool> ActivateAsync(
        IVirtualDesktopService vd, IWindowManager windows, long hwnd, Guid? desktopId, IClock clock, CancellationToken ct = default)
        => (await ActivateMeasuredAsync(vd, windows, hwnd, desktopId, clock, ct)).Focused;

    public static async Task<ActivationTiming> ActivateMeasuredAsync(
        IVirtualDesktopService vd, IWindowManager windows, long hwnd, Guid? desktopId, IClock clock, CancellationToken ct = default)
    {
        var start = clock.Now;
        if (hwnd == 0) return new ActivationTiming("Fenster", TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, false, false, 0);
        var target = ResolveDesktop(vd, hwnd, desktopId);
        var r = await SwitchMeasuredAsync(vd, target, clock, ct);
        var f0 = clock.Now;
        var focused = windows.BringToFront(hwnd);
        var now = clock.Now;
        return new ActivationTiming("Fenster", r.Switch, r.Confirm, now - f0, now - start, r.Needed, focused, r.Polls);
    }
}
