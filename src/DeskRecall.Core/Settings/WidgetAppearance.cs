namespace DeskRecall.Core.Settings;

public enum WidgetWidthPreset { Narrow = 280, Normal = 300, Wide = 320 }

/// <summary>Stufenbreite und Hintergrunddeckkraft des Widgets – unabhängig von WPF testbar.</summary>
public static class WidgetAppearance
{
    public const int MinOpacity = 30;
    public const int MaxOpacity = 100;
    public static readonly int[] WidthSteps = { 280, 300, 320 };

    public static WidgetWidthPreset FromPixels(int pixels)
    {
        var best = WidgetWidthPreset.Normal;
        var bestDist = int.MaxValue;
        foreach (var step in new[] { WidgetWidthPreset.Narrow, WidgetWidthPreset.Normal, WidgetWidthPreset.Wide })
        {
            var d = Math.Abs(pixels - (int)step);
            if (d <= bestDist) { bestDist = d; best = step; } // bei Gleichstand die höhere Stufe (290 → Normal, 310 → Breit)
        }
        return best;
    }

    public static int ToPixels(WidgetWidthPreset preset) => (int)preset;

    public static string Label(WidgetWidthPreset preset) => preset switch
    {
        WidgetWidthPreset.Narrow => "Schmal",
        WidgetWidthPreset.Wide => "Breit",
        _ => "Normal",
    };

    /// <summary>Alpha der WPF-Hintergrundfläche (Text bleibt immer voll deckend). 30 % ist klar durchscheinend, 100 % festes Anthrazit.</summary>
    public static byte PanelAlpha(int opacityPercent, bool acrylic)
    {
        var p = Math.Clamp(opacityPercent, MinOpacity, MaxOpacity) / 100.0;
        // Ohne Acryl trägt die Fläche die Deckkraft allein. Mit Acryl bleibt sie leichter, damit die Unschärfe sichtbar bleibt.
        var alpha = acrylic ? Math.Max(0.18, p * 0.55) : p;
        return (byte)Math.Round(Math.Clamp(alpha, 0.18, 1.0) * 255);
    }

    /// <summary>Acryl-Tönung im Format AABBGGRR (Anthrazit #1E1B26), Alpha folgt dem Regler linear.</summary>
    public static uint AcrylicTintAbgr(int opacityPercent)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacityPercent, MinOpacity, MaxOpacity) / 100.0 * 255);
        // Etwas höher als die WPF-Fläche, damit der Kontrast für den Text stimmt, aber der Regler klar wirkt.
        alpha = (byte)Math.Clamp(alpha + 16, 0x50, 0xFF);
        return ((uint)alpha << 24) | (0x26u << 16) | (0x1Bu << 8) | 0x1Eu;
    }
}
