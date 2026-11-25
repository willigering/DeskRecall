using System.Security.Cryptography;
using System.Text;
using DeskRecall.Core.Models;

namespace DeskRecall.Core.Storage;

/// <summary>
/// Berechnet einen Inhalts-Hash, der nur die wiederherstellbare Anordnung abbildet.
/// Zeitstempel, HWNDs, Prozess-IDs und Fenstertitel (ändern sich ständig, z. B. Browser-Tabs) zählen nicht.
/// </summary>
public static class SessionHasher
{
    public static string Compute(SessionSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append("v1|active:").Append(s.ActiveDesktopIndex).Append('\n');
        foreach (var d in s.Desktops.OrderBy(d => d.Index))
            sb.Append("d:").Append(d.Index).Append(':').Append(d.Name).Append('\n');
        foreach (var m in s.Monitors.OrderBy(m => m.Id, StringComparer.Ordinal))
            sb.Append("m:").Append(m.Id).Append(':').Append(m.Bounds.ToString()).Append('\n');

        var lines = s.Windows.Select(w => string.Join('|',
                AppIdentity.NormalizeProcessName(w.ProcessName),
                (w.ExecutablePath ?? "").ToLowerInvariant(),
                w.AppUserModelId ?? "",
                w.WindowClass,
                w.DesktopIndex,
                w.IsPinned ? 1 : 0,
                w.State,
                w.NormalBounds.ToString(),
                w.MonitorId ?? "",
                (w.ExplorerFolderPath ?? "").ToLowerInvariant(),
                (w.DocumentPath ?? "").ToLowerInvariant()))
            .OrderBy(x => x, StringComparer.Ordinal);
        foreach (var l in lines) sb.Append("w:").Append(l).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }
}
