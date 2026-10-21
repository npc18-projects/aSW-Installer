using System.Text.Json;
using AStoryWealth.Core;

// Mode 2 — generate the optional install groups (Sim Settlements 2 + HD textures):
//   OrderTool optional <base collection.json> <filter collection.json> <instanceDir> <addonId> <out.optional.json>
// The SS2 group follows a "filter collection" that lists every mod to disable to play without SS2
// (nexus 4rfeex, "A StoryWealth - DeSimulated 2"): exact file matches plus anything that shares a logical file name,
// then everything whose plugins need an SS2-group plugin as a master (so no mod is left with a missing master).
if (args.Length >= 1 && args[0] == "optional")
{
    if (args.Length < 6) { Console.Error.WriteLine("usage: OrderTool optional <base.json> <filter.json> <instanceDir> <addonId> <out>"); return 2; }
    var man = VortexCollectionMapper.Map(args[1], args[4]);
    var core = man.Mods.Where(m => !m.IsOptional).ToList();
    var filter = JsonDocument.Parse(File.ReadAllText(args[2])).RootElement.GetProperty("mods").EnumerateArray().ToList();
    var fMd5 = filter.Select(m => m.GetProperty("source").GetProperty("md5").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var fNames = filter.Select(m => m.GetProperty("source").GetProperty("logicalFilename").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var set = new HashSet<string>(core.Where(m => (m.Nexus?.Md5 is { } h && fMd5.Contains(h)) || (m.LogicalFileName is { } ln && fNames.Contains(ln)))
        .Select(m => m.InstallAs), StringComparer.OrdinalIgnoreCase);
    var seedCount = set.Count;
    var sig = PriorityOrder.ScanSignals(args[3], core);
    for (var changed = true; changed;)
    {
        changed = false;
        var plugins = sig.Provider.Where(kv => set.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in core.Where(m => !set.Contains(m.InstallAs)))
            if (sig.Masters.TryGetValue(m.InstallAs, out var ms) && ms.Any(plugins.Contains)) { set.Add(m.InstallAs); changed = true; }
    }
    var ss2 = core.Where(m => set.Contains(m.InstallAs)).OrderBy(m => m.InstallAs, StringComparer.OrdinalIgnoreCase).ToList();
    var hd = man.Mods.Where(m => m.IsOptional).ToList();
    double Gb(IEnumerable<ModEntry> ms) => ms.Sum(m => m.Nexus?.FileSize ?? 0) / 1e9;
    var groupsFile = new OptionalGroupsFile
    {
        AddonId = args[4],
        Groups =
        [
            new OptionalGroup
            {
                Id = "ss2", Name = "Sim Settlements 2", DefaultOn = true,
                Description = $"SS2, its chapters, add-on packs and compatibility patches ({ss2.Count} mods, about {Gb(ss2):F0} GB). Untick to play without it.",
                Source = "A StoryWealth - DeSimulated 2 filter collection (nexus 4rfeex) + mods that need an SS2 plugin as a master",
                Mods = ss2.Select(m => new OrderedMod { Md5 = m.Nexus?.Md5 ?? "", Name = m.InstallAs }).ToList(),
            },
            new OptionalGroup
            {
                Id = "hd-textures", Name = "Optional HD textures", DefaultOn = false, FromOptionalFlag = true,
                Description = $"{hd.Count} high-resolution texture packs (about {Gb(hd):F0} GB). Recommended with 6 GB+ of VRAM.",
                Source = "the collection's own optional mods",
            },
        ],
    };
    OptionalGroups.Save(groupsFile, args[5]);
    Console.WriteLine($"wrote {args[5]}: ss2 = {ss2.Count} mods ({seedCount} from the filter collection, {ss2.Count - seedCount} pulled in by plugin masters), {Gb(ss2):F1} GB; hd-textures = {hd.Count} mods, {Gb(hd):F1} GB");
    return 0;
}


// Generates the shipped structured priority order for a collection.
//   dotnet run --project src/OrderTool -- <collection.json> <instanceDir> <addonId> <out.priority.json>
// <instanceDir> must hold the materialized mods (mods/<installAs>/...) so real file conflicts can be found.
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: OrderTool <collection.json> <instanceDir> <addonId> <out.priority.json>");
    return 2;
}
var (collection, instance, addonId, outPath) = (args[0], args[1], args[2], args[3]);

var manifest = VortexCollectionMapper.Map(collection, addonId);
var mods = manifest.Mods.Where(m => !m.IsOptional).ToList();
var (baseline, rep) = DeployOrder.Compute([], [manifest]);
baseline = baseline.Where(n => mods.Any(m => m.InstallAs.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();

var ruleEdges = DeployOrder.RuleEdges(manifest.Mods, manifest.ModRules ?? []);
var preds = PriorityOrder.Constraints(instance, mods, baseline, ruleEdges);
Console.WriteLine($"baseline: {baseline.Count} mods; constraints on {preds.Count} mods ({preds.Values.Sum(s => s.Count)} edges); "
    + $"{rep.Rules} rules, {rep.Unresolved} inert");

var signals = PriorityOrder.ScanSignals(instance, mods);
var file = PriorityOrder.Build(addonId, mods, baseline, preds, null, signals);
PriorityOrder.Save(file, outPath);

var (entries, report) = PriorityOrder.Apply(file, mods, baseline, preds);
Console.WriteLine($"wrote {outPath}");
Console.WriteLine($"self-check: {report.Mods} mods, {report.Unplaced} unplaced, {report.PushedLater} pushed later, {report.OrderFixes} reordered");
Console.WriteLine();
foreach (var s in file.Separators.Where(s => s.Mods.Count > 0))
    Console.WriteLine($"  {s.Mods.Count,4}  {s.Name}");
var withParent = file.Separators.SelectMany(s => s.Mods).Count(m => m.Parent is not null);
Console.WriteLine();
Console.WriteLine($"{withParent} mods placed directly after the mod they require");
foreach (var s in file.Separators.Where(s => s.Name == "Patches"))
    Console.WriteLine($"orphan patches left in Patches: {s.Mods.Count}");
// audit: how many mods sit away from strict A-Z (+ children right after their parent)? A mod is "displaced" when it is
// not part of the longest run already in natural order, i.e. it would have to move for the separator to be perfectly sorted.
static int Displaced(List<int> seq)
{
    var tails = new List<int>();
    foreach (var x in seq)
    {
        var i = tails.BinarySearch(x);
        if (i < 0) i = ~i;
        if (i == tails.Count) tails.Add(x); else tails[i] = x;
    }
    return seq.Count - tails.Count;
}
var off = 0; var total = 0;
foreach (var s in file.Separators)
{
    var natural = PriorityOrder.NaturalOrder(s.Mods);
    var rank = natural.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
    var seq = s.Mods.Select(m => rank[m.Name]).ToList();
    var d = Displaced(seq);
    total += seq.Count; off += d;
    if (d > 0) Console.WriteLine($"  {s.Name}: {d} of {seq.Count} mods displaced from A-Z by a rule/file conflict");
}
Console.WriteLine($"alphabetical audit: {total - off}/{total} mods in strict A-Z + parent-first order; {off} displaced by a rule or file conflict");
return 0;
