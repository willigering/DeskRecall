using DeskRecall.Core.Apps;
using DeskRecall.Core.Geometry;
using DeskRecall.Core.Models;

namespace DeskRecall.Core.Abstractions;

/// <summary>
/// Gekapselte, austauschbare Schicht für virtuelle Desktops. Die Windows-Implementierung wählt je nach
/// Build den passenden COM-Schnittstellensatz; schlägt COM fehl, arbeitet sie eingeschränkt weiter.
/// </summary>
public interface IVirtualDesktopService
{
    /// <summary>Desktops können gelesen werden (mindestens über Registry/öffentliche API).</summary>
    bool IsAvailable { get; }
    /// <summary>Desktops können verwaltet und fremde Fenster verschoben werden (interne API aktiv).</summary>
    bool CanManage { get; }
    string BackendDescription { get; }
    string? LastError { get; }

    IReadOnlyList<DesktopInfo> GetDesktops();
    Guid? GetCurrentDesktopId();
    Guid? GetWindowDesktopId(long hwnd);
    bool IsWindowPinned(long hwnd);

    bool MoveWindowToDesktop(long hwnd, Guid desktopId);
    bool SetWindowPinned(long hwnd, bool pinned);
    bool SwitchToDesktop(Guid desktopId);
    DesktopInfo? CreateDesktop(string? name = null);
    bool RemoveDesktop(Guid desktopId);
    bool RenameDesktop(Guid desktopId, string name);
    bool MoveDesktop(Guid desktopId, int newIndex);

    void Reinitialize();
}

public sealed record PlacementTarget(RectI NormalBounds, WindowShowState State, MonitorInfo? Monitor, bool Activate = false);

public interface IWindowManager
{
    /// <summary>Alle für den Benutzer sichtbaren Programmfenster (Alt-Tab-Kriterien), auf allen Desktops.</summary>
    IReadOnlyList<WindowRecord> EnumerateAppWindows();
    bool IsAlive(long hwnd);
    RectI? GetWindowBounds(long hwnd);
    WindowShowState? GetState(long hwnd);
    bool ApplyPlacement(long hwnd, PlacementTarget target);
    bool BringToFront(long hwnd);
    bool Minimize(long hwnd);
    /// <summary>Bittet das Fenster höflich ums Schließen (WM_CLOSE) – nie hart beenden.</summary>
    bool Close(long hwnd);
}

public sealed record RunningProcess(int Id, string Name, string? Path);

public sealed record LaunchRequest(
    string FileName,
    string? Arguments,
    string? WorkingDirectory,
    string? AppUserModelId,
    bool StartMinimized,
    bool AllowElevation,
    string DisplayName);

public enum LaunchStatus { Started, RequiresElevation, NotFound, Failed }

public sealed record LaunchResult(LaunchStatus Status, int? ProcessId = null, string? Message = null);

public interface IProcessManager
{
    IReadOnlyList<RunningProcess> GetRunningProcesses();
    LaunchResult Launch(LaunchRequest request);
    ResolvedLaunch? Resolve(WindowRecord record);
}

public interface IMonitorManager
{
    IReadOnlyList<MonitorInfo> GetMonitors();
}

/// <summary>Fragt den Benutzer, bevor ein Programm mit Administratorrechten gestartet wird.</summary>
public interface IElevationConsent
{
    Task<bool> ConfirmAsync(string appName, CancellationToken ct);
}

public interface IClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
