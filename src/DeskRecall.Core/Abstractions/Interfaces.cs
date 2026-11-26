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
    /// <summary>Anheftstatus mehrerer Fenster in einem Durchlauf (eine COM-Runde).</summary>
    IReadOnlyDictionary<long, bool> GetWindowsPinned(IReadOnlyList<long> hwnds);

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
    /// <summary>Alle für den Benutzer sichtbaren Programmfenster (Alt-Tab-Kriterien), auf allen Desktops.
    /// <paramref name="detailed"/> liest Dokumentpfade, Explorer-Ordner und Dateibeschreibungen (für Sicherungen).
    /// Für die laufende Oberfläche reicht <c>false</c>.</summary>
    IReadOnlyList<WindowRecord> EnumerateAppWindows(bool detailed = true);
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

/// <summary>
/// Fragt, ob ein bereits laufender Browser geöffnet werden soll, damit der Benutzer sich dort anmeldet.
/// Das Passwort bleibt im Browser. DeskRecall sieht es nicht.
/// </summary>
public interface IBrowserSignInPrompt
{
    Task<bool> ConfirmAsync(string browserName, CancellationToken ct);
}

public interface IClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
    /// <summary>Plant einen einmaligen Aufruf nach <paramref name="delay"/>. Das zurückgegebene Objekt sagt den Termin ab.</summary>
    IDisposable Schedule(TimeSpan delay, Action action);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var due = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        return new Timer(_ => { try { action(); } catch { } }, null, due, Timeout.InfiniteTimeSpan);
    }
}
