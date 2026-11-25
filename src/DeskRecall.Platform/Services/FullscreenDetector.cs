using DeskRecall.Platform.Interop;
using static DeskRecall.Platform.Interop.NativeMethods;

namespace DeskRecall.Platform.Services;

/// <summary>Erkennt Vollbildanwendungen (Spiele, Videos, Präsentationen), damit das Widget nicht stört.</summary>
public static class FullscreenDetector
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    public static bool IsFullscreenActive(IntPtr ownWindow, out IntPtr monitor)
    {
        monitor = IntPtr.Zero;
        try
        {
            // QUNS_BUSY=2, QUNS_RUNNING_D3D_FULL_SCREEN=3, QUNS_PRESENTATION_MODE=4
            if (SHQueryUserNotificationState(out var state) == 0 && state is 3 or 4)
            {
                monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
                return true;
            }
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == ownWindow || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            if (ShellClasses.Contains(GetClass(fg))) return false;
            if (!GetWindowRect(fg, out var rc)) return false;
            var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            var full = rc.Left <= mi.rcMonitor.Left && rc.Top <= mi.rcMonitor.Top && rc.Right >= mi.rcMonitor.Right && rc.Bottom >= mi.rcMonitor.Bottom;
            // Maximierte Fenster haben eine Titelleiste und lassen die Taskleiste frei – nur rahmenlose Vollbilder zählen.
            if (full && (Style(fg) & WS_CAPTION) == WS_CAPTION && IsZoomed(fg)) full = false;
            if (full) monitor = mon;
            return full;
        }
        catch { return false; }
    }
}
