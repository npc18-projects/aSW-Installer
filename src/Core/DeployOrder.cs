using System.Text.RegularExpressions;

namespace AStoryWealth.Core;

/// <summary>
/// Mirrors Vortex's deploy order onto an MO2 priority list.
///
/// Vortex collections do not ship a final mod order. They ship (a) an install
/// phase per mod and (b) before/after modRules, and Vortex derives the deploy
/// order from those. This class does the same derivation:
///   1. baseline = install phase ascending, then collection array order;
///   2. before/after rules become edges and the baseline is stable-toposorted;
///   3. a rule cycle drops only the edges that close the cycle (logged).
///
/// Everything here works in ASCENDING PRIORITY: index 0 is the lowest priority,
/// the last entry wins file conflicts (the Vortex "later wins" model). The
/// MO2 file writer is the only place that reverses this for modlist.txt.
/// </summary>
public static class DeployOrder
{
    public sealed record Report(
        int Rules, int EdgesApplied, int Unresolved, int CyclesBroken,
        List<string> UnresolvedDetails, List<string> CycleDetails);

    public sealed record Node(string Name, int Phase, int Index);

    /// <summary>
    /// Orders <paramref name="existing"/> (already-installed folders, order kept) followed by the
    /// plan's mods, then applies the rules.
    /// </summary>
    public static (List<string> Order, Report Report) Compute(
        IReadOnlyList<string> existing, IReadOnlyList<AddonManifest> addons)
    {
        var planned = new List<Node>();
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var idx = 0;
        foreach (var a in addons.OrderBy(a => a.InstallPriority))
            foreach (var m in a.Mods)
                if (!string.IsNullOrEmpty(m.InstallAs) && seen.Add(m.InstallAs))
                    planned.Add(new Node(m.InstallAs, m.Phase, idx++));

        // Installed earlier = lower priority. Phase is the author's install ordering.
        var nodes = new List<Node>();
        for (var i = 0; i < existing.Count; i++)
            nodes.Add(new Node(existing[i], int.MinValue, i));
        nodes.AddRange(planned.OrderBy(n => n.Phase).ThenBy(n => n.Index));

        // Rules may reference mods installed by an earlier run, so the resolver
        // indexes every addon's mods, not just the new ones.
        var known = addons.SelectMany(a => a.Mods).Where(m => !string.IsNullOrEmpty(m.InstallAs)).ToList();
        var rules = addons.SelectMany(a => a.ModRules ?? []).ToList();
        return Sort(nodes, known, rules);
    }

    /// <summary>The before/after modRules as (earlier, later) install-folder pairs — "later" deploys
    /// over "earlier". Rules whose endpoints are not in <paramref name="known"/> are dropped.</summary>
    public static List<(string Earlier, string Later)> RuleEdges(IReadOnlyList<ModEntry> known, IReadOnlyList<ModRule> rules)
    {
        var resolver = new RefResolver(known);
        var edges = new List<(string, string)>();
        foreach (var r in rules)
        {
            if (r.Type is not ("before" or "after")) continue;
            var src = resolver.Resolve(r.Source);
            var rf = resolver.Resolve(r.Reference);
            foreach (var s in src)
                foreach (var t in rf)
                    if (!s.Equals(t, StringComparison.OrdinalIgnoreCase))
                        edges.Add(r.Type == "after" ? (t, s) : (s, t));
        }
        return edges;
    }

    public static (List<string> Order, Report Report) Sort(
        IReadOnlyList<Node> nodes, IReadOnlyList<ModEntry> known, IReadOnlyList<ModRule> rules)
    {
        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nodes.Count; i++) pos.TryAdd(nodes[i].Name, i);

        var resolver = new RefResolver(known);
        var succ = Enumerable.Range(0, nodes.Count).Select(_ => new HashSet<int>()).ToList();
        var indeg = new int[nodes.Count];
        int applied = 0, ruleCount = 0;
        var unresolved = new List<string>();

        void Edge(int earlier, int later)
        {
            if (earlier == later) return;
            if (succ[earlier].Add(later)) { indeg[later]++; applied++; }
        }

