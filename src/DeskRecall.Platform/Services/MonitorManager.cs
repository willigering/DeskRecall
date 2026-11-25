using System.Runtime.InteropServices;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Geometry;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Platform.Interop;

namespace DeskRecall.Platform.Services;

/// <summary>Ermittelt Monitore mit stabiler Identität (Gerätepfad) und Anzeigenamen (DisplayConfig-API).</summary>
public sealed class MonitorManager : IMonitorManager
{
    private readonly ILogService _log;
    private IReadOnlyList<MonitorInfo>? _cache;
    private DateTime _cacheTime;

    public MonitorManager(ILogService log) => _log = log;

    public void Invalidate() => _cache = null;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        if (_cache is not null && DateTime.UtcNow - _cacheTime < TimeSpan.FromSeconds(3)) return _cache;
        var names = QueryTargetNames();
        var list = new List<MonitorInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return true;
            var dpi = 96u;
            try { if (NativeMethods.GetDpiForMonitor(hMon, 0, out var dx, out _) == 0) dpi = dx; } catch { }
            names.TryGetValue(mi.szDevice, out var target);
            var id = target.Path;
            if (string.IsNullOrEmpty(id)) id = DeviceInterfaceName(mi.szDevice) ?? mi.szDevice;
            list.Add(new MonitorInfo
            {
                Id = id,
                DeviceName = mi.szDevice,
                FriendlyName = string.IsNullOrWhiteSpace(target.Name) ? FallbackName(mi.szDevice) : target.Name,
                Bounds = new RectI(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                WorkArea = new RectI(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
                Dpi = (int)dpi,
                IsPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            });
            return true;
        }, IntPtr.Zero);
        _cache = list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
        _cacheTime = DateTime.UtcNow;
        return _cache;
    }

    public MonitorInfo? ForWindow(IntPtr hwnd)
    {
        var h = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(h, ref mi)) return null;
        return GetMonitors().FirstOrDefault(m => m.DeviceName == mi.szDevice);
    }

    private static string FallbackName(string device)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return NativeMethods.EnumDisplayDevices(device, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString) ? dd.DeviceString : device.Replace(@"\\.\", "");
    }

    private static string? DeviceInterfaceName(string device)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return NativeMethods.EnumDisplayDevices(device, 0, ref dd, 1) && !string.IsNullOrWhiteSpace(dd.DeviceID) ? dd.DeviceID : null;
    }

    private Dictionary<string, (string Name, string Path)> QueryTargetNames()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, out var nPath, out var nMode) != 0) return result;
            var paths = new DisplayConfig.PATH_INFO[nPath];
            var modes = new DisplayConfig.MODE_INFO[nMode];
            if (DisplayConfig.QueryDisplayConfig(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, ref nPath, paths, ref nMode, modes, IntPtr.Zero) != 0) return result;
            for (var i = 0; i < nPath; i++)
            {
                var p = paths[i];
                var src = new DisplayConfig.SOURCE_DEVICE_NAME();
                src.header.type = 1; src.header.size = Marshal.SizeOf<DisplayConfig.SOURCE_DEVICE_NAME>();
                src.header.adapterId = p.sourceInfo.adapterId; src.header.id = p.sourceInfo.id;
                if (DisplayConfig.DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                var tgt = new DisplayConfig.TARGET_DEVICE_NAME();
                tgt.header.type = 2; tgt.header.size = Marshal.SizeOf<DisplayConfig.TARGET_DEVICE_NAME>();
                tgt.header.adapterId = p.targetInfo.adapterId; tgt.header.id = p.targetInfo.id;
                if (DisplayConfig.DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
                result[src.viewGdiDeviceName] = (tgt.monitorFriendlyDeviceName ?? "", tgt.monitorDevicePath ?? "");
            }
        }
        catch (Exception ex) { _log.Debug("Monitore", "DisplayConfig nicht verfügbar: " + ex.Message); }
        return result;
    }
}

internal static class DisplayConfig
{
    public const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] public struct RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
        public uint scaling; public RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public ulong data0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER { public uint type; public int size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME requestPacket);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME requestPacket);
}
