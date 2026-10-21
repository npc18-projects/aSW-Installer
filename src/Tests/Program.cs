using System.Text;
using AStoryWealth.Core;

// Dependency-free test runner: `dotnet run --project src/Tests` exits non-zero on failure.
var failed = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail is null ? "" : "  -> " + detail)}");
    if (!ok) failed++;
}
string J(IEnumerable<string> s) => string.Join(" | ", s);

ModEntry Mod(string name, int phase = 0, string? md5 = null, string? logical = null) => new()
{
    InstallAs = name, Phase = phase, LogicalFileName = logical,
    Nexus = new NexusFileRef { ModId = 1, FileId = 1, Md5 = md5 },
};
ModRule Rule(string type, string? srcMd5, string? refMd5, string? srcName = null, string? refName = null, string? refExpr = null) => new()
{
    Type = type,
    Source = new ModRuleRef { FileMd5 = srcMd5, LogicalFileName = srcName },
    Reference = new ModRuleRef { FileMd5 = refMd5, LogicalFileName = refName, FileExpression = refExpr },
};
AddonManifest Addon(List<ModEntry> mods, List<ModRule>? rules = null) => new() { AddonId = "t", Mods = mods, ModRules = rules };

// ---- deploy order
{
    var (o, _) = DeployOrder.Compute([], [Addon([Mod("A", 2), Mod("B", 0), Mod("C", 1)])]);
    Check("phase drives baseline order", J(o) == "B | C | A", J(o));
}
{
    var (o, _) = DeployOrder.Compute([], [Addon([Mod("A"), Mod("B"), Mod("C")])]);
    Check("no rules keeps array order", J(o) == "A | B | C", J(o));
}
{
    // C "after" A: C must deploy later than A (already true) ; A "after" C flips them
    var (o, r) = DeployOrder.Compute([], [Addon([Mod("A", md5: "a"), Mod("B", md5: "b"), Mod("C", md5: "c")],
        [Rule("after", "a", "c")])]);
    Check("after: source moves behind reference", J(o) == "B | C | A", J(o));
    Check("after: edge counted", r.EdgesApplied == 1 && r.Unresolved == 0);
}
{
    var (o, _) = DeployOrder.Compute([], [Addon([Mod("A", md5: "a"), Mod("B", md5: "b"), Mod("C", md5: "c")],
        [Rule("before", "c", "a")])]);
    Check("before: source deploys ahead of reference", o.IndexOf("C") < o.IndexOf("A"), J(o));
}
{
    var (o, r) = DeployOrder.Compute([], [Addon(
        [Mod("A", logical: "Alpha"), Mod("B", logical: "Beta"), Mod("C", logical: "Beta Patch")],
        [Rule("after", null, null, "Alpha", null, "Beta*")])]);
    Check("logical name + file expression resolve", J(o) == "B | C | A" && r.Unresolved == 0, J(o) + " unresolved=" + r.Unresolved);
}
{
    var (_, r) = DeployOrder.Compute([], [Addon([Mod("A", md5: "a")], [Rule("after", "a", "zzz")])]);
    Check("unresolved rule is reported", r.Unresolved == 1 && r.UnresolvedDetails[0].Contains("reference unmatched"));
}
{
    var (o, r) = DeployOrder.Compute([], [Addon([Mod("A", md5: "a"), Mod("B", md5: "b")],
        [Rule("after", "a", "b"), Rule("after", "b", "a")])]);
    Check("cycle is broken, not fatal", o.Count == 2 && r.CyclesBroken >= 1, J(o));
}
{
    var (o, _) = DeployOrder.Compute(["Old1", "Old2"], [Addon([Mod("New", 0), Mod("Old1")])]);
    Check("existing mods keep position, new appended", J(o) == "Old1 | Old2 | New", J(o));
}

