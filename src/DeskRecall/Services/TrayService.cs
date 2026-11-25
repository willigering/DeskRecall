using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DeskRecall.Core.Logging;
using static DeskRecall.Services.UiNative;

namespace DeskRecall.Services;

/// <summary>
/// Symbol im Infobereich (eigene, schlanke Shell_NotifyIcon-Implementierung ohne WinForms).
/// Das unsichtbare Fenster empfängt außerdem WM_QUERYENDSESSION und Bildschirmänderungen.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint WM_APP_TRAY = 0x8000 + 42;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

    private readonly ILogService _log;
    private HwndSource? _source;
    private IntPtr _icon;
    private uint _taskbarCreated;
    private bool _visible;
    private string _tip = "DeskRecall";

    public TrayService(ILogService log) => _log = log;

    public event Action? LeftClick;
    public event Func<ContextMenu>? MenuRequested;
    /// <summary>Windows fragt vor dem Abmelden/Herunterfahren (WM_QUERYENDSESSION).</summary>
    public event Action? QueryEndSession;
    public event Action? DisplayChanged;

    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public void Initialize()
    {
        if (_source is not null) return;
        var p = new HwndSourceParameters("DeskRecallTrayHost")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, unsichtbar – kein Message-only-Fenster, damit Broadcasts ankommen
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadAppIcon();
    }

    public void SetVisible(bool visible)
    {
        Initialize();
        if (visible == _visible) return;
        if (visible) Add(); else Remove();
    }

    public void SetToolTip(string tip)
    {
        _tip = tip.Length > 120 ? tip[..120] : tip;
        if (!_visible) return;
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_visible) return;
        var d = Data(NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = 0x00000004 | 0x00000010; // NIIF_USER | NIIF_NOSOUND
        d.hBalloonIcon = _icon;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = Handle, uID = 1, uFlags = flags, uCallbackMessage = WM_APP_TRAY, hIcon = _icon, szTip = _tip,
        szInfo = "", szInfoTitle = "",
    };

    private void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref d))
        {
            // Bereits vorhanden (z. B. nach Absturz) → aktualisieren
            Shell_NotifyIcon(NIM_MODIFY, ref d);
        }
        d.uVersion = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIcon(NIM_SETVERSION, ref d);
        _visible = true;
    }

    private void Remove()
    {
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        _visible = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            var ev = (int)((long)lParam & 0xFFFF);
            switch (ev)
            {
                case WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT:
                    LeftClick?.Invoke();
                    break;
                case WM_RBUTTONUP or WM_CONTEXTMENU:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            // Explorer wurde neu gestartet → Symbol erneut hinzufügen
            if (_visible) { _visible = false; Add(); }
        }
        else if (msg == WM_QUERYENDSESSION)
        {
            try { QueryEndSession?.Invoke(); } catch (Exception ex) { _log.Error("Tray", "Sicherung beim Herunterfahren fehlgeschlagen", ex); }
            handled = true;
            return (IntPtr)1; // Herunterfahren nie blockieren
        }
        else if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED || (msg == WM_SETTINGCHANGE && (int)wParam == 0x2F /* SPI_SETWORKAREA */))
        {
            DisplayChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = MenuRequested?.Invoke();
        if (menu is null) return;
        // Damit das Menü beim Klick daneben verschwindet, muss unser Fenster kurz Vordergrund sein.
        SetForegroundWindow(Handle);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private IntPtr LoadAppIcon()
    {
        var size = 16 * (int)Math.Max(1, Math.Round(GetDpiForWindow(Handle) / 96.0));
        // 1) Aus der EXE (ApplicationIcon wird als Ressource 32512 eingebettet)
        var h = LoadImage(GetModuleHandle(null), (IntPtr)32512, 1 /* IMAGE_ICON */, size, size, 0);
        if (h != IntPtr.Zero) return h;
        // 2) Fallback: eingebettete WPF-Ressource in eine temporäre Datei schreiben
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/DeskRecall.ico"));
            if (info is not null)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "DeskRecall-tray.ico");
                using (var fs = File.Create(tmp)) info.Stream.CopyTo(fs);
                h = LoadImageFile(IntPtr.Zero, tmp, 1, size, size, 0x10 /* LR_LOADFROMFILE */);
            }
        }
        catch (Exception ex) { _log.Warn("Tray", "Symbol konnte nicht geladen werden", ex); }
        return h;
    }

    public void Dispose()
    {
        if (_visible) Remove();
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        _source?.Dispose();
        _source = null;
    }
}
