using System.Text;
using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Load order for plugins.txt. The collection's plugin list is the baseline
/// (Vortex's resolved order); this class only moves a plugin when the engine
/// would otherwise break:
///   - a master-flagged plugin (ESM) loads before every non-master;
///   - a plugin's masters load before it;
///   - explicit pluginRules (loadAfter) from the collection are honoured.
/// Everything else keeps the collection's order (stable topological sort).
/// </summary>
public static class PluginOrder
{
    public sealed record Info(bool IsMaster, bool IsLight, List<string> Masters);

    public static IReadOnlyList<string> VanillaMasters => Vanilla;

    private static readonly string[] Vanilla =
    [
        "Fallout4.esm", "DLCRobot.esm", "DLCworkshop01.esm", "DLCCoast.esm",
        "DLCworkshop02.esm", "DLCworkshop03.esm", "DLCNukaWorld.esm",
    ];

    /// <summary>Reads the TES4 header of a Fallout 4 plugin; null when unreadable.</summary>
    public static Info? ReadHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[24];
            if (fs.Read(head) < 24 || !head[..4].SequenceEqual("TES4"u8)) return null;
            var size = BitConverter.ToUInt32(head[4..8]);
            var flags = BitConverter.ToUInt32(head[8..12]);
            if (size > 16 * 1024 * 1024) return null;
            var data = new byte[size];
            fs.ReadExactly(data);
            var masters = new List<string>();
            for (var p = 0; p + 6 <= data.Length;)
            {
                var type = Encoding.ASCII.GetString(data, p, 4);
                int len = BitConverter.ToUInt16(data, p + 4);
                p += 6;
                if (type == "XXXX" && p + 4 <= data.Length) { p += len; continue; }
                if (p + len > data.Length) break;
                if (type == "MAST")
                    masters.Add(Encoding.UTF8.GetString(data, p, len).TrimEnd('\0'));
                p += len;
            }
            return new Info((flags & 0x1) != 0, (flags & 0x200) != 0, masters);
        }
        catch { return null; }
    }

    /// <summary>Extracts "A loads after B" pairs from a pluginRules blob. Accepts
    /// {plugins:[...]} or a bare array of {name, after:[...]} / {name, loadAfter:[...]}.</summary>
    public static List<(string Plugin, string After)> ParseRules(string? json)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("plugins", out var p)) root = p;
            if (root.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in root.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var name = Str(e, "name") ?? Str(e, "plugin");
                if (name is null) continue;
                foreach (var key in new[] { "after", "loadAfter" })
                    if (e.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var a in arr.EnumerateArray())
                        {
                            var other = a.ValueKind == JsonValueKind.String ? a.GetString()
                                : a.ValueKind == JsonValueKind.Object ? Str(a, "name") : null;
                            if (!string.IsNullOrEmpty(other)) result.Add((name, other));
                        }
            }
        }
        catch (JsonException) { }
        return result;

        static string? Str(JsonElement o, string k) =>
            o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static (List<string> Order, List<string> Notes) Arrange(
        IReadOnlyList<string> desired,
        Func<string, Info?> info,
        IReadOnlyList<(string Plugin, string After)> rules)
    {
        var notes = new List<string>();
        var dupes = desired.GroupBy(d => d, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
        if (dupes > 0) notes.Add($"{dupes} plugin name(s) listed by more than one mod (replacer plugins) — one slot each, header read from the highest-priority mod");
        desired = desired.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // vanilla masters first, in their fixed order, then the collection order
        var baseline = new List<string>();
        foreach (var v in Vanilla)
        {
            var hit = desired.FirstOrDefault(d => d.Equals(v, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) baseline.Add(hit);
        }
        baseline.AddRange(desired.Where(d => !baseline.Contains(d, StringComparer.OrdinalIgnoreCase)));

        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < baseline.Count; i++) idx.TryAdd(baseline[i], i);

        var infos = baseline.Select(info).ToArray();
        var unknown = infos.Count(i => i is null);
        if (unknown > 0) notes.Add($"{unknown} plugin(s) have no readable header (not on disk yet?) — kept at collection position");

        var succ = Enumerable.Range(0, baseline.Count).Select(_ => new HashSet<int>()).ToList();
        var indeg = new int[baseline.Count];
        void Edge(int a, int b)
        {
            if (a != b && succ[a].Add(b)) indeg[b]++;
        }

        // The collection list is already a resolved (LOOT-sorted) order. Only
        // move a plugin when a header proves the engine would reject it.
        for (var i = 0; i < baseline.Count; i++)
        {
            if (infos[i] is null) continue;
            foreach (var m in infos[i]!.Masters)
            {
                if (idx.TryGetValue(m, out var mi)) Edge(mi, i);
                else if (!IsImplicit(m)) notes.Add($"{baseline[i]} needs master {m}, which is not in the plugin list");
            }
        }
        // ESM-flagged plugins load before every non-master.
        
        for (var j = 0; j < baseline.Count; j++)
        {
            if (infos[j] is null) continue;
            if (IsMasterLike(infos[j]!, baseline[j]))
            {
                for (var i = 0; i < j; i++)
                    if (infos[i] is { } ii && !IsMasterLike(ii, baseline[i])) Edge(j, i);
            }

        }
        foreach (var (plugin, after) in rules)
            if (idx.TryGetValue(plugin, out var pi) && idx.TryGetValue(after, out var ai)) Edge(ai, pi);

        // Stable Kahn keyed on baseline position.
        var ready = new SortedSet<int>();
        for (var i = 0; i < baseline.Count; i++) if (indeg[i] == 0) ready.Add(i);
        var done = new bool[baseline.Count];
        var order = new List<string>(baseline.Count);
        while (order.Count < baseline.Count)
        {
            if (ready.Count == 0)
            {
                var victim = Enumerable.Range(0, baseline.Count).First(i => !done[i]);
                notes.Add($"master/rule cycle involving {baseline[victim]} — constraint dropped");
                indeg[victim] = 0;
                ready.Add(victim);
                continue;
            }
            var n = ready.Min; ready.Remove(n);
            if (done[n]) continue;
            done[n] = true;
            order.Add(baseline[n]);
            foreach (var s in succ[n])
                if (--indeg[s] == 0 && !done[s]) ready.Add(s);
        }

        var moved = 0;
        for (var i = 0; i < Math.Min(desired.Count, order.Count); i++) if (!desired[i].Equals(order[i], StringComparison.OrdinalIgnoreCase)) moved++;
        if (moved > 0) notes.Insert(0, $"{moved} plugin position(s) adjusted to satisfy masters / ESM-first / pluginRules");
        return (order, notes);
    }

    /// <summary>Master = ESM flag in the header. The sample collection's LOOT order puts
    /// unflagged .esl files among the ESPs, so the extension alone does not make a master.</summary>
    private static bool IsMasterLike(Info i, string name) =>
        i.IsMaster;

    private static bool IsImplicit(string master) =>
        Vanilla.Contains(master, StringComparer.OrdinalIgnoreCase)
        || master.StartsWith("cc", StringComparison.OrdinalIgnoreCase);
}
