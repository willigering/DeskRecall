using System.Text.Json;
using System.Text.Json.Serialization;
using DeskRecall.Core.Models;
using DeskRecall.Core.Rules;

namespace DeskRecall.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GeneralSettings General { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public RestoreSettings Restore { get; set; } = new();
    public WidgetSettings Widget { get; set; } = new();
    public DesktopSettings Desktops { get; set; } = new();
    public MonitorSettings Monitors { get; set; } = new();
    public ExclusionSettings Exclusions { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
    public List<AppRule> Rules { get; set; } = new();
    public string ActiveProfile { get; set; } = SessionProfile.DefaultName;

    /// <summary>Unbekannte Felder (z. B. aus neueren Versionen) bleiben beim Speichern erhalten.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        s.Rules.Add(new AppRule { Name = "Discord minimiert starten", ProcessName = "Discord", StartState = RuleStartState.Minimized, Enabled = false });
        s.Rules.Add(new AppRule { Name = "Steam auf Desktop 3", ProcessName = "steam", TargetDesktop = 3, Enabled = false });
        return s;
    }
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool RunInBackground { get; set; } = true;
    public bool AutoRestoreOnStartup { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BackupSettings
{
    public SaveMode Mode { get; set; } = SaveMode.OnChange;
    public bool SaveOnShutdown { get; set; } = true;
    public int HistoryCount { get; set; } = 25;
    public bool StoreWindowTitles { get; set; } = true;
    public bool CaptureDocumentPaths { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public TimeSpan? Interval => Mode switch
    {
        SaveMode.Every30Seconds => TimeSpan.FromSeconds(30),
        SaveMode.Every1Minute => TimeSpan.FromMinutes(1),
        SaveMode.Every5Minutes => TimeSpan.FromMinutes(5),
        // Bei Änderungen: zusätzlich ein Sicherheitsnetz alle 2 Minuten (falls ein Ereignis verpasst wird).
        SaveMode.OnChange => TimeSpan.FromMinutes(2),
        _ => null,
    };
}

public sealed class RestoreSettings
{
    public int StartupDelaySeconds { get; set; } = 8;
    public bool LaunchMissingApps { get; set; } = true;
    public int WindowTimeoutSeconds { get; set; } = 120;
    public bool RestoreExplorerFolders { get; set; } = true;
    public bool UseBrowserSessionRestore { get; set; } = true;
    public bool ActivateLastDesktop { get; set; } = true;
    public ElevationPolicy ElevatedApps { get; set; } = ElevationPolicy.Skip;
    public int LaunchStaggerMilliseconds { get; set; } = 700;
    /// <summary>Profil, das beim Systemstart geladen wird. Leer = letzte Sitzung.</summary>
    public string StartupProfile { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class WidgetSettings
{
    public WidgetDock Dock { get; set; } = WidgetDock.Right;
    public WidgetDisplayMode Mode { get; set; } = WidgetDisplayMode.Collapsible;
    public bool AlwaysOnTop { get; set; } = true;
    public int ExpandedWidth { get; set; } = 300;
    public int OpacityPercent { get; set; } = 85;
    public int ScalePercent { get; set; } = 100;
    public FullscreenBehavior Fullscreen { get; set; } = FullscreenBehavior.AutoHide;
    public bool Acrylic { get; set; } = true;
    public bool Animations { get; set; } = true;
    public List<Guid> CollapsedCards { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DesktopSettings
{
    public bool CreateMissingDesktops { get; set; } = true;
    public bool RestoreNamesAndOrder { get; set; } = true;
    public VirtualDesktopApiOverride ApiOverride { get; set; } = VirtualDesktopApiOverride.Auto;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MonitorSettings
{
    public bool ScaleOnResolutionChange { get; set; } = true;
    public bool CorrectOffscreenWindows { get; set; } = true;
    public MonitorFallback Fallback { get; set; } = MonitorFallback.Primary;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ExclusionSettings
{
    public List<string> ProcessNames { get; set; } = new()
    {
        "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "LockApp", "SystemSettingsBroker", "ScreenClippingHost", "Widgets", "PhoneExperienceHost",
    };
    public List<string> TitleContains { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DiagnosticsSettings
{
    public bool VerboseLogging { get; set; }
    public int LogRetentionDays { get; set; } = 14;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class UpdateSettings
{
    /// <summary>Beim Start und etwa alle 6 Stunden nach Updates suchen.</summary>
    public bool CheckAutomatically { get; set; } = true;
    /// <summary>Öffentliche GitHub-Releases-API. Leer = Standard-Feed von willigering/DeskRecall.</summary>
    public string FeedUrl { get; set; } = "";
    /// <summary>Diese Version nicht erneut anbieten (Anzeigeform 1.2.3).</summary>
    public string? SkippedVersion { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
