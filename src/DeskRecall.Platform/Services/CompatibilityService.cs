using System.Security.Principal;
using DeskRecall.Core.Compatibility;
using Microsoft.Win32;

namespace DeskRecall.Platform.Services;

/// <summary>Erkennt Windows-Version/Build/UBR und Rechte des Prozesses – Grundlage für die Kompatibilitätsschicht.</summary>
public sealed class CompatibilityService
{
    public CompatibilityService()
    {
        var v = Environment.OSVersion.Version;
        var ubr = 0;
        string? display = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("UBR") is int u) ubr = u;
            display = key?.GetValue("DisplayVersion") as string;
        }
        catch { }
        Build = new WindowsBuild(v.Major, v.Minor, v.Build, ubr);
        DisplayVersion = display;
        try { using var id = WindowsIdentity.GetCurrent(); IsElevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
    }

    public WindowsBuild Build { get; }
    public string? DisplayVersion { get; }
    public bool IsElevated { get; }
    public bool IsSupported => Build.IsWindows11;

    public string Description => $"{Build.FriendlyName}{(DisplayVersion is null ? "" : $" ({DisplayVersion})")}, Build {Build.Build}.{Build.Revision}";
}
