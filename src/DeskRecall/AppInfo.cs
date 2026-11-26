using System.Reflection;
using DeskRecall.Core.Storage;
using DeskRecall.Core.Update;

namespace DeskRecall;

public static class AppInfo
{
    public static string Version { get; } = VersionFromAssembly();
    public static int BuildNumber { get; } = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>() is { } a
        && System.Version.TryParse(a.Version, out var v) ? v.Revision : 1;
    public static AppDataPaths Paths { get; set; } = null!;
    public static bool IsPortable { get; set; }

    static string VersionFromAssembly()
    {
        var asm = Assembly.GetExecutingAssembly();
        var raw = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? asm.GetName().Version?.ToString();
        return UpdateService.VersionFromAssembly(raw) is { } v ? UpdateService.Display(v) : "1.1.0";
    }
}