// ---- plugin order
{
    PluginOrder.Info? Info(string n) => n switch
    {
        "Mod.esp" => new(false, false, ["Lib.esm"]),
        "Lib.esm" => new(true, false, []),
        "Other.esp" => new(false, false, []),
        _ => null,
    };
    var (o, _) = PluginOrder.Arrange(["Other.esp", "Mod.esp", "Lib.esm"], Info, []);
    Check("masters and ESM load before dependents", J(o) == "Lib.esm | Other.esp | Mod.esp", J(o));
    var (o2, _) = PluginOrder.Arrange(["Other.esp", "Mod.esp", "Lib.esm"], Info, [("Other.esp", "Mod.esp")]);
    Check("pluginRules loadAfter honoured", J(o2) == "Lib.esm | Mod.esp | Other.esp", J(o2));
    var (o3, _) = PluginOrder.Arrange(["Mod.esp", "DLCCoast.esm", "Fallout4.esm"], _ => null, []);
    Check("vanilla masters first in fixed order", o3[0] == "Fallout4.esm" && o3[1] == "DLCCoast.esm", J(o3));
}
{
    var rules = PluginOrder.ParseRules("""{"plugins":[{"name":"B.esp","after":["A.esp",{"name":"C.esp"}]}]}""");
    Check("pluginRules parsing", rules.Count == 2 && rules[0] == ("B.esp", "A.esp") && rules[1] == ("B.esp", "C.esp"));
}

