using DeskRecall.Core.Geometry;
using DeskRecall.Core.Models;
using DeskRecall.Core.Settings;

namespace DeskRecall.Core.Restore;

public sealed record RestoreOptions
{
    public bool LaunchMissingApps { get; init; } = true;
    public TimeSpan WindowTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Wie lange auf Fenster bereits laufender Programme gewartet wird (z. B. aus dem Infobereich).</summary>
    public TimeSpan RunningAppGrace { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Ein neues Fenster muss so lange existieren, bevor es zugeordnet wird (Splashscreens, Größenänderungen beim Start).</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMilliseconds(700);
    /// <summary>Fenster ohne Titelleiste (Splash) werden erst nach dieser Zeit akzeptiert.</summary>
    public TimeSpan SplashGrace { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan LaunchStagger { get; init; } = TimeSpan.FromMilliseconds(700);
    public bool CreateMissingDesktops { get; init; } = true;
    public bool RestoreDesktopNamesAndOrder { get; init; } = true;
    public bool ActivateLastDesktop { get; init; } = true;
    public bool RestoreExplorerFolders { get; init; } = true;
    public bool UseBrowserSessionRestore { get; init; } = true;
    public bool ScaleOnResolutionChange { get; init; } = true;
    public bool CorrectOffscreenWindows { get; init; } = true;
    public MonitorFallback MonitorFallback { get; init; } = MonitorFallback.Primary;
    public ElevationPolicy ElevationPolicy { get; init; } = ElevationPolicy.Skip;
    public bool DryRun { get; init; }
    public IReadOnlyCollection<string> ExcludedProcesses { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> ExcludedTitles { get; init; } = Array.Empty<string>();

    public static RestoreOptions FromSettings(AppSettings s, bool dryRun = false) => new()
    {
        LaunchMissingApps = s.Restore.LaunchMissingApps,
        WindowTimeout = TimeSpan.FromSeconds(s.Restore.WindowTimeoutSeconds),
        LaunchStagger = TimeSpan.FromMilliseconds(s.Restore.LaunchStaggerMilliseconds),
        CreateMissingDesktops = s.Desktops.CreateMissingDesktops,
        RestoreDesktopNamesAndOrder = s.Desktops.RestoreNamesAndOrder,
        ActivateLastDesktop = s.Restore.ActivateLastDesktop,
        RestoreExplorerFolders = s.Restore.RestoreExplorerFolders,
        UseBrowserSessionRestore = s.Restore.UseBrowserSessionRestore,
        ScaleOnResolutionChange = s.Monitors.ScaleOnResolutionChange,
        CorrectOffscreenWindows = s.Monitors.CorrectOffscreenWindows,
        MonitorFallback = s.Monitors.Fallback,
        ElevationPolicy = s.Restore.ElevatedApps,
        DryRun = dryRun,
        ExcludedProcesses = s.Exclusions.ProcessNames.ToArray(),
        ExcludedTitles = s.Exclusions.TitleContains.ToArray(),
    };
}

public enum RestoreItemStatus { Pending, Restored, AlreadyRunning, Skipped, NotFound, NeedsElevation, TimedOut, Failed }

public sealed class RestoreItem
{
    public required WindowRecord Saved { get; init; }
    public int TargetDesktopIndex { get; set; } = -1;
    public Guid? TargetDesktopId { get; set; }
    public WindowShowState TargetState { get; set; }
    public RectI TargetBounds { get; set; }
    public MonitorInfo? TargetMonitor { get; set; }
    public bool NeverAutoStart { get; set; }

    public RestoreItemStatus Status { get; set; } = RestoreItemStatus.Pending;
    public string? Message { get; set; }
    public long? AssignedHandle { get; set; }
    public bool WasLaunched { get; set; }
    public DateTimeOffset? WaitDeadline { get; set; }

    public override string ToString() => $"{Saved.FriendlyName}: {Status} {Message}";
}

public sealed class RestoreReport
{
    public List<RestoreItem> Items { get; } = new();
    public DesktopMapping? Desktops { get; set; }
    public int Launched { get; set; }
    public TimeSpan Duration { get; set; }
    public bool Cancelled { get; set; }
    public bool DryRun { get; set; }

    public int Count(RestoreItemStatus s) => Items.Count(i => i.Status == s);
    public int PlacedCount => Count(RestoreItemStatus.Restored) + Count(RestoreItemStatus.AlreadyRunning);
    public int ProblemCount => Items.Count(i => i.Status is RestoreItemStatus.TimedOut or RestoreItemStatus.Failed or RestoreItemStatus.NotFound or RestoreItemStatus.NeedsElevation);

    public string Summary
    {
        get
        {
            if (Cancelled) return "Wiederherstellung abgebrochen.";
            var s = $"{PlacedCount} von {Items.Count} Fenstern wiederhergestellt";
            if (Launched > 0) s += $", {Launched} Programme gestartet";
            var skipped = Count(RestoreItemStatus.Skipped);
            if (skipped > 0) s += $", {skipped} übersprungen";
            if (ProblemCount > 0) s += $", {ProblemCount} nicht möglich (siehe Diagnose)";
            return s + ".";
        }
    }
}

public sealed record RestoreProgress(string Message, int Done, int Total);
