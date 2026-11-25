using DeskRecall.Core.Models;

namespace DeskRecall.Core.Geometry;

/// <summary>Holt Fenster zurück, die (teilweise) außerhalb aller sichtbaren Arbeitsbereiche liegen.</summary>
public static class PlacementCorrector
{
    /// <summary>Mindestens so viel der Titelleiste muss sichtbar sein, damit das Fenster greifbar bleibt.</summary>
    public const int MinVisibleTitleWidth = 120;
    public const int TitleBarHeight = 32;

    public static bool IsReachable(RectI rect, IReadOnlyList<MonitorInfo> monitors)
    {
        if (rect.IsEmpty) return false;
        var titleBar = new RectI(rect.Left, rect.Top, rect.Right, rect.Top + Math.Min(TitleBarHeight, rect.Height));
        foreach (var m in monitors)
        {
            var inter = titleBar.Intersect(m.WorkArea);
            if (!inter.IsEmpty && inter.Width >= Math.Min(MinVisibleTitleWidth, rect.Width) && inter.Height >= Math.Min(12, titleBar.Height))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Liefert ein garantiert erreichbares Rechteck: ist das Fenster größer als der Arbeitsbereich,
    /// wird es verkleinert; liegt es außerhalb, wird es in den Arbeitsbereich geschoben.
    /// </summary>
    public static RectI Correct(RectI rect, IReadOnlyList<MonitorInfo> monitors, MonitorInfo? preferred = null)
    {
        if (monitors.Count == 0) return rect;
        if (!rect.IsEmpty && IsReachable(rect, monitors) && (preferred is null || preferred.WorkArea.Intersect(rect).Area > 0))
        {
            var host = MonitorRemapper.MonitorForRect(rect, monitors)!;
            return FitSize(rect, host.WorkArea);
        }

        var target = preferred ?? MonitorRemapper.MonitorForRect(rect.IsEmpty ? RectI.FromSize(0, 0, 1, 1) : rect, monitors)!;
        var wa = target.WorkArea;
        var w = rect.IsEmpty ? Math.Min(1024, wa.Width) : rect.Width;
        var h = rect.IsEmpty ? Math.Min(720, wa.Height) : rect.Height;
        var r = FitSize(RectI.FromSize(rect.Left, rect.Top, w, h), wa);
        var left = Math.Clamp(r.Left, wa.Left, wa.Right - r.Width);
        var top = Math.Clamp(r.Top, wa.Top, wa.Bottom - r.Height);
        return RectI.FromSize(left, top, r.Width, r.Height);
    }

    private static RectI FitSize(RectI r, RectI wa)
    {
        // Kleine Überstände (unsichtbare DWM-Rahmen ~7 px) sind normal und werden toleriert.
        const int tolerance = 16;
        var w = r.Width; var h = r.Height;
        if (w > wa.Width + tolerance) w = wa.Width;
        if (h > wa.Height + tolerance) h = wa.Height;
        if (w == r.Width && h == r.Height) return r;
        var left = Math.Clamp(r.Left, wa.Left, wa.Right - w);
        var top = Math.Clamp(r.Top, wa.Top, wa.Bottom - h);
        return RectI.FromSize(left, top, w, h);
    }
}
