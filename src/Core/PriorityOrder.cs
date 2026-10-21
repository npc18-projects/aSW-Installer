using System.Text.Json;
using System.Text.RegularExpressions;

namespace AStoryWealth.Core;

/// <summary>The shipped, MO2-style structured priority order of a collection: named separators
/// (Core, Bug Fixes, ... Pistols ... Patches), each listing its mods in deploy order.</summary>
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
    /// <summary>Nexus categories placed here when a mod has no explicit entry (empty = any category).</summary>
    public List<string> Categories { get; set; } = [];
    /// <summary>Optional name regexes narrowing the categories (pistols, shotguns, ...). A separator with
    /// patterns is tried before the ones without.</summary>
    public List<string> NamePatterns { get; set; } = [];
    /// <summary>Mods in ascending deploy priority (last wins), keyed by archive md5 with the folder name as fallback.</summary>
    public List<OrderedMod> Mods { get; set; } = [];
    /// <summary>Emit this separator even when it holds no mods (the "Added mods" section for users' own mods).</summary>
    public bool AlwaysShow { get; set; }
}

public sealed class OrderedMod
{
    public string Md5 { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Informational: the mod this one follows (a patch, BodySlide set or add-on sits right after what it requires).</summary>
    public string? Parent { get; set; }
}

/// <summary>Facts read from the materialized mod folders that tell which mod requires which.</summary>
public sealed class ModSignals
{
    /// <summary>mod → master plugins its plugins need.</summary>
    public Dictionary<string, HashSet<string>> Masters { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>plugin file name → the mod that ships it.</summary>
    public Dictionary<string, string> Provider { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HasPlugin { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BodySlide { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Turns the flat Vortex-derived deploy order into an MO2-style structured one without changing
/// who wins any conflict.
///
/// Placement: every mod belongs to a separator (shipped assignment, else name pattern + Nexus
/// category). Patches, BodySlide sets and add-ons ("child" mods) move into the separator of the mod
/// they require and sit directly after it; everything else is ordered by name, so the list reads
/// predictably. Safety: the only mods that leave their separator or their natural position are those
/// forced later by a before/after rule or a real file conflict, so every winner stays the same.
/// </summary>
public static class PriorityOrder
{
    public const string DefaultSeparator = "Core Gameplay";
    /// <summary>Last (highest-priority) separator, left empty for mods people add themselves.</summary>
    public const string AddedModsSeparator = "Added mods";
    private const int FrameworkThreshold = 8;   // a mod this many others require is a framework, not a "parent"

    public readonly record struct Entry(string Name, bool IsSeparator);

    public sealed record ApplyReport(int Mods, int Unplaced, int PushedLater, int OrderFixes);

    // ---- scheme ----------------------------------------------------------------------------

    private const string BackpackRx = @"backpack|rucksack|satchel|knapsack|haversack";

    /// <summary>The separator scheme (order matters: first = lowest priority).</summary>
    public static List<SeparatorDef> DefaultScheme() =>
    [
        S("Core Frameworks", [], "Utilities", "Modders Resources and Tutorials"),
        S("Performance & Engine", ["crash|buffout|papyrus|memory|cache|\\bops\\b|engine|stutter|fps|optimi[sz]"], "Bug Fixes", "Performance", "Utilities"),
        S("Bug Fixes", [], "Bug Fixes"),
        S("Tweaks & Immersion", [], "Immersion", "Miscellaneous", "Cheats and God items"),
        S("HUD & Interface", ["hud|icon|font|pip.?boy|compass|crosshair|reticle|map|scope|marker|widget|minimap"], "User Interface"),
        S("Menus & UI Frameworks", [], "User Interface"),
        S("Core Gameplay", [], "Gameplay", "Overhauls", "Vehicles"),
        S("Crafting & Workshop", [], "Crafting - Home/Settlement", "Crafting - Equipment", "Crafting - Other", "Items (Food, Drinks, Chems, etc)"),
        S("Settlement Tools", [], "Player Settlement"),
        S("Sim Settlements 2", [], "Sim Settlements 2"),
        S("Settlement Blueprints", [], "Transfer Settlement Blueprints"),
        S("Companions", [], "Companions"),
        S("Factions", [], "Factions"),
        S("NPCs", [], "NPC"),
        S("Vanilla Location Overhauls", [], "Locations - Vanilla"),
        S("New Locations & Lands", [], "Locations - New", "New Lands", "Player Homes"),
        S("Quests & Adventures", [], "Quests and Adventures"),
        S("Collectibles & Puzzles", [], "Collectibles, Treasure Hunts, and Puzzles"),
        S("Creatures", [], "Creatures"),
        S("World Visuals", [], "Models and Textures", "Visuals and Graphics"),
        S("Weather & Lighting", [], "Weather and Lighting", "Environment"),
        S("Characters", [], "Character Presets", "Body, Face, and Hair", "Tattoos"),
        S("Audio & Radio", [], "Audio - SFX", "Audio - Misc", "Audio - Voice", "Audio - Music", "Radio"),
        S("Animations", [], "Animation"),
        S("Clothing", [], "Clothing"),
        S("Armour", [], "Armour"),
        S("Power Armour", [], "Power Armour"),
        S("Backpacks", [BackpackRx], "Armour", "Clothing", "Gameplay", "Crafting - Equipment", "Weapons and Armour"),
        S("Weapons", [], "Weapons", "Weapons and Armour", "Ammo"),
        S("Patches", [], "Patches"),
        new SeparatorDef { Name = AddedModsSeparator, AlwaysShow = true },     // empty: where people put their own mods
    ];

    private static SeparatorDef S(string name, string[] patterns, params string[] categories) =>
        new() { Name = name, Categories = [.. categories], NamePatterns = [.. patterns] };

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

    /// <summary>Reads each mod's plugin headers and BodySlide content from the materialized mod folders.</summary>
    public static ModSignals ScanSignals(string instanceDir, IReadOnlyList<ModEntry> mods)
    {
        var sig = new ModSignals();
        var modsDir = Path.Combine(instanceDir, "mods");
        foreach (var m in mods)
        {
            var dir = Path.Combine(modsDir, m.InstallAs);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetExtension(f).ToLowerInvariant() is not (".esp" or ".esm" or ".esl")) continue;
                sig.Provider.TryAdd(Path.GetFileName(f), m.InstallAs);
                sig.HasPlugin.Add(m.InstallAs);
                if (PluginOrder.ReadHeader(f) is not { } h) continue;
                if (!sig.Masters.TryGetValue(m.InstallAs, out var set)) sig.Masters[m.InstallAs] = set = new(StringComparer.OrdinalIgnoreCase);
                foreach (var master in h.Masters) set.Add(master);
            }
            if (Directory.Exists(Path.Combine(dir, "Tools", "BodySlide")) || Directory.Exists(Path.Combine(dir, "CalienteTools")))
                sig.BodySlide.Add(m.InstallAs);
        }
        return sig;
    }

    // ---- grouping --------------------------------------------------------------------------

    private static string Short(string installAs)
    {
        var i = installAs.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0 && installAs.StartsWith("A Story", StringComparison.OrdinalIgnoreCase) ? installAs[(i + 3)..] : installAs;
    }

    private static string SortKey(string installAs)
    {
        var k = Regex.Replace(Short(installAs).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        return k.StartsWith("the ", StringComparison.Ordinal) ? k[4..] : k;
    }

    private static int GroupOf(PriorityOrderFile file, ModEntry m)
    {
        var name = Short(m.InstallAs);
        var cat = m.Category ?? "";
        for (var g = 0; g < file.Separators.Count; g++)         // specific (name-pattern) separators first
        {
            var s = file.Separators[g];
            if (s.NamePatterns.Count == 0) continue;
            if (s.Categories.Count > 0 && !s.Categories.Contains(cat, StringComparer.OrdinalIgnoreCase)) continue;
            if (s.NamePatterns.Any(p => Regex.IsMatch(name, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return g;
        }
        for (var g = 0; g < file.Separators.Count; g++)
        {
            var s = file.Separators[g];
            if (s.NamePatterns.Count == 0 && s.Categories.Contains(cat, StringComparer.OrdinalIgnoreCase)) return g;
        }
        var d = file.Separators.FindIndex(s => s.Name.Equals(DefaultSeparator, StringComparison.OrdinalIgnoreCase));
        return d >= 0 ? d : 0;
    }

    private static readonly Regex ChildName = new(
        @"bodyslide|patch|compat|add-?on|support|conversion|cbbe|tweak|\besl\b|\bfix\b|fixes|loose|extension|integration|bridge|replacer|retexture|\bfor\b.+\b(ba2|esp)\b|settings",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string NormName(string installAs)
    {
        var stop = new HashSet<string>
        {
            "bodyslide","body","slide","files","file","cbbe","conversion","patch","patches","esl","esp","loose","ba2","optional","support",
            "compatibility","compatible","version","main","update","addon","for","the","and","a","an","of","by","with","2k","4k","1k","fix",
            "fixes","standalone","physics","bodyslides","preset","presets","outfit","outfits","pack","only","all","in","one","aio","redux",
        };
        var toks = Regex.Replace(Short(installAs).ToLowerInvariant(), @"[^a-z0-9 ]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !stop.Contains(t) && !Regex.IsMatch(t, @"^v?\d+(\.\d+)*$"));
        return string.Join(' ', toks);
    }

    // ---- build (tooling) ---------------------------------------------------------------------

    /// <summary>Generates the shipped order for a collection from its baseline order, constraints and (optionally) the
    /// dependency signals read from the materialized mods.</summary>
    public static PriorityOrderFile Build(string addonId, IReadOnlyList<ModEntry> mods, IReadOnlyList<string> baseline,
        IReadOnlyDictionary<string, HashSet<string>> preds, List<SeparatorDef>? scheme = null, ModSignals? signals = null)
    {
        scheme ??= DefaultScheme();
        var file = new PriorityOrderFile
        {
            AddonId = addonId,
            Note = "Generated by OrderTool. Separators are ordered lowest priority first; inside a separator mods are in ascending "
                 + "deploy order (last wins). A patch, BodySlide set or add-on directly follows the mod it requires (see Parent); the rest "
                 + "is alphabetical. Edit freely: Apply() re-checks every rule and file conflict at install time.",
            Separators = scheme.Select(s => new SeparatorDef { Name = s.Name, Categories = [.. s.Categories], NamePatterns = [.. s.NamePatterns], AlwaysShow = s.AlwaysShow }).ToList(),
        };
        var byName = mods.ToDictionary(m => m.InstallAs, StringComparer.OrdinalIgnoreCase);
        var members = baseline.Where(byName.ContainsKey).ToList();
        var basePos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < members.Count; i++) basePos[members[i]] = i;
        var group0 = members.ToDictionary(n => n, n => GroupOf(file, byName[n]), StringComparer.OrdinalIgnoreCase);

        // ---- who requires whom
        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (signals is not null)
        {
            var requiredBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mod, masters) in signals.Masters)
                foreach (var p in masters.Select(x => signals.Provider.GetValueOrDefault(x)).Where(x => x is not null && !x.Equals(mod, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
                    requiredBy[p!] = requiredBy.GetValueOrDefault(p!) + 1;
            var frameworks = requiredBy.Where(kv => kv.Value >= FrameworkThreshold).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool ChildLike(string n) =>
                byName[n].Category == "Patches" || ChildName.IsMatch(Short(n))
                || (signals.BodySlide.Contains(n) && !signals.HasPlugin.Contains(n));

            var family = members.GroupBy(n => byName[n].Nexus?.ModId ?? 0).Where(g => g.Key != 0 && g.Count() > 1)
                .ToDictionary(g => g.Key, g => g.ToList());
            string? FamilyMain(string n)
            {
                if (!family.TryGetValue(byName[n].Nexus?.ModId ?? 0, out var fam)) return null;
                var main = fam.OrderBy(x => ChildLike(x)).ThenBy(x => !signals.HasPlugin.Contains(x)).ThenBy(x => NormName(x).Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).First();
                return main.Equals(n, StringComparison.OrdinalIgnoreCase) ? null : main;
            }
            var norm = members.ToDictionary(n => n, NormName, StringComparer.OrdinalIgnoreCase);

            foreach (var c in members.Where(ChildLike))
            {
                string? parent = null;
                if (signals.Masters.TryGetValue(c, out var ms))
                {
                    parent = ms.Select(x => signals.Provider.GetValueOrDefault(x))
                        .Where(p => p is not null && !p.Equals(c, StringComparison.OrdinalIgnoreCase) && byName.ContainsKey(p) && !frameworks.Contains(p))
                        .Select(p => p!)
                        .OrderByDescending(p => group0[p]).ThenByDescending(p => basePos[p]).FirstOrDefault();
                }
                parent ??= FamilyMain(c);
                if (parent is null)
                {
                    var cn = norm[c];
                    parent = members.Where(p => !p.Equals(c, StringComparison.OrdinalIgnoreCase) && !ChildLike(p) && norm[p].Length >= 5 && cn.Contains(norm[p], StringComparison.Ordinal))
                        .OrderByDescending(p => norm[p].Length).FirstOrDefault();
                    // "America Rising 2 - Bodyslide files" belongs to "America Rising 2 - Legacy of the Enclave":
                    // the set's own name is a prefix of the parent's
                    parent ??= cn.Length >= 8
                        ? members.Where(p => !p.Equals(c, StringComparison.OrdinalIgnoreCase) && !ChildLike(p) && norm[p].StartsWith(cn + " ", StringComparison.Ordinal))
                                 .OrderBy(p => norm[p].Length).FirstOrDefault()
                        : null;
                }
                if (parent is not null) parentOf[c] = parent;
            }
            // break any cycle (a patch of a patch of the first)
            foreach (var c in parentOf.Keys.ToList())
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c };
                for (var p = parentOf.GetValueOrDefault(c); p is not null; p = parentOf.GetValueOrDefault(p))
                    if (!seen.Add(p)) { parentOf.Remove(c); break; }
            }
        }

        // ---- group: children join their parent's separator
        var groupMemo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int GroupFor(string n) => groupMemo.TryGetValue(n, out var g) ? g : groupMemo[n] = parentOf.TryGetValue(n, out var p) ? GroupFor(p) : group0[n];
        var intended = members.ToDictionary(n => n, GroupFor, StringComparer.OrdinalIgnoreCase);

        // ---- sequence: roots alphabetically, each followed by its children (depth first)
        var children = members.Where(parentOf.ContainsKey).GroupBy(n => parentOf[n], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(SortKey, StringComparer.Ordinal).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        var seqOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var counter = 0;
        void Walk(string n) { seqOf[n] = counter++; if (children.TryGetValue(n, out var cs)) foreach (var c in cs) Walk(c); }
        foreach (var root in members.Where(n => !parentOf.ContainsKey(n)).OrderBy(n => intended[n]).ThenBy(SortKey, StringComparer.Ordinal).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
            Walk(root);

        var (entries, _, _) = Place(file.Separators, members, intended, seqOf, basePos, preds, parentOf);
        var current = -1;
        var names = file.Separators.Select(s => s.Name).ToList();
        foreach (var e in entries)
        {
            if (e.IsSeparator) { current = names.IndexOf(e.Name); continue; }
            file.Separators[current].Mods.Add(new OrderedMod
            {
                Md5 = byName[e.Name].Nexus?.Md5 ?? "",
                Name = e.Name,
                Parent = parentOf.GetValueOrDefault(e.Name),
            });
        }
        return file;
    }

    /// <summary>The order a separator would have with no rule or conflict pinning anything: mods A–Z, each followed by
    /// the patches / BodySlide sets / add-ons that require it. Used to audit the shipped order.</summary>
    public static List<string> NaturalOrder(IReadOnlyList<OrderedMod> mods)
    {
        var names = new HashSet<string>(mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var kids = mods.Where(m => m.Parent is not null && names.Contains(m.Parent))
            .GroupBy(m => m.Parent!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Name).OrderBy(SortKey, StringComparer.Ordinal).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Walk(string n) { result.Add(n); if (kids.TryGetValue(n, out var cs)) foreach (var c in cs) Walk(c); }
        foreach (var r in mods.Where(m => m.Parent is null || !names.Contains(m.Parent)).Select(m => m.Name)
                     .OrderBy(SortKey, StringComparer.Ordinal).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
            Walk(r);
        return result;
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
        var basePos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < members.Count; i++) basePos[members[i]] = i;
        var intended = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seqOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unplaced = 0;
        foreach (var n in members)
        {
            var md5 = byName[n].Nexus?.Md5 ?? "";
            if ((md5.Length > 0 && shippedByMd5.TryGetValue(md5, out var v)) || shippedByName.TryGetValue(n, out v))
            { intended[n] = v.G; seqOf[n] = v.Seq; }
            else { intended[n] = GroupOf(file, byName[n]); seqOf[n] = int.MaxValue; unplaced++; }
        }
        var shippedParent = file.Separators.SelectMany(s => s.Mods).Where(m => m.Parent is not null)
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Parent!, StringComparer.OrdinalIgnoreCase);
        var (entries, pushed, fixes) = Place(file.Separators, members, intended, seqOf, basePos, preds, shippedParent);
        return (entries, new ApplyReport(members.Count, unplaced, pushed, fixes));
    }

    private static bool Reaches(List<List<int>> succ, int from, int to)
    {
        var seen = new HashSet<int> { from };
        var stack = new Stack<int>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n == to) return true;
            foreach (var s in succ[n]) if (seen.Add(s)) stack.Push(s);
        }
        return false;
    }

    /// <summary>Common placement: separators in order, constraints (rules + file conflicts) always honoured.</summary>
    private static (List<Entry> Entries, int Pushed, int Fixes) Place(
        IReadOnlyList<SeparatorDef> separators, List<string> members,
        Dictionary<string, int> intended, Dictionary<string, int> seqOf, Dictionary<string, int> basePos,
        IReadOnlyDictionary<string, HashSet<string>> preds, IReadOnlyDictionary<string, string>? parentOf = null)
    {
        // a mod goes into a later separator when something that must deploy before it sits in one
        var fin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in members)
        {
            var g = intended[n];
            if (preds.TryGetValue(n, out var ps)) foreach (var p in ps) if (fin.TryGetValue(p, out var pg)) g = Math.Max(g, pg);
            fin[n] = g;
        }
        var pushed = members.Count(n => fin[n] != intended[n]);

        var tentative = members.OrderBy(n => fin[n]).ThenBy(n => fin[n] == intended[n] ? seqOf[n] : int.MaxValue).ThenBy(n => basePos[n]).ToList();

        // enforce the constraints inside the tentative order (stable topological pass)
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tentative.Count; i++) index[tentative[i]] = i;
        var indeg = new int[tentative.Count];
        var succ = Enumerable.Range(0, tentative.Count).Select(_ => new List<int>()).ToList();
        foreach (var (later, ps) in preds)
        {
            if (!index.TryGetValue(later, out var li)) continue;
            foreach (var p in ps) if (index.TryGetValue(p, out var pi)) { succ[pi].Add(li); indeg[li]++; }
        }
        // a patch / BodySlide set / add-on deploys right after the mod it requires — unless a rule or a real
        // file conflict says the opposite (then the constraint wins)
        if (parentOf is not null)
            foreach (var (child, parent) in parentOf)
            {
                if (!index.TryGetValue(child, out var ci) || !index.TryGetValue(parent, out var pi)) continue;
                if (Reaches(succ, ci, pi)) continue;
                succ[pi].Add(ci);
                indeg[ci]++;
            }
        var ready = new SortedSet<int>();
        for (var i = 0; i < tentative.Count; i++) if (indeg[i] == 0) ready.Add(i);
        var ordered = new List<string>(tentative.Count);
        var done = new bool[tentative.Count];
        while (ordered.Count < tentative.Count)
        {
            if (ready.Count == 0)   // cannot happen for acyclic constraints; never loop forever
            {
                var first = Array.IndexOf(done, false);
                ready.Add(first);
            }
            var n = ready.Min; ready.Remove(n);
            if (done[n]) continue;
            done[n] = true;
            ordered.Add(tentative[n]);
            foreach (var s in succ[n]) if (--indeg[s] == 0 && !done[s]) ready.Add(s);
        }
        var fixes = 0;
        for (var i = 0; i < ordered.Count; i++) if (!ordered[i].Equals(tentative[i], StringComparison.OrdinalIgnoreCase)) fixes++;

        // emit separators + mods; a mod pulled later by the topological pass joins the separator it ends up under
        var entries = new List<Entry>(ordered.Count + separators.Count);
        var lastGroup = -1;
        var runningMax = 0;
        foreach (var n in ordered)
        {
            runningMax = Math.Max(runningMax, fin[n]);
            if (runningMax != lastGroup)
            {
                entries.Add(new Entry(separators[runningMax].Name, true));
                lastGroup = runningMax;
            }
            entries.Add(new Entry(n, false));
        }
        // separators that are always present (e.g. the empty "Added mods" one at the top) even with no mods
        for (var g = lastGroup + 1; g < separators.Count; g++)
            if (separators[g].AlwaysShow) entries.Add(new Entry(separators[g].Name, true));
        return (entries, pushed, fixes);
    }
}
