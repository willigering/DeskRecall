using System.Runtime.InteropServices;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Compatibility;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using DeskRecall.Platform.Interop;
using DeskRecall.Platform.Services;
using DeskRecall.Platform.VirtualDesktops.Com;

namespace DeskRecall.Platform.VirtualDesktops;

/// <summary>
/// Windows-Implementierung von <see cref="IVirtualDesktopService"/>.
/// – wählt den internen COM-Schnittstellensatz anhand von Build/UBR (siehe <see cref="VirtualDesktopApiResolver"/>)
/// – prüft ihn per Selbsttest gegen die Registry, bevor er benutzt wird
/// – fällt bei Problemen auf „nur lesen“ (öffentliche API + Registry) zurück und protokolliert das
/// – initialisiert sich nach einem Explorer-Neustart automatisch neu.
/// </summary>
public sealed class WindowsVirtualDesktopService : IVirtualDesktopService, IDisposable
{
    private const string Src = "VirtualDesktop";
    private readonly ComThread _com = new("DeskRecall.VirtualDesktopCOM");
    private readonly ILogService _log;
    private readonly CompatibilityService _compat;
    private readonly Func<VirtualDesktopApiOverride> _override;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly object _initLock = new();

    private IServiceProvider10? _shell;
    private IVdBackend? _backend;
    private IVirtualDesktopManagerPublic? _public;
    private IApplicationViewCollection? _views;
    private IVirtualDesktopPinnedApps? _pinned;
    private DateTime _lastReinit = DateTime.MinValue;

    public WindowsVirtualDesktopService(ILogService log, CompatibilityService compat, Func<VirtualDesktopApiOverride> overrideSet)
    {
        _log = log; _compat = compat; _override = overrideSet;
        Initialize();
    }

    public VirtualDesktopApiSet ActiveSet => _backend?.ApiSet ?? VirtualDesktopApiSet.PublicOnly;
    public bool IsAvailable => _backend is not null || _public is not null;
    public bool CanManage => _backend is not null;
    public string? LastError { get; private set; }
    public string BackendDescription => _backend is not null
        ? $"{VirtualDesktopApiResolver.Describe(_backend.ApiSet)} – voll verfügbar"
        : _public is not null ? "Nur lesen (öffentliche API) – Desktops können nicht verwaltet werden" : "Nicht verfügbar";

    public void Reinitialize()
    {
        lock (_initLock)
        {
            if (DateTime.UtcNow - _lastReinit < TimeSpan.FromSeconds(2)) return;
            _lastReinit = DateTime.UtcNow;
            _log.Info(Src, "Initialisiere Virtual-Desktop-Schnittstelle neu (z. B. nach Explorer-Neustart).");
            Initialize();
        }
    }

    private void Initialize()
    {
        try { _com.Invoke(InitializeOnComThread, TimeSpan.FromSeconds(15)); }
        catch (Exception ex) { LastError = ex.Message; _log.Error(Src, "Initialisierung fehlgeschlagen", ex); }
    }

