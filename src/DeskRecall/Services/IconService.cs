using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DeskRecall.Services.UiNative;

namespace DeskRecall.Services;

/// <summary>Liefert die Originalsymbole der Programme (aus der EXE oder vom Fenster) – zwischengespeichert.</summary>
public sealed class IconService
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? ForWindow(long hwnd, string? exePath, string? aumidKey = null)
    {
        var key = !string.IsNullOrEmpty(exePath) && !exePath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            ? exePath : aumidKey ?? ("hwnd:" + hwnd);
        if (_cache.TryGetValue(key, out var cached) && cached is not null) return cached;
        var img = FromFile(exePath) ?? FromWindow(hwnd);
        if (img is not null || !key.StartsWith("hwnd:", StringComparison.Ordinal)) _cache[key] = img;
        return img;
    }

    public ImageSource? ForPath(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        return _cache.GetOrAdd(exePath, p => FromFile(p));
    }

    /// <summary>Lädt Symbole im Hintergrund und legt nur erfolgreiche, eingefrorene Bitmaps ab.</summary>
    public void Prefetch(long hwnd, string? exePath, string? aumidKey = null)
    {
        var key = !string.IsNullOrEmpty(exePath) && !exePath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            ? exePath : aumidKey ?? ("hwnd:" + hwnd);
        if (_cache.TryGetValue(key, out var cached) && cached is not null) return;
        try
        {
            var img = FromFile(exePath) ?? FromWindow(hwnd);
            if (img is not null) _cache[key] = img;
        }
        catch { }
    }

    private static ImageSource? FromFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
        try
        {
            if (SHDefExtractIcon(path, 0, 0, out var large, out var small, (32u << 16) | 32u) == 0)
            {
                if (small != IntPtr.Zero) DestroyIcon(small);
                if (large != IntPtr.Zero) return FromHIcon(large, destroy: true);
            }
            var info = new SHFILEINFO();
            var r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /* SHGFI_ICON */);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero) return FromHIcon(info.hIcon, destroy: true);
        }
        catch { }
        return null;
    }

    private static ImageSource? FromWindow(long hwnd)
    {
        if (hwnd == 0) return null;
        try
        {
            var h = (IntPtr)hwnd;
            // WM_GETICON (ICON_BIG=1, ICON_SMALL2=2) mit Zeitlimit, damit hängende Programme uns nicht blockieren
            SendMessageTimeout(h, 0x007F, (IntPtr)1, IntPtr.Zero, 0x0002 /* SMTO_ABORTIFHUNG */, 100, out var icon);
            if (icon == IntPtr.Zero) SendMessageTimeout(h, 0x007F, (IntPtr)2, IntPtr.Zero, 0x0002, 100, out icon);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, -14 /* GCLP_HICON */);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, -34 /* GCLP_HICONSM */);
            return icon == IntPtr.Zero ? null : FromHIcon(icon, destroy: false);
        }
        catch { return null; }
    }

    private static ImageSource? FromHIcon(IntPtr hIcon, bool destroy)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (destroy) DestroyIcon(hIcon); }
    }
}
