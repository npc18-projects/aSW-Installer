using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Which GitHub repository releases come from, and the public key their signatures are checked against.
/// Embedded only (Data/update.json), so nothing next to the exe can point the updater somewhere else.
/// With no repository or no key the updater stays off.
/// </summary>
public sealed class UpdateConfig
{
    /// <summary>"owner/name" of the GitHub repository.</summary>
    public string Repo { get; set; } = "";
    /// <summary>Base64 of an ECDSA P-256 public key (SubjectPublicKeyInfo). Every release zip must carry a matching signature.</summary>
    public string PublicKey { get; set; } = "";

    public bool Enabled => Repo.Count(c => c == '/') == 1 && !Repo.StartsWith('/') && !Repo.EndsWith('/') && PublicKey.Length > 0;

    public static UpdateConfig Load()
    {
        try
        {
            var asm = typeof(UpdateConfig).Assembly;
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("update.json", StringComparison.OrdinalIgnoreCase));
            if (res is null) return new();
            using var s = asm.GetManifestResourceStream(res)!;
            return JsonSerializer.Deserialize<UpdateConfig>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }
}

/// <summary>What the user can turn on or off. Stored next to the other app settings.</summary>
public sealed class UpdateSettings
{
    public bool CheckAtStartup { get; set; } = true;

    private static string PathFor() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth", "update-settings.json");

    public static UpdateSettings Load()
    {
        try { return File.Exists(PathFor()) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(PathFor())) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor())!);
            File.WriteAllText(PathFor(), JsonSerializer.Serialize(this));
        }
        catch { /* settings are a convenience */ }
    }
}

public sealed record UpdateInfo(string Version, string Tag, string ZipName, string ZipUrl, long ZipSize, string SigUrl, string ReleaseUrl, string Notes);

/// <summary>
/// Looks for a newer release on GitHub, downloads it, checks it, and stages it. Nothing is trusted until the zip's
/// signature matches the embedded public key, the zip unpacks safely, and the exe inside reports the version the release
/// claims and that version is newer than the running one (so an old signed release can't be replayed as an update).
/// </summary>
public sealed class GitHubUpdater
{
    private static readonly string[] AllowedHosts =
    [
        "github.com", "api.github.com", "objects.githubusercontent.com",
        "release-assets.githubusercontent.com", "github-releases.githubusercontent.com",
    ];
    private const long MaxZipBytes = 400L * 1024 * 1024;

    private readonly UpdateConfig _cfg;
    private readonly HttpClient _http;
    private readonly string _stagingRoot;
    private readonly Func<string, string?> _exeVersion;

