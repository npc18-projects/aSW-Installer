using System.Text.Json;
using AStoryWealth.NexusApi;

namespace AStoryWealth.Core;

/// <summary>
/// Auto-discovers aSW add-ons: lists the team accounts' published Fallout 4 collections (the base collection is left out).
/// A collection's own list of mods is only fetched when someone installs it.
/// </summary>
public static class AddonGrabber
{
    /// <summary>Accounts whose published Fallout 4 collections are the official add-ons.</summary>
    public static readonly string[] OwnerNames =
    [
        "Exoclyps", "Rantanplan76", "AshThaMoth", "aradonsl",
        "lexinight", "ThatGhostBox", "dovakhin1998twitch", "Npc18"
    ];

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
        // Keep the member ids whatever happens. Keep the listing only when it is whole: every account found and something
        // came back (a half list saved for an hour would hide add-ons until the next run).
        var whole = all.Count > 0 && OwnerNames.All(ids.ContainsKey);
        SaveCache(whole ? new ApiCache(ids, DateTime.UtcNow, all) : new ApiCache(ids, default, null));
        return all.Where(c => c.Slug != CollectionSync.BaseSlug).ToList();
    }

}
