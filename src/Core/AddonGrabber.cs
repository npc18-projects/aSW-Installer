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

    /// <summary>
    /// Lists every team collection (base slug excluded) straight from GraphQL —
    /// no archive downloads. Manifests are only fetched when an add-on installs.
    /// </summary>
    public static async Task<List<NexusApiClient.UserCollection>> ListAsync(string apiKey, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Report(string s) { progress?.Report(s); InstallLog.Info("addons", s); }

        using var api = new NexusApiClient(apiKey);
        var all = new List<NexusApiClient.UserCollection>();
        foreach (var owner in OwnerNames)
        {
            try
            {
                var found = await api.ListUserCollectionsAsync(owner, ct: ct);
                Report($"{owner}: {found.Count} collections.");
                all.AddRange(found);
            }
            catch (Exception ex)
            {
                InstallLog.Info("addons", $"{owner}: {ex.Message}"); // one bad account must not kill the sweep
            }
        }
        Report($"{all.Count} collections from {OwnerNames.Length} team accounts.");
        return all.Where(c => c.Slug != CollectionSync.BaseSlug).ToList();
    }

    public static async Task<GrabResult> GrabAsync(string apiKey, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Report(string s) { progress?.Report(s); InstallLog.Info("addons", s); }

        using var api = new NexusApiClient(apiKey);
        var collections = new List<NexusApiClient.UserCollection>();
        var failedOwners = new List<string>();
        foreach (var owner in OwnerNames)
        {
            try
            {
                var found = await api.ListUserCollectionsAsync(owner, ct: ct);
                Report($"{owner}: {found.Count} collections.");
                collections.AddRange(found);
            }
            catch (Exception ex)
            {
                InstallLog.Info("addons", $"{owner}: {ex.Message}"); // one bad account must not kill the sweep
                failedOwners.Add(owner);
            }
        }
        Report($"{collections.Count} collections from {OwnerNames.Length} team accounts.");

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
