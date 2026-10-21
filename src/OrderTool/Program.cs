using AStoryWealth.Core;

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
