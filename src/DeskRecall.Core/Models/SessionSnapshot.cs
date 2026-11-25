namespace DeskRecall.Core.Models;

public sealed class SessionSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public SessionKind Kind { get; set; } = SessionKind.Auto;
    public string? Name { get; set; }
    public string? WindowsVersion { get; set; }
    public string? AppVersion { get; set; }

    public List<MonitorInfo> Monitors { get; set; } = new();
    public List<DesktopInfo> Desktops { get; set; } = new();
    public Guid? ActiveDesktopId { get; set; }
    public int ActiveDesktopIndex { get; set; }
    public List<WindowRecord> Windows { get; set; } = new();

    /// <summary>Inhalts-Hash (ohne Zeitstempel/Titel/HWNDs) zur Erkennung unveränderter Zustände.</summary>
    public string? ContentHash { get; set; }

    public int AppCount => Windows.Select(w => w.AppKey).Distinct().Count();
    public int DesktopCount => Math.Max(Desktops.Count, Windows.Count == 0 ? 0 : Windows.Max(w => w.DesktopIndex) + 1);
}
