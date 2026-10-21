using System.Text.Json;
using AStoryWealth.NexusApi;

namespace AStoryWealth.Core;

/// <summary>
/// Syncs the base collection from Nexus the way Vortex does: downloads the
/// collection archive itself (A-Name-&lt;revId&gt;-&lt;rev&gt;-&lt;ts&gt;.7z), extracts
/// collection.json, and tracks revision updates across launches.
/// State lives in &lt;cacheDir&gt;/collections-state.json so restarts are cheap and
/// update checks are a single GraphQL query.
/// </summary>
public sealed class CollectionSync
{
    public const string BaseSlug = "5atq9t";
    private const string Domain = "fallout4";

    public sealed record SyncState(string Slug, int Revision, long RevisionId, string CollectionJsonPath, string ArchivePath);
    public sealed record SyncResult(int Revision, long RevisionId, string CollectionJsonPath, string ArchivePath, bool WasCached, bool UpdateAvailable, int LatestRevision);

    private readonly string _cacheDir;
    private readonly string _statePath;

    public CollectionSync(string cacheDir)
    {
        _cacheDir = cacheDir;
        _statePath = Path.Combine(cacheDir, "collections-state.json");
    }

    /// <summary>Load previous state (cached collection.json) if present.</summary>
    public SyncState? LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(_statePath));
        }
        catch { return null; }
    }

    /// <summary>
    /// Ensure the collection manifest for the latest published revision is on disk.
    /// progress reports human-readable lines (percentages included) for the UI.
    /// </summary>
    public async Task<SyncResult> EnsureLatestAsync(string apiKey, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Report(string s) { progress?.Report(s); InstallLog.Info("sync", s); }

        using var api = new NexusApiClient(apiKey);
        var rev = await api.GetCollectionRevisionAsync(BaseSlug, Domain, revision: null, ct); // null = latest
        var latestRevision = rev.GetProperty("revisionNumber").GetInt32();
        var collectionId = rev.GetProperty("collectionId").GetInt64();
        var linkPath = rev.GetProperty("downloadLink").GetString()!; // /v2/collections/709/revisions/<revId>/download_link
        var revisionId = long.Parse(linkPath.Split('/')[5]); // /v2/collections/<cid>/revisions/<revId>/download_link

        var cached = LoadState();
        if (cached is not null && cached.Revision == latestRevision && File.Exists(cached.CollectionJsonPath))
        {
            Report($"Collection r{latestRevision} already cached — checking for updates done.");
            return new SyncResult(cached.Revision, cached.RevisionId, cached.CollectionJsonPath, cached.ArchivePath, true, false, latestRevision);
        }

        var uri = await api.GetCollectionArchiveLinkAsync(linkPath, ct);
        if (uri is null)
            throw new InvalidOperationException("Collection archive requires premium (403 from download_link).");

        var archivePath = Path.Combine(_cacheDir, $"A-StoryWealth-{revisionId}-{latestRevision}.7z");
        if (!File.Exists(archivePath))
        {
            Report($"Downloading collection r{latestRevision}…");
            var prog = new Progress<double>(p => Report($"Downloading collection r{latestRevision}… {p:F0}%"));
            await api.DownloadFileAsync(uri, archivePath, null, prog, ct);
            Report($"Collection archive downloaded ({new FileInfo(archivePath).Length / 1_000_000.0:F1} MB).");
        }
        else Report($"Collection archive for r{latestRevision} found on disk — skipping download.");

        var outDir = Path.Combine(_cacheDir, "collections", BaseSlug, $"r{latestRevision}");
        Directory.CreateDirectory(outDir);
        var jsonPath = Path.Combine(outDir, "collection.json");
        if (!File.Exists(jsonPath))
        {
            SevenZip.ExtractSingleFile(archivePath, "collection.json", outDir);
            Report("collection.json extracted.");
        }

        var json = JsonSerializer.Serialize(new SyncState(BaseSlug, latestRevision, revisionId, jsonPath, archivePath));
        File.WriteAllText(_statePath, json);

        return new SyncResult(latestRevision, revisionId, jsonPath, archivePath, false,
            cached is not null && cached.Revision < latestRevision, latestRevision);
    }
    /// <summary>Manifest of one add-on collection fetched for auto-discovery. No state file; the .addonmanifest.json is the cache.</summary>
    public sealed record AddonJson(string Slug, string Name, int Revision, string CollectionJsonPath);

    /// <summary>
    /// Fetch collection.json for an add-on collection (same pipeline as the base:
    /// revision query, premium archive link, 7z extract). Cached per slug+revision.
    /// </summary>
    public async Task<AddonJson> EnsureAddonJsonAsync(string apiKey, string slug, string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Report(string s) { progress?.Report(s); InstallLog.Info("sync", s); }

        var slugDir = Path.Combine(_cacheDir, "collections", slug);
        // The collections/<slug> dir may not exist yet (fresh cache) — tolerate its
        // absence instead of letting EnumerateDirectories throw DirectoryNotFoundException.
        var existing = Directory.Exists(slugDir)
            ? Directory.EnumerateDirectories(slugDir)
                .Select(d => Path.GetFileName(d))
                .Where(d => d.StartsWith('r') && int.TryParse(d[1..], out _))
                .Select(d => (Rev: int.Parse(d[1..]), Dir: d))
                .OrderByDescending(x => x.Rev)
                .ToList()
            : new List<(int Rev, string Dir)>();
        foreach (var (rev, dir) in existing)
        {
            var json = Path.Combine(_cacheDir, "collections", slug, dir, "collection.json");
            if (File.Exists(json)) return new AddonJson(slug, name, rev, json);
        }

        using var api = new NexusApiClient(apiKey);
        var revEl = await api.GetCollectionRevisionAsync(slug, Domain, revision: null, ct);
        var latestRevision = revEl.GetProperty("revisionNumber").GetInt32();
        var linkPath = revEl.GetProperty("downloadLink").GetString()!;
        var revisionId = long.Parse(linkPath.Split('/')[5]);

        var cachedJson = existing.Where(x => x.Rev == latestRevision)
            .Select(x => Path.Combine(_cacheDir, "collections", slug, x.Dir, "collection.json"))
            .FirstOrDefault(File.Exists);
        if (cachedJson is not null) return new AddonJson(slug, name, latestRevision, cachedJson);

        var uri = await api.GetCollectionArchiveLinkAsync(linkPath, ct);
        if (uri is null)
            throw new InvalidOperationException("Collection archive requires premium (403 from download_link).");

        var archivePath = Path.Combine(_cacheDir, $"A-{slug}-{revisionId}-{latestRevision}.7z");
        if (!File.Exists(archivePath))
        {
            Report($"Downloading {name} r{latestRevision}…");
            var prog = new Progress<double>(p => Report($"Downloading {name} r{latestRevision}… {p:F0}%"));
            await api.DownloadFileAsync(uri, archivePath, null, prog, ct);
        }
        var outDir = Path.Combine(_cacheDir, "collections", slug, $"r{latestRevision}");
        Directory.CreateDirectory(outDir);
        var jsonPath = Path.Combine(outDir, "collection.json");
        if (!File.Exists(jsonPath))
        {
            SevenZip.ExtractSingleFile(archivePath, "collection.json", outDir);
            Report($"{name} manifest extracted.");
        }
        return new AddonJson(slug, name, latestRevision, jsonPath);
    }
}

