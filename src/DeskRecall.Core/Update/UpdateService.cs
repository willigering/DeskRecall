using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskRecall.Core.Update;

public enum UpdateChannel { Portable, FrameworkDependent }

public enum UpdateCheckStatus { Ok, NoRelease, Unavailable, Failed }

public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Notes,
    string? PageUrl,
    string? AssetName,
    string? AssetUrl,
    long AssetSize,
    string? Sha256,
    string? Sha256AssetUrl);

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release, string? Message, int? HttpStatus = null);

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Auto-Update über GitHub-Releases (ohne Oberfläche, damit die Logik testbar ist):
/// Versionsvergleich, JSON auswerten, Asset wählen (portable vs. framework-abhängig),
/// herunterladen, Größe und ggf. SHA-256 prüfen, Dateien austauschen und zurückrollen.
/// Es wird niemals ein Zugriffstoken verwendet; der Feed muss öffentlich erreichbar sein.
/// </summary>
public static class UpdateService
{
    public const string DefaultRepo = "willigering/DeskRecall";
    public const string DefaultFeedUrl = "https://api.github.com/repos/" + DefaultRepo + "/releases/latest";
    public const string DefaultReleasePage = "https://github.com/" + DefaultRepo + "/releases/latest";
    public const string ExeName = "DeskRecall.exe";
    public const string PortableFlagName = "portable.flag";
    public const string OldExtension = ".old";
    public const string AfterUpdateArgument = "--nach-update";
    public static readonly string[] CompanionFiles = { "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", PortableFlagName };
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(6);

    // ------------------------------------------------------------------ Feed-URL

    /// <summary>
    /// Leere Angabe → Standard-Feed. Eine GitHub-Repo- oder Release-URL wird zur
    /// <c>/releases/latest</c>-API-Adresse umgeschrieben. Andere URLs bleiben unverändert.
    /// </summary>
    public static string NormalizeFeedUrl(string? configured)
    {
        var s = (configured ?? "").Trim();
        if (s.Length == 0) return DefaultFeedUrl;

        if (Regex.Match(s, @"^https?://api\.github\.com/repos/([^/\s]+)/([^/\s]+)(?:/releases/latest)?/?$", RegexOptions.IgnoreCase) is { Success: true } api)
            return $"https://api.github.com/repos/{api.Groups[1].Value}/{api.Groups[2].Value}/releases/latest";

        if (Regex.Match(s, @"^https?://(?:www\.)?github\.com/([^/\s]+)/([^/\s]+)(?:/releases(?:/latest)?)?/?$", RegexOptions.IgnoreCase) is { Success: true } web)
            return $"https://api.github.com/repos/{web.Groups[1].Value}/{web.Groups[2].Value}/releases/latest";

        return s;
    }

    public static UpdateChannel DetectChannel(string? exeDirectory, bool portableHint = false)
    {
        if (portableHint) return UpdateChannel.Portable;
        if (!string.IsNullOrWhiteSpace(exeDirectory) && File.Exists(Path.Combine(exeDirectory, PortableFlagName)))
            return UpdateChannel.Portable;
        return UpdateChannel.FrameworkDependent;
    }

    // ------------------------------------------------------------------ Versionen

