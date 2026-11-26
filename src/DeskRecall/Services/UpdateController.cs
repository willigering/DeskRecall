using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DeskRecall.Core.Logging;
using DeskRecall.Core.Settings;
using DeskRecall.Core.Update;
using DeskRecall.ViewModels;

namespace DeskRecall.Services;

/// <summary>
/// Prüft GitHub-Releases (öffentlicher Feed, ohne Token), zeigt einen ruhigen Hinweis
/// und spielt das passende Paket ein: laufende exe → .old, neue Datei an denselben Ort, Neustart.
/// Benutzerdaten bleiben unberührt.
/// </summary>
public sealed class UpdateController : ObservableObject, IDisposable
{
    public const string Src = "Aktualisierung";

    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _ui;
    private bool _busy;
    private bool _started;
    private string? _notice;
    private string? _status = "Noch nicht geprüft.";
    private double _progress;
    private ReleaseInfo? _available;
    private string? _balloonedVersion;
    private bool _skipNextAuto;

    public UpdateController(ISettingsService settings, ILogService log, IDialogService dialogs)
    {
        _settings = settings;
        _log = log;
        _dialogs = dialogs;
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = UpdateService.DefaultCheckInterval };
        _timer.Tick += async (_, _) => await CheckAsync(manual: false);
        CheckNowCommand = new AsyncRelayCommand(() => CheckAsync(true), () => !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => HasUpdate && !IsBusy);
        DismissCommand = new RelayCommand(Dismiss);
        SkipCommand = new RelayCommand(Skip);
        OpenPageCommand = new RelayCommand(OpenReleasePage);
    }

    public AsyncRelayCommand CheckNowCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand DismissCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand OpenPageCommand { get; }

    /// <summary>Wird vor dem Start der neuen exe aufgerufen (Mutex freigeben).</summary>
    public Action? PrepareRelaunch { get; set; }
    /// <summary>Beendet die laufende Instanz nach erfolgreichem Start der neuen exe.</summary>
    public Action? ShutdownApp { get; set; }
    /// <summary>Ruhiger Hinweis im Infobereich (einmal je Version).</summary>
    public event Action<string, string>? TrayNotice;

    public bool HasUpdate => _available is not null;
    public string? NoticeText => _notice;
    public string StatusText => _status ?? "";
    public bool IsBusy => _busy;
    public double Progress => _progress;
    public bool IsIndeterminate => _busy && _progress < 0;
    public string? AvailableVersion => _available is null ? null : UpdateService.Display(_available.Version);
    public Version? LocalVersion => UpdateService.VersionFromAssembly(AppInfo.Version) ?? new Version(1, 1, 0);

    public void Start(IReadOnlyList<string> args)
    {
        if (_started) return;
        _started = true;

        var exe = Environment.ProcessPath;
        var dir = exe is null ? null : Path.GetDirectoryName(exe);
        if (dir is not null)
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 20 && UpdateService.DeleteOldFiles(dir) > 0; i++)
                    await Task.Delay(500);
            });

        var after = UpdateService.AfterUpdateVersion(args);
        if (after is not null)
        {
            var v = string.IsNullOrEmpty(after) ? UpdateService.Display(LocalVersion ?? new Version(1, 0, 0)) : after;
            SetStatus($"Aktualisiert auf {v}.");
            _log.Info(Src, $"Erfolgreich aktualisiert auf {v}.");
            TrayNotice?.Invoke("DeskRecall aktualisiert", $"DeskRecall {v} ist eingerichtet. Ihre Sitzungsdaten sind unverändert.");
            _skipNextAuto = true;
        }

        if (_settings.Current.Update.CheckAutomatically)
            _timer.Start();

        _settings.Changed += s =>
        {
            if (s.Update.CheckAutomatically) _timer.Start();
            else _timer.Stop();
        };

        if (_settings.Current.Update.CheckAutomatically && !_skipNextAuto)
            _ = _ui.InvokeAsync(async () => { await Task.Delay(2500); await CheckAsync(manual: false); });
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            SetStatus(manual ? "Suche nach Updates …" : StatusText);
            var channel = UpdateService.DetectChannel(
                Path.GetDirectoryName(Environment.ProcessPath),
                AppInfo.IsPortable);
            UpdateCheckResult result;
            try
            {
                using var client = UpdateService.CreateClient(AppInfo.Version);
                result = await Task.Run(() => UpdateService.FetchLatestAsync(
                    client, TimeSpan.FromSeconds(8),
                    _settings.Current.Update.FeedUrl,
                    channel)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.Warn(Src, "Update-Prüfung fehlgeschlagen", ex);
                result = new UpdateCheckResult(UpdateCheckStatus.Failed, null, "Update-Prüfung fehlgeschlagen: " + ex.Message);
            }

            if (result.Status is UpdateCheckStatus.Unavailable)
            {
                _log.Warn(Src, result.Message ?? "Update-Feed nicht erreichbar.");
                SetAvailable(null);
                SetStatus(result.Message ?? "Update-Feed nicht erreichbar.");
                if (manual)
                    _dialogs.Info("Keine Verbindung zum Update-Feed",
                        (result.Message ?? "Der Feed ist nicht öffentlich erreichbar.") +
                        "\n\nDeskRecall sendet kein Zugriffstoken. Sobald die Releases öffentlich lesbar sind (oder eine öffentliche Feed-URL eingetragen ist), funktioniert die Suche.");
                return;
            }

            if (result.Status is UpdateCheckStatus.Failed or UpdateCheckStatus.NoRelease)
            {
                if (result.Status == UpdateCheckStatus.Failed)
                    _log.Warn(Src, result.Message ?? "Update-Prüfung fehlgeschlagen.");
                SetAvailable(null);
                SetStatus(result.Status == UpdateCheckStatus.NoRelease
                    ? "Kein stabiles Release gefunden."
                    : (result.Message ?? "Prüfung fehlgeschlagen."));
                if (manual)
                    _dialogs.Info("Aktualisierung", result.Message ?? "Ob eine neue Version vorliegt, konnte gerade nicht geprüft werden.");
                return;
            }

            var release = result.Release!;
            var local = LocalVersion;
            if (!UpdateService.ShouldOffer(release, local, _settings.Current.Update.SkippedVersion, manual))
            {
                SetAvailable(null);
                SetStatus($"Aktuell – Version {UpdateService.Display(local ?? new Version(0, 0, 0))}.");
                if (manual)
                    _dialogs.Info("Alles aktuell",
                        $"Sie verwenden DeskRecall {UpdateService.Display(local ?? new Version(0, 0, 0))}, das ist die neueste Version.");
                return;
            }

            SetAvailable(release);
            SetStatus($"Version {UpdateService.Display(release.Version)} ist verfügbar.");
            _log.Info(Src, $"Neue Version {UpdateService.Display(release.Version)} gefunden (lokal {UpdateService.Display(local ?? new Version(0, 0, 0))}, Kanal {channel}).");
            if (!manual && _balloonedVersion != UpdateService.Display(release.Version))
            {
                _balloonedVersion = UpdateService.Display(release.Version);
                TrayNotice?.Invoke("Update verfügbar",
                    $"DeskRecall {UpdateService.Display(release.Version)} ist bereit. Ihre Daten bleiben beim Aktualisieren erhalten.");
            }
        }
        finally { SetBusy(false); }
    }

    async Task InstallAsync()
    {
        var r = _available;
        if (r is null) return;
        if (string.IsNullOrEmpty(r.AssetUrl) || string.IsNullOrEmpty(r.AssetName))
        {
            _dialogs.Info("Kein Download gefunden",
                $"Im Release {UpdateService.Display(r.Version)} fehlt das passende Paket. Sie können die Datei auf der Release-Seite herunterladen.");
            OpenReleasePage();
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null || !Path.GetFileName(exe).EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Info("Update nicht möglich", "Automatisch aktualisieren geht nur mit der DeskRecall.exe aus dem Download.");
            return;
        }
        var folder = Path.GetDirectoryName(exe)!;
        if (!UpdateService.IsWritable(folder))
        {
            _dialogs.Info("Keine Schreibrechte",
                $"DeskRecall darf im Ordner „{folder}“ nichts ändern (z. B. unter „Programme“ ohne Administratorrechte). Entpacken Sie die portable Version in einen eigenen Ordner oder laden Sie das Update von der Release-Seite.");
            return;
        }

        SetBusy(true);
        var neu = UpdateService.Display(r.Version);
        var basis = Path.Combine(Path.GetTempPath(), "DeskRecall-Update-" + neu);
        var zip = basis + ".zip";
        try
        {
            SetStatus("Lade herunter …");
            SetProgress(-1);
            using var client = UpdateService.CreateClient(AppInfo.Version);
            var mb = r.AssetSize / 1048576.0;
            var progress = new Progress<double>(p =>
            {
                SetProgress(p);
                SetStatus(p < 0 ? "Lade herunter …"
                    : mb > 0 ? $"Lade herunter … {p * 100:0} % ({p * mb:0.0} von {mb:0.0} MB)"
                    : $"Lade herunter … {p * 100:0} %");
            });
            await Task.Run(() => UpdateService.DownloadAsync(client, r.AssetUrl!, zip, progress)).ConfigureAwait(true);

            var sha = r.Sha256;
            if (sha is null && r.Sha256AssetUrl is not null)
            {
                SetStatus("Lade Prüfsumme …");
                try
                {
                    var text = await client.GetStringAsync(r.Sha256AssetUrl).ConfigureAwait(true);
                    sha = UpdateService.HashFromText(text, r.AssetName);
                }
                catch (Exception ex) { _log.Warn(Src, "SHA-256-Datei konnte nicht geladen werden", ex); }
            }

            SetStatus("Prüfe Download …");
            await Task.Run(() => UpdateService.VerifyDownload(zip, r.AssetSize, sha)).ConfigureAwait(true);
            if (sha is null)
                _log.Warn(Src, "Für dieses Release liegt keine SHA-256-Datei vor – es wurde nur die Dateigröße geprüft.");

            SetStatus("Installiere …");
            var unpacked = await Task.Run(() => UpdateService.Extract(zip, basis)).ConfigureAwait(true);
            var swap = UpdateService.Swap(exe, unpacked);
            try
            {
                PrepareRelaunch?.Invoke();
                Process.Start(new ProcessStartInfo(swap.NewExe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = folder,
                    ArgumentList = { UpdateService.AfterUpdateArgument, neu },
                });
            }
            catch (Exception ex)
            {
                swap.Rollback();
                throw new UpdateException("Die neue Version ließ sich nicht starten (" + ex.Message + "). Die bisherige Version wurde wiederhergestellt.", ex);
            }
            try { File.Delete(zip); } catch { }
            _log.Info(Src, $"Dateien ausgetauscht, starte {neu} neu.");
            ShutdownApp?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warn(Src, "Update konnte nicht installiert werden", ex);
            var msg = ex is UpdateException ? ex.Message : "Beim Aktualisieren ist ein Fehler aufgetreten: " + ex.Message;
            SetStatus(msg);
            _dialogs.Info("Update nicht möglich", msg + "\n\nDeskRecall läuft unverändert weiter. Sie können die neue Version auch selbst von der Release-Seite laden.");
        }
        finally { SetBusy(false); }
    }

    void Dismiss()
    {
        // Hinweis ausblenden, beim nächsten Intervall aber wieder erscheinen, bis übersprungen oder installiert.
        _notice = null;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasUpdate));
    }

    void Skip()
    {
        if (_available is null) return;
        var v = UpdateService.Display(_available.Version);
        _settings.Update(s => s.Update.SkippedVersion = v);
        SetAvailable(null);
        SetStatus($"Version {v} übersprungen.");
    }

    void OpenReleasePage()
    {
        var url = _available?.PageUrl ?? UpdateService.DefaultReleasePage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _log.Warn(Src, "Release-Seite konnte nicht geöffnet werden", ex); }
    }

    void SetAvailable(ReleaseInfo? r)
    {
        _available = r;
        _notice = r is null ? null : $"Version {UpdateService.Display(r.Version)} ist verfügbar";
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(AvailableVersion));
        CommandManager.InvalidateRequerySuggested();
    }

    void SetStatus(string text)
    {
        _status = text;
        OnPropertyChanged(nameof(StatusText));
    }

    void SetBusy(bool value)
    {
        if (_busy == value) return;
        _busy = value;
        if (!value) _progress = 0;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(Progress));
        CommandManager.InvalidateRequerySuggested();
    }

    void SetProgress(double value)
    {
        _progress = value;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    public void Dispose() => _timer.Stop();
}
