using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskRecall.Core.Abstractions;
using DeskRecall.Core.Apps;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Models;
using Microsoft.Win32;

namespace DeskRecall.Platform.Services;

/// <summary>
/// Startet Programme ohne Rechteerhöhung (CreateProcess). Verlangt ein Programm Administratorrechte
/// (ERROR_ELEVATION_REQUIRED), wird es nur nach ausdrücklicher Zustimmung mit „runas“ gestartet.
/// </summary>
public sealed class ProcessManager : IProcessManager
{
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ELEVATION_REQUIRED = 740, ERROR_CANCELLED = 1223;
    private readonly ILogService _log;
    private readonly AppLocator _locator;

    public ProcessManager(ILogService log)
    {
        _log = log;
        _locator = AppLocator.CreateDefault(LookupAppPath);
    }

    public IReadOnlyList<RunningProcess> GetRunningProcesses()
    {
        var list = new List<RunningProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string? path = null;
                try { path = ProcessInfo.Get(p.Id, false, _ => p.ProcessName).Path; } catch { }
                list.Add(new RunningProcess(p.Id, p.ProcessName, path));
            }
        }
        return list;
    }

    public ResolvedLaunch? Resolve(WindowRecord record) => _locator.Resolve(record);

    public LaunchResult Launch(LaunchRequest r)
    {
        if (!string.IsNullOrEmpty(r.AppUserModelId))
            return LaunchPackaged(r);

        var file = r.FileName;
        if (string.Equals(file, "explorer.exe", StringComparison.OrdinalIgnoreCase))
            file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (Path.IsPathRooted(file) && !File.Exists(file))
            return new LaunchResult(LaunchStatus.NotFound, null, $"Datei nicht gefunden: {file}");

        var psi = new ProcessStartInfo(file, r.Arguments ?? "")
        {
            UseShellExecute = false, // CreateProcess: löst nie selbst eine UAC-Abfrage aus
            WorkingDirectory = string.IsNullOrEmpty(r.WorkingDirectory) || !Directory.Exists(r.WorkingDirectory) ? "" : r.WorkingDirectory,
            WindowStyle = r.StartMinimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
        };
        try
        {
            using var p = Process.Start(psi);
            return new LaunchResult(LaunchStatus.Started, p?.Id);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_ELEVATION_REQUIRED)
        {
            if (!r.AllowElevation)
                return new LaunchResult(LaunchStatus.RequiresElevation, null, "benötigt Administratorrechte – nicht automatisch erhöht gestartet");
            try
            {
                // Nur nach Zustimmung des Benutzers; Windows zeigt zusätzlich die UAC-Abfrage.
                using var p = Process.Start(new ProcessStartInfo(file, r.Arguments ?? "") { UseShellExecute = true, Verb = "runas", WorkingDirectory = psi.WorkingDirectory });
                return new LaunchResult(LaunchStatus.Started, p?.Id);
            }
            catch (Win32Exception ex2) when (ex2.NativeErrorCode == ERROR_CANCELLED)
            {
                return new LaunchResult(LaunchStatus.RequiresElevation, null, "UAC-Abfrage abgelehnt");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
        {
            return new LaunchResult(LaunchStatus.NotFound, null, ex.Message);
        }
        catch (Exception ex)
        {
            _log.Warn("Start", $"{r.DisplayName} konnte nicht gestartet werden", ex);
            return new LaunchResult(LaunchStatus.Failed, null, ex.Message);
        }
    }

    private LaunchResult LaunchPackaged(LaunchRequest r)
    {
        try
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            var hr = mgr.ActivateApplication(r.AppUserModelId!, null, 0, out var pid);
            if (hr == 0) return new LaunchResult(LaunchStatus.Started, (int)pid);
            _log.Debug("Start", $"ActivateApplication 0x{hr:X8} – Fallback über shell:AppsFolder");
        }
        catch (Exception ex) { _log.Debug("Start", "ActivateApplication nicht verfügbar: " + ex.Message); }

        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var p = Process.Start(new ProcessStartInfo(explorer, CommandLine.Quote("shell:AppsFolder\\" + r.AppUserModelId)) { UseShellExecute = false });
            return new LaunchResult(LaunchStatus.Started, null);
        }
        catch (Exception ex) { return new LaunchResult(LaunchStatus.Failed, null, ex.Message); }
    }

    /// <summary>App Paths (HKCU/HKLM) und Steam-Registry für Programme, deren Pfad sich geändert hat.</summary>
    private static string? LookupAppPath(string exeName)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                if (key?.GetValue(null) is string p && !string.IsNullOrWhiteSpace(p)) return Environment.ExpandEnvironmentVariables(p.Trim('"'));
            }
            catch { }
        }
        if (exeName.Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamExe") is string s) return s.Replace('/', '\\');
            }
            catch { }
        }
        return null;
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }
}