    /// <summary>„v1.2.3“, „1.2“, „V2.0.0-beta“ → 1.2.3 / 1.2.0 / 2.0.0. Ungültig → null.</summary>
    public static Version? VersionFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag.Trim(), @"^[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:[-+].*)?$");
        if (!m.Success) return null;
        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        try { return new Version(Part(1), Part(2), Part(3)); } catch { return null; }
    }

    /// <summary>Vergleicht nur Major.Minor.Build (die Revisions-/Build-Nummer der Dateiversion spielt keine Rolle).</summary>
    public static bool IsNewer(Version? remote, Version? local)
    {
        if (remote is null) return false;
        if (local is null) return true;
        static Version N(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        return N(remote) > N(local);
    }

    public static string Display(Version v) => $"{v.Major}.{Math.Max(v.Minor, 0)}.{Math.Max(v.Build, 0)}";

    public static Version? VersionFromAssembly(string? informationalOrFileVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalOrFileVersion)) return null;
        var first = informationalOrFileVersion.Trim();
        var space = first.IndexOf(' ');
        if (space > 0) first = first[..space];
        return VersionFromTag(first);
    }

    // ------------------------------------------------------------------ JSON / Assets

    /// <summary>Wertet die Antwort von /releases/latest aus. Entwürfe, Vorabversionen und ungültige Tags → null.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name") ?? "";
        var version = VersionFromTag(tag);
        if (version is null) return null;

        var assets = new List<(string Name, string Url, long Size)>();
        if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in a.EnumerateArray())
            {
                var n = Str(x, "name");
                var u = Str(x, "browser_download_url");
                if (n is null || u is null) continue;
                long size = x.TryGetProperty("size", out var s) && s.TryGetInt64(out var g) ? g : 0;
                assets.Add((n, u, size));
            }
        }

        return BuildRelease(version, tag, Str(r, "body") ?? "", Str(r, "html_url"), assets, preferred: null);
    }

    /// <summary>Wählt das Asset passend zum Installationskanal und hängt SHA-256 an, falls vorhanden.</summary>
    public static ReleaseInfo BindAsset(ReleaseInfo release, IEnumerable<(string Name, string Url, long Size)> assets, UpdateChannel channel)
    {
        var list = assets.ToList();
        return BuildRelease(release.Version, release.Tag, release.Notes, release.PageUrl, list, channel);
    }

    static ReleaseInfo BuildRelease(Version version, string tag, string body, string? page, List<(string Name, string Url, long Size)> assets, UpdateChannel? preferred)
    {
        string? zip = preferred is { } ch ? SelectAsset(assets.Select(x => x.Name), ch) : null;
        var chosen = zip is null ? default : assets.First(x => x.Name == zip);
        string? hashAsset = zip is null ? null : assets.Select(x => x.Name).FirstOrDefault(n =>
            n.Equals(zip + ".sha256", StringComparison.OrdinalIgnoreCase)
            || n.Equals(zip + ".sha256.txt", StringComparison.OrdinalIgnoreCase)
            || n.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase)
            || n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

        return new ReleaseInfo(version, tag, ShortNotes(body), page,
            zip, chosen.Url, chosen.Size, zip is null ? null : HashFromText(body, zip),
            hashAsset is null ? null : assets.First(x => x.Name == hashAsset).Url);
    }

    /// <summary>
    /// Portable: „DeskRecall-*-portable-win-x64.zip“.
    /// Framework-abhängig: „DeskRecall-*-win-x64.zip“ ohne „portable“ und ohne „source“.
    /// Geteilte Teile (teilNvonM) und Quelltext-Archive werden nie gewählt.
    /// </summary>
    public static string? SelectAsset(IEnumerable<string> names, UpdateChannel channel)
    {
        bool IsZip(string n) => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        bool IsPart(string n) => Regex.IsMatch(n, @"teil\d+von\d+", RegexOptions.IgnoreCase) || n.Contains(".teil", StringComparison.OrdinalIgnoreCase);
        bool IsSource(string n) => n.Contains("source", StringComparison.OrdinalIgnoreCase) || n.Contains("quell", StringComparison.OrdinalIgnoreCase);
        bool IsPortable(string n) => n.Contains("portable", StringComparison.OrdinalIgnoreCase);
        bool IsWin(string n) => n.Contains("win-x64", StringComparison.OrdinalIgnoreCase) || (n.Contains("win", StringComparison.OrdinalIgnoreCase) && n.Contains("x64", StringComparison.OrdinalIgnoreCase));

        var zips = names.Where(n => IsZip(n) && !IsPart(n) && !IsSource(n)).ToList();
        if (channel == UpdateChannel.Portable)
        {
            return zips.FirstOrDefault(n => Regex.IsMatch(n, @"^DeskRecall-[\d.]+-portable-win-x64\.zip$", RegexOptions.IgnoreCase))
                ?? zips.FirstOrDefault(n => IsPortable(n) && IsWin(n))
                ?? zips.FirstOrDefault(IsPortable)
                ?? (zips.Count == 1 && !IsPortable(zips[0]) ? null : zips.Count == 1 ? zips[0] : null);
        }

        return zips.FirstOrDefault(n => Regex.IsMatch(n, @"^DeskRecall-[\d.]+-win-x64\.zip$", RegexOptions.IgnoreCase) && !IsPortable(n))
            ?? zips.FirstOrDefault(n => IsWin(n) && !IsPortable(n))
            ?? zips.FirstOrDefault(n => !IsPortable(n));
    }

    /// <summary>Sucht einen SHA-256 (64 Hex-Zeichen) in Release-Text oder .sha256-Datei.</summary>
    public static string? HashFromText(string? text, string? fileName = null)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var hex = new Regex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])");
        var lines = text.Split('\n');
        if (fileName is not null)
        {
            foreach (var z in lines)
                if (MentionsFile(z, fileName) && hex.Match(z) is { Success: true } m)
                    return m.Value.ToLowerInvariant();
            return null; // nicht die Prüfsumme einer anderen Datei übernehmen
        }
        foreach (var z in lines)
            if (Regex.IsMatch(z, @"sha-?256", RegexOptions.IgnoreCase) && hex.Match(z) is { Success: true } m)
                return m.Value.ToLowerInvariant();
        var all = hex.Matches(text);
        return all.Count == 1 ? all[0].Value.ToLowerInvariant() : null;
    }

    static bool MentionsFile(string line, string fileName)
    {
        var i = line.IndexOf(fileName, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        if (i > 0 && IsFileNameChar(line[i - 1])) return false;
        var end = i + fileName.Length;
        if (end < line.Length && IsFileNameChar(line[end])) return false;
        return true;
    }

    static bool IsFileNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or '.';

    public static string ShortNotes(string? body, int max = 3000)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var t = body.Replace("\r\n", "\n").Trim();
        t = Regex.Replace(t, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        t = t.Replace("**", "").Replace("__", "");
        t = Regex.Replace(t, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Length <= max ? t : t[..max].TrimEnd() + " …";
    }

    static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    // ------------------------------------------------------------------ Netz

    public static HttpClient CreateClient(string? ownVersion)
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskRecall", ownVersion ?? "1.0"));
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/" + DefaultRepo + ")"));
        return c;
    }

    /// <summary>Fragt das neueste Release ab. 404/401/403 → Unavailable mit klarer Meldung; kein Token wird gesendet.</summary>
    public static async Task<UpdateCheckResult> FetchLatestAsync(HttpClient client, TimeSpan timeout, string? feedUrl = null, UpdateChannel? channel = null, CancellationToken ct = default)
    {
        var url = NormalizeFeedUrl(feedUrl);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var resp = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable, null,
                    code == 404
                        ? "Der Update-Feed ist nicht erreichbar (kein öffentliches Release oder unbekannte Adresse). Automatische Updates funktionieren, sobald die Releases öffentlich lesbar sind."
                        : "Zugang zum Update-Feed wurde verweigert. Es wird kein Zugriffstoken verwendet; die Feed-URL muss öffentlich erreichbar sein.",
                    code);
            }
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                    $"Der Update-Feed antwortete mit HTTP {code}.", code);
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var release = ParseRelease(json);
            if (release is null)
                return new UpdateCheckResult(UpdateCheckStatus.NoRelease, null, "Kein stabiles Release gefunden.", code);

            if (channel is { } ch)
            {
                using var doc = JsonDocument.Parse(json);
                var assets = new List<(string, string, long)>();
                if (doc.RootElement.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in a.EnumerateArray())
                    {
                        var n = Str(x, "name"); var u = Str(x, "browser_download_url");
                        if (n is null || u is null) continue;
                        long size = x.TryGetProperty("size", out var s) && s.TryGetInt64(out var g) ? g : 0;
                        assets.Add((n, u, size));
                    }
                }
                release = BindAsset(release, assets, ch);
            }
            return new UpdateCheckResult(UpdateCheckStatus.Ok, release, null, code);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "Die Update-Prüfung hat zu lange gedauert.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "Update-Prüfung fehlgeschlagen: " + ex.Message);
        }
    }

    public static async Task DownloadAsync(HttpClient client, string url, string destination, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(destination);
        var buffer = new byte[81920];
        long read = 0; int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            progress?.Report(total > 0 ? Math.Min(1.0, (double)read / total.Value) : -1);
        }
        progress?.Report(1.0);
    }

    // ------------------------------------------------------------------ Paket prüfen

    public static string Sha256OfFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Prüft Dateigröße (falls bekannt) und SHA-256 (falls angegeben). Bei Abweichung wird eine Ausnahme ausgelöst.</summary>
    public static void VerifyDownload(string path, long expectedSize, string? expectedSha256)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            throw new UpdateException("Der Download ist leer oder unvollständig.");
        if (expectedSize > 0 && info.Length != expectedSize)
            throw new UpdateException($"Die Dateigröße stimmt nicht (erwartet {expectedSize} Bytes, erhalten {info.Length} Bytes). Es wurde nichts verändert.");
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = Sha256OfFile(path);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("Die Prüfsumme des Downloads stimmt nicht (Datei beschädigt oder verändert). Es wurde nichts verändert.");
        }
    }

    public static string Extract(string zip, string destinationFolder)
    {
        if (Directory.Exists(destinationFolder)) Directory.Delete(destinationFolder, true);
        Directory.CreateDirectory(destinationFolder);
        try { ZipFile.ExtractToDirectory(zip, destinationFolder); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new UpdateException("Das Update-Paket ist beschädigt.", ex);
        }
        return FindExeFolder(destinationFolder) ?? throw new UpdateException("Im Update-Paket fehlt " + ExeName + ".");
    }

    public static string? FindExeFolder(string folder)
    {
        if (File.Exists(Path.Combine(folder, ExeName))) return folder;
        var under = Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, ExeName))).ToList();
        return under.Count == 1 ? under[0] : null;
    }

    public static bool IsWritable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".deskrecall-update-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ Austausch

    /// <summary>
    /// Tauscht die laufende exe (und Begleitdateien bzw. DLLs des Pakets) aus:
    /// vorhandene Datei → „.old“ umbenennen (geht unter Windows, während die Datei läuft),
    /// neue Datei hineinkopieren. Benutzerdaten (Ordner „Daten“, settings.json) bleiben unberührt.
    /// Bei einem Fehler wird alles zurückgerollt.
    /// </summary>
    public static FileSwap Swap(string currentExe, string newFolder)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(currentExe))!;
        if (!IsWritable(folder))
            throw new UpdateException($"DeskRecall darf im Ordner „{folder}“ nichts ändern (fehlende Schreibrechte).");

        var pairs = new List<(string New, string Target)>();
        var exeName = Path.GetFileName(currentExe);
        if (!File.Exists(Path.Combine(newFolder, exeName)) && File.Exists(Path.Combine(newFolder, ExeName)))
            exeName = ExeName;
        if (!File.Exists(Path.Combine(newFolder, exeName)))
            throw new UpdateException("Im Update-Paket fehlt " + ExeName + ".");

        pairs.Add((Path.Combine(newFolder, exeName), Path.GetFullPath(currentExe)));

        foreach (var file in Directory.EnumerateFiles(newFolder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(exeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(OldExtension, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Daten", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(folder, name);
            // portable.flag nur übernehmen, wenn sie im neuen Paket vorkommt – die vorhandene Installation nicht umbiegen
            pairs.Add((file, target));
        }

        var renamed = new List<(string Target, string Old)>();
        var copied = new List<string>();
        try
        {
            foreach (var (neu, ziel) in pairs)
            {
                if (File.Exists(ziel))
                {
                    var alt = UniqueOldName(ziel);
                    File.Move(ziel, alt);
                    renamed.Add((ziel, alt));
                }
                File.Copy(neu, ziel, overwrite: false);
                copied.Add(ziel);
            }
            return new FileSwap(pairs[0].Target, copied, renamed);
        }
        catch (Exception ex)
        {
            Rollback(copied, renamed);
            throw ex as UpdateException ?? new UpdateException("Die neuen Dateien konnten nicht kopiert werden: " + ex.Message, ex);
        }
    }

    internal static void Rollback(List<string> copied, List<(string Target, string Old)> renamed)
    {
        foreach (var k in copied) try { if (File.Exists(k)) File.Delete(k); } catch { }
        foreach (var (ziel, alt) in Enumerable.Reverse(renamed))
            try { if (!File.Exists(ziel) && File.Exists(alt)) File.Move(alt, ziel); } catch { }
    }

    static string UniqueOldName(string target)
    {
        var alt = target + OldExtension;
        if (!File.Exists(alt)) return alt;
        try { File.Delete(alt); return alt; } catch { }
        for (var i = 1; ; i++)
        {
            var a = $"{target}.{i}{OldExtension}";
            if (!File.Exists(a)) return a;
            try { File.Delete(a); return a; } catch { }
        }
    }

    /// <summary>Löscht übrig gebliebene „.old“-Dateien eines früheren Updates. Liefert, wie viele noch gesperrt sind.</summary>
    public static int DeleteOldFiles(string folder)
    {
        var rest = 0;
        if (!Directory.Exists(folder)) return 0;
        foreach (var f in Directory.GetFiles(folder, "*" + OldExtension))
        {
            try { File.Delete(f); } catch { rest++; }
        }
        return rest;
    }

    public static string? AfterUpdateVersion(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals(AfterUpdateArgument, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Count && !args[i + 1].StartsWith("--") ? args[i + 1] : "";
        }
        return null;
    }

    public static bool ShouldOffer(ReleaseInfo? release, Version? local, string? skipped, bool manual) =>
        release is not null && IsNewer(release.Version, local)
        && (manual || !string.Equals(Display(release.Version), VersionFromTag(skipped) is { } u ? Display(u) : null));
}

/// <summary>Ergebnis von <see cref="UpdateService.Swap"/>.</summary>
public sealed class FileSwap
{
    readonly List<string> _copied;
    readonly List<(string Target, string Old)> _renamed;
    internal FileSwap(string newExe, List<string> copied, List<(string Target, string Old)> renamed)
    {
        NewExe = newExe;
        _copied = copied;
        _renamed = renamed;
    }

    public string NewExe { get; }
    public IReadOnlyList<string> OldFiles => _renamed.Select(u => u.Old).ToList();
    public void Rollback() => UpdateService.Rollback(_copied, _renamed);
}
