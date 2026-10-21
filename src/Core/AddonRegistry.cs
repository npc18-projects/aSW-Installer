using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>What the instance has installed from add-on collections: the add-on, the revision, its separator and the mod
/// folders it owns. Lets a base re-run keep add-on mods on top, lets a re-run of an add-on re-place its mods, and is what
/// update / remove build on.</summary>
public sealed class InstalledAddonMod
{
    public string Name { get; set; } = "";
    public string Md5 { get; set; } = "";
}

public sealed class InstalledAddon
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Revision { get; set; } = "";
    /// <summary>Separator the add-on's block sits under (without the "_separator" suffix); empty when every mod was hoisted.</summary>
    public string Separator { get; set; } = "";
    /// <summary>Every mod folder this add-on needs and installed or shares with another add-on.</summary>
    public List<InstalledAddonMod> Mods { get; set; } = [];
    /// <summary>The subset placed inside the base list (they had to deploy before a base mod), not in the add-on's block.</summary>
    public List<string> Hoisted { get; set; } = [];
    /// <summary>md5 of the add-on's optional mods the user picked; kept so a re-run does not drop them.</summary>
    public List<string> Optional { get; set; } = [];
    public DateTime InstalledUtc { get; set; }
}

public sealed class AddonRegistryFile
{
    public int Schema { get; set; } = 1;
    public List<InstalledAddon> Addons { get; set; } = [];
    /// <summary>Optional install groups of the base list that were installed (e.g. "ss2"); null until a base run records them.</summary>
    public List<string>? BaseGroups { get; set; }
    /// <summary>md5 of the base list's optional mods the user picked.</summary>
    public List<string> BaseOptional { get; set; } = [];
}

public static class AddonRegistry
{
    public const string FileName = ".asw-addons.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static AddonRegistryFile Load(string instanceDir)
    {
        try
        {
            var p = Path.Combine(instanceDir, FileName);
            return File.Exists(p) ? JsonSerializer.Deserialize<AddonRegistryFile>(File.ReadAllText(p), Json) ?? new() : new();
        }
        catch { return new(); }
    }

    public static void Save(string instanceDir, AddonRegistryFile file) =>
        File.WriteAllText(Path.Combine(instanceDir, FileName), JsonSerializer.Serialize(file, Json), new System.Text.UTF8Encoding(false));

    /// <summary>Records (or replaces) one add-on, keeping the order add-ons were first installed in. Optional picks already
    /// recorded for it are kept unless the new record carries its own.</summary>
    public static void Upsert(string instanceDir, InstalledAddon addon)
    {
        var file = Load(instanceDir);
        var i = file.Addons.FindIndex(a => a.Id.Equals(addon.Id, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            if (addon.Optional.Count == 0) addon.Optional = file.Addons[i].Optional;
            file.Addons[i] = addon;
        }
        else file.Addons.Add(addon);
        Save(instanceDir, file);
    }

    /// <summary>The optional-mod picks (md5) recorded for the base list (id null) or an add-on.</summary>
    public static List<string> OptionalFor(AddonRegistryFile file, string? addonId) =>
        addonId is null ? file.BaseOptional : file.Addons.FirstOrDefault(a => a.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase))?.Optional ?? [];

    /// <summary>Remembers the optional picks (union with what was recorded) and, for the base list, the groups installed.</summary>
    public static void RecordOptional(string instanceDir, string? addonId, IEnumerable<string> md5s, IEnumerable<string>? baseGroups = null)
    {
        var file = Load(instanceDir);
        var set = OptionalFor(file, addonId).Concat(md5s).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addonId is null)
        {
            file.BaseOptional = set;
            if (baseGroups is not null) file.BaseGroups = baseGroups.ToList();
        }
        else if (file.Addons.FirstOrDefault(a => a.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase)) is { } a) a.Optional = set;
        Save(instanceDir, file);
    }
}