// ---- MO2 files end to end
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        void WritePlugin(string mod, string file, bool master, params string[] masters)
        {
            var d = Path.Combine(dir, "mods", mod); Directory.CreateDirectory(d);
            var sub = new List<byte>();
            foreach (var m in masters)
            {
                var b = Encoding.UTF8.GetBytes(m + "\0");
                sub.AddRange("MAST"u8.ToArray()); sub.AddRange(BitConverter.GetBytes((ushort)b.Length)); sub.AddRange(b);
                sub.AddRange("DATA"u8.ToArray()); sub.AddRange(BitConverter.GetBytes((ushort)8)); sub.AddRange(new byte[8]);
            }
            var rec = new List<byte>();
            rec.AddRange("TES4"u8.ToArray()); rec.AddRange(BitConverter.GetBytes((uint)sub.Count));
            rec.AddRange(BitConverter.GetBytes(master ? 1u : 0u)); rec.AddRange(new byte[12]); rec.AddRange(sub);
            File.WriteAllBytes(Path.Combine(d, file), rec.ToArray());
        }
        WritePlugin("Dep", "Dep.esp", false, "Lib.esm");
        WritePlugin("Lib", "Lib.esm", true);
        WritePlugin("Off", "Off.esp", false);
        WritePlugin("Gone", "Gone.esp", false);   // present on disk only in a mod this run does not install

        var addon = Addon([Mod("Dep", 0), Mod("Lib", 0), Mod("Off", 0)]);
        addon.Plugins = [new PluginEntry { Plugin = "Dep.esp" }, new PluginEntry { Plugin = "Lib.esm" }, new PluginEntry { Plugin = "Off.esp", Enabled = false }, new PluginEntry { Plugin = "Missing2k.esp" }];
        var plan = new ManifestResolver().Resolve([addon]);
        Mo2InstanceWriter.WriteLoadFiles(dir, plan);

        var prof = Path.Combine(dir, "profiles", "A StoryWealth");
        var ml = File.ReadAllLines(Path.Combine(prof, "modlist.txt")).Where(l => !l.StartsWith('#')).ToArray();
        Check("modlist.txt is highest-priority first", J(ml) == "+Off | +Lib | +Dep", J(ml));
        var pl = File.ReadAllLines(Path.Combine(prof, "plugins.txt")).Where(l => !l.StartsWith('#')).ToArray();
        Check("plugins.txt: master first, enabled starred, disabled bare", J(pl) == "*Lib.esm | *Dep.esp | Off.esp", J(pl));
        Check("loadorder.txt written", File.ReadAllLines(Path.Combine(prof, "loadorder.txt")).Where(l => !l.StartsWith('#')).SequenceEqual(["Lib.esm", "Dep.esp", "Off.esp"]));

        Check("plugin without a file is left out of the lists", !pl.Contains("*Missing2k.esp") && !pl.Contains("Missing2k.esp"));

        // re-run (addon merge): order and enabled state stable
        Mo2InstanceWriter.WriteLoadFiles(dir, plan);
        var ml2 = File.ReadAllLines(Path.Combine(prof, "modlist.txt")).Where(l => !l.StartsWith('#')).ToArray();
        Check("rerun is idempotent", J(ml2) == J(ml));

        // legacy headerless file (older builds wrote ascending order) is read as ascending
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+Dep\n+Lib\n");
        Mo2InstanceWriter.WriteLoadFiles(dir, plan);
        var ml3 = File.ReadAllLines(Path.Combine(prof, "modlist.txt")).Where(l => !l.StartsWith('#')).ToArray();
        Check("legacy modlist migrated", J(ml3) == "+Off | +Lib | +Dep", J(ml3));
        // stale profile: this run's mods come back enabled, other mods keep their state
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "# This file was automatically generated by Mod Organizer.\n-Dep\n-Gone\n+Lib\n");
        Mo2InstanceWriter.WriteLoadFiles(dir, plan);
        var mls = File.ReadAllLines(Path.Combine(prof, "modlist.txt")).Where(l => !l.StartsWith('#')).ToArray();
        Check("stale disabled state of this run's mods is reset", mls.Contains("+Dep") && mls.Contains("+Lib"), J(mls));
        Check("mods outside the run keep their disabled state", mls.Contains("-Gone"), J(mls));

    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- structured priority order
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        ModEntry CatMod(string name, string cat, string md5) { var m = Mod(name, 0, md5); m.Category = cat; return m; }
        var scheme = new List<SeparatorDef>
        {
            new() { Name = "Fixes", Categories = ["Bug Fixes"] },
            new() { Name = "Gameplay", Categories = ["Gameplay"] },
            new() { Name = "Weapons", Categories = ["Weapons"] },
        };
        var fileScheme = new PriorityOrderFile { AddonId = "t", Separators = scheme };
        // baseline (Vortex-derived) order is NOT grouped by category
        var mods = new List<ModEntry> { CatMod("W1", "Weapons", "w1"), CatMod("F1", "Bug Fixes", "f1"), CatMod("G1", "Gameplay", "g1"), CatMod("F2", "Bug Fixes", "f2") };
        var baseline = new List<string> { "W1", "F1", "G1", "F2" };
        var noPreds = new Dictionary<string, HashSet<string>>();

        var built = PriorityOrder.Build("t", mods, baseline, noPreds, scheme);
        var (entries, rep) = PriorityOrder.Apply(built, mods, baseline, noPreds);
        string Flat(IEnumerable<PriorityOrder.Entry> e) => J(e.Select(x => x.IsSeparator ? $"[{x.Name}]" : x.Name));
        Check("structure: mods grouped under ordered separators", Flat(entries) == "[Fixes] | F1 | F2 | [Gameplay] | G1 | [Weapons] | W1", Flat(entries));
        Check("structure: nothing moves without constraints", rep.PushedLater == 0 && rep.Unplaced == 0);

        // F2 must deploy after W1 (file conflict / rule): it can only get there by joining the later separator
        var preds = new Dictionary<string, HashSet<string>> { ["F2"] = ["W1"] };
        var built2 = PriorityOrder.Build("t", mods, baseline, preds, scheme);
        var (e2, r2) = PriorityOrder.Apply(built2, mods, baseline, preds);
        var f = Flat(e2);
        Check("structure: constraint pushes the later mod into a later separator, never flips the winner",
            f.IndexOf("W1", StringComparison.Ordinal) < f.IndexOf("F2", StringComparison.Ordinal) && built2.Separators[2].Mods.Any(m => m.Name == "F2"), f);

        // a mod the shipped file does not know goes to its category's separator
        var withNew = new List<ModEntry>(mods) { CatMod("G2", "Gameplay", "g2") };
        var (e3, r3) = PriorityOrder.Apply(built, withNew, [.. baseline, "G2"], noPreds);
        Check("structure: unknown mod is placed by category", Flat(e3).Contains("[Gameplay] | G1 | G2") && r3.Unplaced == 1, Flat(e3));

        // end to end through the profile writer (a shipped order for addon id 'ordtest' next to the app)
        var orderDir = Path.Combine(AppContext.BaseDirectory, "order"); Directory.CreateDirectory(orderDir);
        var shippedPath = Path.Combine(orderDir, "ordtest.priority.json");
        try
        {
            var built3 = PriorityOrder.Build("ordtest", mods, baseline, noPreds, scheme);
            PriorityOrder.Save(built3, shippedPath);
            var addon = new AddonManifest { AddonId = "ordtest", Mods = mods };
            var plan = new ManifestResolver().Resolve([addon]);
            Mo2InstanceWriter.WriteLoadFiles(dir, plan);
            var lines = File.ReadAllLines(Path.Combine(dir, "profiles", "A StoryWealth", "modlist.txt")).Where(l => !l.StartsWith('#')).ToArray();
            Check("each separator has its MO2 mod folder + meta.ini (else MO2 drops it)",
                new[] { "Fixes", "Gameplay", "Weapons" }.All(n => File.Exists(Path.Combine(dir, "mods", n + "_separator", "meta.ini"))));
            Check("modlist.txt carries MO2 separators (highest priority first)",
                J(lines) == "+W1 | -Weapons_separator | +G1 | -Gameplay_separator | +F2 | +F1 | -Fixes_separator", J(lines));
            Mo2InstanceWriter.WriteLoadFiles(dir, plan);   // rerun must not duplicate separators
            var again = File.ReadAllLines(Path.Combine(dir, "profiles", "A StoryWealth", "modlist.txt")).Where(l => !l.StartsWith('#')).ToArray();
            Check("separators are not duplicated on rerun", J(again) == J(lines), J(again));
        }
        finally { try { File.Delete(shippedPath); } catch { } }
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- hash-guided layout repair
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        string Md5(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(b));
        var a = "alpha"u8.ToArray(); var b2 = "beta"u8.ToArray(); var c = "gamma"u8.ToArray();
        Directory.CreateDirectory(Path.Combine(dir, "wrap", "Sub"));
        File.WriteAllBytes(Path.Combine(dir, "wrap", "Tool.exe"), a);
        File.WriteAllBytes(Path.Combine(dir, "wrap", "Sub", "x.txt"), b2);
        var mod = Mod("M");
        mod.HashesJson = $$"""[{"path":"Tools\\Tool\\Tool.exe","md5":"{{Md5(a)}}"},{"path":"Tools\\Tool\\Sub\\x.txt","md5":"{{Md5(b2)}}"}]""";
        // content sits in "wrap"; flatten it first to mimic an extracted mod root
        var root = Path.Combine(dir, "mod"); Directory.Move(Path.Combine(dir, "wrap"), root);
        Check("repair: shared prefix is restored", CollectionVerifier.Verify(root, mod).Ok
            && File.Exists(Path.Combine(root, "Tools", "Tool", "Tool.exe")));

        var root2 = Path.Combine(dir, "mod2");
        Directory.CreateDirectory(Path.Combine(root2, "ESP-COMPAT")); Directory.CreateDirectory(Path.Combine(root2, "ESL-COMPAT"));
        File.WriteAllBytes(Path.Combine(root2, "ESP-COMPAT", "P.esp"), a); File.WriteAllBytes(Path.Combine(root2, "ESL-COMPAT", "P.esp"), c);
        var mod2 = Mod("M2"); mod2.HashesJson = $$"""[{"path":"P.esp","md5":"{{Md5(c)}}"}]""";
        Check("repair: variant file placed by md5", CollectionVerifier.Verify(root2, mod2).Ok
            && File.ReadAllBytes(Path.Combine(root2, "P.esp")).SequenceEqual(c));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- Tools folder, Root Builder settings, tool executables
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var tool = Path.Combine(dir, "mods", "A Storywealth - FO4Edit", "Tools", "FO4Edit 4.1.5f");
        Directory.CreateDirectory(tool);
        File.WriteAllText(Path.Combine(tool, "FO4Edit64.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "mods", "A Storywealth - FO4Edit", "meta.ini"), "[General]");
        var other = Path.Combine(dir, "mods", "A Storywealth - Mixed");
        Directory.CreateDirectory(Path.Combine(other, "Tools")); Directory.CreateDirectory(Path.Combine(other, "Meshes"));
        Check("tool mod relocated to Tools", Mo2InstanceWriter.RelocateToolMod(dir, "A Storywealth - FO4Edit")
            && File.Exists(Path.Combine(dir, "Tools", "FO4Edit 4.1.5f", "FO4Edit64.exe"))
            && !Directory.Exists(Path.Combine(dir, "mods", "A Storywealth - FO4Edit")));
        Check("mixed mod is not treated as a tool", !Mo2InstanceWriter.RelocateToolMod(dir, "A Storywealth - Mixed"));
        var presets = Path.Combine(dir, "mods", "A Storywealth - Outfit BodySlide Files", "Tools", "BodySlide", "SliderSets");
        Directory.CreateDirectory(presets); File.WriteAllText(Path.Combine(presets, "x.osp"), "x");
        var bsApp = Path.Combine(dir, "mods", "A Storywealth - BodySlide", "Tools", "BodySlide");
        Directory.CreateDirectory(bsApp); File.WriteAllText(Path.Combine(bsApp, "BodySlide x64.exe"), "x");
        Check("BodySlide preset mods stay in the mod list", !Mo2InstanceWriter.RelocateToolMod(dir, "A Storywealth - Outfit BodySlide Files"));
        Check("BodySlide itself stays a mod (VFS-merged presets)", !Mo2InstanceWriter.RelocateToolMod(dir, "A Storywealth - BodySlide"));
        Check("tools index remembers relocated mods", Mo2InstanceWriter.InstalledTools(dir).Contains("A Storywealth - FO4Edit"));

        // executables: Tools-folder tool + Root Builder game-folder exe
        var game = Path.Combine(dir, "Stock Game"); Directory.CreateDirectory(game);
        Directory.CreateDirectory(Path.Combine(dir, "mods", "F4SE", "Root"));
        File.WriteAllText(Path.Combine(dir, "mods", "F4SE", "Root", "f4se_loader.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "ModOrganizer.ini"), "[General]\ngameName=Fallout 4\n\n[customExecutables]\nsize=0\n");
        Mo2InstanceWriter.WriteCustomExecutables(dir, game, [
            new ToolEntry { Name = "FO4Edit", Exe = "Data\\Tools\\FO4Edit 4.1.5f\\FO4Edit64.exe" },
            new ToolEntry { Name = "F4SE", Exe = "f4se_loader.exe" },
            new ToolEntry { Name = "Ghost", Exe = "Data\\Tools\\Nope\\nope.exe" }]);
        var ini = File.ReadAllText(Path.Combine(dir, "ModOrganizer.ini"));
        Check("tool binary points into the Tools folder", ini.Contains("binary=" + Path.Combine(dir, "Tools", "FO4Edit 4.1.5f", "FO4Edit64.exe").Replace(Path.DirectorySeparatorChar, (char)47)));
        Check("Root Builder exe is registered via Root/", ini.Contains("title=F4SE"));
        Check("missing tool is skipped", !ini.Contains("title=Ghost"));

        var gameFwd = game.Replace('\\', '/');
        File.WriteAllText(Path.Combine(dir, "ModOrganizer.ini"), $"[customExecutables]\n2\\title=A StoryWealth (F4SE)\n2\\binary={gameFwd}/f4se_loader.exe\n");
        Mo2InstanceWriter.PointF4seAtRootMod(dir, game);
        Check("F4SE launcher is pointed at the Root mod", File.ReadAllText(Path.Combine(dir, "ModOrganizer.ini")).Contains("mods/F4SE/Root/f4se_loader.exe"));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- native downgrader (same scheme as the collection's Downgrader assets)
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var game = Path.Combine(dir, "game"); var assets = Path.Combine(dir, "Assets");
        Directory.CreateDirectory(game); Directory.CreateDirectory(Path.Combine(assets, "Keys")); Directory.CreateDirectory(Path.Combine(assets, "Patch"));
        File.WriteAllText(Path.Combine(game, "Fallout4.exe"), "next-gen");
        var truth = System.Text.Encoding.ASCII.GetBytes(new string('x', 5000) + "OLD-RUNTIME");
        var key = new byte[32]; Random.Shared.NextBytes(key);
        const ulong id = 12345678901234567890UL;
        File.WriteAllText(Path.Combine(assets, "Keys", "Fallout4.exe.key"), id + "\t" + Convert.ToBase64String(key));
        for (var i = 0; i < 3; i++)   // three chunks, written out of order on disk names
        {
            var slice = truth[(i * 1700)..Math.Min(truth.Length, (i + 1) * 1700)];
            using var aes = System.Security.Cryptography.Aes.Create(); aes.Key = key; aes.GenerateIV();
            var enc = aes.EncryptCbc(slice, aes.IV);
            File.WriteAllBytes(Path.Combine(assets, "Patch", $"{id}_{i}.patch"), [.. aes.IV, .. enc]);
        }
        string? Ver(string p) => File.ReadAllText(p).EndsWith("OLD-RUNTIME") ? "1.10.163.0" : "1.11.0.0";
        var n = GameDowngrader.Patch(game, assets, Ver, [new GameDowngrader.Target("Fallout4.exe", "1.10.163.0")]);
        Check("downgrader: chunks decrypt and replace the file", n == 1 && File.ReadAllBytes(Path.Combine(game, "Fallout4.exe")).SequenceEqual(truth));
        Check("downgrader: already-downgraded file is left alone",
            GameDowngrader.Patch(game, assets, Ver, [new GameDowngrader.Target("Fallout4.exe", "1.10.163.0")]) == 0);

        File.WriteAllText(Path.Combine(game, "Fallout4.exe"), "next-gen");
        var threw = false;
        try { GameDowngrader.Patch(game, assets, _ => "9.9.9.9", [new GameDowngrader.Target("Fallout4.exe", "1.10.163.0")]); }
        catch (InvalidOperationException) { threw = true; }
        Check("downgrader: wrong result version throws and keeps the original",
            threw && File.ReadAllText(Path.Combine(game, "Fallout4.exe")) == "next-gen" && !File.Exists(Path.Combine(game, "Fallout4.exe.asw-new")));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- real collection samples (samples/*.collection.json, optional)
foreach (var sample in FindSamples())
{
    var addon = VortexCollectionMapper.Map(sample, "sample");
    var (order, rep) = DeployOrder.Compute([], [addon]);
    Console.WriteLine($"{Path.GetFileName(sample)}: {addon.Mods.Count} mods, rules={rep.Rules} edges={rep.EdgesApplied} unresolved={rep.Unresolved} cycles={rep.CyclesBroken}");
    foreach (var u in rep.UnresolvedDetails) Console.WriteLine("    unresolved: " + u);
    foreach (var c in rep.CycleDetails) Console.WriteLine("    cycle: " + c);
    Check("sample: every mod ordered exactly once", order.Count == addon.Mods.Select(m => m.InstallAs).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    var pos = order.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
    var byMd5 = addon.Mods.Where(m => m.Nexus?.Md5 is { Length: > 0 }).GroupBy(m => m.Nexus!.Md5!).ToDictionary(g => g.Key, g => g.First().InstallAs);
    int checkedRules = 0, violated = 0;
    foreach (var r in addon.ModRules ?? [])
    {
        if (r.Type is not ("before" or "after") || r.Source?.FileMd5 is not { } s || r.Reference?.FileMd5 is not { } t) continue;
        if (!byMd5.TryGetValue(s, out var sn) || !byMd5.TryGetValue(t, out var tn)) continue;
        checkedRules++;
        if (!(r.Type == "after" ? pos[sn] > pos[tn] : pos[sn] < pos[tn])) violated++;
    }
    Check($"sample: md5 rules satisfied ({checkedRules} checked)", violated <= rep.CyclesBroken, $"{violated} violated, {rep.CyclesBroken} cycles");
    var pr = PluginOrder.ParseRules(addon.PluginRulesJson);
    var (lo, notes) = PluginOrder.Arrange(addon.Plugins.Select(p => p.Plugin).ToList(), _ => null, pr);
    Console.WriteLine($"    plugins: {lo.Count} ordered, {pr.Count} load-after pairs, notes: {notes.Count}");
    foreach (var n in notes.Take(8)) Console.WriteLine("      " + n);
}

Console.WriteLine(failed == 0 ? "\nall passed" : $"\n{failed} FAILED");
return failed == 0 ? 0 : 1;

static IEnumerable<string> FindSamples()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
    {
        var s = Path.Combine(d.FullName, "samples");
        if (Directory.Exists(s)) return Directory.GetFiles(s, "*.collection.json");
    }
    return [];
}
