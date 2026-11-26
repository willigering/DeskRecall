using DeskRecall.Core.Models;

namespace DeskRecall.Core.Geometry;

/// <summary>
/// Erkennt und berechnet Snap-Zonen (halbe Seite, Viertel, Vollbild) anhand des Arbeitsbereichs.
/// Die Zone ist ein Anteil des Monitors. Dadurch bleibt „linke Hälfte“ auch bei anderer Auflösung
/// oder Skalierung eine Hälfte, statt ein mitgerechnetes Pixelrechteck.
/// </summary>
public static class SnapLayoutMath
{
    public const int TolerancePx = 28;

    public static bool UsesWorkArea(SnapLayout snap) => snap is
        SnapLayout.LeftHalf or SnapLayout.RightHalf or SnapLayout.TopHalf or SnapLayout.BottomHalf or
        SnapLayout.TopLeft or SnapLayout.TopRight or SnapLayout.BottomLeft or SnapLayout.BottomRight;

    public static SnapLayout Classify(RectI bounds, WindowShowState state, MonitorInfo? monitor)
    {
        if (state == WindowShowState.Maximized) return SnapLayout.Maximized;
        if (monitor is null || bounds.IsEmpty) return SnapLayout.Free;
        if (Covers(bounds, monitor.Bounds)) return SnapLayout.FullScreen;
        var zone = Detect(bounds, monitor.WorkArea);
        if (zone == SnapLayout.Free && Covers(bounds, monitor.WorkArea)) return SnapLayout.Maximized;
        return zone;
    }

    public static SnapLayout Detect(RectI bounds, RectI workArea)
    {
        if (bounds.IsEmpty || workArea.IsEmpty) return SnapLayout.Free;
        var midX = workArea.Left + workArea.Width / 2;
        var midY = workArea.Top + workArea.Height / 2;
        var left = Near(bounds.Left, workArea.Left);
        var right = Near(bounds.Right, workArea.Right);
        var top = Near(bounds.Top, workArea.Top);
        var bottom = Near(bounds.Bottom, workArea.Bottom);
        var atMidXRight = Near(bounds.Right, midX);
        var atMidXLeft = Near(bounds.Left, midX);
        var atMidYBottom = Near(bounds.Bottom, midY);
        var atMidYTop = Near(bounds.Top, midY);

        if (left && top && atMidXRight && atMidYBottom) return SnapLayout.TopLeft;
        if (right && top && atMidXLeft && atMidYBottom) return SnapLayout.TopRight;
        if (left && bottom && atMidXRight && atMidYTop) return SnapLayout.BottomLeft;
        if (right && bottom && atMidXLeft && atMidYTop) return SnapLayout.BottomRight;
        if (left && top && bottom && atMidXRight) return SnapLayout.LeftHalf;
        if (right && top && bottom && atMidXLeft) return SnapLayout.RightHalf;
        if (top && left && right && atMidYBottom) return SnapLayout.TopHalf;
        if (bottom && left && right && atMidYTop) return SnapLayout.BottomHalf;
        return SnapLayout.Free;
    }

    /// <summary>Pixelrechteck der Zone auf dem genannten Monitor. Frei und Maximiert liefern default.</summary>
    public static RectI BoundsFor(SnapLayout snap, MonitorInfo monitor)
    {
        var wa = monitor.WorkArea;
        var midX = wa.Left + wa.Width / 2;
        var midY = wa.Top + wa.Height / 2;
        return snap switch
        {
            SnapLayout.LeftHalf => new RectI(wa.Left, wa.Top, midX, wa.Bottom),
            SnapLayout.RightHalf => new RectI(midX, wa.Top, wa.Right, wa.Bottom),
            SnapLayout.TopHalf => new RectI(wa.Left, wa.Top, wa.Right, midY),
            SnapLayout.BottomHalf => new RectI(wa.Left, midY, wa.Right, wa.Bottom),
            SnapLayout.TopLeft => new RectI(wa.Left, wa.Top, midX, midY),
            SnapLayout.TopRight => new RectI(midX, wa.Top, wa.Right, midY),
            SnapLayout.BottomLeft => new RectI(wa.Left, midY, midX, wa.Bottom),
            SnapLayout.BottomRight => new RectI(midX, midY, wa.Right, wa.Bottom),
            SnapLayout.FullScreen => monitor.Bounds,
            _ => default,
        };
    }

    private static bool Covers(RectI bounds, RectI area)
    {
        if (area.IsEmpty || area.Width < 80 || area.Height < 80) return false;
        var inter = bounds.Intersect(area).Area;
        return inter >= area.Area * 95 / 100 && Near(bounds.Left, area.Left) && Near(bounds.Top, area.Top)
               && Near(bounds.Right, area.Right) && Near(bounds.Bottom, area.Bottom);
    }

    private static bool Near(int a, int b) => Math.Abs(a - b) <= TolerancePx;
}
