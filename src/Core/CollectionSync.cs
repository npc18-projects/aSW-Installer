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
    public async Task<SyncResult> EnsureLatestAsync(NexusCredential apiKey, IProgress<string>? progress = null, CancellationToken ct = default)
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
    public async Task<AddonJson> EnsureAddonJsonAsync(NexusCredential apiKey, string slug, string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Report(string s) { progress?.Report(s); InstallLog.Info("sync", s); }

        // The slug names a folder. Nexus slugs are short letters and digits; anything else is refused.
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, "^[A-Za-z0-9_-]{1,40}$"))
            throw new InvalidOperationException($"Unexpected collection id '{slug}'");
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
        AddonJson? NewestCached()
        {
            foreach (var (rev, dir) in existing)
            {
                var json = Path.Combine(_cacheDir, "collections", slug, dir, "collection.json");
                if (File.Exists(json)) return new AddonJson(slug, name, rev, json);
            }
            return null;
        }

        // Always ask Nexus which revision is the latest: a saved copy is only right when it is that revision, or when
        // Nexus can't be reached (then the newest saved copy is better than nothing).
        using var api = new NexusApiClient(apiKey);
        JsonElement revEl;
        try { revEl = await api.GetCollectionRevisionAsync(slug, Domain, revision: null, ct); }
        catch (Exception ex) when (ex is not NexusSignedOutException && ex is not OperationCanceledException && NewestCached() is not null)
        {
            InstallLog.Warn("sync", $"{slug}: couldn't ask Nexus for the latest revision ({ex.Message.Split('\n')[0]}); using the saved copy");
            return NewestCached()!;
        }
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
