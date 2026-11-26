namespace DeskRecall.Core.Apps;

/// <summary>Liefert installierte Programme (Startmenü und nach Möglichkeit UWP) für den Programm-Wähler.</summary>
public interface IInstalledAppSource
{
    Task<IReadOnlyList<InstalledAppEntry>> ListAsync(CancellationToken ct = default);
    InstalledAppEntry? ResolveShortcut(string path);
}
