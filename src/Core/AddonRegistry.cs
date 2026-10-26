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
    /// <summary>Plugins the installer turned off because a master was missing; turned back on when the master appears.</summary>
    public List<string> AutoDisabled { get; set; } = [];
}

public static class AddonRegistry
{
    public const string FileName = ".asw-addons.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>For showing things: any trouble reading the file gives an empty record. Code that writes the record back uses <see cref="LoadStrict"/>.</summary>
    public static AddonRegistryFile Load(string instanceDir)
    {
        try { return LoadStrict(instanceDir); }
        catch { return new(); }
    }

    /// <summary>
    /// Reads the record. A file that can't be opened (antivirus, MO2) is retried and then reported as an error, so a caller that
    /// is about to write never starts from an empty record by mistake. A file that isn't valid JSON is kept as a .bak copy.
    /// </summary>
    public static AddonRegistryFile LoadStrict(string instanceDir)
    {
        var p = Path.Combine(instanceDir, FileName);
        if (!File.Exists(p)) return new();
        string text;
        for (var attempt = 1; ; attempt++)
        {
            try { text = File.ReadAllText(p); break; }
            catch (IOException) when (attempt < 4) { Thread.Sleep(150 * attempt); }
        }
        try { return JsonSerializer.Deserialize<AddonRegistryFile>(text, Json) ?? new(); }
        catch (JsonException)
        {
            try { File.Copy(p, p + ".bak", overwrite: true); } catch { }
            return new();
        }
    }

    /// <summary>Written to a temporary file first and then moved over the record, so a crash leaves the old one whole.</summary>
    public static void Save(string instanceDir, AddonRegistryFile file)
    {
        var p = Path.Combine(instanceDir, FileName);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json), new System.Text.UTF8Encoding(false));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>Records (or replaces) one add-on, keeping the order add-ons were first installed in. Optional picks already
    /// recorded for it are kept unless the new record carries its own.</summary>
    public static void Upsert(string instanceDir, InstalledAddon addon)
    {
        var file = LoadStrict(instanceDir);
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
        var file = LoadStrict(instanceDir);
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
