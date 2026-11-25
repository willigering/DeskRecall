using DeskRecall.Core.Logging;
using DeskRecall.Platform.VirtualDesktops;

namespace DeskRecall.Platform.Services;

/// <summary>Liest die in Explorer-Fenstern geöffneten Ordner über Shell.Application (nur Pfade).</summary>
public sealed class ExplorerFolderService : IDisposable
{
    private readonly ComThread _com = new("DeskRecall.ShellCOM");
    private readonly ILogService _log;
    private bool _warned;

    public ExplorerFolderService(ILogService log) => _log = log;

    public IReadOnlyDictionary<long, string> GetOpenFolders()
    {
        try
        {
            return _com.Invoke(() =>
            {
                var result = new Dictionary<long, string>();
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null) return result;
                dynamic shell = Activator.CreateInstance(type)!;
                try
                {
                    dynamic windows = shell.Windows();
                    int count = windows.Count;
                    for (var i = 0; i < count; i++)
                    {
                        try
                        {
                            dynamic w = windows.Item(i);
                            if (w is null) continue;
                            long hwnd = Convert.ToInt64(w.HWND);
                            if (result.ContainsKey(hwnd)) continue; // Explorer-Tabs: erster Tab pro Fenster
                            string? path = null;
                            try { path = (string?)w.Document.Folder.Self.Path; } catch { }
                            if (string.IsNullOrEmpty(path))
                            {
                                string url = w.LocationURL;
                                if (!string.IsNullOrEmpty(url) && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                                    path = new Uri(url).LocalPath;
                            }
                            if (!string.IsNullOrEmpty(path)) result[hwnd] = path!;
                        }
                        catch { }
                    }
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                return result;
            }, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            if (!_warned) { _warned = true; _log.Warn("Explorer", "Explorer-Ordner konnten nicht gelesen werden", ex); }
            return new Dictionary<long, string>();
        }
    }

    public void Dispose() => _com.Dispose();
}