        foreach (var r in rules)
        {
            if (r.Type is not ("before" or "after")) continue;
            ruleCount++;
            var src = resolver.Resolve(r.Source);
            var rf = resolver.Resolve(r.Reference);
            if (src.Count == 0 || rf.Count == 0)
            {
                unresolved.Add($"{r.Type}: {Describe(r.Source)} -> {Describe(r.Reference)}"
                    + (src.Count == 0 ? " [source unmatched]" : " [reference unmatched]"));
                continue;
            }
            foreach (var s in src)
                foreach (var t in rf)
                {
                    if (!pos.TryGetValue(s, out var si) || !pos.TryGetValue(t, out var ti)) continue;
                    // "after": source overrides reference -> source later (higher priority).
                    if (r.Type == "after") Edge(ti, si); else Edge(si, ti);
                }
        }

        // Stable Kahn: always release the lowest baseline position first.
        var ready = new SortedSet<int>();
        for (var i = 0; i < nodes.Count; i++) if (indeg[i] == 0) ready.Add(i);
        var done = new bool[nodes.Count];
        var order = new List<string>(nodes.Count);
        var cycles = new List<string>();
        while (order.Count < nodes.Count)
        {
            if (ready.Count == 0)
            {
                // Cycle: free the earliest-baseline blocked node by dropping the
                // edges that block it.
                var victim = Enumerable.Range(0, nodes.Count).First(i => !done[i]);
                var blockers = Enumerable.Range(0, nodes.Count).Where(i => !done[i] && succ[i].Contains(victim)).ToList();
                foreach (var b in blockers) { succ[b].Remove(victim); indeg[victim]--; }
                cycles.Add($"{nodes[victim].Name} (ignored order vs: {string.Join(", ", blockers.Select(b => nodes[b].Name))})");
                if (indeg[victim] == 0) ready.Add(victim);
                continue;
            }
            var n = ready.Min; ready.Remove(n);
            done[n] = true;
            order.Add(nodes[n].Name);
            foreach (var s in succ[n])
                if (--indeg[s] == 0 && !done[s]) ready.Add(s);
        }
        return (order, new Report(ruleCount, applied, unresolved.Count, cycles.Count, unresolved, cycles));
    }

    private static string Describe(ModRuleRef? r) =>
        r is null ? "?" : r.LogicalFileName ?? r.FileExpression ?? r.FileMd5 ?? r.IdHint ?? "?";

    /// <summary>Resolves a Vortex rule reference to install folders: md5, then exact
    /// logical file name, then file expression (wildcards).</summary>
    private sealed class RefResolver
    {
        private readonly Dictionary<string, List<string>> _md5 = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _logical = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Logical, string Name)> _all = [];
        private readonly List<(string Prefix, string Name)> _prefix = [];

        public RefResolver(IEnumerable<ModEntry> mods)
        {
            foreach (var m in mods)
            {
                if (m.Nexus?.Md5 is { Length: > 0 } md5) Add(_md5, md5, m.InstallAs);
                var logical = m.LogicalFileName;
                if (!string.IsNullOrEmpty(logical))
                {
                    Add(_logical, logical, m.InstallAs);
                    _all.Add((logical, m.InstallAs));
                    // Vortex mod ids / idHints look like "<logical>-<nexusModId>-<version>-<timestamp>"
                    if (m.Nexus is { ModId: > 0 } nx) _prefix.Add(($"{logical}-{nx.ModId}-", m.InstallAs));
                }
            }
        }

        private static void Add(Dictionary<string, List<string>> d, string k, string v)
        {
            if (!d.TryGetValue(k, out var l)) d[k] = l = [];
            if (!l.Contains(v, StringComparer.OrdinalIgnoreCase)) l.Add(v);
        }

        public List<string> Resolve(ModRuleRef? r)
        {
            if (r is null) return [];
            if (r.FileMd5 is { Length: > 0 } md5 && _md5.TryGetValue(md5, out var byMd5)) return byMd5;
            foreach (var id in new[] { r.IdHint, r.FileExpression })
                if (id is { Length: > 0 })
                {
                    var hit = _prefix.Where(x => id.StartsWith(x.Prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (hit.Count > 0) return hit;
                }
            if (r.LogicalFileName is { Length: > 0 } ln && _logical.TryGetValue(ln, out var byName)) return byName;
            if (r.FileExpression is { Length: > 0 } expr)
            {
                var rx = ToRegex(expr);
                var hit = _all.Where(x => rx.IsMatch(x.Logical)).Select(x => x.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (hit.Count > 0) return hit;
            }
            return [];
        }

        private static Regex ToRegex(string expr) =>
            new("^" + Regex.Escape(expr).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
