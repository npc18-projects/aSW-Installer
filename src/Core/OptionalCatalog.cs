namespace AStoryWealth.Core;

public enum OptionState { Available, Installed, NotApplicable }

/// <summary>One row of the Optional tab: an optional mod of the base list or of an installed add-on, or a whole base optional
/// group (e.g. Sim Settlements 2) that was left out.</summary>
public sealed class OptionalItem
{
    /// <summary>"base" or the add-on's slug.</summary>
    public string SourceId { get; init; } = "";
    public string SourceName { get; init; } = "";
    /// <summary>collection.json this row came from (used to run the install).</summary>
    public string SourceJson { get; init; } = "";
    public string Name { get; init; } = "";
    public long Bytes { get; init; }
    public int ModCount { get; init; } = 1;
    /// <summary>The collection author's own note for this mod, plus any curated note.</summary>
    public string Instruction { get; init; } = "";
    public OptionState State { get; init; }
    /// <summary>Why a not-applicable option is greyed out.</summary>
    public string Reason { get; init; } = "";
    /// <summary>The mod folder name in the instance; empty for a group row.</summary>
    public string InstallAs { get; init; } = "";
    /// <summary>md5 of the mod; empty for a group row.</summary>
    public string Md5 { get; init; } = "";
    /// <summary>Set for a base group row.</summary>
    public string GroupId { get; init; } = "";
}

/// <summary>One install run the Optional tab asks for: a source and what to add to it.</summary>
public sealed record OptionalJob(string SourceId, string SourceName, string JsonPath, List<string> Md5s, List<string> Groups);

public static class OptionalCatalog
{
    public const string BaseId = "base";
    private const string BaseAddonId = "a-storywealth";

    /// <param name="baseJson">The base list's collection.json.</param>
    /// <param name="addonJson">collection.json of an installed add-on (instance copy, else cache); null when it cannot be found.</param>
    /// <param name="addonName">Display name for an add-on slug that may not be installed.</param>
    public static List<OptionalItem> Build(string instanceDir, string baseJson, Func<InstalledAddon, string?> addonJson, Func<string, string>? addonName = null)
    {
        addonName ??= AddonOptions.NameOf;
        var registry = AddonRegistry.Load(instanceDir);
        var installedIds = new HashSet<string>(registry.Addons.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var curated = AddonOptions.Load();
        var groups = OptionalGroups.Load(BaseAddonId);
        var baseGroups = registry.BaseGroups ?? OptionalGroups.Defaults(groups);
        var items = new List<OptionalItem>();

        var picked = new HashSet<string>(registry.BaseOptional.Concat(registry.Addons.SelectMany(a => a.Optional))
            .Concat(registry.Addons.SelectMany(a => a.Mods.Select(m => m.Md5))), StringComparer.OrdinalIgnoreCase);
        bool OnDisk(ModEntry m) =>
            Directory.Exists(Path.Combine(instanceDir, "mods", m.InstallAs))
            || (m.Nexus?.Md5 is { Length: > 0 } h && picked.Contains(h));

        OptionalItem Row(string sourceId, string sourceName, string json, ModEntry m)
        {
            var cur = AddonOptions.Find(curated, sourceId, m);
            var state = OnDisk(m) ? OptionState.Installed : OptionState.Available;
            var reason = "";
            if (cur is not null && state != OptionState.Installed)
            {
                var missingAddons = cur.NeedsAddons.Where(id => !installedIds.Contains(id)).ToList();
                var missingGroups = cur.NeedsGroups.Where(id => !baseGroups.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missingAddons.Count > 0 || missingGroups.Count > 0)
                {
                    state = OptionState.NotApplicable;
                    reason = "Needs " + string.Join(" and ",
                        missingAddons.Select(a => $"the {addonName(a)} add-on")
                            .Concat(missingGroups.Select(g => groups.FirstOrDefault(x => x.Id.Equals(g, StringComparison.OrdinalIgnoreCase))?.Name ?? g)));
                }
            }
            var note = string.Join(" ", new[] { m.Instructions, cur?.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return new OptionalItem
            {
                SourceId = sourceId, SourceName = sourceName, SourceJson = json,
                Name = string.IsNullOrWhiteSpace(m.DisplayName) ? m.InstallAs : m.DisplayName,
                Bytes = m.Nexus?.FileSize ?? 0, Md5 = m.Nexus?.Md5 ?? "", InstallAs = m.InstallAs,
                Instruction = note, State = state, Reason = reason,
            };
        }

        // ---- base list: its own optional mods, and the groups (Sim Settlements 2, ...) that were left out
        var baseMods = VortexCollectionMapper.Map(baseJson, BaseAddonId).Mods;
        foreach (var g in groups.Where(g => !g.FromOptionalFlag))
        {
            var only = OptionalGroups.Excluded(baseMods, groups, groups.Where(x => x.Id != g.Id).Select(x => x.Id).ToList());
            var members = baseMods.Where(m => only.Contains(m.InstallAs)).ToList();
            items.Add(new OptionalItem
            {
                SourceId = BaseId, SourceName = "A StoryWealth", SourceJson = baseJson, Name = g.Name, GroupId = g.Id,
                Bytes = members.Sum(m => m.Nexus?.FileSize ?? 0), ModCount = members.Count,
                Instruction = g.Description.Replace(" Untick it to play without Sim Settlements.", "", StringComparison.OrdinalIgnoreCase).Trim(),
                State = baseGroups.Contains(g.Id, StringComparer.OrdinalIgnoreCase) ? OptionState.Installed : OptionState.Available,
            });
        }
        foreach (var m in baseMods.Where(m => m.IsOptional)) items.Add(Row(BaseId, "A StoryWealth", baseJson, m));

        // ---- installed add-ons only
        foreach (var a in registry.Addons)
        {
            var json = addonJson(a);
            if (json is null || !File.Exists(json)) continue;
            foreach (var m in VortexCollectionMapper.Map(json, a.Id).Mods.Where(m => m.IsOptional))
                items.Add(Row(a.Id, a.Name.Trim(), json, m));
        }
        // sources in install order (base first), each source A–Z
        return items.GroupBy(i => i.SourceId).SelectMany(g => g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The runs needed to install the ticked rows: the base list first (its groups and optional mods), then each add-on.</summary>
    public static List<OptionalJob> Jobs(IEnumerable<OptionalItem> ticked, string baseJson)
    {
        var jobs = new List<OptionalJob>();
        foreach (var g in ticked.Where(i => i.State == OptionState.Available).GroupBy(i => i.SourceId).OrderBy(g => g.Key == BaseId ? 0 : 1))
        {
            var first = g.First();
            jobs.Add(new OptionalJob(g.Key, first.SourceName, first.SourceJson,
                g.Where(i => i.Md5.Length > 0).Select(i => i.Md5).ToList(),
                g.Where(i => i.GroupId.Length > 0).Select(i => i.GroupId).ToList()));
        }
        return jobs;
    }
}
