using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskRecall.Platform.Services;

/// <summary>
/// Fährt Windows herunter wie der Eintrag im Startmenü: Hybrid-Herunterfahren, geplante Ursache.
/// Reagierende Programme bekommen noch WM_QUERYENDSESSION und können speichern.
/// Hängende Programme beendet Windows nach seinem eigenen Timeout.
/// </summary>
public static class SystemPower
{
    const uint EwxShutdown = 0x00000001;
    const uint EwxForceIfHung = 0x00000010;
    const uint EwxHybridShutdown = 0x00400000;
    const uint ShtdnReasonFlagPlanned = 0x80000000;
    const uint TokenAdjustPrivileges = 0x0020;
    const uint TokenQuery = 0x0008;
    const uint SePrivilegeEnabled = 0x00000002;

    public static bool RequestShutdown()
    {
        try
        {
            EnablePrivilege("SeShutdownPrivilege");
            if (ExitWindowsEx(EwxShutdown | EwxHybridShutdown | EwxForceIfHung, ShtdnReasonFlagPlanned))
                return true;
        }
        catch { }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /hybrid /t 0",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return p is not null;
        }
        catch
        {
            return false;
        }
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SePrivilegeEnabled },
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES next, uint length, IntPtr previous, IntPtr returnLength);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint flags, uint reason);
}
