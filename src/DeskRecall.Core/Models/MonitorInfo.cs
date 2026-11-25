using DeskRecall.Core.Geometry;

namespace DeskRecall.Core.Models;

public sealed class MonitorInfo
{
    /// <summary>Stabile Identität (Gerätepfad des Monitors, z. B. MONITOR\GSM5B7F\{…}\0001), sonst Gerätename.</summary>
    public string Id { get; set; } = "";
    /// <summary>GDI-Gerätename, z. B. \\.\DISPLAY1 (kann sich nach Neustart ändern).</summary>
    public string DeviceName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public RectI Bounds { get; set; }
    public RectI WorkArea { get; set; }
    public int Dpi { get; set; } = 96;
    public bool IsPrimary { get; set; }

    public int ScalePercent => (int)Math.Round(Dpi * 100.0 / 96.0);
    public override string ToString() => $"{(string.IsNullOrEmpty(FriendlyName) ? DeviceName : FriendlyName)} {Bounds}";
}