    private void InitializeOnComThread()
    {
        _backend = null; _views = null; _pinned = null; _shell = null; _public = null;
        try { _public = (IVirtualDesktopManagerPublic)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid.VirtualDesktopManager)!)!; }
        catch (Exception ex) { _log.Warn(Src, "Öffentliche Virtual-Desktop-API nicht verfügbar", ex); }

        var requested = VirtualDesktopApiResolver.Resolve(_compat.Build, _override());
        if (requested == VirtualDesktopApiSet.PublicOnly)
        {
            _log.Warn(Src, $"{_compat.Description}: interne Schnittstelle wird nicht verwendet – nur Lesezugriff.");
            return;
        }

        try
        {
            _shell = (IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid.ImmersiveShell)!)!;
            var viewIid = typeof(IApplicationViewCollection).GUID;
            var viewSvc = viewIid;
            _views = (IApplicationViewCollection)_shell.QueryService(ref viewSvc, ref viewIid);
            try
            {
                var pinSvc = Clsid.VirtualDesktopPinnedApps;
                var pinIid = typeof(IVirtualDesktopPinnedApps).GUID;
                _pinned = (IVirtualDesktopPinnedApps)_shell.QueryService(ref pinSvc, ref pinIid);
            }
            catch (Exception ex) { _log.Warn(Src, "Anheften von Fenstern nicht verfügbar", ex); }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error(Src, "ImmersiveShell nicht erreichbar – nur Lesezugriff.", ex);
            return;
        }

        foreach (var set in VirtualDesktopApiResolver.FallbackOrder(requested))
        {
            try
            {
                var b = BackendFactory.Create(set, _shell);
                if (b is null) continue;
                if (SelfTest(b, out var reason))
                {
                    _backend = b;
                    LastError = null;
                    _log.Info(Src, $"{_compat.Description}: Schnittstellensatz „{VirtualDesktopApiResolver.Describe(set)}“ aktiv ({b.GetCount()} Desktops).");
                    return;
                }
                _log.Warn(Src, $"Selbsttest für „{VirtualDesktopApiResolver.Describe(set)}“ fehlgeschlagen: {reason}");
            }
            catch (Exception ex)
            {
                _log.Info(Src, $"Schnittstellensatz „{VirtualDesktopApiResolver.Describe(set)}“ passt nicht zu diesem Build ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            }
        }
        LastError = "Kein passender Schnittstellensatz für diesen Windows-Build gefunden.";
        _log.Error(Src, $"{LastError} {_compat.Description}. DeskRecall arbeitet eingeschränkt (Fenster werden nicht zwischen Desktops verschoben). Ein Update von DeskRecall behebt das in der Regel.");
    }

    private static bool SelfTest(IVdBackend b, out string reason)
    {
        var count = b.GetCount();
        if (count < 1 || count > 200) { reason = $"unplausible Anzahl {count}"; return false; }
        var desktops = b.GetDesktops();
        if (desktops.Count != count) { reason = "Anzahl stimmt nicht mit Liste überein"; return false; }
        if (desktops.Any(d => d.Id == Guid.Empty) || desktops.Select(d => d.Id).Distinct().Count() != count) { reason = "ungültige Desktop-IDs"; return false; }
        var current = b.GetCurrentId();
        if (!desktops.Any(d => d.Id == current)) { reason = "aktiver Desktop nicht in der Liste"; return false; }
        var reg = VirtualDesktopRegistry.ReadIds();
        if (reg.Count > 1 && !reg.ToHashSet().SetEquals(desktops.Select(d => d.Id))) { reason = "Desktop-IDs weichen von der Registry ab"; return false; }
        reason = "";
        return true;
    }

    private static bool IsDisconnected(Exception ex) => ex is InvalidComObjectException || (uint)ex.HResult is 0x80010108 or 0x800706BA or 0x800706BF or 0x80010012 or 0x8001010E;

    private T Call<T>(string what, T fallback, Func<IVdBackend, T> func)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var b = _backend;
            if (b is null) return fallback;
            try { return _com.Invoke(() => func(b)); }
            catch (Exception ex) when (attempt == 0 && IsDisconnected(ex)) { Reinitialize(); }
            catch (Exception ex)
            {
                LastError = $"{what}: {ex.Message}";
                _log.Warn(Src, $"{what} fehlgeschlagen", ex);
                return fallback;
            }
        }
        return fallback;
    }

    public IReadOnlyList<DesktopInfo> GetDesktops()
    {
        if (_backend is not null)
        {
            var list = Call("Desktops lesen", (List<(Guid, string)>?)null, b => b.GetDesktops());
            if (list is not null) return list.Select((d, i) => new DesktopInfo { Id = d.Item1, Index = i, Name = d.Item2 }).ToList();
        }
        return VirtualDesktopRegistry.ReadIds().Select((id, i) => new DesktopInfo { Id = id, Index = i, Name = VirtualDesktopRegistry.ReadName(id) }).ToList();
    }

    public Guid? GetCurrentDesktopId()
    {
        if (_backend is not null)
        {
            var id = Call("Aktiven Desktop lesen", Guid.Empty, b => b.GetCurrentId());
            if (id != Guid.Empty) return id;
        }
        return VirtualDesktopRegistry.ReadCurrent();
    }

    public Guid? GetWindowDesktopId(long hwnd) => GetWindowDesktopIds(new[] { hwnd }).TryGetValue(hwnd, out var g) ? g : null;

    public IReadOnlyDictionary<long, Guid> GetWindowDesktopIds(IReadOnlyList<long> hwnds)
    {
        var pub = _public;
        if (pub is null || hwnds.Count == 0) return new Dictionary<long, Guid>();
        try
        {
            return _com.Invoke(() =>
            {
                var d = new Dictionary<long, Guid>();
                foreach (var h in hwnds)
                    if (pub.GetWindowDesktopId((IntPtr)h, out var id) == 0 && id != Guid.Empty) d[h] = id;
                return d;
            });
        }
        catch (Exception ex)
        {
            if (IsDisconnected(ex)) Reinitialize();
            return new Dictionary<long, Guid>();
        }
    }

    public bool IsWindowOnCurrentDesktop(long hwnd)
    {
        var pub = _public;
        if (pub is null) return true;
        try { return _com.Invoke(() => pub.IsWindowOnCurrentVirtualDesktop((IntPtr)hwnd, out var on) != 0 || on != 0); }
        catch { return true; }
    }

    private T WithView<T>(long hwnd, T fallback, Func<IntPtr, T> func)
    {
        var views = _views;
        if (views is null) return fallback;
        return _com.Invoke(() =>
        {
            if (views.GetViewForHwnd((IntPtr)hwnd, out var view) != 0 || view == IntPtr.Zero) return fallback;
            try { return func(view); }
            finally { Marshal.Release(view); }
        });
    }

    public bool IsWindowPinned(long hwnd) => GetWindowsPinned(new[] { hwnd }).TryGetValue(hwnd, out var p) && p;

    public IReadOnlyDictionary<long, bool> GetWindowsPinned(IReadOnlyList<long> hwnds)
    {
        var pinned = _pinned;
        var views = _views;
        if (pinned is null || views is null || hwnds.Count == 0) return new Dictionary<long, bool>();
        try
        {
            return _com.Invoke(() =>
            {
                var d = new Dictionary<long, bool>(hwnds.Count);
                foreach (var h in hwnds)
                {
                    if (views.GetViewForHwnd((IntPtr)h, out var view) != 0 || view == IntPtr.Zero)
                    {
                        d[h] = false;
                        continue;
                    }
                    try { d[h] = pinned.IsViewPinned(view, out var p) == 0 && p != 0; }
                    finally { Marshal.Release(view); }
                }
                return d;
            });
        }
        catch (Exception ex)
        {
            if (IsDisconnected(ex)) Reinitialize();
            return new Dictionary<long, bool>();
        }
    }

    public bool SetWindowPinned(long hwnd, bool pin)
    {
        var pinned = _pinned;
        if (pinned is null) return false;
        try { return WithView(hwnd, false, v => (pin ? pinned.PinView(v) : pinned.UnpinView(v)) == 0); }
        catch (Exception ex) { _log.Warn(Src, "Anheften fehlgeschlagen", ex); return false; }
    }

    public bool MoveWindowToDesktop(long hwnd, Guid desktopId)
    {
        NativeMethods.GetWindowThreadProcessId((IntPtr)hwnd, out var pid);
        if (pid == _ownPid && _public is { } pub)
        {
            try { if (_com.Invoke(() => pub.MoveWindowToDesktop((IntPtr)hwnd, ref desktopId)) == 0) return true; } catch { }
        }
        if (_backend is null || _views is null) return false;
        return Call("Fenster verschieben", false, b => WithView(hwnd, false, v => { b.MoveView(v, desktopId); return true; }));
    }

    public bool SwitchToDesktop(Guid desktopId) => Call("Desktop wechseln", false, b =>
    {
        // Wie MScholtes: Taskleiste kurz in den Vordergrund holen, damit Taskleisten-Symbole nicht blinken.
        var host = NativeMethods.FindWindow("XamlExplorerHostIslandWindow", null);
        if (host != IntPtr.Zero)
        {
            var hostThread = NativeMethods.GetWindowThreadProcessId(host, out _);
            var fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
            var me = NativeMethods.GetCurrentThreadId();
            if (hostThread != 0 && fgThread != 0 && fgThread != me)
            {
                NativeMethods.AttachThreadInput(hostThread, me, true);
                NativeMethods.AttachThreadInput(fgThread, me, true);
                NativeMethods.SetForegroundWindow(host);
                NativeMethods.AttachThreadInput(fgThread, me, false);
                NativeMethods.AttachThreadInput(hostThread, me, false);
            }
        }
        b.Switch(desktopId);
        if (host != IntPtr.Zero) NativeMethods.ShowWindow(host, NativeMethods.SW_MINIMIZE);
        return true;
    });

    public DesktopInfo? CreateDesktop(string? name = null)
    {
        var id = Call("Desktop anlegen", Guid.Empty, b =>
        {
            var newId = b.Create();
            if (!string.IsNullOrWhiteSpace(name)) b.Rename(newId, name!);
            return newId;
        });
        if (id == Guid.Empty) return null;
        return GetDesktops().FirstOrDefault(d => d.Id == id) ?? new DesktopInfo { Id = id, Name = name ?? "" };
    }

    public bool RemoveDesktop(Guid desktopId) => Call("Desktop schließen", false, b =>
    {
        var list = b.GetDesktops();
        if (list.Count <= 1) return false;
        var idx = list.FindIndex(d => d.Id == desktopId);
        if (idx < 0) return false;
        var fallback = idx > 0 ? list[idx - 1].Id : list[idx + 1].Id;
        b.Remove(desktopId, fallback); // Fenster wandern auf den Nachbar-Desktop – es wird nichts geschlossen
        return true;
    });

    public bool RenameDesktop(Guid desktopId, string name) => Call("Desktop umbenennen", false, b => { b.Rename(desktopId, name ?? ""); return true; });

    public bool MoveDesktop(Guid desktopId, int newIndex) => Call("Desktop verschieben", false, b =>
    {
        var count = b.GetCount();
        b.Move(desktopId, Math.Clamp(newIndex, 0, count - 1));
        return true;
    });

    public void Dispose() => _com.Dispose();
}
