using DeskRecall.Core.Logging;
using Microsoft.Win32;

namespace DeskRecall.Platform.Services;

/// <summary>Autostart über HKCU\…\Run – ohne Administratorrechte, nur für den aktuellen Benutzer.</summary>
public sealed class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskRecall";
    public const string AutostartArgument = "--autostart";
    private readonly ILogService _log;

    public AutostartService(ILogService log) => _log = log;

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskRecall.exe");

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && s.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled) key.SetValue(ValueName, $"\"{ExecutablePath}\" {AutostartArgument}");
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { _log.Warn("Autostart", "Autostart konnte nicht geändert werden", ex); }
    }
}
