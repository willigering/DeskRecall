using Microsoft.Win32;

namespace DeskRecall.Platform.VirtualDesktops;

/// <summary>Nur-Lese-Fallback: Desktop-IDs, Namen und aktiven Desktop aus der Registry (HKCU) lesen.</summary>
internal static class VirtualDesktopRegistry
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    public static List<Guid> ReadIds()
    {
        var list = new List<Guid>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root);
            if (key?.GetValue("VirtualDesktopIDs") is byte[] bytes)
                for (var i = 0; i + 16 <= bytes.Length; i += 16) list.Add(new Guid(bytes.AsSpan(i, 16)));
        }
        catch { }
        return list;
    }

    public static Guid? ReadCurrent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root);
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } b) return new Guid(b);
        }
        catch { }
        return null;
    }

    public static string ReadName(Guid id)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\Desktops\{id:B}");
            return key?.GetValue("Name") as string ?? "";
        }
        catch { return ""; }
    }
}
