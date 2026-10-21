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
return 0;
