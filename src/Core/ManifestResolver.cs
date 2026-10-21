using System.Security.Cryptography;
using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Resolves a set of addon manifests (plus the base collection) into a
/// validated install plan. Pure logic — no IO — so it is unit-testable.
/// </summary>
public sealed class ManifestResolver
{
    public sealed record ValidationIssue(string Severity, string Message);

    public sealed record ResolvedPlan(
        List<(AddonManifest Addon, ModEntry Mod)> InstallOrder,
        List<PluginEntry> PluginState,
        List<ValidationIssue> Issues)
    {
        public bool HasErrors => Issues.Any(i => i.Severity == "error");
    }

    /// <param name="baseRevisionNumber">Revision fetched from Nexus for the base collection.</param>
    public ResolvedPlan Resolve(IEnumerable<AddonManifest> manifests, int? baseRevisionNumber = null)
    {
        var issues = new List<ValidationIssue>();
        var addons = manifests.OrderBy(m => m.InstallPriority).ToList();

        if (addons.Any(m => m.SchemaVersion != 1))
            issues.Add(new("error", "Unknown schemaVersion — refusing to resolve"));

        // baseRevision pinning
        foreach (var m in addons)
        {
            if (m.Requires.BaseCollectionId == m.AddonId) continue; // self-referential (mapped collection.json)
            if (m.Requires.IsLatest)
                issues.Add(new("warn", $"{m.AddonId}: requires.baseRevision is \"latest\""));
            else if (baseRevisionNumber is int rev && m.Requires.RevisionNumber is int pin && pin != rev)
                issues.Add(new("error", $"{m.AddonId}: pinned baseRevision {pin} != base collection revision {rev}"));
        }

        // installAs uniqueness
        var installAsSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in addons)
            foreach (var mod in m.Mods)
            {
                if (string.IsNullOrWhiteSpace(mod.InstallAs))
                    issues.Add(new("error", $"{m.AddonId}: ModEntry with empty installAs"));
                else if (!installAsSeen.TryAdd(mod.InstallAs, m.AddonId))
                    issues.Add(new("error",
                        $"installAs collision '{mod.InstallAs}': {installAsSeen[mod.InstallAs]} vs {m.AddonId}"));
            }

        // nexus refs sanity
        foreach (var m in addons)
            foreach (var mod in m.Mods)
                if (mod.Nexus is null || mod.Nexus.ModId <= 0 || mod.Nexus.FileId <= 0)
                    issues.Add(new("error", $"{m.AddonId}: mod '{mod.InstallAs}' has invalid nexus ref"));

        // conflicts: declared incompatible pairs
        var byId = addons.ToDictionary(a => a.AddonId);
        foreach (var m in addons)
            foreach (var c in m.Conflicts.Where(c => c.Resolution == "incompatible"))
                if (byId.ContainsKey(c.With))
                    issues.Add(new("error",
                        $"incompatible addons selected: {m.AddonId} + {c.With} — {c.Note}"));

        // plugin state: addons in priority order; later addons win enabled-state
        var pluginState = new Dictionary<string, PluginEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in addons)
            foreach (var p in m.Plugins)
                pluginState[p.Plugin] = p;
        // disabled-by-default entries only apply if another addon didn't enable
        var plan = new ResolvedPlan(
            addons.SelectMany(m => m.Mods.Select(mod => (m, mod))).ToList(),
            pluginState.Values.ToList(),
            issues);
        return plan;
    }

    /// <summary>Verify a downloaded archive against manifest size + md5 (free mode) or sha256 when known.</summary>
    public static async Task<(bool ok, string detail)> VerifyArchiveAsync(string path, ModEntry mod)
    {
        var fi = new FileInfo(path);
        if (mod.Nexus?.FileSize is long expected && fi.Length != expected)
            return (false, $"size mismatch: expected {expected}, got {fi.Length}");

        var md5 = mod.Nexus?.Md5;
        if (string.IsNullOrEmpty(md5))
            return (false, $"no md5 in manifest for {mod.InstallAs} (nexus {mod.Nexus?.ModId}/{mod.Nexus?.FileId}) — refusing unverified archive");
        {
            var actual = await ComputeMd5Async(path);
            if (!actual.Equals(md5, StringComparison.OrdinalIgnoreCase))
                return (false, $"md5 mismatch for nexus {mod.Nexus!.ModId}/{mod.Nexus!.FileId}: expected {md5}, got {actual}");
        }
        return (true, "ok");
    }

    public static async Task<string> ComputeMd5Async(string path)
    {
        await using var fs = File.OpenRead(path);
        using var md5 = MD5.Create();
        return Convert.ToHexString(await md5.ComputeHashAsync(fs)).ToLowerInvariant();
    }
}
