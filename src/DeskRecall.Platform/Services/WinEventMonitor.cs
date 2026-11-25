using static DeskRecall.Platform.Interop.NativeMethods;

namespace DeskRecall.Platform.Services;

/// <summary>
/// Leichtgewichtige Änderungserkennung über SetWinEventHook (WINEVENT_OUTOFCONTEXT): kein Hook in fremde
/// Prozesse, keine Tastatur-/Maus-Hooks, kein Polling. Muss auf einem Thread mit Nachrichtenschleife erstellt werden.
/// </summary>
public sealed class WinEventMonitor : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZEEND = 0x000B,
        EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017,
        EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003,
        EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    private readonly WinEventDelegate _callback; // Referenz halten, sonst sammelt der GC den Delegaten ein
    private readonly List<IntPtr> _hooks = new();

    /// <summary>Strukturelle Änderung (Fenster geöffnet/geschlossen/verschoben/minimiert, Desktop gewechselt).</summary>
    public event Action<IntPtr, uint>? WindowChanged;
    /// <summary>Vordergrundfenster gewechselt (für Vollbilderkennung).</summary>
    public event Action<IntPtr>? ForegroundChanged;
    /// <summary>Ein neues Top-Level-Fenster wurde sichtbar (für Live-Regeln).</summary>
    public event Action<IntPtr>? WindowShown;

    public WinEventMonitor()
    {
        _callback = OnEvent;
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
    }

    private void Hook(uint min, uint max)
    {
        var h = SetWinEventHook(min, max, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // nur Fenster selbst (OBJID_WINDOW)
        try
        {
            if (ev == EVENT_SYSTEM_FOREGROUND) { ForegroundChanged?.Invoke(hwnd); WindowChanged?.Invoke(hwnd, ev); return; }
            if (ev is EVENT_OBJECT_SHOW or EVENT_OBJECT_HIDE or EVENT_OBJECT_DESTROY)
            {
                // Nur Top-Level-Fenster interessieren
                if ((Style(hwnd) & WS_CHILD) != 0 && ev != EVENT_OBJECT_DESTROY) return;
                if (ev == EVENT_OBJECT_SHOW) WindowShown?.Invoke(hwnd);
            }
            WindowChanged?.Invoke(hwnd, ev);
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
    }
}
