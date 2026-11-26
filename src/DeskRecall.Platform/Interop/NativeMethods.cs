using System.Runtime.InteropServices;
using System.Text;

namespace DeskRecall.Platform.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
public struct WINDOWPLACEMENT
{
    public int length;
    public int flags;
    public int showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAY_DEVICE
{
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
    public int StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public INPUTUNION u;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx, dy;
    public uint mouseData, dwFlags, time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk, wScan;
    public uint dwFlags, time;
    public IntPtr dwExtraInfo;
}

public static class NativeMethods
{
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_MENU = 0x12;
    public const int ASFW_ANY = -1;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;

    public const uint GW_OWNER = 4;
    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3, SW_SHOWNOACTIVATE = 4,
        SW_SHOW = 5, SW_MINIMIZE = 6, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;
    public const int WPF_SETMINPOSITION = 0x1, WPF_RESTORETOMAXIMIZED = 0x2, WPF_ASYNCWINDOWPLACEMENT = 0x4;

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20,
        SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200, SWP_ASYNCWINDOWPOS = 0x4000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint WM_CLOSE = 0x0010;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;

    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWM_CLOAKED_APP = 0x1, DWM_CLOAKED_SHELL = 0x2, DWM_CLOAKED_INHERITED = 0x4;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetApplicationUserModelId(IntPtr hProcess, ref int length, StringBuilder? id);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr process, int cls, IntPtr buffer, int length, out int returnLength);

    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate cb, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] public static extern int WindowsCreateString(string src, int length, out IntPtr hstring);
    [DllImport("combase.dll")] public static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] public static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);

    public static long Style(IntPtr h) => GetWindowLongPtr(h, GWL_STYLE).ToInt64();
    public static long ExStyle(IntPtr h) => GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();

    public static string GetText(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 2);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? HStringToString(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        try
        {
            var p = WindowsGetStringRawBuffer(h, out var len);
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p, len);
        }
        finally { WindowsDeleteString(h); }
    }

    public static IntPtr CreateHString(string s)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(s, s.Length, out var h));
        return h;
    }
}
