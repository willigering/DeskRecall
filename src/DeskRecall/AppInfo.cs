using System.Reflection;
using DeskRecall.Core.Storage;

namespace DeskRecall;

public static class AppInfo
{
    public static string Version { get; } = "1.0.0";
    public static int BuildNumber { get; } = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>() is { } a
        && System.Version.TryParse(a.Version, out var v) ? v.Revision : 1;
    public static AppDataPaths Paths { get; set; } = null!;
    public static bool IsPortable { get; set; }
}
