namespace DeskRecall.Core.Models;

public enum WindowShowState { Normal, Minimized, Maximized }

/// <summary>
/// Wie das Fenster auf seinem Monitor liegt. Halbe und Viertel sind Snap-Zonen des Arbeitsbereichs,
/// Vollbild deckt den ganzen Monitor ab (einschließlich Taskleiste), frei ist eine beliebige Position.
/// </summary>
public enum SnapLayout
{
    Free,
    LeftHalf,
    RightHalf,
    TopHalf,
    BottomHalf,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Maximized,
    FullScreen,
}

public enum SessionKind { Auto, Manual, Shutdown, Profile, Imported }

public enum AppKind { Normal, Browser, Explorer, Uwp, SingleInstance, NativeSession }

public enum SaveMode { OnChange, Every30Seconds, Every1Minute, Every5Minutes, ManualOnly }

public enum WidgetDock { Right, Left }

public enum WidgetDisplayMode { AlwaysExpanded, Collapsible, AutoHide, TrayOnly }

public enum FullscreenBehavior { AutoHide, KeepVisible }

public enum ElevationPolicy { Skip, Ask }

public enum MonitorFallback { Primary, Nearest }

public enum VirtualDesktopApiOverride { Auto, Win11Legacy, Win11_22H2, Win11_23H2, Win11_24H2, PublicApiOnly }
