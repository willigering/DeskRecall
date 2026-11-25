using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeskRecall.Platform.Interop;

namespace DeskRecall.Platform.Services;

public sealed record ProcessDetails(int Pid, string Name, string? Path, bool AccessDenied, bool IsElevated, string? PackageAumid, string? CommandLine, long CreationTime);

/// <summary>Liest Prozessdetails mit minimalen Rechten (PROCESS_QUERY_LIMITED_INFORMATION) und cacht sie pro Prozessstart.</summary>
public static class ProcessInfo
{
    private static readonly ConcurrentDictionary<int, ProcessDetails> Cache = new();
    private static readonly ConcurrentDictionary<string, string?> DescriptionCache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    /// <param name="fallbackName">Prozessname aus einer Prozessliste, falls der Prozess nicht geöffnet werden darf.</param>
    public static ProcessDetails Get(int pid, bool withCommandLine, Func<int, string?>? fallbackName = null)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            var n = fallbackName?.Invoke(pid) ?? (Cache.TryGetValue(pid, out var c) ? c.Name : "");
            return new ProcessDetails(pid, n, null, true, true, null, null, 0);
        }
        try
        {
            GetProcessTimes(h, out var creation, out _, out _, out _);
            if (Cache.TryGetValue(pid, out var cached) && cached.CreationTime == creation && (!withCommandLine || cached.CommandLine is not null))
                return cached;
            var d = Read(h, pid, creation, withCommandLine, fallbackName);
            Cache[pid] = d;
            return d;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    public static void Prune(IEnumerable<int> alive)
    {
        var set = alive.ToHashSet();
        foreach (var k in Cache.Keys) if (!set.Contains(k)) Cache.TryRemove(k, out _);
    }

    private static ProcessDetails Read(IntPtr h, int pid, long creation, bool withCommandLine, Func<int, string?>? fallbackName)
    {
        string? path = null;
        var sb = new StringBuilder(1024); var size = sb.Capacity;
        if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
        var name = path is not null ? System.IO.Path.GetFileNameWithoutExtension(path) : fallbackName?.Invoke(pid) ?? "";

        var elevated = false;
        if (NativeMethods.OpenProcessToken(h, NativeMethods.TOKEN_QUERY, out var token))
        {
            try { if (NativeMethods.GetTokenInformation(token, 20 /*TokenElevation*/, out var e, 4, out _)) elevated = e != 0; }
            finally { NativeMethods.CloseHandle(token); }
        }
        else elevated = true; // Token nicht lesbar → Prozess ist höher privilegiert

        string? aumid = null;
        var len = 0;
        if (NativeMethods.GetApplicationUserModelId(h, ref len, null) == 122 /*ERROR_INSUFFICIENT_BUFFER*/ && len > 0)
        {
            var ab = new StringBuilder(len);
            if (NativeMethods.GetApplicationUserModelId(h, ref len, ab) == 0) aumid = ab.ToString();
        }

        var cmd = withCommandLine ? ReadCommandLine(h) ?? "" : null;
        return new ProcessDetails(pid, name, path, false, elevated, aumid, cmd, creation);
    }

    /// <summary>Befehlszeile über NtQueryInformationProcess(ProcessCommandLineInformation) – nur zur Dokumentpfad-Erkennung.</summary>
    private static string? ReadCommandLine(IntPtr h)
    {
        try
        {
            NativeMethods.NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (NativeMethods.NtQueryInformationProcess(h, 60, buf, needed, out _) != 0) return null;
                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
                var length = (ushort)Marshal.ReadInt16(buf);
                var ptr = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr, length / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    public static string? FileDescription(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return DescriptionCache.GetOrAdd(path, p =>
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(p);
                var d = fvi.FileDescription?.Trim();
                if (string.IsNullOrWhiteSpace(d) || d.Length > 48) d = fvi.ProductName?.Trim();
                return string.IsNullOrWhiteSpace(d) ? null : d;
            }
            catch { return null; }
        });
    }
}
