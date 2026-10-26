using System.Text.Json;
using AStoryWealth.NexusApi;

namespace AStoryWealth.Core;

/// <summary>
/// Auto-discovers aSW add-ons: lists the team account's published Fallout 4
/// collections, syncs each one's collection.json, maps it to an add-on manifest
/// and writes it into the Addons folder. The base collection slug is skipped.
/// </summary>
public static class AddonGrabber
{
    /// <summary>Accounts whose published Fallout 4 collections are the official add-ons.</summary>
    public static readonly string[] OwnerNames =
    [
        "Exoclyps", "Rantanplan76", "AshThaMoth", "aradonsl",
        "lexinight", "ThatGhostBox", "dovakhin1998twitch", "Npc18"
    ];

    /// <summary>
    /// Added = never-before-seen manifests written this run.
    /// Removed = stale manifests deleted (only when the full sweep succeeded; a
    /// failed owner listing or collection fetch forces a conservative "keep all").
    /// Slugs = slugs successfully refreshed this run.
    /// </summary>
    public sealed record GrabResult(int Added, int Removed, List<string> Slugs);

    private sealed record ApiCache(Dictionary<string, string>? MemberIds = null, DateTime ListedAtUtc = default, List<NexusApiClient.UserCollection>? Listing = null);

    private static readonly TimeSpan ListingAge = TimeSpan.FromHours(1);
    private static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth", "nexus-cache.json");

    private static ApiCache LoadCache()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize<ApiCache>(File.ReadAllText(CachePath)) ?? new ApiCache() : new ApiCache(); }
        catch { return new ApiCache(); }
    }

    private static void SaveCache(ApiCache c)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!); File.WriteAllText(CachePath, JsonSerializer.Serialize(c)); }
        catch { /* a missing cache only costs a request next time */ }
    }

    /// <summary>
    /// Lists every team collection (base slug excluded) straight from GraphQL — no archive downloads.
    /// One query for all team accounts; the member ids are looked up once and kept, and the listing itself is
    /// reused for an hour so reopening the app doesn't ask Nexus again. Manifests are only fetched when an add-on installs.
    /// </summary>
    public static async Task<List<NexusApiClient.UserCollection>> ListAsync(NexusCredential apiKey, IProgress<string>? progress = null, CancellationToken ct = default, bool refresh = false)
    {
        void Report(string s) { progress?.Report(s); InstallLog.Info("addons", s); }

        var cache = LoadCache();
        if (!refresh && cache.Listing is { Count: > 0 } && DateTime.UtcNow - cache.ListedAtUtc < ListingAge)
        {
            Report($"{cache.Listing.Count} collections (saved listing).");
            return cache.Listing.Where(c => c.Slug != CollectionSync.BaseSlug).ToList();
        }

        using var api = new NexusApiClient(apiKey);
        var ids = cache.MemberIds ?? new Dictionary<string, string>();
        foreach (var owner in OwnerNames.Where(o => !ids.ContainsKey(o)))
        {
            try { ids[owner] = await api.GetMemberIdAsync(owner, ct); }
            catch (Exception ex) { InstallLog.Info("addons", $"{owner}: {ex.Message}"); } // one bad account must not kill the sweep
        }
        var all = ids.Count > 0
            ? await api.ListCollectionsByUsersAsync(OwnerNames.Where(ids.ContainsKey).Select(o => ids[o]), ct: ct)
            : new List<NexusApiClient.UserCollection>();
        Report($"{all.Count} collections from {ids.Count} team accounts.");
        SaveCache(new ApiCache(ids, DateTime.UtcNow, all));
        return all.Where(c => c.Slug != CollectionSync.BaseSlug).ToList();
    }

    public static async Task<GrabResult> GrabAsync(NexusCredential apiKey, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Report(string s) { progress?.Report(s); InstallLog.Info("addons", s); }

        var collections = await ListAsync(apiKey, progress, ct, refresh: true);
        var failedOwners = new List<string>();
        if (collections.Count == 0) failedOwners.Add("all"); // nothing came back: don't treat that as "every add-on was removed"

        var cacheDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        cacheDir = Path.Combine(cacheDir, "AStoryWealth", "Collections");
        Directory.CreateDirectory(cacheDir);
        var sync = new CollectionSync(cacheDir);

        var before = new HashSet<string>(Directory.GetFiles(AddonStore.AddonsDir).Select(Path.GetFileName)!);
        var slugs = new List<string>();
        var failedCollections = new List<string>();
        int added = 0, removed = 0;

        foreach (var c in collections)
        {
            if (c.Slug == CollectionSync.BaseSlug) continue; // base, not an add-on
            // A single collection fetch failing must not abort the whole grab;
            // its already-on-disk manifest is preserved by the pruning guard below.
            CollectionSync.AddonJson? addonJson = null;
            try
            {
                addonJson = await sync.EnsureAddonJsonAsync(apiKey, c.Slug, c.Name, progress, ct);
            }
            catch (Exception ex)
            {
                InstallLog.Info("addons", $"{c.Slug}: {ex.Message}");
                failedCollections.Add(c.Slug);
                continue;
            }

            var manifest = VortexCollectionMapper.Map(addonJson.CollectionJsonPath, addonJson.Slug);
            manifest.Name = c.Name;
            manifest.Author = c.Author;
            manifest.Version = $"r{addonJson.Revision}";
            var outPath = Path.Combine(AddonStore.AddonsDir, $"{c.Slug}.addonmanifest.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(manifest));
            Report($"{c.Name} r{addonJson.Revision} → {Path.GetFileName(outPath)}");
            slugs.Add(c.Slug);
            if (!before.Contains(Path.GetFileName(outPath))) added++;
        }

        // Prune manifests whose collection was removed from the owner account.
        // Only safe when we fully enumerated every owner AND fetched every
        // surviving collection — if any listing or fetch failed we cannot tell a
        // genuine removal from a transient error, so we keep everything on disk.
        if (failedOwners.Count == 0 && failedCollections.Count == 0)
        {
            var keep = slugs.Select(s => $"{s}.addonmanifest.json").ToHashSet();
            foreach (var f in Directory.GetFiles(AddonStore.AddonsDir))
            {
                var fn = Path.GetFileName(f);
                if (fn.EndsWith(".addonmanifest.json") && !keep.Contains(fn))
                {
                    File.Delete(f);
                    removed++;
                }
            }
        }

        return new GrabResult(added, removed, slugs);
    }
}
