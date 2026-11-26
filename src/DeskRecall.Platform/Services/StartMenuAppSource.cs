using System.Runtime.InteropServices;
using System.Text;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Logging;

namespace DeskRecall.Platform.Services;

/// <summary>
/// Sammelt Startmenü-Verknüpfungen (.lnk) und, soweit erreichbar, Einträge aus der Windows-Apps-Liste.
/// Fehler einzelner Dateien werden übersprungen.
/// </summary>
public sealed class StartMenuAppSource : IInstalledAppSource
{
    private readonly ILogService _log;
    public StartMenuAppSource(ILogService log) => _log = log;

    public async Task<IReadOnlyList<InstalledAppEntry>> ListAsync(CancellationToken ct = default)
        => await Task.Run(() => ListCore(ct), ct).ConfigureAwait(false);

    IReadOnlyList<InstalledAppEntry> ListCore(CancellationToken ct)
    {
        var shortcuts = new List<ShortcutRecord>();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };
        IEnumerable<string> Enumerate(string root)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            try { return Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { return Array.Empty<string>(); }
        }

        foreach (var path in InstalledAppCatalog.EnumerateShortcutFiles(roots, Enumerate))
        {
            ct.ThrowIfCancellationRequested();
            var rec = TryResolveShortcut(path);
            if (rec is not null) shortcuts.Add(rec);
        }

        var fromStart = InstalledAppCatalog.FromShortcuts(shortcuts);
        var fromApps = InstalledAppCatalog.FromUwpPackages(EnumerateAppsFolder(ct));
        return InstalledAppCatalog.Merge(fromStart, fromApps);
    }

    public InstalledAppEntry? ResolveShortcut(string path)
    {
        try { return InstalledAppCatalog.FromDroppedPath(path, p => TryResolveShortcut(p)); }
        catch (Exception ex) { _log.Debug("Apps", "Verknüpfung konnte nicht gelesen werden: " + path + " – " + ex.Message); return InstalledAppCatalog.FromDroppedPath(path); }
    }

    ShortcutRecord? TryResolveShortcut(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(path, 0);
                var target = new StringBuilder(260);
                var data = new WIN32_FIND_DATAW();
                link.GetPath(target, target.Capacity, ref data, 0);
                var args = new StringBuilder(1024);
                link.GetArguments(args, args.Capacity);
                var work = new StringBuilder(260);
                link.GetWorkingDirectory(work, work.Capacity);
                var icon = new StringBuilder(260);
                link.GetIconLocation(icon, icon.Capacity, out _);
                string? aumid = ReadAumid(link);
                var targetPath = target.Length > 0 ? target.ToString() : null;
                aumid ??= InstalledAppCatalog.ParseAumidFromTarget(targetPath, args.Length > 0 ? args.ToString() : null);
                return new ShortcutRecord(
                    path,
                    InstalledAppCatalog.DisplayNameFromShortcut(path),
                    string.IsNullOrWhiteSpace(targetPath) ? null : targetPath,
                    args.Length > 0 ? args.ToString() : null,
                    work.Length > 0 ? work.ToString() : null,
                    icon.Length > 0 ? icon.ToString() : null,
                    aumid);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        catch (Exception ex)
        {
            _log.Debug("Apps", "Startmenü-Verknüpfung unlesbar: " + path + " – " + ex.Message);
            return new ShortcutRecord(path, InstalledAppCatalog.DisplayNameFromShortcut(path), null, null, null, null, null);
        }
    }

    static string? ReadAumid(IShellLinkW link)
    {
        try
        {
            if (link is not IPropertyStore store) return null;
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-0285D8242902"), pid = 5 };
            store.GetValue(ref key, out var pv);
            try
            {
                if (pv.vt == 31 /* VT_LPWSTR */ && pv.p != IntPtr.Zero)
                    return Marshal.PtrToStringUni(pv.p);
            }
            finally { PropVariantClear(ref pv); }
        }
        catch { }
        return null;
    }

    IEnumerable<(string Name, string Aumid, string? LogoPath)> EnumerateAppsFolder(CancellationToken ct)
    {
        var list = new List<(string, string, string?)>();
        IShellItem? folder = null;
        IEnumShellItems? e = null;
        try
        {
            var iidItem = typeof(IShellItem).GUID;
            var hr = SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iidItem, out folder);
            if (hr != 0 || folder is null) return list;
            var bhid = BHID_EnumItems;
            var iidEnum = typeof(IEnumShellItems).GUID;
            hr = folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var unk);
            if (hr != 0 || unk is null) return list;
            e = (IEnumShellItems)unk;
            var arr = new IShellItem[1];
            while (e.Next(1, arr, out var got) == 0 && got == 1)
            {
                ct.ThrowIfCancellationRequested();
                var item = arr[0];
                try
                {
                    item.GetDisplayName(SIGDN_NORMALDISPLAY, out var namePtr);
                    var name = namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr) : null;
                    if (namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtr);
                    item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out var parsePtr);
                    var parse = parsePtr != IntPtr.Zero ? Marshal.PtrToStringUni(parsePtr) : null;
                    if (parsePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(parsePtr);
                    var aumid = InstalledAppCatalog.ParseAumidFromTarget(parse) ?? parse;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(aumid) && aumid!.Contains('!'))
                        list.Add((name!, aumid!, null));
                }
                catch { }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        catch (Exception ex) { _log.Debug("Apps", "Apps-Liste (shell:AppsFolder) nicht lesbar: " + ex.Message); }
        finally
        {
            if (e is not null) Marshal.ReleaseComObject(e);
            if (folder is not null) Marshal.ReleaseComObject(folder);
        }
        return list;
    }

    static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    const uint SIGDN_NORMALDISPLAY = 0;
    const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, ref WIN32_FIND_DATAW pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder psz, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string psz);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder psz, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string psz);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder psz, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string psz);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder psz, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string psz, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string psz, int reserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string psz);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(int celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] rgelt, out int pceltFetched);
        [PreserveSig] int Skip(int celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes, ftCreationTimeLo, ftCreationTimeHi, ftLastAccessTimeLo, ftLastAccessTimeHi, ftLastWriteTimeLo, ftLastWriteTimeHi, nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt, wReserved1, wReserved2, wReserved3; public IntPtr p; }
}
