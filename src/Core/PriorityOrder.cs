using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>The shipped, MO2-style structured priority order of a collection: named separators
/// (Core, Bug Fixes, ... Patches), each owning Nexus categories and listing its mods in deploy order.</summary>
public sealed class PriorityOrderFile
{
    public int Schema { get; set; } = 1;
    public string AddonId { get; set; } = "";
    public string Note { get; set; } = "";
    public List<SeparatorDef> Separators { get; set; } = [];
}

public sealed class SeparatorDef
{
    public string Name { get; set; } = "";
    /// <summary>Nexus categories placed here when a mod has no explicit entry (new mods in a later revision).</summary>
    public List<string> Categories { get; set; } = [];
    /// <summary>Mods in ascending deploy priority (last wins), keyed by archive md5 with the folder name as fallback.</summary>
    public List<OrderedMod> Mods { get; set; } = [];
}

public sealed class OrderedMod
{
    public string Md5 { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Turns the flat Vortex-derived deploy order into an MO2-style structured one without changing
/// who wins any conflict. Mods are grouped under separators (by shipped assignment, else Nexus
/// category), and the groups are ordered. The only mods that leave their intended separator are
/// those forced later by a before/after rule or a real file conflict with a mod in a later
/// separator, so every rule and every conflict resolves exactly as in the Vortex-derived order.
/// </summary>
public static class PriorityOrder
{
    public const string DefaultSeparator = "Core Gameplay";

    public readonly record struct Entry(string Name, bool IsSeparator);

    public sealed record ApplyReport(int Mods, int Unplaced, int PushedLater, int OrderFixes);

    /// <summary>The separator scheme (order matters: first = lowest priority).</summary>
    public static List<SeparatorDef> DefaultScheme() =>
    [
        S("Core", "Utilities", "Modders Resources and Tutorials"),
        S("Bug Fixes", "Bug Fixes", "Performance"),
        S("Tweaks", "Immersion", "Miscellaneous", "Cheats and God items"),
        S("User Interface", "User Interface"),
        S("Core Gameplay", "Gameplay", "Overhauls", "Crafting - Home/Settlement", "Crafting - Equipment", "Crafting - Other",
            "Items (Food, Drinks, Chems, etc)", "Ammo", "Vehicles"),
        S("Settlements", "Player Settlement", "Sim Settlements 2", "Transfer Settlement Blueprints"),
        S("Companions & Factions", "Companions", "Factions", "NPC"),
        S("Locations", "Locations - Vanilla", "Locations - New", "New Lands", "Player Homes"),
        S("Quests & Adventures", "Quests and Adventures", "Collectibles, Treasure Hunts, and Puzzles"),
        S("Creatures", "Creatures"),
        S("World Visuals", "Models and Textures", "Visuals and Graphics", "Environment", "Weather and Lighting"),
        S("Characters", "Character Presets", "Body, Face, and Hair", "Tattoos"),
        S("Audio", "Audio - SFX", "Audio - Misc", "Audio - Voice", "Audio - Music", "Radio"),
        S("Animations", "Animation"),
        S("Clothing", "Clothing"),
        S("Armour", "Armour"),
        S("Power Armour", "Power Armour"),
        S("Weapons", "Weapons", "Weapons and Armour"),
        S("Patches", "Patches"),
    ];

    private static SeparatorDef S(string name, params string[] categories) => new() { Name = name, Categories = [.. categories] };

    // ---- file I/O ------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static PriorityOrderFile? Load(string path)
    {
        try { return JsonSerializer.Deserialize<PriorityOrderFile>(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    public static void Save(PriorityOrderFile file, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json), new System.Text.UTF8Encoding(false));

    /// <summary>The order shipped for an addon id: an override file next to the app wins over the one embedded in the library.</summary>
    public static PriorityOrderFile? LoadShipped(string addonId)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "order", addonId + ".priority.json");
        if (File.Exists(local) && Load(local) is { } f) return f;
        var asm = typeof(PriorityOrder).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(addonId + ".priority.json", StringComparison.OrdinalIgnoreCase));
        if (res is null) return null;
        try
        {
            using var s = asm.GetManifestResourceStream(res)!;
            return JsonSerializer.Deserialize<PriorityOrderFile>(s, Json);
        }
        catch { return null; }
    }

    // ---- constraints -----------------------------------------------------------------------

    /// <summary>mod → mods that must deploy BEFORE it: real file conflicts (the earlier one in the
    /// baseline loses) plus before/after rules that agree with the baseline.</summary>
    public static Dictionary<string, HashSet<string>> Constraints(
        string instanceDir, IReadOnlyList<ModEntry> mods, IReadOnlyList<string> baseline,
        IReadOnlyList<(string Earlier, string Later)> ruleEdges)
    {
        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < baseline.Count; i++) pos.TryAdd(baseline[i], i);
        var preds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string earlier, string later)
        {
            if (!pos.TryGetValue(earlier, out var pe) || !pos.TryGetValue(later, out var pl) || pe >= pl) return;
            if (!preds.TryGetValue(later, out var s)) preds[later] = s = new(StringComparer.OrdinalIgnoreCase);
            s.Add(earlier);
        }

