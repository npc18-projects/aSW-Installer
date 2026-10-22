namespace AStoryWealth.Core;

/// <summary>
/// Where an add-on's mods go in an instance that already holds the base list (and maybe other add-ons).
///
/// Vortex only writes before/after rules between mods that are in the same collection, so an add-on repeats the few base
/// mods it needs rules against. Those repeats are ANCHORS: never installed again, but their rules say where the add-on's
/// own mods must sit relative to the real base mods.
///
/// Default placement is one block at the top of the base list (just under "Added mods"), under the add-on's own
/// separator: later deploys win, so every "after X" rule holds by construction. A new mod that has to deploy BEFORE a
/// base mod (a patch that must lose to it) cannot sit up there, so it is hoisted: placed in the base list directly
/// below that mod, together with the add-on mods that must deploy before it. Inside the block the order is A–Z with
/// patches / BodySlide sets directly after the mod they need, never against a rule or a real file conflict.
/// </summary>
public static class AddonPlacement
{
    public sealed record Result(List<string> Head, List<string> Block, HashSet<string> Hoisted);

    /// <summary>
    /// Add-ons installed before folders carried the add-on's name are named "A Storywealth - Mod". When this add-on's
    /// registry entry owns such a folder, the mod keeps it instead of being installed a second time under the new name.
    /// </summary>
    public static int KeepExistingFolders(AddonManifest addon, AddonRegistryFile registry, string instanceDir)
    {
        var mine = registry.Addons.FirstOrDefault(a => a.Id.Equals(addon.AddonId, StringComparison.OrdinalIgnoreCase));
        if (mine is null) return 0;
        var owned = new HashSet<string>(mine.Mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var m in addon.Mods.Where(m => !m.IsAnchor))
        {
            if (Directory.Exists(Path.Combine(instanceDir, "mods", m.InstallAs))) continue;
            var old = m.LegacyInstallAs.FirstOrDefault(l => owned.Contains(l) && Directory.Exists(Path.Combine(instanceDir, "mods", l)));
            if (old is null) continue;
            m.InstallAs = old;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Points the add-on's repeats of installed mods at their existing folders and flags them. A repeat is an entry whose
    /// md5 matches a mod of the base list or of another installed add-on AND whose folder is on disk; anything else
    /// (including a base mod the user left out) is installed by the add-on as usual. Returns how many were flagged.
    /// </summary>
    public static int MarkAnchors(AddonManifest addon, IReadOnlyList<ModEntry>? baseMods, AddonRegistryFile registry, string instanceDir)
    {
        var folderByMd5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool Present(string folder) => Directory.Exists(Path.Combine(instanceDir, "mods", folder));
        foreach (var m in baseMods ?? [])
            if (m.Nexus?.Md5 is { Length: > 0 } h && Present(m.InstallAs)) folderByMd5.TryAdd(h, m.InstallAs);
        foreach (var other in registry.Addons.Where(a => !a.Id.Equals(addon.AddonId, StringComparison.OrdinalIgnoreCase)))
            foreach (var m in other.Mods)
                if (m.Md5.Length > 0 && Present(m.Name)) folderByMd5.TryAdd(m.Md5, m.Name);
        var n = 0;
        foreach (var m in addon.Mods)
            if (m.Nexus?.Md5 is { Length: > 0 } h && folderByMd5.TryGetValue(h, out var folder))
            { m.InstallAs = folder; m.IsAnchor = true; n++; }
        return n;
    }

    /// <summary>The separator name for an add-on: its collection name without surrounding blanks.</summary>
    public static string Label(AddonManifest addon) =>
        string.IsNullOrWhiteSpace(addon.Name) ? addon.AddonId : addon.Name.Trim();

    /// <param name="head">The installed list below "Added mods", lowest priority first (separators included), without this add-on's own earlier placement.</param>
    public static Result Place(IReadOnlyList<string> head, AddonManifest addon, string instanceDir, string label)
    {
        var byName = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in addon.Mods.Where(m => !m.IsAnchor && m.InstallAs.Length > 0)) byName.TryAdd(m.InstallAs, m);
        var newSet = new HashSet<string>(byName.Keys, StringComparer.OrdinalIgnoreCase);
        var headPos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < head.Count; i++) headPos.TryAdd(head[i], i);

        // the add-on's own order: install phase, array order, before/after rules (anchors take part so rules through them count)
        var (full, _) = DeployOrder.Compute([], [addon]);
        var intra = full.Where(newSet.Contains).ToList();
        var edges = DeployOrder.RuleEdges(addon.Mods.Where(m => m.InstallAs.Length > 0).ToList(), addon.ModRules ?? []);

        // hoisting: a new mod that must deploy before an installed mod goes just below that mod; so does everything that must
        // deploy before the hoisted mod (it could not stay above it)
        var target = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (earlier, later) in edges)
            if (newSet.Contains(earlier) && !newSet.Contains(later) && headPos.TryGetValue(later, out var p))
                target[earlier] = Math.Min(target.GetValueOrDefault(earlier, int.MaxValue), p);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (earlier, later) in edges)
                if (newSet.Contains(earlier) && target.TryGetValue(later, out var tl) && newSet.Contains(later)
                    && tl < target.GetValueOrDefault(earlier, int.MaxValue))
                { target[earlier] = tl; changed = true; }
        }
        var hoisted = new HashSet<string>(target.Keys, StringComparer.OrdinalIgnoreCase);

        var outHead = new List<string>(head.Count + hoisted.Count);
        var hoistAt = intra.Where(hoisted.Contains).GroupBy(n => target[n]).ToDictionary(g => g.Key, g => g.ToList());
        for (var i = 0; i < head.Count; i++)
        {
            if (hoistAt.TryGetValue(i, out var here)) outHead.AddRange(here);
            outHead.Add(head[i]);
        }

        var block = intra.Where(n => !hoisted.Contains(n)).ToList();
        if (block.Count > 1)
        {
            // A–Z with patches/BodySlide after their mods, constraints (rules + real file conflicts) always honoured
            var blockMods = block.Select(n => byName[n]).ToList();
            var preds = PriorityOrder.Constraints(instanceDir, blockMods, block, edges);
            var signals = PriorityOrder.ScanSignals(instanceDir, blockMods);
            var file = PriorityOrder.Build(addon.AddonId, blockMods, block, preds, [new SeparatorDef { Name = label }], signals);
            block = file.Separators[0].Mods.Select(m => m.Name).ToList();
        }
        return new Result(outHead, block, hoisted);
    }
}
