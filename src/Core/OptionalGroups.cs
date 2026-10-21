using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>Optional install groups of a collection ("Sim Settlements 2", "HD textures"): the user can leave a whole
/// group out and the installer then skips its downloads, extraction, mod-list entries and plugins.</summary>
public sealed class OptionalGroupsFile
{
    public int Schema { get; set; } = 1;
    public string AddonId { get; set; } = "";
    public List<OptionalGroup> Groups { get; set; } = [];
}

public sealed class OptionalGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Whether the group is installed unless the user opts out.</summary>
    public bool DefaultOn { get; set; }
    /// <summary>The group is the collection's own Vortex "optional" mods (the HD textures) rather than a listed set.</summary>
    public bool FromOptionalFlag { get; set; }
    /// <summary>Where the list came from (for maintainers).</summary>
    public string Source { get; set; } = "";
    public List<OrderedMod> Mods { get; set; } = [];
}

public static class OptionalGroups
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static void Save(OptionalGroupsFile file, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json), new System.Text.UTF8Encoding(false));

    /// <summary>The groups shipped for an addon id (override file next to the app, else embedded). Empty when none ship.</summary>
    public static List<OptionalGroup> Load(string addonId)
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "order", addonId + ".optional.json");
            if (File.Exists(local))
                return JsonSerializer.Deserialize<OptionalGroupsFile>(File.ReadAllText(local), Json)?.Groups ?? [];
            var asm = typeof(OptionalGroups).Assembly;
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(addonId + ".optional.json", StringComparison.OrdinalIgnoreCase));
            if (res is null) return [];
            using var s = asm.GetManifestResourceStream(res)!;
            return JsonSerializer.Deserialize<OptionalGroupsFile>(s, Json)?.Groups ?? [];
        }
        catch { return []; }
    }

    /// <summary>Group ids selected by default.</summary>
    public static List<string> Defaults(IEnumerable<OptionalGroup> groups) => groups.Where(g => g.DefaultOn).Select(g => g.Id).ToList();

    /// <summary>
    /// Install folders to leave out: every mod of a group that is NOT selected. Without a shipped definition the
    /// collection's own optional-flagged mods are skipped, as before.
    /// </summary>
    public static HashSet<string> Excluded(IReadOnlyList<ModEntry> mods, IReadOnlyList<OptionalGroup> groups, IReadOnlyCollection<string> selected)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0)
        {
            foreach (var m in mods.Where(m => m.IsOptional)) excluded.Add(m.InstallAs);
            return excluded;
        }
        foreach (var g in groups.Where(g => !selected.Contains(g.Id, StringComparer.OrdinalIgnoreCase)))
        {
            if (g.FromOptionalFlag)
                foreach (var m in mods.Where(m => m.IsOptional)) excluded.Add(m.InstallAs);
            var md5 = new HashSet<string>(g.Mods.Where(x => x.Md5.Length > 0).Select(x => x.Md5), StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(g.Mods.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods)
                if ((m.Nexus?.Md5 is { Length: > 0 } h && md5.Contains(h)) || names.Contains(m.InstallAs)) excluded.Add(m.InstallAs);
        }
        // the collection's own optional mods that no group claims stay skipped (nothing asked for them)
        var claimed = groups.Any(g => g.FromOptionalFlag);
        if (!claimed) foreach (var m in mods.Where(m => m.IsOptional)) excluded.Add(m.InstallAs);
        return excluded;
    }
}