    public GitHubUpdater(UpdateConfig cfg, HttpClient? http = null, string? stagingRoot = null, Func<string, string?>? exeVersion = null)
    {
        _cfg = cfg;
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0) _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("aSW-Installer", "1"));
        _stagingRoot = stagingRoot ?? DefaultStagingRoot();
        _exeVersion = exeVersion ?? (p => FileVersionInfo.GetVersionInfo(p).ProductVersion);
    }

    public static string DefaultStagingRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth", "updates");

    /// <summary>"v0.4.1", "0.4.1" and "0.4.1+abc" all compare as 0.4.1. Pre-release suffixes count as older than the release.</summary>
    public static bool TryParse(string? s, out Version v, out bool pre)
    {
        v = new Version(0, 0, 0); pre = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = s.IndexOf('-');
        if (dash >= 0) { pre = true; s = s[..dash]; }
        if (!Version.TryParse(s, out var parsed)) return false;
        v = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (!TryParse(candidate, out var c, out var cPre) || cPre) return false;       // never offer a pre-release
        if (!TryParse(current, out var cur, out _)) return false;
        return c > cur;
    }

    private static bool AllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The newest published release if it is newer than <paramref name="current"/> and carries a zip plus its signature; otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(string current, CancellationToken ct = default)
    {
        if (!_cfg.Enabled) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_cfg.Repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;      // no release yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewer(tag, current)) return null;
        TryParse(tag, out var ver, out _);

        string? zipName = null, zipUrl = null, sigUrl = null; long size = 0;
        var assets = r.GetProperty("assets").EnumerateArray().Select(a => (
            Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? "",
            Size: a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0)).ToList();
        var zip = assets.FirstOrDefault(a => a.Name.StartsWith("AStoryWealth-Installer-", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (zip.Name is { Length: > 0 })
        {
            zipName = zip.Name; zipUrl = zip.Url; size = zip.Size;
            sigUrl = assets.FirstOrDefault(a => a.Name.Equals(zip.Name + ".sig", StringComparison.OrdinalIgnoreCase)).Url;
        }
        if (zipUrl is null || string.IsNullOrEmpty(sigUrl) || !AllowedUrl(zipUrl) || !AllowedUrl(sigUrl)) return null;   // not a usable, signed release
        if (size > MaxZipBytes) return null;
        return new UpdateInfo($"{ver.Major}.{ver.Minor}.{ver.Build}", tag, zipName!, zipUrl, size, sigUrl,
            r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "", r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
    }

    /// <summary>True when <paramref name="signature"/> (base64 or raw DER) is a valid ECDSA P-256 / SHA-256 signature of the stream.</summary>
    public static bool VerifySignature(Stream data, byte[] signature, string publicKeyBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            data.Position = 0;
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch { return false; }
    }

    private static byte[] DecodeSignature(byte[] raw)
    {
        var text = System.Text.Encoding.ASCII.GetString(raw).Trim();
        try { return Convert.FromBase64String(text); } catch { return raw; }
    }

    /// <summary>Downloads, verifies and unpacks the update. Returns the folder holding the new files, ready for <see cref="UpdateApplier"/>.</summary>
    public async Task<string> DownloadAndVerifyAsync(UpdateInfo info, string currentVersion, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!_cfg.Enabled) throw new InvalidOperationException("Updates are not set up in this build.");
        var dir = Path.Combine(_stagingRoot, info.Version);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, info.ZipName);
        try
        {
            using (var resp = await _http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                if (resp.RequestMessage?.RequestUri is { } final && !AllowedUrl(final.ToString())) throw new InvalidOperationException("The download was redirected somewhere unexpected.");
                var total = resp.Content.Headers.ContentLength ?? info.ZipSize;
                if (total > MaxZipBytes) throw new InvalidOperationException("The update is larger than expected.");
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buf = new byte[1 << 16]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    done += n;
                    if (done > MaxZipBytes) throw new InvalidOperationException("The update is larger than expected.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    if (total > 0) progress?.Report(done * 100.0 / total);
                }
            }
            byte[] sig;
            using (var resp = await _http.GetAsync(info.SigUrl, ct))
            {
                resp.EnsureSuccessStatusCode();
                if (resp.RequestMessage?.RequestUri is { } final && !AllowedUrl(final.ToString())) throw new InvalidOperationException("The signature download was redirected somewhere unexpected.");
                sig = DecodeSignature(await resp.Content.ReadAsByteArrayAsync(ct));
            }
            await using (var fs = File.OpenRead(zipPath))
                if (!VerifySignature(fs, sig, _cfg.PublicKey))
                    throw new InvalidOperationException("The update's signature doesn't match. It was not installed.");

            var staged = Path.Combine(dir, "files");
            ZipFile.ExtractToDirectory(zipPath, staged);                       // rejects paths that escape the folder
            var exe = Directory.GetFiles(staged, "AStoryWealth.Installer.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? throw new InvalidOperationException("The update doesn't contain the installer.");
            var stagedVersion = _exeVersion(exe);
            if (!TryParse(stagedVersion, out var sv, out _) || !TryParse(info.Version, out var iv, out _) || sv != iv)
                throw new InvalidOperationException($"The update says it is {info.Version} but the program inside is {stagedVersion}.");
            if (!IsNewer(stagedVersion!, currentVersion))
                throw new InvalidOperationException("The update isn't newer than this version.");
            File.Delete(zipPath);
            return staged;
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { }
            throw;
        }
    }
}

/// <summary>
/// Copies a staged update over the install folder once the running app has exited. It runs in a separate process (the new
/// exe started with --apply-update), because a running exe can't overwrite itself. Every replaced file is backed up first
/// and put back if anything fails.
/// </summary>
public static class UpdateApplier
{
    public const string Flag = "--apply-update";
    public const string BackupFolder = ".update-backup";

    public static bool Apply(string stagedDir, string appDir, int waitForPid, bool restart, string? exeName = null, Action<string>? log = null)
    {
        log ??= _ => { };
        exeName ??= "AStoryWealth.Installer.exe";
        try
        {
            if (waitForPid > 0)
            {
                try { using var p = Process.GetProcessById(waitForPid); p.WaitForExit(60_000); }
                catch (ArgumentException) { /* already gone */ }
            }
            var backup = Path.Combine(appDir, BackupFolder);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.CreateDirectory(backup);
            var copied = new List<string>();   // files now holding new content, relative paths
            var created = new List<string>();  // files that did not exist before
            try
            {
                foreach (var src in Directory.EnumerateFiles(stagedDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(stagedDir, src);
                    if (rel.StartsWith("order" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // the user's overrides stay
                    var dst = Path.Combine(appDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (File.Exists(dst))
                    {
                        var bk = Path.Combine(backup, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(bk)!);
                        File.Copy(dst, bk, true);
                    }
                    else created.Add(rel);
                    CopyWithRetry(src, dst);
                    copied.Add(rel);
                }
            }
            catch (Exception ex)
            {
                log($"update failed ({ex.Message}); putting the old files back");
                foreach (var rel in copied)
                {
                    var dst = Path.Combine(appDir, rel);
                    var bk = Path.Combine(backup, rel);
                    try { if (File.Exists(bk)) File.Copy(bk, dst, true); else if (created.Contains(rel)) File.Delete(dst); } catch { }
                }
                return false;
            }
            log($"updated {copied.Count} file(s)");
            if (restart)
                Process.Start(new ProcessStartInfo(Path.Combine(appDir, exeName)) { UseShellExecute = true, WorkingDirectory = appDir });
            return true;
        }
        catch (Exception ex)
        {
            log("update error: " + ex.Message);
            return false;
        }
    }

    private static void CopyWithRetry(string src, string dst)
    {
        for (var i = 0; ; i++)
        {
            try { File.Copy(src, dst, true); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(500); }   // the old exe can take a moment to let go
        }
    }

    /// <summary>True when files can be created in the folder (it isn't read-only or locked down).</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Starts the staged (new) exe as the helper that replaces this install, then the caller should exit.</summary>
    public static void LaunchHelper(string stagedDir, string appDir)
    {
        var helper = Path.Combine(stagedDir, "AStoryWealth.Installer.exe");
        Process.Start(new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            WorkingDirectory = stagedDir,
            ArgumentList = { Flag, stagedDir, appDir, Environment.ProcessId.ToString() },
        });
    }
}
