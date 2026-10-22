namespace AStoryWealth.Core;

public sealed class RemovalResult
{
    public int FoldersDeleted { get; set; }
    public int PluginsRemoved { get; set; }
    public long BytesFreed { get; set; }
    /// <summary>Mods that stay, with the reason (another add-on needs them, or the base list does).</summary>
    public List<string> Kept { get; } = [];
    /// <summary>Set when nothing could be removed at all.</summary>
    public string? Refused { get; set; }
}

/// <summary>
/// Takes an add-on, or one optional mod, back out of an instance: its mod folders, its lines in modlist.txt, the plugins
/// only those folders provided, its separator, and its record in .asw-addons.json. A mod another installed add-on lists
/// (or the base list needs) is kept. Downloaded archives are left alone. Mod Organizer 2 must be closed: it rewrites
/// the profile when it exits.
/// </summary>
public static class Removal
{
    /// <summary>What removing an add-on would delete, without touching anything.</summary>
    public static (List<string> Delete, List<string> Kept) PlanAddon(string instanceDir, string addonId, IReadOnlySet<string> baseNames)
    {
        var reg = AddonRegistry.Load(instanceDir);
        var addon = reg.Addons.FirstOrDefault(a => a.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase));
        if (addon is null) return ([], []);
        var others = new HashSet<string>(reg.Addons.Where(a => a != addon).SelectMany(a => a.Mods.Select(m => m.Name)), StringComparer.OrdinalIgnoreCase);
        var delete = new List<string>(); var kept = new List<string>();
        foreach (var m in addon.Mods.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(ModPath(instanceDir, m))) continue;
            if (baseNames.Contains(m)) kept.Add($"{m} (the base list needs it)");
            else if (others.Contains(m)) kept.Add($"{m} (another add-on needs it)");
            else delete.Add(m);
        }
        return (delete, kept);
    }

    public static RemovalResult RemoveAddon(string instanceDir, string addonId, IReadOnlySet<string> baseNames)
    {
        var res = new RemovalResult();
        var reg = AddonRegistry.Load(instanceDir);
        var addon = reg.Addons.FirstOrDefault(a => a.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase));
        if (addon is null) { res.Refused = "That add-on isn't recorded in this install."; return res; }
        var (delete, kept) = PlanAddon(instanceDir, addonId, baseNames);
        res.Kept.AddRange(kept);
        var separatorName = addon.Separator.Length > 0 ? addon.Separator + "_separator" : null;
        Apply(instanceDir, delete, separatorName, res);
        reg.Addons.Remove(addon);
        AddonRegistry.Save(instanceDir, reg);
        try { File.Delete(Path.Combine(instanceDir, ".asw", addon.Id + ".collection.json")); } catch { }
        InstallLog.Info("remove", $"removed add-on {addon.Name.Trim()}: {res.FoldersDeleted} folder(s), {res.PluginsRemoved} plugin(s), kept {res.Kept.Count}");
        return res;
    }

    /// <summary>Removes one optional mod of the base list (<paramref name="addonId"/> null) or of an add-on.</summary>
    public static RemovalResult RemoveOptional(string instanceDir, string? addonId, string installAs, string md5, IReadOnlySet<string> baseRequired)
    {
        var res = new RemovalResult();
        var reg = AddonRegistry.Load(instanceDir);
        foreach (var other in reg.Addons.Where(a => addonId is null || !a.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase)))
            if (other.Mods.Any(m => m.Name.Equals(installAs, StringComparison.OrdinalIgnoreCase)))
            { res.Refused = $"The {other.Name.Trim()} add-on needs this mod."; return res; }
        if (addonId is not null && baseRequired.Contains(installAs)) { res.Refused = "The base list needs this mod."; return res; }

        Apply(instanceDir, Directory.Exists(ModPath(instanceDir, installAs)) ? [installAs] : [], null, res);
        if (addonId is null) reg.BaseOptional.RemoveAll(h => h.Equals(md5, StringComparison.OrdinalIgnoreCase));
        else if (reg.Addons.FirstOrDefault(a => a.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase)) is { } a)
        {
            a.Optional.RemoveAll(h => h.Equals(md5, StringComparison.OrdinalIgnoreCase));
            a.Mods.RemoveAll(m => m.Name.Equals(installAs, StringComparison.OrdinalIgnoreCase));
        }
        AddonRegistry.Save(instanceDir, reg);
        InstallLog.Info("remove", $"removed optional mod {installAs}: {res.FoldersDeleted} folder(s), {res.PluginsRemoved} plugin(s)");
        return res;
    }

    private static string ModPath(string instanceDir, string name) =>
        Path.Combine(instanceDir, "mods", Mo2InstanceWriter.SafeName(name, "installAs"));

    private static readonly string[] PluginExt = [".esp", ".esm", ".esl"];

    private static IEnumerable<string> PluginsIn(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => PluginExt.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileName)!;

    private static void Apply(string instanceDir, List<string> names, string? separatorName, RemovalResult res)
    {
        var modsRoot = Path.GetFullPath(Path.Combine(instanceDir, "mods"));
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var path = Path.GetFullPath(ModPath(instanceDir, name));
            if (!path.StartsWith(modsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path)) continue;
            foreach (var p in PluginsIn(path)) plugins.Add(p);
            try { res.BytesFreed += Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch { }
            InstallRunner.DeleteTree(path);
            res.FoldersDeleted++;
            gone.Add(name);
        }
        if (separatorName is not null) { gone.Add(separatorName); Mo2InstanceWriter.RemoveStaleSeparators(instanceDir, [separatorName]); }

        var profile = Path.Combine(instanceDir, "profiles", "A StoryWealth");
        var modlist = Path.Combine(profile, "modlist.txt");
        if (File.Exists(modlist))
        {
            var kept = File.ReadAllLines(modlist).Where(l => l.Length < 2 || l[0] == '#' || !gone.Contains(l[1..])).ToList();
            File.WriteAllLines(modlist, kept, new System.Text.UTF8Encoding(false));
        }

        // plugins only the removed folders provided; a plugin another remaining mod also provides stays
        if (plugins.Count > 0 && Directory.Exists(modsRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(modsRoot))
            {
                if (plugins.Count == 0) break;
                try { foreach (var p in PluginsIn(dir)) plugins.Remove(p); } catch { }
            }
            foreach (var file in new[] { "plugins.txt", "loadorder.txt" })
            {
                var path = Path.Combine(profile, file);
                if (!File.Exists(path) || plugins.Count == 0) continue;
                var lines = File.ReadAllLines(path);
                var keep = lines.Where(l => l.Length == 0 || l[0] == '#' || !plugins.Contains(l.TrimStart('*', '+').Trim())).ToList();
                if (file == "plugins.txt") res.PluginsRemoved = lines.Length - keep.Count;
                File.WriteAllLines(path, keep, new System.Text.UTF8Encoding(false));
            }
        }
    }
}