        foreach (var (e, l) in ruleEdges) Add(e, l);

        var modsDir = Path.Combine(instanceDir, "mods");
        var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var docExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".md", ".pdf", ".url", ".rtf", ".html", ".docx", ".jpg", ".jpeg", ".png", ".zip", ".7z", ".rar" };
        foreach (var m in mods)
        {
            var dir = Path.Combine(modsDir, m.InstallAs);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, f);
                if (rel.StartsWith("fomod", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.StartsWith("Root" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(rel);
                if (name.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (docExt.Contains(Path.GetExtension(name)) && rel == name) continue;   // loose docs/screenshots at the mod root
                if (!byPath.TryGetValue(rel, out var list)) byPath[rel] = list = [];
                list.Add(m.InstallAs);
            }
        }
        foreach (var list in byPath.Values.Where(l => l.Count > 1))
            foreach (var a in list)
                foreach (var b in list)
                    Add(a, b);
        return preds;
    }

    // ---- build (tooling) ---------------------------------------------------------------------

    /// <summary>Generates the shipped order for a collection from its baseline order and constraints.</summary>
    public static PriorityOrderFile Build(string addonId, IReadOnlyList<ModEntry> mods, IReadOnlyList<string> baseline,
        IReadOnlyDictionary<string, HashSet<string>> preds, List<SeparatorDef>? scheme = null)
    {
        scheme ??= DefaultScheme();
        var file = new PriorityOrderFile
        {
            AddonId = addonId,
            Note = "Generated by OrderTool. Separators are ordered lowest priority first; mods inside a separator are in "
                 + "ascending deploy order (last wins). Edit freely: Apply() re-checks every rule and file conflict at install time.",
            Separators = scheme.Select(s => new SeparatorDef { Name = s.Name, Categories = [.. s.Categories] }).ToList(),
        };
        var byName = mods.ToDictionary(m => m.InstallAs, StringComparer.OrdinalIgnoreCase);
        var group = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in baseline.Where(byName.ContainsKey))
        {
            var g = CategoryGroup(file, byName[n].Category);
            if (preds.TryGetValue(n, out var ps))
                foreach (var p in ps) if (group.TryGetValue(p, out var pg)) g = Math.Max(g, pg);
            group[n] = g;
        }
        foreach (var n in baseline.Where(group.ContainsKey))
            file.Separators[group[n]].Mods.Add(new OrderedMod { Md5 = byName[n].Nexus?.Md5 ?? "", Name = n });
        return file;
    }

    private static int CategoryGroup(PriorityOrderFile file, string? category)
    {
        for (var g = 0; g < file.Separators.Count; g++)
            if (file.Separators[g].Categories.Contains(category ?? "", StringComparer.OrdinalIgnoreCase)) return g;
        var d = file.Separators.FindIndex(s => s.Name.Equals(DefaultSeparator, StringComparison.OrdinalIgnoreCase));
        return d >= 0 ? d : 0;
    }

    // ---- apply (install time) ----------------------------------------------------------------

    /// <summary>Structured order for the mods in <paramref name="baseline"/>: separators in scheme order,
    /// each followed by its mods, ascending priority. Constraints always win over the shipped placement.</summary>
    public static (List<Entry> Entries, ApplyReport Report) Apply(
        PriorityOrderFile file, IReadOnlyList<ModEntry> mods, IReadOnlyList<string> baseline,
        IReadOnlyDictionary<string, HashSet<string>> preds)
    {
        var byName = mods.ToDictionary(m => m.InstallAs, StringComparer.OrdinalIgnoreCase);
        var shippedByMd5 = new Dictionary<string, (int G, int Seq)>(StringComparer.OrdinalIgnoreCase);
        var shippedByName = new Dictionary<string, (int G, int Seq)>(StringComparer.OrdinalIgnoreCase);
        var seq = 0;
        for (var g = 0; g < file.Separators.Count; g++)
            foreach (var om in file.Separators[g].Mods)
            {
                var v = (g, seq++);
                if (om.Md5.Length > 0) shippedByMd5.TryAdd(om.Md5, v);
                shippedByName.TryAdd(om.Name, v);
            }

        var members = baseline.Where(byName.ContainsKey).ToList();
        var intended = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seqOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unplaced = 0;
        foreach (var n in members)
        {
            var md5 = byName[n].Nexus?.Md5 ?? "";
            if ((md5.Length > 0 && shippedByMd5.TryGetValue(md5, out var v)) || shippedByName.TryGetValue(n, out v))
            { intended[n] = v.G; seqOf[n] = v.Seq; }
            else { intended[n] = CategoryGroup(file, byName[n].Category); seqOf[n] = int.MaxValue; unplaced++; }
        }

        // groups: pushed later when something that must deploy before them sits in a later separator
        var fin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in members)
        {
            var g = intended[n];
            if (preds.TryGetValue(n, out var ps)) foreach (var p in ps) if (fin.TryGetValue(p, out var pg)) g = Math.Max(g, pg);
            fin[n] = g;
        }
        var pushed = members.Count(n => fin[n] != intended[n]);

        // tentative order: separator, shipped sequence, baseline position
        var basePos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < members.Count; i++) basePos[members[i]] = i;
        var tentative = members.OrderBy(n => fin[n]).ThenBy(n => fin[n] == intended[n] ? seqOf[n] : int.MaxValue).ThenBy(n => basePos[n]).ToList();

        // enforce constraints inside the tentative order (stable topological pass)
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tentative.Count; i++) index[tentative[i]] = i;
        var indeg = new int[tentative.Count];
        var succ = Enumerable.Range(0, tentative.Count).Select(_ => new List<int>()).ToList();
        foreach (var (later, ps) in preds)
        {
            if (!index.TryGetValue(later, out var li)) continue;
            foreach (var p in ps) if (index.TryGetValue(p, out var pi)) { succ[pi].Add(li); indeg[li]++; }
        }
        var ready = new SortedSet<int>();
        for (var i = 0; i < tentative.Count; i++) if (indeg[i] == 0) ready.Add(i);
        var ordered = new List<string>(tentative.Count);
        while (ordered.Count < tentative.Count)
        {
            if (ready.Count == 0)   // cannot happen for acyclic constraints; never loop forever
            {
                for (var i = 0; i < tentative.Count; i++) if (!ordered.Contains(tentative[i], StringComparer.OrdinalIgnoreCase)) { ready.Add(i); break; }
            }
            var n = ready.Min; ready.Remove(n);
            ordered.Add(tentative[n]);
            foreach (var s in succ[n]) if (--indeg[s] == 0) ready.Add(s);
        }
        var fixes = 0;
        for (var i = 0; i < ordered.Count; i++) if (!ordered[i].Equals(tentative[i], StringComparison.OrdinalIgnoreCase)) fixes++;

        // emit separators + mods; a pulled mod can only be earlier than its separator via the topological
        // pass above, so re-derive each mod's final separator from its position.
        var entries = new List<Entry>(ordered.Count + file.Separators.Count);
        var lastGroup = -1;
        var runningMax = 0;
        foreach (var n in ordered)
        {
            runningMax = Math.Max(runningMax, fin[n]);
            if (runningMax != lastGroup)
            {
                entries.Add(new Entry(file.Separators[runningMax].Name, true));
                lastGroup = runningMax;
            }
            entries.Add(new Entry(n, false));
        }
        return (entries, new ApplyReport(members.Count, unplaced, pushed, fixes));
    }
}
