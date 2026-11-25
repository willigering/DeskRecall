using DeskRecall.Core.Models;

namespace DeskRecall.Core.Apps;

/// <summary>Wissen über bekannte Programme: Browser, Single-Instance-Apps, eigene Sitzungswiederherstellung, Startparameter.</summary>
public static class AppCatalog
{
    private static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["firefox"] = "Firefox", ["chrome"] = "Google Chrome", ["msedge"] = "Microsoft Edge", ["brave"] = "Brave",
        ["vivaldi"] = "Vivaldi", ["opera"] = "Opera", ["opera_gx"] = "Opera GX", ["floorp"] = "Floorp",
        ["librewolf"] = "LibreWolf", ["waterfox"] = "Waterfox", ["zen"] = "Zen Browser", ["chromium"] = "Chromium",
        ["thorium"] = "Thorium",
    };

    /// <summary>Programme, die bei erneutem Start nur die laufende Instanz aktivieren (nie doppelt starten).</summary>
    private static readonly HashSet<string> SingleInstance = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "discord", "discordptb", "discordcanary", "spotify", "ms-teams", "teams", "slack",
        "telegram", "whatsapp", "signal", "epicgameslauncher", "battle.net", "eadesktop", "galaxyclient", "upc",
        "obs64", "teamspeak", "ts3client_win64", "thunderbird", "outlook", "olk", "onenote", "1password", "bitwarden",
        "keepassxc", "nvidia app", "nvcontainer", "msiafterburner", "rtss", "lghub", "icue", "wallpaper64",
    };

    /// <summary>Programme mit eigener Sitzungswiederherstellung – einmal ohne Argumente starten, Fenster öffnen sie selbst.</summary>
    private static readonly HashSet<string> NativeSession = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "cursor", "windowsterminal", "notepad", "notepad++", "obsidian", "rider64", "idea64", "pycharm64",
        "webstorm64", "clion64", "sublime_text", "zed",
    };

    /// <summary>System-/Shell-Prozesse, die nie gestartet werden.</summary>
    private static readonly HashSet<string> NeverLaunch = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "explorer", "dwm", "sihost", "taskmgr", "systemsettings", "lockapp", "searchhost",
        "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "deskrecall", "consent", "werfault",
        "rundll32", "dllhost", "conhost", "openwith", "pickerhost",
    };

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["resolve"] = "DaVinci Resolve", ["photoshop"] = "Photoshop", ["devenv"] = "Visual Studio", ["code"] = "Visual Studio Code",
        ["steam"] = "Steam", ["steamwebhelper"] = "Steam", ["discord"] = "Discord", ["epicgameslauncher"] = "Epic Games",
        ["explorer"] = "Explorer", ["windowsterminal"] = "Terminal", ["spotify"] = "Spotify", ["obs64"] = "OBS Studio",
        ["winword"] = "Word", ["excel"] = "Excel", ["powerpnt"] = "PowerPoint", ["afterfx"] = "After Effects",
        ["premiere pro"] = "Premiere Pro", ["illustrator"] = "Illustrator", ["blender"] = "Blender", ["ms-teams"] = "Teams",
    };

    public static bool IsBrowser(string processName) => Browsers.ContainsKey(AppIdentity.NormalizeProcessName(processName));
    public static bool IsSingleInstance(string processName) => SingleInstance.Contains(AppIdentity.NormalizeProcessName(processName));
    public static bool HasNativeSessionRestore(string processName) => NativeSession.Contains(AppIdentity.NormalizeProcessName(processName));
    public static bool IsNeverLaunch(string processName) => NeverLaunch.Contains(AppIdentity.NormalizeProcessName(processName));

    public static string? FriendlyName(string processName)
    {
        var n = AppIdentity.NormalizeProcessName(processName);
        if (Browsers.TryGetValue(n, out var b)) return b;
        return FriendlyNames.TryGetValue(n, out var f) ? f : null;
    }

    public static AppKind Classify(string processName, string windowClass, string? aumid)
    {
        var n = AppIdentity.NormalizeProcessName(processName);
        if (n == "explorer" && windowClass is "CabinetWClass" or "ExploreWClass") return AppKind.Explorer;
        if (Browsers.ContainsKey(n)) return AppKind.Browser;
        if (n == "applicationframehost" || windowClass == "ApplicationFrameWindow") return AppKind.Uwp;
        if (SingleInstance.Contains(n)) return AppKind.SingleInstance;
        if (NativeSession.Contains(n)) return AppKind.NativeSession;
        return AppKind.Normal;
    }

    /// <summary>Zusätzliche Startargumente für „minimiert starten“, sofern das Programm sie unterstützt.</summary>
    public static string? MinimizedStartArguments(string processName) => AppIdentity.NormalizeProcessName(processName) switch
    {
        "steam" => "-silent",
        "discord" or "discordptb" or "discordcanary" => "--start-minimized",
        "spotify" => "--minimized",
        "ms-teams" => "--system-initiated",
        _ => null,
    };
}