/// <summary>7-Zip helpers (hidden window, same binary as Mo2InstanceWriter).</summary>
public static class SevenZip
{
    private static readonly Lazy<string> _exePath = new(Locate);

    /// <summary>
    /// 7-Zip console binary. Search order: ASW_7Z env var, a copy bundled next to
    /// the app (7z.exe / 7zz / 7za), PATH, then the usual per-OS install spots.
    /// </summary>
    public static string ExePath => _exePath.Value;

    private static string Locate()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "7z.exe", "7za.exe" } : new[] { "7zz", "7z", "7za" };

        if (Environment.GetEnvironmentVariable("ASW_7Z") is { Length: > 0 } env)
        {
            if (File.Exists(env)) return env;
            throw new FileNotFoundException($"ASW_7Z points to '{env}', which does not exist.");
        }

        foreach (var n in names)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, n);
            if (File.Exists(bundled)) return bundled;
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
            foreach (var n in names)
            {
                try
                {
                    var p = Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
                if (root.Length > 0 && Path.Combine(root, "7-Zip", "7z.exe") is var p && File.Exists(p))
                    return p;
        }
        else
        {
            foreach (var p in new[] { "/usr/bin/7zz", "/usr/bin/7z", "/usr/local/bin/7zz", "/usr/local/bin/7z" })
                if (File.Exists(p)) return p;
        }

        throw new FileNotFoundException(
            "7-Zip not found. Install 7-Zip, put 7z on PATH, drop 7z.exe next to the installer, or set ASW_7Z to its full path.");
    }

    public static void ExtractSingleFile(string archive, string entryName, string outDir)
    {
        entryName = Mo2InstanceWriter.SafeName(entryName, "archive entry");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"e \"{archive}\" \"{entryName}\" -o\"{outDir}\" -y -bso0 -bsp0",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var se = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"7z extract '{entryName}' failed (exit {proc.ExitCode}): {se}");
        if (!File.Exists(Path.Combine(outDir, entryName)))
            throw new InvalidOperationException($"'{entryName}' not found inside {archive}");
    }

    /// <summary>Extracts the entries matching a wildcard (recursive, paths kept) into outDir.</summary>
    public static void ExtractMatching(string archive, string pattern, string outDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"x \"{archive}\" -o\"{outDir}\" \"{pattern}\" -r -y -bso0 -bsp0",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var se = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"7z extract '{pattern}' from '{Path.GetFileName(archive)}' failed (exit {proc.ExitCode}): {se}");
    }

    /// <summary>Extracts every entry of an archive into outDir.</summary>
    public static void ExtractAll(string archive, string outDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"x \"{archive}\" -o\"{outDir}\" -y -bso0 -bsp0",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var se = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"7z extract-all '{Path.GetFileName(archive)}' failed (exit {proc.ExitCode}): {se}");
    }
}

