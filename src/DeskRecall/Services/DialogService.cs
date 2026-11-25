using System.Windows;
using DeskRecall.Core.Abstractions;
using DeskRecall.Views;

namespace DeskRecall.Services;

public interface IDialogService
{
    bool Confirm(string title, string message, string ok = "OK", string cancel = "Abbrechen", bool danger = false, IEnumerable<string>? details = null);
    string? Prompt(string title, string label, string initial = "", string ok = "Speichern");
    void Info(string title, string message);
    string? OpenFile(string title, string filter);
    string? SaveFile(string title, string filter, string defaultName);
}

/// <summary>Dialoge im DeskRecall-Design. Immer auf dem UI-Thread ausgeführt.</summary>
public sealed class DialogService : IDialogService, IElevationConsent
{
    private static T OnUi<T>(Func<T> f) => Application.Current.Dispatcher.CheckAccess() ? f() : Application.Current.Dispatcher.Invoke(f);

    public bool Confirm(string title, string message, string ok = "OK", string cancel = "Abbrechen", bool danger = false, IEnumerable<string>? details = null)
        => OnUi(() => MessageDialog.ShowConfirm(title, message, ok, cancel, danger, details?.ToList()));

    public string? Prompt(string title, string label, string initial = "", string ok = "Speichern")
        => OnUi(() => MessageDialog.ShowPrompt(title, label, initial, ok));

    public void Info(string title, string message) => OnUi(() => { MessageDialog.ShowConfirm(title, message, "OK", null, false, null); return true; });

    public string? OpenFile(string title, string filter) => OnUi(() =>
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog() == true ? d.FileName : null;
    });

    public string? SaveFile(string title, string filter, string defaultName) => OnUi(() =>
    {
        var d = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return d.ShowDialog() == true ? d.FileName : null;
    });

    /// <summary>Wird von der Wiederherstellung gefragt, bevor ein Programm mit Administratorrechten (UAC) gestartet wird.</summary>
    public Task<bool> ConfirmAsync(string appName, CancellationToken ct)
        => Task.FromResult(Confirm("Administratorrechte erforderlich",
            $"„{appName}“ lief mit Administratorrechten. Soll es jetzt mit Administratorrechten gestartet werden?\n\nWindows zeigt danach eine Sicherheitsabfrage (UAC).",
            "Mit Adminrechten starten", "Überspringen"));
}
