using DeskRecall.Core.Models;

namespace DeskRecall.Core.Compatibility;

/// <summary>
/// Die internen Virtual-Desktop-COM-Schnittstellen von Windows ändern sich mit Builds (GUIDs und
/// Methodenreihenfolge). Grundlage der Sätze: MScholtes/VirtualDesktop (MIT-Lizenz).
/// </summary>
public enum VirtualDesktopApiSet
{
    /// <summary>Nur dokumentierte API (IVirtualDesktopManager): Lesen ja, Verschieben fremder Fenster nein.</summary>
    PublicOnly,
    /// <summary>Windows 11 21H2 (22000) bis 22H2 vor 22621.2215 – Methoden mit HMONITOR-Parameter.</summary>
    Win11Legacy,
    /// <summary>Windows 11 22H2/23H2 ab 22621.2215 bis vor .3085.</summary>
    Win11_22H2,
    /// <summary>Windows 11 22H2/23H2 ab 22621/22631.3085.</summary>
    Win11_23H2,
    /// <summary>Windows 11 24H2 / 25H2 (26100, 26200 …) – zusätzliche Methode SwitchDesktopAndMoveForegroundView.</summary>
    Win11_24H2,
}

public readonly record struct WindowsBuild(int Major, int Minor, int Build, int Revision)
{
    public bool IsWindows11 => Major == 10 && Build >= 22000;
    public override string ToString() => $"{Major}.{Minor}.{Build}.{Revision}";

    public string FriendlyName => Major != 10 ? "Windows (nicht unterstützt)" : Build switch
    {
        >= 26200 => "Windows 11 25H2",
        >= 26100 => "Windows 11 24H2",
        >= 22631 => "Windows 11 23H2",
        >= 22621 => "Windows 11 22H2",
        >= 22000 => "Windows 11 21H2",
        >= 10240 => "Windows 10",
        _ => "Windows (nicht unterstützt)",
    };
}

public static class VirtualDesktopApiResolver
{
    public static VirtualDesktopApiSet Resolve(WindowsBuild v, VirtualDesktopApiOverride overrideSet = VirtualDesktopApiOverride.Auto)
    {
        switch (overrideSet)
        {
            case VirtualDesktopApiOverride.PublicApiOnly: return VirtualDesktopApiSet.PublicOnly;
            case VirtualDesktopApiOverride.Win11Legacy: return VirtualDesktopApiSet.Win11Legacy;
            case VirtualDesktopApiOverride.Win11_22H2: return VirtualDesktopApiSet.Win11_22H2;
            case VirtualDesktopApiOverride.Win11_23H2: return VirtualDesktopApiSet.Win11_23H2;
            case VirtualDesktopApiOverride.Win11_24H2: return VirtualDesktopApiSet.Win11_24H2;
        }

        if (v.Major != 10 || v.Build < 22000) return VirtualDesktopApiSet.PublicOnly; // Windows 10 & älter: nur Lesen
        if (v.Build >= 26100) return VirtualDesktopApiSet.Win11_24H2;
        if (v.Build >= 22621)
        {
            if (v.Revision >= 3085) return VirtualDesktopApiSet.Win11_23H2;
            if (v.Revision >= 2215) return VirtualDesktopApiSet.Win11_22H2;
            // 22631 gab es erst nach 22621.2215 – auch mit kleiner UBR den 22H2-Satz verwenden
            return v.Build >= 22631 ? VirtualDesktopApiSet.Win11_22H2 : VirtualDesktopApiSet.Win11Legacy;
        }
        return VirtualDesktopApiSet.Win11Legacy; // 22000 … 22620
    }

    /// <summary>
    /// Alternativen, falls der gewählte Satz bei der Selbstprüfung scheitert. Nur Sätze mit anderer
    /// Schnittstellen-GUID werden probiert – eine falsche GUID wird von Windows sicher abgelehnt
    /// (E_NOINTERFACE). 23H2 und 24H2 teilen sich die GUID, daher nie automatisch gegeneinander tauschen.
    /// </summary>
    public static IReadOnlyList<VirtualDesktopApiSet> FallbackOrder(VirtualDesktopApiSet primary) => primary switch
    {
        VirtualDesktopApiSet.Win11_24H2 => new[] { VirtualDesktopApiSet.Win11_24H2, VirtualDesktopApiSet.Win11_22H2, VirtualDesktopApiSet.Win11Legacy },
        VirtualDesktopApiSet.Win11_23H2 => new[] { VirtualDesktopApiSet.Win11_23H2, VirtualDesktopApiSet.Win11_22H2, VirtualDesktopApiSet.Win11Legacy },
        VirtualDesktopApiSet.Win11_22H2 => new[] { VirtualDesktopApiSet.Win11_22H2, VirtualDesktopApiSet.Win11_23H2, VirtualDesktopApiSet.Win11Legacy },
        VirtualDesktopApiSet.Win11Legacy => new[] { VirtualDesktopApiSet.Win11Legacy, VirtualDesktopApiSet.Win11_22H2, VirtualDesktopApiSet.Win11_23H2 },
        _ => Array.Empty<VirtualDesktopApiSet>(),
    };

    public static string Describe(VirtualDesktopApiSet set) => set switch
    {
        VirtualDesktopApiSet.Win11Legacy => "Windows 11 21H2/22H2 (vor 22621.2215)",
        VirtualDesktopApiSet.Win11_22H2 => "Windows 11 22H2 (ab 22621.2215)",
        VirtualDesktopApiSet.Win11_23H2 => "Windows 11 22H2/23H2 (ab Build .3085)",
        VirtualDesktopApiSet.Win11_24H2 => "Windows 11 24H2/25H2",
        _ => "Nur öffentliche API (eingeschränkt)",
    };
}
