using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Geometry;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Platform.Interop;
using DeskRecall.Platform.VirtualDesktops;
using static DeskRecall.Platform.Interop.NativeMethods;

namespace DeskRecall.Platform.Services;

/// <summary>
/// Ermittelt Programmfenster über EnumWindows (Alt-Tab-Kriterien, alle virtuellen Desktops) und setzt
/// Position, Größe und Zustand DPI-korrekt per SetWindowPlacement/SetWindowPos – ohne Desktop-Wechsel.
/// </summary>
public sealed class WindowManager : IWindowManager
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "ThumbnailDeviceHelperWnd", "EdgeUiInputTopWndClass", "NotifyIconOverflowWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.Internal.Shell.TabProxyWindow", "PseudoConsoleWindow",
        "ForegroundStaging", "MultitaskingViewFrame", "Shell_InputSwitchTopLevelWindow", "SysShadow", "tooltips_class32",
        "IME", "MSCTFIME UI", "Internet Explorer_Hidden", "Xaml_WindowedPopupClass",
    };

    private readonly WindowsVirtualDesktopService _desktops;
    private readonly MonitorManager _monitors;
    private readonly ExplorerFolderService _explorer;
    private readonly ILogService _log;
    private readonly Func<bool> _captureDocuments;
    private readonly int _ownPid = Environment.ProcessId;
    private static Dictionary<int, string> _processNames = new();
    private static long _processNamesTick;

    public WindowManager(WindowsVirtualDesktopService desktops, MonitorManager monitors, ExplorerFolderService explorer, ILogService log, Func<bool> captureDocuments)
    {
        _desktops = desktops; _monitors = monitors; _explorer = explorer; _log = log; _captureDocuments = captureDocuments;
    }

    private sealed record RawWindow(IntPtr Handle, int Pid, string Class, string Title, long Style, long ExStyle, int Cloaked, int Z);

    public IReadOnlyList<WindowRecord> EnumerateAppWindows(bool detailed = true)
    {
        var raw = new List<RawWindow>();
        var z = 0;
        EnumWindows((h, _) =>
        {
            z++;
            try { if (TryGetCandidate(h, z, out var r)) raw.Add(r!); } catch { }
            return true;
        }, IntPtr.Zero);

        // Fenster auf anderen Desktops sind „von der Shell verborgen“ (cloaked) – nur diese zulassen.
        var desktopIds = _desktops.GetWindowDesktopIds(raw.Select(r => (long)r.Handle).ToList());
        var current = _desktops.GetCurrentDesktopId();
        raw = raw.Where(r =>
        {
            if (r.Cloaked == 0) return true;
            if ((r.Cloaked & DWM_CLOAKED_SHELL) == 0) return false;            // von der App selbst verborgen
            return desktopIds.TryGetValue(r.Handle, out var id) && id != current; // auf anderem Desktop
        }).ToList();

        string? NameOf(int pid)
        {
            if (detailed) RefreshProcessNames();
            return _processNames.TryGetValue(pid, out var n) ? n : null;
        }

        IReadOnlyDictionary<long, string>? folders = null;
        var monitors = _monitors.GetMonitors();
        var captureDocs = detailed && _captureDocuments();
        var result = new List<WindowRecord>(raw.Count);

        foreach (var r in raw)
        {
            try
            {
                var pid = r.Pid;
                var cls = r.Class;
                // UWP: ApplicationFrameHost beherbergt das eigentliche App-Fenster (CoreWindow)
                if (cls == "ApplicationFrameWindow")
                {
                    var corePid = FindCoreWindowPid(r.Handle, pid);
                    if (corePid != 0) pid = corePid;
                }
                var proc = ProcessInfo.Get(pid, captureDocs, NameOf);
                if (string.IsNullOrEmpty(proc.Name)) continue;

                var aumid = proc.PackageAumid ?? WindowAumid.Get(r.Handle);
                var kind = AppCatalog.Classify(proc.Name, cls, aumid);
                if (kind == AppKind.Normal && proc.PackageAumid is not null && proc.Path?.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) == true)
                    kind = AppKind.Uwp; // Paket-Apps (Store) nur über ihre AppUserModelID startbar

                var (bounds, state) = ReadPlacement(r.Handle, monitors);
                var mon = detailed ? _monitors.ForWindow(r.Handle) : null;
                var rec = new WindowRecord
                {
                    Handle = (long)r.Handle,
                    ProcessId = pid,
                    ProcessName = proc.Name,
                    ExecutablePath = proc.Path,
                    AppUserModelId = aumid,
                    DisplayName = AppCatalog.FriendlyName(proc.Name) ?? (detailed ? ProcessInfo.FileDescription(proc.Path) : null) ?? proc.Name,
                    WindowClass = cls,
                    Title = r.Title,
                    DesktopId = desktopIds.TryGetValue((long)r.Handle, out var did) ? did : Guid.Empty,
                    NormalBounds = bounds,
                    State = state,
                    MonitorId = mon?.Id,
                    ZOrder = r.Z,
                    HasCaption = (r.Style & WS_CAPTION) == WS_CAPTION,
                    IsResizable = (r.Style & WS_THICKFRAME) != 0,
                    Kind = kind,
                    IsElevated = proc.IsElevated,
                };
                if (detailed && kind == AppKind.Explorer)
                {
                    folders ??= _explorer.GetOpenFolders();
                    if (folders.TryGetValue((long)r.Handle, out var f)) rec.ExplorerFolderPath = f;
                }
                if (captureDocs && proc.CommandLine is { Length: > 0 } cmd)
                    rec.DocumentPath = DocumentPathExtractor.Extract(cmd, proc.Name, File.Exists);
                result.Add(rec);
            }
            catch (Exception ex) { _log.Debug("Fenster", $"Fenster übersprungen: {ex.Message}"); }
        }

        // auf allen Desktops angeheftete Fenster erkennen – eine COM-Runde statt einer je Fenster
        if (_desktops.CanManage && result.Count > 0)
        {
            var pins = _desktops.GetWindowsPinned(result.Select(r => r.Handle).ToList());
            foreach (var rec in result)
                rec.IsPinned = pins.TryGetValue(rec.Handle, out var p) && p;
        }

        ProcessInfo.Prune(result.Select(w => w.ProcessId).Concat(raw.Select(r => r.Pid)));
        return result;
    }

    private static void RefreshProcessNames()
    {
        var now = Environment.TickCount64;
        if (now - _processNamesTick < 2000) return;
        var names = new Dictionary<int, string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { names[p.Id] = p.ProcessName; } catch { }
                p.Dispose();
            }
        }
        catch { }
        _processNames = names;
        _processNamesTick = now;
    }

    private bool TryGetCandidate(IntPtr h, int z, out RawWindow? raw)
    {
        raw = null;
        if (!IsWindowVisible(h)) return false;
        var ex = ExStyle(h);
        var style = Style(h);
        if ((style & WS_CHILD) != 0) return false;
        var appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & WS_EX_NOACTIVATE) != 0) return false;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        }
        GetWindowThreadProcessId(h, out var pid);
        if (pid == _ownPid || pid == 0) return false;
        var cls = GetClass(h);
        if (IgnoredClasses.Contains(cls)) return false;
        var title = GetText(h);
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (!GetWindowRect(h, out var rc) || rc.Right - rc.Left < 40 || rc.Bottom - rc.Top < 20)
        {
            if (!IsIconic(h)) return false;
        }
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int));
        raw = new RawWindow(h, pid, cls, title, style, ex, cloaked, z);
        return true;
    }

    private static int FindCoreWindowPid(IntPtr frame, int framePid)
    {
        var found = 0;
        EnumChildWindows(frame, (c, _) =>
        {
            GetWindowThreadProcessId(c, out var p);
            if (p != framePid && GetClass(c) == "Windows.UI.Core.CoreWindow") { found = p; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Normale Position in Bildschirmkoordinaten + Zustand.</summary>
    private (RectI Bounds, WindowShowState State) ReadPlacement(IntPtr h, IReadOnlyList<MonitorInfo> monitors)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(h, ref wp);
        var state = wp.showCmd == SW_SHOWMINIMIZED || IsIconic(h) ? WindowShowState.Minimized
                  : wp.showCmd == SW_SHOWMAXIMIZED || IsZoomed(h) ? WindowShowState.Maximized
                  : WindowShowState.Normal;

        if (state == WindowShowState.Normal && GetWindowRect(h, out var rc))
            return (new RectI(rc.Left, rc.Top, rc.Right, rc.Bottom), state); // exakt, inkl. Andocken (Snap)

        // rcNormalPosition ist in Arbeitsbereichskoordinaten → in Bildschirmkoordinaten umrechnen
        var n = wp.rcNormalPosition;
        var r = new RectI(n.Left, n.Top, n.Right, n.Bottom);
        var mon = MonitorRemapper.MonitorForRect(r, monitors);
        if (mon is not null) r = r.Offset(mon.WorkArea.Left - mon.Bounds.Left, mon.WorkArea.Top - mon.Bounds.Top);
        return (r, state);
    }

    public bool IsAlive(long hwnd) => IsWindow((IntPtr)hwnd);

    public RectI? GetWindowBounds(long hwnd) => GetWindowRect((IntPtr)hwnd, out var rc) ? new RectI(rc.Left, rc.Top, rc.Right, rc.Bottom) : null;

    public WindowShowState? GetState(long hwnd)
    {
        var h = (IntPtr)hwnd;
        if (!IsWindow(h)) return null;
        return IsIconic(h) ? WindowShowState.Minimized : IsZoomed(h) ? WindowShowState.Maximized : WindowShowState.Normal;
    }

    public bool ApplyPlacement(long hwnd, PlacementTarget target)
    {
        var h = (IntPtr)hwnd;
        if (!IsWindow(h)) return false;
        if (IsHungAppWindow(h)) { _log.Warn("Fenster", "Fenster reagiert nicht – Platzierung übersprungen."); return false; }

        var monitors = _monitors.GetMonitors();
        var mon = target.Monitor ?? MonitorRemapper.MonitorForRect(target.NormalBounds, monitors);
        var dx = mon is null ? 0 : mon.WorkArea.Left - mon.Bounds.Left;
        var dy = mon is null ? 0 : mon.WorkArea.Top - mon.Bounds.Top;
        var b = target.NormalBounds;

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(h, ref wp)) return false;

        // Maximiert auf anderem Monitor: zuerst wiederherstellen, sonst bleibt es auf dem alten Monitor maximiert
        if (target.State == WindowShowState.Maximized && IsZoomed(h) && mon is not null && _monitors.ForWindow(h)?.Id != mon.Id)
        {
            var restore = wp; restore.showCmd = SW_SHOWNOACTIVATE; restore.flags = WPF_ASYNCWINDOWPLACEMENT;
            SetWindowPlacement(h, ref restore);
        }

        wp.flags = WPF_ASYNCWINDOWPLACEMENT;
        wp.rcNormalPosition = new RECT(b.Left - dx, b.Top - dy, b.Right - dx, b.Bottom - dy);
        wp.showCmd = target.State switch
        {
            WindowShowState.Minimized => SW_SHOWMINNOACTIVE,
            WindowShowState.Maximized => SW_SHOWMAXIMIZED,
            _ => target.Activate ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE,
        };
        var ok = SetWindowPlacement(h, ref wp);

        if (ok && target.State == WindowShowState.Normal)
        {
            // Zweimal setzen: beim Wechsel auf einen Monitor mit anderer Skalierung (DPI) passt Windows die Größe an.
            const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS;
            SetWindowPos(h, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, flags);
            SetWindowPos(h, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, flags);
        }
        if (!ok) _log.Debug("Fenster", $"SetWindowPlacement fehlgeschlagen (Fehler {Marshal.GetLastPInvokeError()}).");
        return ok;
    }

    public bool BringToFront(long hwnd)
    {
        var h = (IntPtr)hwnd;
        if (!IsWindow(h)) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (IsForeground(h)) return true;
        if (SetForegroundWindow(h) && IsForeground(h)) return true;

        // Das Widget ist WS_EX_NOACTIVATE und bekommt damit keine Vordergrundrechte.
        // Alt-Taste (SendInput), AllowSetForegroundWindow und AttachThreadInput umgehen die Sperre.
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
        SendAlt();
        if (SetForegroundWindow(h) && IsForeground(h)) return true;

        var fg = GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        var targetThread = GetWindowThreadProcessId(h, out _);
        var me = GetCurrentThreadId();
        var attachedFg = fgThread != 0 && fgThread != me && AttachThreadInput(fgThread, me, true);
        var attachedTarget = targetThread != 0 && targetThread != me && targetThread != fgThread && AttachThreadInput(targetThread, me, true);
        try
        {
            BringWindowToTop(h);
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(targetThread, me, false);
            if (attachedFg) AttachThreadInput(fgThread, me, false);
        }
        if (IsForeground(h)) return true;

        SwitchToThisWindow(h, true);
        return IsForeground(h);
    }

    private static bool IsForeground(IntPtr h) => GetForegroundWindow() == h;

    private static void SendAlt()
    {
        var down = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_MENU } } };
        var up = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    public bool Minimize(long hwnd) => ShowWindowAsync((IntPtr)hwnd, SW_MINIMIZE);

    public bool Close(long hwnd) => PostMessage((IntPtr)hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}