/// <summary>
/// Tracks whether the base A StoryWealth pack is installed (marker file written on
/// successful install, path recorded). Add-on lists only show when this says yes.
/// </summary>
public static class InstallState
{
    private static string StatePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "install.json");
        }
    }

    /// <summary>Reads install.json, tolerating corruption. On any read/parse
    /// failure the corrupt file is moved aside to install.json.bak and null is
    /// returned (empty state). Deserialized records are normalized so Resume
    /// list fields are never null.</summary>
    private static InstallRecord? ReadRecord()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var rec = JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(StatePath));
            if (rec?.Resume is { } r)
            {
                var dd = r.DoneDownloads ?? new List<string>();
                var di = r.DoneInstalls ?? new List<string>();
                if (!ReferenceEquals(dd, r.DoneDownloads) || !ReferenceEquals(di, r.DoneInstalls))
                    rec = rec with { Resume = r with { DoneDownloads = dd, DoneInstalls = di } };
            }
            return rec;
        }
        catch
        {
            try { if (File.Exists(StatePath)) File.Move(StatePath, StatePath + ".bak", overwrite: true); } catch { }
            return null;
        }
    }

    /// <summary>Atomic write: serialize to a temp file then File.Replace over the
    /// target so a crash mid-write never leaves a half-written install.json.</summary>
    private static void WriteRecord(InstallRecord rec)
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(dir);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rec));
            File.Replace(tmp, StatePath, null);
        }
        catch
        {
            // state persistence must never throw into the install flow
        }
    }

    public sealed record InstallRecord(string InstallDir, string CompletedUtc,
        string? LastDir = null, string? LastReuseDir = null, string? LastDownloadsDir = null, bool? LastStockGame = null,
        ResumeInfo? Resume = null);

    /// <summary>Checkpoint of an in-flight install. Written by InstallRunner after
    /// each completed step so an interrupted run resumes instead of restarting.</summary>
    public sealed record ResumeInfo(
        string JsonPath, string AddonId, string InstallDir, string GamePath, bool StockGame,
        string? ReuseDir, string? DownloadsDir,
        List<string> DoneDownloads, List<string> DoneInstalls, bool GameCopied, Dictionary<string,string>? DownloadedFiles = null, bool IncludeOptional = false, List<string>? OptionalGroups = null, string? BaseJsonPath = null);

    public static ResumeInfo? GetResume() => ReadRecord()?.Resume;

    public static void SetResume(ResumeInfo resume)
    {
        var old = ReadRecord();
        var dir = old?.InstallDir ?? resume.InstallDir;
        var done = old?.CompletedUtc ?? "";
        WriteRecord(new InstallRecord(dir, done,
            LastDir: old?.LastDir ?? resume.InstallDir,
            LastReuseDir: old?.LastReuseDir ?? resume.ReuseDir,
            LastDownloadsDir: old?.LastDownloadsDir ?? resume.DownloadsDir,
            LastStockGame: old?.LastStockGame ?? resume.StockGame,
            Resume: resume));
    }

    /// <summary>User-tuned thread counts for downloads/extraction. Defaults to
    /// one less than the logical core count; persisted in threads.json.</summary>
    public sealed record ThreadSettings(int Download, int Extract);

    public static ThreadSettings DefaultThreads()
    {
        var t = Math.Max(1, Environment.ProcessorCount - 1);
        return new ThreadSettings(t, t);
    }

    private static string ThreadsPath => Path.Combine(Path.GetDirectoryName(StatePath)!, "threads.json");

    public static ThreadSettings GetThreads()
    {
        try
        {
            if (File.Exists(ThreadsPath))
            {
                var s = JsonSerializer.Deserialize<ThreadSettings>(File.ReadAllText(ThreadsPath));
                if (s is { Download: > 0, Extract: > 0 }) return s;
            }
        }
        catch { }
        return DefaultThreads();
    }

    public static void SetThreads(ThreadSettings t)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThreadsPath)!);
            File.WriteAllText(ThreadsPath, JsonSerializer.Serialize(
                new ThreadSettings(Math.Max(1, t.Download), Math.Max(1, t.Extract))));
        }
        catch { }
    }

    public static void ClearResume()
    {
        var rec = ReadRecord();
        if (rec is null) return;
        WriteRecord(rec with { Resume = null });
    }

    /// <summary>Install dir if a completed install is recorded AND ModOrganizer.exe is really there; otherwise null.</summary>
    public static string? GetInstallDir()
    {
        var rec = ReadRecord();
        if (rec?.InstallDir is not { } d) return null;
        return File.Exists(Path.Combine(d, "ModOrganizer.exe")) ? d : null;
    }

    public static void SetInstallDir(string installDir)
    {
        // copy-on-write: carry every other field through, only update InstallDir + CompletedUtc
        var old = ReadRecord();
        WriteRecord(new InstallRecord(installDir, DateTime.UtcNow.ToString("O"),
            LastDir: old?.LastDir, LastReuseDir: old?.LastReuseDir,
            LastDownloadsDir: old?.LastDownloadsDir, LastStockGame: old?.LastStockGame,
            Resume: old?.Resume));
    }

    /// <summary>Last install dir used, even if that install never finished — remembered so the field pre-fills next launch.</summary>
    public static string? GetLastDir()
    {
        var rec = ReadRecord();
        return rec?.LastDir is { } d && Directory.Exists(d) ? d : rec?.InstallDir is { } d2 && Directory.Exists(d2) ? d2 : null;
    }

    /// <summary>Writes the last-used install options at install START (not completion) so a cancelled run is still remembered.</summary>
    public static void SetLastPath(string installDir, string? reuseDir, string? downloadsDir, bool stockGame)
    {
        // copy-on-write: preserve Resume and any other fields, only update the Last* options
        var old = ReadRecord();
        var dir = old?.InstallDir ?? installDir;
        var done = old?.CompletedUtc ?? "";
        WriteRecord(new InstallRecord(dir, done,
            LastDir: installDir, LastReuseDir: reuseDir, LastDownloadsDir: downloadsDir,
            LastStockGame: stockGame, Resume: old?.Resume));
    }

    public static (string? Dir, string? ReuseDir, string? DownloadsDir, bool StockGame) GetLastPath()
    {
        var rec = ReadRecord();
        if (rec is null) return (null, null, null, false);
        return (rec.LastDir, rec.LastReuseDir, rec.LastDownloadsDir, rec.LastStockGame ?? false);
    }
}
