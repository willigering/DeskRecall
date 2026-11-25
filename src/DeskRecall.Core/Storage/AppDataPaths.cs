namespace DeskRecall.Core.Storage;

/// <summary>Alle Daten liegen lokal unter %LOCALAPPDATA%\DeskRecall (oder im Portable-Modus neben der EXE).</summary>
public sealed class AppDataPaths
{
    public AppDataPaths(string root)
    {
        Root = root;
        Sessions = Path.Combine(root, "sessions");
        History = Path.Combine(Sessions, "history");
        Profiles = Path.Combine(root, "profiles");
        Logs = Path.Combine(root, "logs");
        SettingsFile = Path.Combine(root, "settings.json");
        LastSessionFile = Path.Combine(Sessions, "last-session.json");
    }

    public string Root { get; }
    public string Sessions { get; }
    public string History { get; }
    public string Profiles { get; }
    public string Logs { get; }
    public string SettingsFile { get; }
    public string LastSessionFile { get; }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Sessions, History, Profiles, Logs }) Directory.CreateDirectory(d);
    }

    /// <summary>Portable-Modus: liegt eine Datei „portable.flag“ neben der EXE, werden Daten im Ordner „Daten“ daneben gespeichert.</summary>
    public static AppDataPaths Resolve(string? exeDirectory, string localAppData)
    {
        if (!string.IsNullOrEmpty(exeDirectory) && File.Exists(Path.Combine(exeDirectory, "portable.flag")))
            return new AppDataPaths(Path.Combine(exeDirectory, "Daten"));
        return new AppDataPaths(Path.Combine(localAppData, "DeskRecall"));
    }
}
