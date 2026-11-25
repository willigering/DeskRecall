// Interne Virtual-Desktop-Schnittstellen von Windows 11.
// GUIDs und Methodenreihenfolge nach MScholtes/VirtualDesktop (MIT-Lizenz, © 2017 Markus Scholtes),
// siehe THIRD-PARTY-NOTICES.md. Abweichungen: Views und HSTRINGs werden als IntPtr übergeben,
// damit keine in .NET 5+ entfernte WinRT-Marshalling-Unterstützung nötig ist.
using System.Runtime.InteropServices;

namespace DeskRecall.Platform.VirtualDesktops.Com;

internal static class Clsid
{
    public static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    public static readonly Guid VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    public static readonly Guid VirtualDesktopPinnedApps = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out int count);
    void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
}

/// <summary>Dokumentierte, stabile Schnittstelle (shobjidl_core.h).</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManagerPublic
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrent);
    [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

/// <summary>IApplicationViewCollection – Views werden nur als Zeiger weitergereicht.</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
internal interface IApplicationViewCollection
{
    [PreserveSig] int GetViews(out IntPtr array);
    [PreserveSig] int GetViewsByZOrder(out IntPtr array);
    [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
    [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
internal interface IVirtualDesktopPinnedApps
{
    [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId, out int pinned);
    [PreserveSig] int PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
    [PreserveSig] int UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
    [PreserveSig] int IsViewPinned(IntPtr view, out int pinned);
    [PreserveSig] int PinView(IntPtr view);
    [PreserveSig] int UnpinView(IntPtr view);
}

// ---------------------------------------------------------------------------------------------
// IVirtualDesktop – zwei Varianten
// ---------------------------------------------------------------------------------------------

/// <summary>Windows 11 ab 22621.2215 (22H2/23H2/24H2/25H2).</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop11
{
    [PreserveSig] int IsViewVisible(IntPtr view, out int visible);
    Guid GetId();
    IntPtr GetName();           // HSTRING
    IntPtr GetWallpaperPath();  // HSTRING
    [PreserveSig] int IsRemote(out int remote);
}

/// <summary>Windows 11 21H2 / 22H2 vor 22621.2215.</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("536D3495-B208-4CC9-AE26-DE8111275BF8")]
internal interface IVirtualDesktopLegacy
{
    [PreserveSig] int IsViewVisible(IntPtr view, out int visible);
    Guid GetId();
    IntPtr Unknown1();
    IntPtr GetName();           // HSTRING
    IntPtr GetWallpaperPath();  // HSTRING
}

// ---------------------------------------------------------------------------------------------
// IVirtualDesktopManagerInternal – vier Varianten
// ---------------------------------------------------------------------------------------------

/// <summary>Windows 11 24H2/25H2 (26100+): zusätzliche Methode nach SwitchDesktop.</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal24H2
{
    int GetCount();
    void MoveViewToDesktop(IntPtr view, IVirtualDesktop11 desktop);
    [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);
    IVirtualDesktop11 GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop11 from, int direction, out IVirtualDesktop11 desktop);
    void SwitchDesktop(IVirtualDesktop11 desktop);
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop11 desktop);
    IVirtualDesktop11 CreateDesktop();
    void MoveDesktop(IVirtualDesktop11 desktop, int nIndex);
    void RemoveDesktop(IVirtualDesktop11 desktop, IVirtualDesktop11 fallback);
    IVirtualDesktop11 FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop11 desktop, out IObjectArray unknown1, out IObjectArray unknown2);
    void SetDesktopName(IVirtualDesktop11 desktop, IntPtr name);
}

/// <summary>Windows 11 22H2/23H2 ab Build .3085.</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal23H2
{
    int GetCount();
    void MoveViewToDesktop(IntPtr view, IVirtualDesktop11 desktop);
    [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);
    IVirtualDesktop11 GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop11 from, int direction, out IVirtualDesktop11 desktop);
    void SwitchDesktop(IVirtualDesktop11 desktop);
    IVirtualDesktop11 CreateDesktop();
    void MoveDesktop(IVirtualDesktop11 desktop, int nIndex);
    void RemoveDesktop(IVirtualDesktop11 desktop, IVirtualDesktop11 fallback);
    IVirtualDesktop11 FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop11 desktop, out IObjectArray unknown1, out IObjectArray unknown2);
    void SetDesktopName(IVirtualDesktop11 desktop, IntPtr name);
}

/// <summary>Windows 11 22H2 ab 22621.2215 bis vor .3085 (gleiche Reihenfolge wie 23H2, andere GUID).</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E")]
internal interface IVirtualDesktopManagerInternal22H2
{
    int GetCount();
    void MoveViewToDesktop(IntPtr view, IVirtualDesktop11 desktop);
    [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);
    IVirtualDesktop11 GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop11 from, int direction, out IVirtualDesktop11 desktop);
    void SwitchDesktop(IVirtualDesktop11 desktop);
    IVirtualDesktop11 CreateDesktop();
    void MoveDesktop(IVirtualDesktop11 desktop, int nIndex);
    void RemoveDesktop(IVirtualDesktop11 desktop, IVirtualDesktop11 fallback);
    IVirtualDesktop11 FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop11 desktop, out IObjectArray unknown1, out IObjectArray unknown2);
    void SetDesktopName(IVirtualDesktop11 desktop, IntPtr name);
}

/// <summary>Windows 11 21H2 (22000) und 22H2 vor 22621.2215 – Methoden mit HMONITOR-Parameter.</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("B2F925B9-5A0F-4D2E-9F4D-2B1507593C10")]
internal interface IVirtualDesktopManagerInternalLegacy
{
    int GetCount(IntPtr hWndOrMon);
    void MoveViewToDesktop(IntPtr view, IVirtualDesktopLegacy desktop);
    [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);
    IVirtualDesktopLegacy GetCurrentDesktop(IntPtr hWndOrMon);
    IObjectArray GetAllCurrentDesktops();
    void GetDesktops(IntPtr hWndOrMon, out IObjectArray desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktopLegacy from, int direction, out IVirtualDesktopLegacy desktop);
    void SwitchDesktop(IntPtr hWndOrMon, IVirtualDesktopLegacy desktop);
    IVirtualDesktopLegacy CreateDesktop(IntPtr hWndOrMon);
    void MoveDesktop(IVirtualDesktopLegacy desktop, IntPtr hWndOrMon, int nIndex);
    void RemoveDesktop(IVirtualDesktopLegacy desktop, IVirtualDesktopLegacy fallback);
    IVirtualDesktopLegacy FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktopLegacy desktop, out IObjectArray unknown1, out IObjectArray unknown2);
    void SetDesktopName(IVirtualDesktopLegacy desktop, IntPtr name);
}
