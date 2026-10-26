using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AStoryWealth.Core;
using AStoryWealth.NexusApi;

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

// ---- a group left out after it was installed: its extracted folders are listed disabled at the bottom
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(dir, "mods", "Left"));      // extracted earlier, now unticked
        var addon = Addon([Mod("Kept"), Mod("Left")]);
        var plan = new ManifestResolver().Resolve([addon]);
        Mo2InstanceWriter.WriteLoadFiles(dir, plan, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Left" });
        var lines = File.ReadAllLines(Path.Combine(dir, "profiles", "A StoryWealth", "modlist.txt")).Where(l => !l.StartsWith('#')).ToArray();
        Check("left-out mod already on disk is listed disabled, lowest priority", J(lines) == "+Kept | -Left", J(lines));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- optional install groups (Sim Settlements 2, HD textures)
{
    var ss2a = Mod("SS2 Core", 0, "s1"); var ss2b = Mod("SS2 Patch", 0, "s2"); var hdm = Mod("Textures9", 0, "h1"); hdm.IsOptional = true;
    var plain = Mod("Plain", 0, "p1");
    var all = new List<ModEntry> { ss2a, ss2b, hdm, plain };
    var groups = new List<OptionalGroup>
    {
        new() { Id = "ss2", Name = "SS2", DefaultOn = true, Mods = [new OrderedMod { Md5 = "s1", Name = "SS2 Core" }, new OrderedMod { Md5 = "", Name = "SS2 Patch" }] },
        new() { Id = "hd", Name = "HD", DefaultOn = false, FromOptionalFlag = true },
    };
    Check("defaults: SS2 on, HD textures off", J(OptionalGroups.Defaults(groups)) == "ss2");
    var none = OptionalGroups.Excluded(all, groups, OptionalGroups.Defaults(groups));
    Check("defaults leave out only the HD textures", J(none.OrderBy(x => x)) == "Textures9", J(none));
    var noSs2 = OptionalGroups.Excluded(all, groups, ["hd"]);
    Check("unticking SS2 leaves out its mods (by md5 or name), keeps the HD textures", J(noSs2.OrderBy(x => x)) == "SS2 Core | SS2 Patch", J(noSs2));
    var nothing = OptionalGroups.Excluded(all, groups, ["ss2", "hd"]);
    Check("everything ticked leaves nothing out", nothing.Count == 0);
    var legacy = OptionalGroups.Excluded(all, [], []);
    Check("no shipped groups: only the collection's optional mods are skipped", J(legacy) == "Textures9", J(legacy));
}

// ---- free-account manual download: pick the right file out of the browser folder
{
    var root = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    var watch = Path.Combine(root, "browser"); var dest = Path.Combine(root, "downloads");
    Directory.CreateDirectory(watch);
    try
    {
        File.WriteAllText(Path.Combine(watch, "unrelated.zip"), "something else entirely");
        File.WriteAllText(Path.Combine(watch, "same-size-wrong.7z"), "WRONG");
        File.WriteAllText(Path.Combine(watch, "Real Mod-1-1-0.7z"), "RIGHT");
        var m = Mod("Real Mod", 0, "x"); m.Nexus!.FileSize = 5;
        var got = await ManualDownloader.FetchAsync(m, watch,
            n => Path.Combine(dest, n),
            p => Task.FromResult((File.ReadAllText(p) == "RIGHT", "content differs")),
            _ => Check("a file already in the browser folder needs no page to be opened", false), CancellationToken.None);
        Check("manual download takes the verified file and moves it to the downloads folder",
            got == Path.Combine(dest, "Real Mod-1-1-0.7z") && File.Exists(got) && !File.Exists(Path.Combine(watch, "Real Mod-1-1-0.7z")), got);
        Check("manual download leaves other files in the browser folder alone",
            File.Exists(Path.Combine(watch, "same-size-wrong.7z")) && File.Exists(Path.Combine(watch, "unrelated.zip")));
        Check("page url carries mod id and file id",
            ManualDownloader.PageUrl(m) == $"https://www.nexusmods.com/{m.Nexus.Game}/mods/1?tab=files&file_id=1", ManualDownloader.PageUrl(m));
    }
    finally { try { Directory.Delete(root, true); } catch { } }
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

        // dependency-aware placement: a patch / BodySlide set follows the mod it requires, inside that mod's separator
        {
            var m2 = new List<ModEntry>
            {
                CatMod("Zeta Rifle", "Weapons", "z1"),                      // parent (sorts last alphabetically)
                CatMod("Alpha Pistol", "Weapons", "a1"),
                CatMod("Zeta Rifle - Compat Patch", "Patches", "z2"),       // plugin master => Zeta Rifle
                CatMod("Zeta Rifle BodySlide Files", "Armour", "z3"),       // BodySlide-only, name match => Zeta Rifle
            };
            var bl = new List<string> { "Zeta Rifle - Compat Patch", "Alpha Pistol", "Zeta Rifle BodySlide Files", "Zeta Rifle" };
            var sig = new ModSignals();
            sig.Provider["Zeta.esp"] = "Zeta Rifle"; sig.HasPlugin.Add("Zeta Rifle");
            sig.Masters["Zeta Rifle - Compat Patch"] = new(StringComparer.OrdinalIgnoreCase) { "Zeta.esp" }; sig.HasPlugin.Add("Zeta Rifle - Compat Patch");
            sig.BodySlide.Add("Zeta Rifle BodySlide Files");
            var sch = new List<SeparatorDef>
            {
                new() { Name = "Armour", Categories = ["Armour"] },
                new() { Name = "Weapons", Categories = ["Weapons"] },
                new() { Name = "Patches", Categories = ["Patches"] },
            };
            var b = PriorityOrder.Build("t", m2, bl, new Dictionary<string, HashSet<string>>(), sch, sig);
            var (en, _) = PriorityOrder.Apply(b, m2, bl, new Dictionary<string, HashSet<string>>());
            Check("children follow their parent, in the parent's separator",
                Flat(en) == "[Weapons] | Alpha Pistol | Zeta Rifle | Zeta Rifle BodySlide Files | Zeta Rifle - Compat Patch", Flat(en));
            Check("child records its parent", b.Separators[1].Mods.Count(x => x.Parent == "Zeta Rifle") == 2);

            // a real constraint (the patch must deploy BEFORE its parent) beats the parent-first preference
            var pinned = new Dictionary<string, HashSet<string>> { ["Zeta Rifle"] = ["Zeta Rifle - Compat Patch"] };
            var b2 = PriorityOrder.Build("t", m2, bl, pinned, sch, sig);
            var (en2, _) = PriorityOrder.Apply(b2, m2, bl, pinned);
            var f2 = Flat(en2);
            Check("a conflict that needs the child first is honoured", f2.IndexOf("Compat Patch", StringComparison.Ordinal) < f2.IndexOf("Zeta Rifle |", StringComparison.Ordinal)
                || f2.IndexOf("Compat Patch", StringComparison.Ordinal) < f2.LastIndexOf("Zeta Rifle", StringComparison.Ordinal), f2);
        }

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

            // "Added mods": an always-present empty separator on top; mods people add under it stay there on rerun
            var withAdded = new List<SeparatorDef>(scheme) { new() { Name = PriorityOrder.AddedModsSeparator, AlwaysShow = true } };
            PriorityOrder.Save(PriorityOrder.Build("ordtest", mods, baseline, noPreds, withAdded), shippedPath);
            var modlistPath = Path.Combine(dir, "profiles", "A StoryWealth", "modlist.txt");
            Mo2InstanceWriter.WriteLoadFiles(dir, plan);
            var l1 = File.ReadAllLines(modlistPath).Where(l => !l.StartsWith('#')).ToArray();
            Check("empty 'Added mods' separator is the highest-priority entry", l1[0] == "-Added mods_separator", J(l1));
            Check("its folder exists so MO2 keeps it", File.Exists(Path.Combine(dir, "mods", "Added mods_separator", "meta.ini")));

            File.WriteAllLines(modlistPath, ["# This file was automatically generated by Mod Organizer.", "+UserMod", .. l1]);
            Mo2InstanceWriter.WriteLoadFiles(dir, plan);
            var l2 = File.ReadAllLines(modlistPath).Where(l => !l.StartsWith('#')).ToArray();
            Check("a user-added mod stays above 'Added mods' on rerun", l2[0] == "+UserMod" && l2[1] == "-Added mods_separator", J(l2.Take(3)));

            // MO2 can re-add a left-out mod's folder above 'Added mods'; a skipped mod must never be treated as the user's
            File.WriteAllLines(modlistPath, ["# This file was automatically generated by Mod Organizer.", "+SkippedMod", "+UserMod", "-Added mods_separator", .. l2.Skip(2)]);
            Mo2InstanceWriter.WriteLoadFiles(dir, plan, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SkippedMod" });
            var l3 = File.ReadAllLines(modlistPath).Where(l => !l.StartsWith('#')).ToArray();
            Check("a skipped mod above 'Added mods' neither crashes nor counts as user-added", l3[0] == "+UserMod" && !l3.Contains("+SkippedMod"), J(l3.Take(4)));

            // a duplicated line in a hand-edited profile must not crash the reader
            File.WriteAllLines(modlistPath, ["# This file was automatically generated by Mod Organizer.", .. l3, "+UserMod"]);
            Mo2InstanceWriter.WriteLoadFiles(dir, plan);
            Check("duplicate profile lines are tolerated", File.ReadAllLines(modlistPath).Count(l => l == "+UserMod") == 1);
        }
        finally { try { File.Delete(shippedPath); } catch { } }
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- add-ons: anchors, own block on top of the base list, hoisting, survival of a base re-run
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    var orderDir = Path.Combine(AppContext.BaseDirectory, "order"); Directory.CreateDirectory(orderDir);
    var shippedPath = Path.Combine(orderDir, "baseord.priority.json");
    try
    {
        foreach (var n in new[] { "Base A", "Base B", "Base C" }) Directory.CreateDirectory(Path.Combine(dir, "mods", n));
        var profile = Path.Combine(dir, "profiles", "A StoryWealth"); Directory.CreateDirectory(profile);
        var modlistPath = Path.Combine(profile, "modlist.txt");
        File.WriteAllLines(modlistPath, ["# This file was automatically generated by Mod Organizer.", "+UserMod", "-Added mods_separator", "+Base C", "+Base B", "+Base A"]);
        var baseMods = new List<ModEntry> { Mod("Base A", 0, "ma"), Mod("Base B", 0, "mb"), Mod("Base C", 0, "mc") };
        // the add-on repeats Base B (under another name) so its rules can reference it
        var addonMods = new List<ModEntry> { Mod("Other - Base B", 0, "mb"), Mod("Add One", 0, "m1"), Mod("Add Two", 0, "m2"), Mod("Add Patch", 0, "m3") };
        var addon = new AddonManifest
        {
            AddonId = "addtest", Name = "Test Add-on", Mods = addonMods,
            ModRules = [Rule("before", "m3", "mb"), Rule("after", "m2", "mb")],
        };
        var anchors = AddonPlacement.MarkAnchors(addon, baseMods, new AddonRegistryFile(), dir);
        Check("a repeat of an installed mod becomes an anchor pointing at the existing folder",
            anchors == 1 && addonMods[0].IsAnchor && addonMods[0].InstallAs == "Base B");
        Check("a base mod that is not on disk is not an anchor (the add-on installs it)",
            AddonPlacement.MarkAnchors(new AddonManifest { AddonId = "x", Mods = [Mod("Gone", 0, "zz")] }, [Mod("Gone", 0, "zz")], new AddonRegistryFile(), dir) == 0);

        var plan = new ManifestResolver().Resolve([addon]);
        Mo2InstanceWriter.WriteLoadFiles(dir, plan, isAddon: true);
        List<string> Asc() => File.ReadAllLines(modlistPath).Where(l => !l.StartsWith('#')).Select(l => l[1..]).Reverse().ToList();
        Check("add-on: own block under its separator, below 'Added mods'; a mod that must lose to a base mod is hoisted in beside it",
            J(Asc()) == "Base A | Add Patch | Base B | Base C | Test Add-on_separator | Add One | Add Two | Added mods_separator | UserMod", J(Asc()));
        Check("add-on separator has a mod folder", File.Exists(Path.Combine(dir, "mods", "Test Add-on_separator", "meta.ini")));
        var reg = AddonRegistry.Load(dir).Addons.Single();
        Check("registry records the add-on's own mods, its separator and the hoisted mod, not the base anchor",
            reg.Separator == "Test Add-on" && J(reg.Mods.Select(m => m.Name).OrderBy(x => x)) == "Add One | Add Patch | Add Two" && J(reg.Hoisted) == "Add Patch",
            J(reg.Mods.Select(m => m.Name)));
        var first = J(Asc());
        Mo2InstanceWriter.WriteLoadFiles(dir, plan, isAddon: true);
        Check("re-running the add-on gives the same list (no duplicate separator or mods)", J(Asc()) == first, J(Asc()));

        // a base re-run (structured order) keeps the add-on above the base list and below 'Added mods'
        var scheme = new List<SeparatorDef> { new() { Name = "Everything" }, new() { Name = PriorityOrder.AddedModsSeparator, AlwaysShow = true } };
        PriorityOrder.Save(PriorityOrder.Build("baseord", baseMods, ["Base A", "Base B", "Base C"], new Dictionary<string, HashSet<string>>(), scheme), shippedPath);
        var basePlan = new ManifestResolver().Resolve([new AddonManifest { AddonId = "baseord", Mods = baseMods }]);
        Mo2InstanceWriter.WriteLoadFiles(dir, basePlan);
        var after = Asc();
        Check("a base re-run keeps the add-on's block and separator on top of the base list, under 'Added mods'",
            after.IndexOf("Test Add-on_separator") > after.IndexOf("Base C") && after.IndexOf("Add Two") < after.IndexOf("Added mods_separator")
            && after.Contains("Add One") && after.Contains("Add Patch"), J(after));
        Check("the add-on's separator folder survives the base re-run", Directory.Exists(Path.Combine(dir, "mods", "Test Add-on_separator")));

        // a mod shared with an installed add-on is an anchor of the second add-on, not installed twice
        var second = new AddonManifest { AddonId = "second", Name = "Second", Mods = [Mod("Dup Name", 0, "m1"), Mod("Own", 0, "m9")] };
        var anchors2 = AddonPlacement.MarkAnchors(second, baseMods, AddonRegistry.Load(dir), dir);
        Directory.CreateDirectory(Path.Combine(dir, "mods", "Add One"));
        var anchors3 = AddonPlacement.MarkAnchors(second, baseMods, AddonRegistry.Load(dir), dir);
        Check("a mod already installed by another add-on is shared, not reinstalled",
            anchors2 == 0 && anchors3 == 1 && second.Mods[0].InstallAs == "Add One" && second.Mods[0].IsAnchor && !second.Mods[1].IsAnchor);
    }
    finally { try { File.Delete(shippedPath); } catch { } try { Directory.Delete(dir, true); } catch { } }
}

// ---- optional tab: what is listed, greyed out, installed; and the runs it asks for
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        string Coll(string name, params (string Name, string Md5, bool Opt, string? Ins)[] mods) =>
            $$"""{"info":{"name":"{{name}}"},"mods":[{{string.Join(",", mods.Select((m, i) =>
                $$"""{"name":"{{m.Name}}","version":"1","optional":{{(m.Opt ? "true" : "false")}},"domainName":"fallout4","source":{"type":"nexus","modId":{{i + 1}},"fileId":{{i + 1}},"md5":"{{m.Md5}}","fileSize":1000000},"phase":0{{(m.Ins is null ? "" : $$""","instructions":"{{m.Ins}}" """)}} }"""))}}]}""";
        var baseJson = Path.Combine(dir, "base.json");
        File.WriteAllText(baseJson, Coll("Base", ("Base Mod", "b1", false, null), ("Base Extra", "b2", true, "Only if you want it")));
        var yaeJson = Path.Combine(dir, "wrbrop.json");
        File.WriteAllText(yaeJson, Coll("YAE", ("YAE Core", "y1", false, null),
            ("Project Valkyrie", "a0791cb8b0b466346530de087c127d70", true, "Install if you have the thuggyverse addon"),
            ("Some AMD Patch", "y3", true, "Use if you have an AMD Graphics Card"),
            ("Done Already", "y4", true, null)));
        Directory.CreateDirectory(Path.Combine(dir, "mods", "A Storywealth - Done Already"));
        AddonRegistry.Save(dir, new AddonRegistryFile
        {
            Addons = [new InstalledAddon { Id = "wrbrop", Name = "aSW - YAE Deluxe", Revision = "r16", Separator = "aSW - YAE Deluxe" }],
        });
        var items = OptionalCatalog.Build(dir, baseJson, a => a.Id == "wrbrop" ? yaeJson : null);
        OptionalItem Find(string n) => items.First(i => i.Name == n);
        Check("only installed sources are listed (base + the one add-on)", items.Select(i => i.SourceId).Distinct().OrderBy(x => x).SequenceEqual(["base", "wrbrop"]), J(items.Select(i => i.SourceId + ":" + i.Name)));
        Check("a base optional mod is offered with the author's note", Find("Base Extra").State == OptionState.Available && Find("Base Extra").Instruction.Contains("Only if you want it"));
        var pv = Find("Project Valkyrie");
        Check("an option that needs another add-on is greyed out with the reason", pv.State == OptionState.NotApplicable && pv.Reason == "Needs the Thuggyverse add-on", pv.Reason);
        Check("a hardware note is shown and the option stays selectable (no GPU check)", Find("Some AMD Patch").State == OptionState.Available && Find("Some AMD Patch").Instruction.Contains("AMD"));
        Check("an option already on disk is marked installed", Find("Done Already").State == OptionState.Installed);
        // once the required add-on is installed the option applies
        AddonRegistry.Save(dir, new AddonRegistryFile { Addons = [new InstalledAddon { Id = "wrbrop", Name = "YAE", Revision = "r16" }, new InstalledAddon { Id = "yilurh", Name = "Thuggyverse", Revision = "r9" }] });
        Check("…and becomes available when that add-on is installed", OptionalCatalog.Build(dir, baseJson, a => a.Id == "wrbrop" ? yaeJson : null).First(i => i.Name == "Project Valkyrie").State == OptionState.Available);
        // runs: base first, then add-ons; group rows become groups, mods become md5s
        var jobs = OptionalCatalog.Jobs([Find("Some AMD Patch"), Find("Base Extra")], baseJson);
        Check("jobs: base list first, then the add-on, each with its md5s",
            jobs.Count == 2 && jobs[0].SourceId == "base" && jobs[0].Md5s.SequenceEqual(["b2"]) && jobs[1].SourceId == "wrbrop" && jobs[1].Md5s.SequenceEqual(["y3"]), J(jobs.Select(j => j.SourceId)));
        Check("an installed or greyed-out row never makes a job", OptionalCatalog.Jobs([Find("Done Already"), pv], baseJson).Count == 0);
        // picks are remembered per source and survive a registry rewrite
        AddonRegistry.RecordOptional(dir, "wrbrop", ["y3"]);
        AddonRegistry.RecordOptional(dir, null, ["b2"], ["ss2"]);
        var reg2 = AddonRegistry.Load(dir);
        Check("optional picks and base groups are recorded", AddonRegistry.OptionalFor(reg2, "wrbrop").SequenceEqual(["y3"]) && reg2.BaseOptional.SequenceEqual(["b2"]) && reg2.BaseGroups!.SequenceEqual(["ss2"]));
        AddonRegistry.Upsert(dir, new InstalledAddon { Id = "wrbrop", Name = "YAE", Revision = "r17" });
        Check("re-recording the add-on keeps its optional picks", AddonRegistry.OptionalFor(AddonRegistry.Load(dir), "wrbrop").SequenceEqual(["y3"]));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- the install name is the folder name (a name with ".." used to differ, so MO2 never saw the mod in modlist.txt)
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "c.json");
        File.WriteAllText(json, """
            {"info":{"name":"X"},"mods":[
              {"name":"Tumbajamba's Raider Power Armor - The A... flamer","version":"1","optional":false,"domainName":"fallout4","phase":0,
               "source":{"type":"nexus","modId":1,"fileId":1,"md5":"a","fileSize":1,"logicalFilename":"Tumbajamba's Raider Power Armor - The A... flamer"}},
              {"name":"Plain Mod","version":"1","optional":false,"domainName":"fallout4","phase":0,"source":{"type":"nexus","modId":2,"fileId":2,"md5":"b","fileSize":1}}]}
            """);
        var mods = VortexCollectionMapper.Map(json, "t").Mods;
        Check("every install name already is its own folder name", mods.All(m => m.InstallAs == Mo2InstanceWriter.SafeName(m.InstallAs, "x")), J(mods.Select(m => m.InstallAs)));
        Check("a name with '...' keeps working: modlist name = folder name", mods[0].InstallAs.EndsWith("The A_. flamer"), mods[0].InstallAs);
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- two mods with the same file name get different folders (Pickman's Passion had two "Main File")
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "c.json");
        File.WriteAllText(json, """
            {"info":{"name":"A Storywealth - Pickman's Passion"},"mods":[
              {"name":"Main File","version":"1","optional":false,"domainName":"fallout4","phase":0,"source":{"type":"nexus","modId":10,"fileId":1,"md5":"a","fileSize":1,"logicalFilename":"Main File"}},
              {"name":"Main File","version":"1","optional":false,"domainName":"fallout4","phase":0,"source":{"type":"nexus","modId":20,"fileId":2,"md5":"b","fileSize":1,"logicalFilename":"Main File"}},
              {"name":"Main File","version":"1","optional":false,"domainName":"fallout4","phase":0,"source":{"type":"nexus","modId":20,"fileId":3,"md5":"c","fileSize":1,"logicalFilename":"Main File"}},
              {"name":"Other","version":"1","optional":false,"domainName":"fallout4","phase":0,"source":{"type":"nexus","modId":30,"fileId":4,"md5":"d","fileSize":1,"logicalFilename":"Other"}}]}
            """);
        var mods = VortexCollectionMapper.Map(json, "x").Mods;
        Check("mods that share a file name get their own folder", mods.Select(m => m.InstallAs).Distinct(StringComparer.OrdinalIgnoreCase).Count() == mods.Count, J(mods.Select(m => m.InstallAs)));
        Check("a name nobody shares is left alone", mods[3].InstallAs == "Pickman's Passion - Other", mods[3].InstallAs);
        Check("the plan accepts the add-on", new ManifestResolver().Resolve([new AddonManifest { AddonId = "x", Mods = mods }]).Issues.All(i => !i.Message.Contains("collision")), "");
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- an add-on title with a slash makes a flat separator folder
Check("a '/' in an add-on title doesn't nest the separator folder",
    AddonPlacement.Label(new AddonManifest { AddonId = "k", Name = "A StoryWealth aSW harder / survival" }) == "A StoryWealth aSW harder - survival"
    && AddonPlacement.Label(new AddonManifest { AddonId = "k", Name = "A Storywealth - Atmosphere" }) == "A Storywealth - Atmosphere",
    AddonPlacement.Label(new AddonManifest { AddonId = "k", Name = "A StoryWealth aSW harder / survival" }));

// ---- add-on folder names carry the add-on's name
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        string Coll(string name) => "{\"info\":{\"name\":\"" + name + "\"},\"mods\":[{\"name\":\"Cool Mod\",\"version\":\"1\",\"optional\":false,\"domainName\":\"fallout4\",\"phase\":0,\"source\":{\"type\":\"nexus\",\"modId\":5,\"fileId\":6,\"md5\":\"c\",\"fileSize\":1}}]}";
        string Map1(string name) { var f = Path.Combine(dir, "c.json"); File.WriteAllText(f, Coll(name)); return VortexCollectionMapper.Map(f, "x").Mods[0].InstallAs; }
        Check("base keeps the plain prefix", Map1("A StoryWealth") == "A Storywealth - Cool Mod", Map1("A StoryWealth"));
        Check("add-on title is the prefix", Map1("A Storywealth - Atmosphere") == "Atmosphere - Cool Mod", Map1("A Storywealth - Atmosphere"));
        Check("aSW / A Storywealth is dropped from an add-on title", Map1("aSW - Thuggyverse") == "Thuggyverse - Cool Mod" && Map1("Thuggyverse") == "Thuggyverse - Cool Mod", Map1("aSW - Thuggyverse"));

        var f2 = Path.Combine(dir, "c.json"); File.WriteAllText(f2, Coll("A Storywealth - Atmosphere"));
        var addon = VortexCollectionMapper.Map(f2, "vq4q7d");
        var inst = Path.Combine(dir, "inst");
        Directory.CreateDirectory(Path.Combine(inst, "mods", "A Storywealth - Cool Mod"));
        var reg = new AddonRegistryFile { Addons = [new InstalledAddon { Id = "vq4q7d", Mods = [new InstalledAddonMod { Name = "A Storywealth - Cool Mod", Md5 = "c" }] }] };
        AddonPlacement.KeepExistingFolders(addon, reg, inst);
        Check("an add-on installed under the old name keeps its folder", addon.Mods[0].InstallAs == "A Storywealth - Cool Mod", addon.Mods[0].InstallAs);
        var other = VortexCollectionMapper.Map(f2, "vq4q7d");
        AddonPlacement.KeepExistingFolders(other, new AddonRegistryFile(), inst);
        var mid = VortexCollectionMapper.Map(f2, "vq4q7d");
        Directory.CreateDirectory(Path.Combine(inst, "mods", "A Storywealth - Atmosphere - Cool Mod"));
        var reg3 = new AddonRegistryFile { Addons = [new InstalledAddon { Id = "vq4q7d", Mods = [new InstalledAddonMod { Name = "A Storywealth - Atmosphere - Cool Mod", Md5 = "c" }] }] };
        AddonPlacement.KeepExistingFolders(mid, reg3, inst);
        Check("a 0.4.3 folder (A Storywealth - Atmosphere - Mod) is kept too", mid.Mods[0].InstallAs == "A Storywealth - Atmosphere - Cool Mod", mid.Mods[0].InstallAs);
        Check("a folder the add-on doesn't own is not taken", other.Mods[0].InstallAs == "Atmosphere - Cool Mod", other.Mods[0].InstallAs);
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- curated mods the collection lists as required but that need SS2 become optional
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "c.json");
        File.WriteAllText(f, "{\"info\":{\"name\":\"aSW-Faction Addon\"},\"mods\":[" +
            "{\"name\":\"IT_SS2\",\"version\":\"1\",\"optional\":false,\"domainName\":\"fallout4\",\"phase\":0,\"source\":{\"type\":\"nexus\",\"modId\":1,\"fileId\":1,\"md5\":\"b51ff46b9456c95ae1986353e6d2f9b9\",\"fileSize\":1}}," +
            "{\"name\":\"Other\",\"version\":\"1\",\"optional\":false,\"domainName\":\"fallout4\",\"phase\":0,\"source\":{\"type\":\"nexus\",\"modId\":2,\"fileId\":2,\"md5\":\"zz\",\"fileSize\":1}}]}");
        var faction = VortexCollectionMapper.Map(f, "0shihl");
        Check("the Faction SS2 mod is optional", faction.Mods[0].IsOptional && !faction.Mods[1].IsOptional);
        Check("only for that add-on", !VortexCollectionMapper.Map(f, "other").Mods[0].IsOptional);
        Check("the shipped list names the SS2 group", AddonOptions.Load().Any(e => e.MakeOptional && e.NeedsGroups.Contains("ss2")));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- FOMOD config in UTF-16 with a UTF-8 declaration, and wrapper folders two levels deep
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        var xml = Path.Combine(dir, "ModuleConfig.xml");
        File.WriteAllText(xml, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><config><moduleName>x</moduleName></config>", System.Text.Encoding.Unicode);
        Check("a UTF-16 ModuleConfig with a UTF-8 declaration loads", FomodResolver.LoadXml(xml).Root?.Name.LocalName == "config");
        var mod = Path.Combine(dir, "mod");
        Directory.CreateDirectory(Path.Combine(mod, "Replacer-96858-2", "AdVictoriam Replacer"));
        File.WriteAllText(Path.Combine(mod, "Replacer-96858-2", "AdVictoriam Replacer", "thing.esp"), "x");
        Mo2InstanceWriter.StripWrapper(mod, "t");
        Check("a wrapper inside a wrapper is stripped too", File.Exists(Path.Combine(mod, "thing.esp")), string.Join(",", Directory.GetFileSystemEntries(mod)));
        var real = Path.Combine(dir, "real");
        Directory.CreateDirectory(Path.Combine(real, "Wrapper", "Meshes", "a"));
        File.WriteAllText(Path.Combine(real, "Wrapper", "Meshes", "a", "x.nif"), "x");
        Mo2InstanceWriter.StripWrapper(real, "t");
        Check("real game folders are never mistaken for a wrapper", File.Exists(Path.Combine(real, "Meshes", "a", "x.nif")));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- changelog text
{
    var md = "## Revision 284\n\nSmaller revision.\n\n### Updated Mods\n\n- A StoryWealth - Armors Patch\n- Blood Fixes *Loose Files* **replaced with** *ba2*\n\n[Page](https://x.y)";
    var plain = ChangelogFormat.ToPlain(md);
    Check("headings lose their #, and the leading 'Revision N' line is dropped", plain.StartsWith("Smaller revision") && plain.Contains("Updated Mods") && !plain.Contains('#'), plain);
    Check("a trailing backslash line break is dropped", !ChangelogFormat.ToPlain("Added - A\\\nAdded - B\\\n\\\nRemoved - C").Contains('\\'));
    Check("bullets become dots", plain.Contains("•  A StoryWealth - Armors Patch"), plain);
    Check("bold and italic marks go, links keep their text", plain.Contains("Blood Fixes Loose Files replaced with ba2") && plain.Contains("Page") && !plain.Contains("https"), plain);
    Check("nothing in gives nothing out", ChangelogFormat.ToPlain(null) == "" && ChangelogFormat.ToPlain("  ") == "");
    var node = System.Text.Json.JsonDocument.Parse("{\"endorsements\":5,\"latestPublishedRevision\":{\"revisionNumber\":284,\"modCount\":9,\"totalSize\":\"100\",\"collectionChangelog\":{\"description\":\"hi\",\"createdAt\":\"2026-10-20T08:08:41Z\"}}}").RootElement;
    var st = AStoryWealth.NexusApi.NexusApiClient.ParseStats(node);
    Check("stats carry the newest revision's changelog", st.Revision == 284 && st.Changelog == "hi" && st.ChangelogDate?.Year == 2026);
}

// ---- game INIs: resolution from the main display
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var ini = "[General]\nsLanguage=en\n\n[Display]\nbFull Screen=1\niSize H=720\niSize W=1280\n; keep\nfoo=bar\n";
        var set = IniEdit.SetValue(ini, "Display", "iSize W", "2560");
        Check("one value changes, the rest of the file stays", set.Contains("iSize W=2560") && set.Contains("iSize H=720") && set.Contains("; keep") && set.Contains("sLanguage=en"), set);
        var added = IniEdit.SetValue("[General]\na=1\n", "Display", "iSize W", "1920");
        Check("a missing section and key are added", IniEdit.GetValue(added, "Display", "iSize W") == "1920" && IniEdit.GetValue(added, "General", "a") == "1");
        Check("a missing key goes into its own section", IniEdit.SetValue("[Display]\na=1\n\n[Other]\nb=2\n", "Display", "x", "9").IndexOf("x=9") < IniEdit.SetValue("[Display]\na=1\n\n[Other]\nb=2\n", "Display", "x", "9").IndexOf("[Other]"));
        Check("Windows line endings are kept", IniEdit.SetValue("[Display]\r\niSize W=1\r\n", "Display", "iSize W", "2").Contains("\r\n") && !IniEdit.SetValue("[Display]\r\niSize W=1\r\n", "Display", "iSize W", "2").Replace("\r\n", "").Contains('\n'));

        var game = Path.Combine(dir, "game"); var inst = Path.Combine(dir, "inst");
        Directory.CreateDirectory(Path.Combine(game, "Fallout4"));
        File.WriteAllText(Path.Combine(game, "Fallout4", "Fallout4Prefs.ini"), ini);
        Mo2InstanceWriter.EnsureProfileFiles(inst, game, (2560, 1440));
        var prefs = Path.Combine(inst, "profiles", "A StoryWealth", "fallout4prefs.ini");
        Check("a new profile gets the main display's size", GameDisplay.ReadResolution(prefs) == (2560, 1440), GameDisplay.ReadResolution(prefs)?.ToString());
        File.WriteAllText(prefs, IniEdit.SetValue(File.ReadAllText(prefs), "Display", "iSize W", "1111"));
        Mo2InstanceWriter.EnsureProfileFiles(inst, game, (3840, 2160));
        Check("an existing profile's settings are left alone", GameDisplay.ReadResolution(prefs)?.W == 1111);
        Check("matching the display later keeps a backup of the old file", GameDisplay.ApplyResolution(prefs, 3840, 2160) && File.Exists(prefs + GameDisplay.BackupSuffix) && GameDisplay.ReadResolution(prefs) == (3840, 2160));
        Check("a silly size is refused", !GameDisplay.ApplyResolution(prefs, 10, 10));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- the INIs shipped inside the exe
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var inst = Path.Combine(dir, "inst"); var game = Path.Combine(dir, "game");
        Directory.CreateDirectory(game);   // no game INIs at all: whatever is in the profile came from the exe
        Mo2InstanceWriter.EnsureProfileFiles(inst, game, (2560, 1440));
        var prof = Path.Combine(inst, "profiles", "A StoryWealth");
        string Read(string n) => File.ReadAllText(Path.Combine(prof, n));
        Check("the shipped Fallout4.ini is used", Read("fallout4.ini").Contains("uGridsToLoad"), Read("fallout4.ini").Length.ToString());
        Check("the shipped Fallout4Prefs.ini is used and gets this display's size", GameDisplay.ReadResolution(Path.Combine(prof, "fallout4prefs.ini")) == (2560, 1440) && Read("fallout4prefs.ini").Contains("[Display]") && Read("fallout4prefs.ini").Length > 1000);
        Check("the shipped Fallout4Custom.ini keeps MO2's local saves line", Read("fallout4custom.ini").Contains("sLocalSavePath=__MO_Saves"));

        // "Use recommended INIs": a person's edited files are replaced, the oldest copy is kept, and the display size is applied
        File.WriteAllText(Path.Combine(prof, "fallout4.ini"), "[General]\nmine=1\n");
        var replaced = GameDisplay.UseRecommended(inst, (1920, 1080));
        Check("the recommended INIs replace the profile's own", replaced.Count == 3 && Read("fallout4.ini").Contains("uGridsToLoad") && !Read("fallout4.ini").Contains("mine=1"));
        Check("the replaced file is kept as a backup", File.ReadAllText(Path.Combine(prof, "fallout4.ini" + GameDisplay.BackupSuffix)).Contains("mine=1"));
        Check("and the resolution is the display's", GameDisplay.ReadResolution(Path.Combine(prof, "fallout4prefs.ini")) == (1920, 1080));
        File.WriteAllText(Path.Combine(prof, "fallout4.ini"), "[General]\nsecond=1\n");
        GameDisplay.UseRecommended(inst, null);
        Check("a second replacement keeps the oldest backup", File.ReadAllText(Path.Combine(prof, "fallout4.ini" + GameDisplay.BackupSuffix)).Contains("mine=1"));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- Home tab: INI settings are tracked against the recommended INIs
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var inst = Path.Combine(dir, "inst"); var game = Path.Combine(dir, "game");
        Directory.CreateDirectory(game);
        Mo2InstanceWriter.EnsureProfileFiles(inst, game, (2560, 1440));
        var prof = Path.Combine(inst, "profiles", "A StoryWealth");
        HealthItem Row((int, int)? display) => InstallHealth.Check(inst, display).First(h => h.Label == "INI settings");
        Check("a fresh profile matches the recommended INIs", Row((2560, 1440)).Level == HealthLevel.Ok, Row((2560, 1440)).Detail);

        var prefsPath = Path.Combine(prof, "fallout4prefs.ini");
        var prefs = File.ReadAllText(prefsPath);
        var shadowNow = IniEdit.GetValue(prefs, "Display", "fShadowDistance");
        File.WriteAllText(prefsPath, IniEdit.SetValue(prefs, "AudioMenu", "fAudioMasterVolume", "0.1000"));
        Check("volume and other normal in-game changes are ignored", Row((2560, 1440)).Level == HealthLevel.Ok);
        File.WriteAllText(prefsPath, IniEdit.SetValue(File.ReadAllText(prefsPath), "Display", "fShadowDistance", "9999.0"));
        var changed = Row((2560, 1440));
        Check("a changed setting is named, with the recommended value", changed.Level == HealthLevel.Warn && changed.Detail.Contains("fShadowDistance") && changed.Detail.Contains("9999") && changed.Action == "use-recommended", changed.Detail);
        File.WriteAllText(prefsPath, IniEdit.SetValue(File.ReadAllText(prefsPath), "Display", "fShadowDistance", (shadowNow ?? "3000.0000") + "00"));
        Check("the same number written differently is not a change", Row((2560, 1440)).Level == HealthLevel.Ok, Row((2560, 1440)).Detail);

        var res = Row((3840, 2160));
        Check("only the screen size off gives the match-display action", res.Level == HealthLevel.Warn && res.Action == "match-resolution" && res.Detail.Contains("3840x2160"), res.Detail);
        File.Delete(Path.Combine(prof, "fallout4custom.ini"));
        Check("a missing INI is a failure", Row(null).Level == HealthLevel.Fail && Row(null).Detail.Contains("Fallout4Custom.ini"));
        GameDisplay.UseRecommended(inst, (2560, 1440));
        Check("using the recommended INIs makes it green again", Row((2560, 1440)).Level == HealthLevel.Ok, Row((2560, 1440)).Detail);
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- update dot: the installed list against the latest one
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var inst = Path.Combine(dir, "inst"); Directory.CreateDirectory(Path.Combine(inst, ".asw"));
        var latest = Path.Combine(dir, "latest.json");
        File.WriteAllText(Path.Combine(inst, ".asw", "a-storywealth.collection.json"), "{\"v\":282}");
        File.WriteAllText(latest, "{\"v\":282}");
        Check("same list: no update", InstallHealth.BaseUpdateAvailable(inst, latest) == false);
        File.WriteAllText(latest, "{\"v\":284}");
        Check("a different latest list: update available", InstallHealth.BaseUpdateAvailable(inst, latest) == true);
        Check("no saved copy: unknown", InstallHealth.BaseUpdateAvailable(Path.Combine(dir, "other"), latest) is null);
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- Home tab: install health
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var prof = Path.Combine(dir, "profiles", "A StoryWealth");
        Directory.CreateDirectory(prof);
        Directory.CreateDirectory(Path.Combine(dir, "mods", "Mod A"));
        File.WriteAllText(Path.Combine(dir, "ModOrganizer.exe"), "x");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# c\n+Mod A\n-Mod B\n-Weapons_separator\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "# c\n*One.esp\nTwo.esp\n");
        File.WriteAllText(Path.Combine(dir, "ModOrganizer.ini"), "[General]\ndownload_directory=%BASE_DIR%/dl\n");
        var health = InstallHealth.Check(dir);
        HealthItem H(string l) => health.First(h => h.Label == l);
        Check("a disabled mod is a failure and separators don't count", H("Mods").Level == HealthLevel.Fail && H("Mods").Detail.Contains("Mod B") && !H("Mods").Detail.Contains("Weapons"), H("Mods").Detail);
        Check("a disabled plugin is a failure", H("Plugins").Level == HealthLevel.Fail && H("Plugins").Detail.Contains("Two.esp"), H("Plugins").Detail);
        Check("F4SE missing from MO2 is reported", H("F4SE").Level == HealthLevel.Fail);
        var sum = InstallHealth.Summary(dir);
        Check("the summary counts mods and plugins", sum.Mods == 2 && sum.EnabledMods == 1 && sum.Plugins == 2 && sum.EnabledPlugins == 1);
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+Mod A\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*One.esp\n");
        var clean = InstallHealth.Check(dir);
        Check("nothing disabled gives green for mods and plugins", clean.First(h => h.Label == "Mods").Level == HealthLevel.Ok && clean.First(h => h.Label == "Plugins").Level == HealthLevel.Ok);
        Check("MO2's download folder comes from its ini", Mo2Launcher.DownloadsDir(dir) == Path.Combine(dir, "dl").Replace('/', '\\'), Mo2Launcher.DownloadsDir(dir));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- Home tab: finding the F4SE program in MO2's list
{
    var ini = new[] { "[General]", "x=1", "[customExecutables]", "size=3",
        @"1\title=Fallout 4", @"1\binary=D:/G/Stock Game/Fallout4.exe",
        @"2\title=A StoryWealth (F4SE)", @"2\binary=D:/G/mods/F4SE/Root/f4se_loader.exe",
        @"3\title=Other", @"3\binary=D:/G/Tools/x.exe", "[Settings]", @"4\title=Nope" };
    Check("the F4SE program is found by its file", Mo2Launcher.FindF4seTitleInIni(ini) == "A StoryWealth (F4SE)", Mo2Launcher.FindF4seTitleInIni(ini));
    Check("no F4SE program gives null", Mo2Launcher.FindF4seTitleInIni(ini.Where(l => !l.Contains("f4se")).ToArray()) is null);
}

// ---- plugins whose master isn't installed are turned off
{
    var infos = new Dictionary<string, PluginOrder.Info>(StringComparer.OrdinalIgnoreCase)
    {
        ["SS2.esm"] = new(true, false, []),
        ["IT_SS2.esp"] = new(false, false, ["SS2.esm", "Fallout4.esm"]),
        ["IT_SS2_Patch.esp"] = new(false, false, ["IT_SS2.esp"]),
        ["Plain.esp"] = new(false, false, ["Fallout4.esm", "DLCRobot.esm"]),
        ["NeedsGone.esp"] = new(false, false, ["Gone.esp"]),
    };
    var order = new[] { "IT_SS2.esp", "IT_SS2_Patch.esp", "Plain.esp", "NeedsGone.esp" };
    var en = order.ToDictionary(n => n, _ => true, StringComparer.OrdinalIgnoreCase);
    var bad = PluginOrder.MissingMasters(order, en, n => infos.TryGetValue(n, out var i) ? i : null).Select(x => x.Plugin).ToList();
    Check("a plugin missing its master is flagged, along with plugins that need it", bad.Order(StringComparer.Ordinal).SequenceEqual(new[] { "IT_SS2.esp", "IT_SS2_Patch.esp", "NeedsGone.esp" }.Order(StringComparer.Ordinal)), string.Join(",", bad));
    Check("vanilla masters never count as missing", !bad.Contains("Plain.esp"));
    en["IT_SS2.esp"] = true; var withSs2 = new[] { "SS2.esm", "IT_SS2.esp", "IT_SS2_Patch.esp" }; var en2 = withSs2.ToDictionary(n => n, _ => true, StringComparer.OrdinalIgnoreCase);
    Check("nothing is flagged when the master is there and enabled", PluginOrder.MissingMasters(withSs2, en2, n => infos.TryGetValue(n, out var i) ? i : null).Count == 0);
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

// ---- self-update from GitHub releases (no network: a fake HTTP handler serves the release, zip and signature)
{
    var root = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        Check("versions compare numerically, a pre-release is never offered",
            GitHubUpdater.IsNewer("v0.4.1", "0.4.0") && GitHubUpdater.IsNewer("0.10.0", "0.9.9") && !GitHubUpdater.IsNewer("0.4.0", "0.4.0")
            && !GitHubUpdater.IsNewer("0.5.0-beta", "0.4.0") && !GitHubUpdater.IsNewer("nonsense", "0.4.0") && GitHubUpdater.IsNewer("0.5.0", "0.4.0+abc123"));

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cfg = new UpdateConfig { Repo = "team/asw-installer", PublicKey = pub };
        Check("the updater is off without a repository or a key",
            !new UpdateConfig { Repo = "", PublicKey = pub }.Enabled && !new UpdateConfig { Repo = "team/x", PublicKey = "" }.Enabled
            && !new UpdateConfig { Repo = "nonsense", PublicKey = pub }.Enabled && cfg.Enabled);

        // a release zip (the exe is a stand-in; the version it reports is injected below)
        var zipBytes = new MemoryStream();
        using (var z = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
        {
            void Add(string n, string c) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(c); }
            Add("AStoryWealth.Installer.exe", "new exe"); Add("7z.exe", "7z"); Add("README.md", "readme");
        }
        var zip = zipBytes.ToArray();
        byte[] Sign(ECDsa k, byte[] data) => k.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var zipName = "AStoryWealth-Installer-0.5.0-win-x64.zip";
        string Release(string tag, bool pre = false, bool withSig = true, string host = "github.com") => $$"""
            {"tag_name":"{{tag}}","draft":false,"prerelease":{{(pre ? "true" : "false")}},"html_url":"https://github.com/team/asw-installer/releases/{{tag}}","body":"notes",
             "assets":[{"name":"{{zipName}}","size":{{zip.Length}},"browser_download_url":"https://{{host}}/team/asw-installer/releases/download/{{tag}}/{{zipName}}"}
                       {{(withSig ? $$""",{"name":"{{zipName}}.sig","size":96,"browser_download_url":"https://{{host}}/team/asw-installer/releases/download/{{tag}}/{{zipName}}.sig"}""" : "")}}]}
            """;
        HttpResponseMessage Ok(HttpRequestMessage rq, HttpContent c) => new(System.Net.HttpStatusCode.OK) { Content = c, RequestMessage = rq };
        int calls = 0;
        GitHubUpdater Updater(string releaseJson, ECDsa signer, UpdateConfig? c = null, string? exeVersion = "0.5.0+abc") => new(c ?? cfg,
            new HttpClient(new FakeHandler(rq =>
            {
                calls++;
                var u = rq.RequestUri!.ToString();
                if (u.EndsWith("/releases/latest")) return Ok(rq, new StringContent(releaseJson));
                if (u.Contains("/releases?per_page")) return Ok(rq, new StringContent("[" + releaseJson + "]"));
                if (u.EndsWith(".zip")) return Ok(rq, new ByteArrayContent(zip));
                if (u.EndsWith(".sig")) return Ok(rq, new StringContent(Convert.ToBase64String(Sign(signer, zip))));
                return new(System.Net.HttpStatusCode.NotFound) { RequestMessage = rq };
            })), Path.Combine(root, "staging"), _ => exeVersion);

        var info = await Updater(Release("v0.5.0"), ecdsa).CheckAsync("0.4.0");
        Check("a newer signed release is found", info is not null && info.Version == "0.5.0" && info.ZipName == zipName && info.Notes == "notes", info?.Version);
        Check("the same or an older version is not offered", await Updater(Release("v0.4.0"), ecdsa).CheckAsync("0.4.0") is null && await Updater(Release("v0.3.9"), ecdsa).CheckAsync("0.4.0") is null);
        Check("a pre-release is ignored", await Updater(Release("v0.5.0", pre: true), ecdsa).CheckAsync("0.4.0") is null);
        Check("versions order as 0.4.6 < 0.5.0-beta.1 < 0.5.0-beta.2 < 0.5.0 < 0.5.1",
            new[] { "0.4.6", "0.5.0-beta.1", "0.5.0-beta.2", "0.5.0-beta.10", "0.5.0", "0.5.1" }.Zip(new[] { "0.5.0-beta.1", "0.5.0-beta.2", "0.5.0-beta.10", "0.5.0", "0.5.1" })
                .All(x => GitHubUpdater.Compare(x.First, x.Second) < 0 && GitHubUpdater.Compare(x.Second, x.First) > 0)
            && GitHubUpdater.Compare("v0.5.0+abc", "0.5.0") == 0);
        Check("a test build is offered to someone on an older release only when they opted in",
            await Updater(Release("v0.5.0-beta.1", pre: true), ecdsa).CheckAsync("0.4.0") is null
            && (await Updater(Release("v0.5.0-beta.1", pre: true), ecdsa).CheckAsync("0.4.0", includeTestBuilds: true))?.Version == "0.5.0-beta.1");
        Check("a test build is not offered once you're on that test build or a later one",
            await Updater(Release("v0.5.0-beta.1", pre: true), ecdsa).CheckAsync("0.5.0-beta.1", true) is null
            && await Updater(Release("v0.5.0-beta.1", pre: true), ecdsa).CheckAsync("0.5.0", true) is null);
        var betaInfo = await Updater(Release("v0.5.0-beta.1", pre: true), ecdsa, exeVersion: "0.5.0-beta.1+abc").CheckAsync("0.4.0", true);
        var betaStaged = await Updater(Release("v0.5.0-beta.1", pre: true), ecdsa, exeVersion: "0.5.0-beta.1+abc").DownloadAndVerifyAsync(betaInfo!, "0.4.0");
        Check("a signed test build downloads and verifies", File.Exists(Path.Combine(betaStaged, "AStoryWealth.Installer.exe")));
        Check("a release without a signature is ignored", await Updater(Release("v0.5.0", withSig: false), ecdsa).CheckAsync("0.4.0") is null);
        Check("assets hosted somewhere else are ignored", await Updater(Release("v0.5.0", host: "evil.example.com"), ecdsa).CheckAsync("0.4.0") is null);
        calls = 0;
        Check("a build with no repository or key makes no request", await Updater(Release("v0.5.0"), ecdsa, new UpdateConfig()).CheckAsync("0.4.0") is null && calls == 0);

        Check("signature check: right key passes, wrong key and tampered data fail",
            GitHubUpdater.VerifySignature(new MemoryStream(zip), Sign(ecdsa, zip), pub)
            && !GitHubUpdater.VerifySignature(new MemoryStream(zip), Sign(other, zip), pub)
            && !GitHubUpdater.VerifySignature(new MemoryStream([.. zip, 0]), Sign(ecdsa, zip), pub));

        var good = Updater(Release("v0.5.0"), ecdsa);
        var staged = await good.DownloadAndVerifyAsync(info!, "0.4.0");
        Check("a verified update is unpacked and ready", File.Exists(Path.Combine(staged, "AStoryWealth.Installer.exe")) && File.Exists(Path.Combine(staged, "7z.exe")));
        var forged = Updater(Release("v0.5.0"), other);
        var forgedThrew = false;
        try { await forged.DownloadAndVerifyAsync(info!, "0.4.0"); } catch (InvalidOperationException) { forgedThrew = true; }
        Check("an update signed with another key is refused and nothing is left behind", forgedThrew && !Directory.Exists(Path.Combine(root, "staging", "0.5.0")));
        var replayThrew = false;
        try { await Updater(Release("v0.5.0"), ecdsa, exeVersion: "0.4.9").DownloadAndVerifyAsync(info!, "0.4.0"); } catch (InvalidOperationException) { replayThrew = true; }
        Check("an old signed build under a new tag is refused (the exe's own version must match)", replayThrew);

        // applying: files replaced, the user's order\ overrides kept, and a failure puts everything back
        var app = Path.Combine(root, "app"); var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(Path.Combine(app, "order")); Directory.CreateDirectory(Path.Combine(stage, "order")); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(app, "AStoryWealth.Installer.exe"), "old exe"); File.WriteAllText(Path.Combine(app, "7z.exe"), "old 7z");
        File.WriteAllText(Path.Combine(app, "order", "mine.json"), "mine");
        File.WriteAllText(Path.Combine(stage, "AStoryWealth.Installer.exe"), "new exe"); File.WriteAllText(Path.Combine(stage, "7z.exe"), "new 7z");
        File.WriteAllText(Path.Combine(stage, "NEW.txt"), "added"); File.WriteAllText(Path.Combine(stage, "order", "mine.json"), "shipped");
        var applied = UpdateApplier.Apply(stage, app, waitForPid: 0, restart: false);
        Check("apply replaces and adds files and leaves order\\ alone",
            applied && File.ReadAllText(Path.Combine(app, "AStoryWealth.Installer.exe")) == "new exe" && File.ReadAllText(Path.Combine(app, "NEW.txt")) == "added"
            && File.ReadAllText(Path.Combine(app, "order", "mine.json")) == "mine");
        // failure: a staged file whose target is a directory
        var app2 = Path.Combine(root, "app2"); var stage2 = Path.Combine(root, "stage2");
        Directory.CreateDirectory(app2); Directory.CreateDirectory(stage2); Directory.CreateDirectory(Path.Combine(app2, "blocked.txt"));
        File.WriteAllText(Path.Combine(app2, "AStoryWealth.Installer.exe"), "old exe");
        File.WriteAllText(Path.Combine(stage2, "AStoryWealth.Installer.exe"), "new exe"); File.WriteAllText(Path.Combine(stage2, "ZZ-blocked.txt"), "x");
        Directory.Move(Path.Combine(app2, "blocked.txt"), Path.Combine(app2, "ZZ-blocked.txt"));
        var failedApply = UpdateApplier.Apply(stage2, app2, waitForPid: 0, restart: false);
        Check("a failed apply puts the old files back", !failedApply && File.ReadAllText(Path.Combine(app2, "AStoryWealth.Installer.exe")) == "old exe");
        Check("a read-only install folder is detected", UpdateApplier.CanWrite(app) && !UpdateApplier.CanWrite(Path.Combine(root, "no-such-folder")));
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

// ---- the install record on a machine that has never saved one (a fresh PC): it must be written and read back
{
    var state = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    var inst = Path.Combine(state, "instance");
    try
    {
        Directory.CreateDirectory(inst);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.exe"), "x");
        InstallState.UseDirectory(Path.Combine(state, "appdata"));
        Check("a new machine has no recorded install", InstallState.GetInstallDir() is null && InstallState.GetResume() is null);
        InstallState.SetResume(new InstallState.ResumeInfo("c.json", "a-storywealth", inst, "game", true, null, null, [], [], false));
        Check("the first checkpoint is saved (it used to be lost without a trace)", InstallState.GetResume() is { } r && r.InstallDir == inst);
        InstallState.SetInstallDir(inst);
        InstallState.ClearResume();
        Check("a finished install is remembered, so the app knows it is installed", InstallState.GetInstallDir() == inst && InstallState.GetResume() is null, InstallState.GetInstallDir());
        InstallState.SetLastPath(inst, null, null, true);
        Check("later writes keep the install and the remembered paths", InstallState.GetInstallDir() == inst && InstallState.GetLastDir() == inst);
    }
    finally { InstallState.UseDirectory(null); try { Directory.Delete(state, true); } catch { } }
}

// ---- a collection that lists a file a few bytes bigger than it really is (Nightmare Deathclaw redux, 3 bytes) still verifies
{
    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        var bytes = Encoding.ASCII.GetBytes(new string('x', 1000));
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        var f = Path.Combine(dir, "a.zip"); File.WriteAllBytes(f, bytes);
        ModEntry M(long size, string hash) => new() { InstallAs = "m", Nexus = new NexusFileRef { ModId = 1, FileId = 1, Md5 = hash, FileSize = size } };
        Check("a size a few bytes off still verifies when the md5 matches", (await ManifestResolver.VerifyArchiveAsync(f, M(1003, md5))).ok);
        Check("an exact size verifies", (await ManifestResolver.VerifyArchiveAsync(f, M(1000, md5))).ok);
        Check("a few bytes off with the wrong md5 still fails (the md5 decides)", !(await ManifestResolver.VerifyArchiveAsync(f, M(1003, "0123456789abcdef0123456789abcdef"))).ok);
        Check("a truncated file (a big size gap) fails on size", (await ManifestResolver.VerifyArchiveAsync(f, M(1_000_000, md5))) is { ok: false, detail: var d } && d.StartsWith("size mismatch"));
        // a free-account download whose size is 3 bytes off the collection's is still picked up from the browser folder
        var watch = Path.Combine(dir, "browser"); Directory.CreateDirectory(watch);
        File.Copy(f, Path.Combine(watch, "Nightmare Deathclaw redux V1.5-1-1.zip"));
        var got = await ManualDownloader.FetchAsync(M(1003, md5), watch, n => Path.Combine(dir, "dl", n),
            p => ManifestResolver.VerifyArchiveAsync(p, M(1003, md5)), _ => Check("no page should be opened for a file that is already there", false), CancellationToken.None);
        Check("a manual download 3 bytes off the listed size is accepted", File.Exists(got));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ---- removing an add-on or one optional mod
{
    var inst = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var profile = Path.Combine(inst, "profiles", "A StoryWealth"); Directory.CreateDirectory(profile);
        void MakeMod(string name, params string[] plugins)
        {
            var d = Path.Combine(inst, "mods", name); Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "data.bin"), "12345");
            foreach (var p in plugins) File.WriteAllText(Path.Combine(d, p), "p");
        }
        MakeMod("Base A", "BaseA.esp"); MakeMod("Add One", "AddOne.esp"); MakeMod("Add Two"); MakeMod("Shared", "Shared.esp"); MakeMod("Opt Pick", "OptPick.esp");
        MakeMod("Base Opt", "BaseOpt.esp"); MakeMod("Y1", "Y1.esp");
        MakeMod("X_separator");   // folder MO2 uses for the separator
        File.WriteAllText(Path.Combine(inst, "mods", "X_separator", "meta.ini"), "[General]\nnotes=" + Mo2InstanceWriter.SeparatorMarker + "\n");
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), ["# h", "+Y1", "-Y_separator", "+Opt Pick", "+Add Two", "+Shared", "+Add One", "-X_separator", "+Base Opt", "+Base A"]);
        File.WriteAllLines(Path.Combine(profile, "plugins.txt"), ["# h", "*BaseA.esp", "*BaseOpt.esp", "*AddOne.esp", "*Shared.esp", "*OptPick.esp", "*Y1.esp"]);
        File.WriteAllLines(Path.Combine(profile, "loadorder.txt"), ["# h", "BaseA.esp", "BaseOpt.esp", "AddOne.esp", "Shared.esp", "OptPick.esp", "Y1.esp"]);
        Directory.CreateDirectory(Path.Combine(inst, ".asw")); File.WriteAllText(Path.Combine(inst, ".asw", "x.collection.json"), "{}");
        AddonRegistry.Save(inst, new AddonRegistryFile
        {
            BaseOptional = ["mdbase"],
            Addons =
            [
                new InstalledAddon { Id = "x", Name = "Add-on X", Separator = "X", Optional = ["mdopt"], Mods = [new() { Name = "Add One", Md5 = "m1" }, new() { Name = "Add Two", Md5 = "m2" }, new() { Name = "Shared", Md5 = "ms" }, new() { Name = "Opt Pick", Md5 = "mdopt" }] },
                new InstalledAddon { Id = "y", Name = "Add-on Y", Separator = "Y", Mods = [new() { Name = "Shared", Md5 = "ms" }, new() { Name = "Y1", Md5 = "m3" }] },
            ],
        });
        var baseNames = new HashSet<string>(["Base A", "Base Opt"], StringComparer.OrdinalIgnoreCase);

        var (plan, planKept) = Removal.PlanAddon(inst, "x", baseNames);
        Check("the plan lists what goes and what stays, without touching anything",
            J(plan.OrderBy(x => x)) == "Add One | Add Two | Opt Pick" && planKept.Count == 1 && planKept[0].StartsWith("Shared") && Directory.Exists(Path.Combine(inst, "mods", "Add One")));

        var optBlocked = Removal.RemoveOptional(inst, null, "Shared", "ms", baseNames);
        Check("an optional mod another add-on needs is refused", optBlocked.Refused is not null && Directory.Exists(Path.Combine(inst, "mods", "Shared")));
        var optBase = Removal.RemoveOptional(inst, null, "Base Opt", "mdbase", baseNames);
        Check("removing a base optional mod deletes its folder, line and plugin, and forgets the pick",
            optBase.FoldersDeleted == 1 && !Directory.Exists(Path.Combine(inst, "mods", "Base Opt"))
            && !File.ReadAllText(Path.Combine(profile, "modlist.txt")).Contains("Base Opt") && !File.ReadAllText(Path.Combine(profile, "plugins.txt")).Contains("BaseOpt.esp")
            && AddonRegistry.Load(inst).BaseOptional.Count == 0);

        var r = Removal.RemoveAddon(inst, "x", baseNames);
        var ml = File.ReadAllText(Path.Combine(profile, "modlist.txt")); var pl = File.ReadAllText(Path.Combine(profile, "plugins.txt")); var lo = File.ReadAllText(Path.Combine(profile, "loadorder.txt"));
        Check("removing an add-on deletes its own mods and separator, keeps shared and base mods",
            r.FoldersDeleted == 3 && !Directory.Exists(Path.Combine(inst, "mods", "Add One")) && !Directory.Exists(Path.Combine(inst, "mods", "Opt Pick")) && !Directory.Exists(Path.Combine(inst, "mods", "X_separator"))
            && Directory.Exists(Path.Combine(inst, "mods", "Shared")) && Directory.Exists(Path.Combine(inst, "mods", "Base A")) && r.Kept.Count == 1, J(r.Kept));
        Check("its lines leave modlist.txt, the others stay",
            !ml.Contains("Add One") && !ml.Contains("Add Two") && !ml.Contains("Opt Pick") && !ml.Contains("X_separator") && ml.Contains("Shared") && ml.Contains("Y1") && ml.Contains("Base A"), ml);
        Check("only plugins the removed mods provided leave plugins.txt and loadorder.txt",
            !pl.Contains("AddOne.esp") && !pl.Contains("OptPick.esp") && pl.Contains("Shared.esp") && pl.Contains("Y1.esp") && pl.Contains("BaseA.esp") && !lo.Contains("AddOne.esp") && lo.Contains("Shared.esp"), pl);
        var reg = AddonRegistry.Load(inst);
        Check("the record, and the kept copy of its collection list, are gone; the other add-on is untouched",
            reg.Addons.Count == 1 && reg.Addons[0].Id == "y" && !File.Exists(Path.Combine(inst, ".asw", "x.collection.json")));
        Check("removing something that isn't installed is refused", Removal.RemoveAddon(inst, "x", baseNames).Refused is not null);
    }
    finally { try { Directory.Delete(inst, true); } catch { } }
}

// ---- support report: one zip with logs, lists and a summary; the API key never leaves the PC
{
    var root = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var inst = Path.Combine(root, "inst"); var profile = Path.Combine(inst, "profiles", "A StoryWealth");
        Directory.CreateDirectory(profile);
        const string key = "SECRETKEY1234567890";
        File.WriteAllText(Path.Combine(inst, "install-20261022-100000.log"), $"2026 [INFO ] start\nheader apikey: {key}\n");
        File.WriteAllText(Path.Combine(inst, "install-20261022-110000.log"), "second run\n");
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), ["# header", "+Mod B", "-Fixes_separator", "-Mod A"]);
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*A.esp\n");
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"), $"nexus={key}");
        AddonRegistry.Save(inst, new AddonRegistryFile { BaseGroups = ["ss2"], Addons = [new InstalledAddon { Id = "x", Name = "Some Add-on", Revision = "r5" }] });
        var zipPath = Path.Combine(root, "out", "report.zip");
        ReportBuilder.Build(zipPath, new ReportContext("9.9.9", inst, "G:\\Games\\Fallout 4", null, true, false, false, key));
        using var z = ZipFile.OpenRead(zipPath);
        string Read(string n) { using var r = new StreamReader(z.GetEntry(n)!.Open()); return r.ReadToEnd(); }
        var names = z.Entries.Select(e => e.FullName).ToList();
        Check("the report holds the summary, the logs, the lists and the add-on record",
            new[] { "report.txt", "logs/install-20261022-100000.log", "logs/install-20261022-110000.log", "profile/modlist.txt", "profile/plugins.txt", "instance/.asw-addons.json" }.All(names.Contains), J(names));
        Check("the summary says the version, game mode, account type, mods and add-ons",
            Read("report.txt") is var s && s.Contains("9.9.9") && s.Contains("separate copy") && s.Contains("free") && s.Contains("2 mods") && s.Contains("Some Add-on r5") && s.Contains("ss2"), Read("report.txt"));
        Check("the API key is removed from the logs", !Read("logs/install-20261022-100000.log").Contains(key) && Read("logs/install-20261022-100000.log").Contains("[key removed]"));
        Check("files that could hold the key (ModOrganizer.ini) are not included", !names.Any(n => n.Contains("ModOrganizer.ini")));
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

// ---- the report hides who sent it
{
    var who = new ReportBuilder.LocalIdentity(@"C:\Users\jdoe-pc", "jdoe-pc", "DESKTOP-ABC123", "NexusFan");
    var log = @"downloads C:\Users\jdoe-pc\AppData\Local\AStoryWealth\Downloads, also c:/users/JDOE-PC/x, user nexusfan premium=True on DESKTOP-ABC123. Mod: Nexus Fanatic Pack";
    var outp = ReportBuilder.Anonymize(log, who);
    Check("the profile folder becomes %USERPROFILE% in both slash styles", outp.Contains(@"%USERPROFILE%\AppData") && outp.Contains("%USERPROFILE%/x") && !outp.Contains("jdoe", StringComparison.OrdinalIgnoreCase), outp);
    Check("the Nexus account and PC name are hidden, case ignored", outp.Contains("user <nexus-user> premium") && outp.Contains("on <pc>.") && !outp.Contains("DESKTOP-ABC123"), outp);
    Check("a longer word that merely contains the name is left alone", outp.Contains("Nexus Fanatic Pack"), outp);
    Check("short names are not touched (they'd mangle ordinary words)", ReportBuilder.Anonymize("a mod for the user", new("C:\\Users\\a", "a", "pc", "me")) == "a mod for the user");

    var root = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var inst = Path.Combine(root, "inst"); var profile = Path.Combine(inst, "profiles", "A StoryWealth");
        Directory.CreateDirectory(profile);
        var me = ReportBuilder.LocalNames(null);
        File.WriteAllText(Path.Combine(inst, "install-20261022-100000.log"), $"downloads in {me.ProfileDir}\\AStoryWealth for NexusZed\n");
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), ["+NexusZed Retexture"]);
        var zipPath = Path.Combine(root, "r.zip");
        ReportBuilder.Build(zipPath, new ReportContext("9.9.9", inst, null, null, true, false, false, null, "NexusZed"));
        using var z = ZipFile.OpenRead(zipPath);
        string Read(string n) { using var r = new StreamReader(z.GetEntry(n)!.Open()); return r.ReadToEnd(); }
        var logText = Read("logs/install-20261022-100000.log");
        Check("in a built report the log hides the profile folder and the account", logText.Contains("%USERPROFILE%\\AStoryWealth") && logText.Contains("<nexus-user>") && !logText.Contains("NexusZed"), logText);
        Check("the report says names are hidden", Read("report.txt").Contains("are hidden"));
        Check("the mod list is left as it is", Read("profile/modlist.txt").Contains("NexusZed Retexture"));
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

// ---- Nexus sign-in: OAuth with PKCE
{
    // the example from RFC 7636, appendix B
    Check("PKCE: the S256 challenge of the RFC's example verifier",
        NexusSession.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    var v1 = NexusSession.NewVerifier(); var v2 = NexusSession.NewVerifier();
    Check("PKCE: verifiers are 43-128 URL-safe characters and differ each time",
        v1.Length is >= 43 and <= 128 && v1 != v2 && v1.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'), v1);

    var cfg = new NexusOAuthConfig { ClientId = "test_client", Port = 18089 };
    var urlSession = new NexusSession(cfg, new MemStore(null));
    var au = urlSession.BuildAuthorizeUrl("st4te", "chal");
    Check("the authorize URL carries the client, the exact loopback redirect, S256, state and challenge",
        au.StartsWith("https://users.nexusmods.com/oauth/authorize?") && au.Contains("client_id=test_client") && au.Contains("response_type=code")
        && au.Contains("redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:18089/callback")) && au.Contains("code_challenge_method=S256")
        && au.Contains("state=st4te") && au.Contains("code_challenge=chal"), au);

    Check("callback: a code with the right state is accepted", NexusSession.ParseCallback("GET /callback?code=abc%20d&state=S HTTP/1.1", "S") is { Code: "abc d", Error: null });
    Check("callback: a wrong state is refused, not accepted", NexusSession.ParseCallback("GET /callback?code=abc&state=X HTTP/1.1", "S") is { Code: null, Error: not null });
    Check("callback: Nexus's error text is passed on", NexusSession.ParseCallback("GET /callback?error=access_denied&error_description=You+said+no&state=S HTTP/1.1", "S") is { Error: "You said no" });
    Check("callback: other paths (favicon) are ignored", NexusSession.ParseCallback("GET /favicon.ico HTTP/1.1", "S").Ignore);

    var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    var tk = NexusSession.ParseTokens("{\"access_token\":\"A1\",\"refresh_token\":\"R1\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", null, now);
    Check("tokens: access, refresh and expiry are read", tk is { AccessToken: "A1", RefreshToken: "R1" } && tk.ExpiresAtUtc == now.AddHours(1));
    Check("tokens: a refresh answer without a new refresh token keeps the old one", NexusSession.ParseTokens("{\"access_token\":\"A2\",\"expires_in\":60}", "R1", now).RefreshToken == "R1");

    // a session with a stored sign-in
    string StoredJson(string a, string r, DateTime exp) => System.Text.Json.JsonSerializer.Serialize(new { access = a, refresh = r, expires = exp });
    var calls = new List<string>();
    NexusSession Make(MemStore store, Func<HttpRequestMessage, HttpResponseMessage> respond) => new(cfg, store, new FakeHandler(r => { calls.Add(r.RequestUri!.ToString() + " " + (r.Content?.ReadAsStringAsync().Result ?? "")); return respond(r); }));
    HttpResponseMessage Json(int code, string body) => new((System.Net.HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    const string TokenBody = "{\"access_token\":\"NEW\",\"refresh_token\":\"R2\",\"expires_in\":3600}";

    var fresh = new MemStore(StoredJson("OLD", "R1", DateTime.UtcNow.AddHours(1)));
    var s1 = Make(fresh, _ => Json(500, "{}"));
    Check("a stored sign-in is picked up and used without asking Nexus", s1.IsSignedIn && s1.GetAccessTokenAsync(default).Result == "OLD" && calls.Count == 0);

    var expired = new MemStore(StoredJson("OLD", "R1", DateTime.UtcNow.AddSeconds(-5)));
    calls.Clear();
    var s2 = Make(expired, _ => Json(200, TokenBody));
    Check("an expired access token is renewed with the refresh token", s2.GetAccessTokenAsync(default).Result == "NEW"
        && calls.Count == 1 && calls[0].Contains("grant_type=refresh_token") && calls[0].Contains("refresh_token=R1") && calls[0].Contains("client_id=test_client"), J(calls));
    Check("the renewed tokens are saved (the new refresh token replaces the old)", expired.Json!.Contains("\"R2\"") && expired.Json.Contains("NEW"), expired.Json);

    var revoked = new MemStore(StoredJson("OLD", "R1", DateTime.UtcNow.AddSeconds(-5)));
    var s3 = Make(revoked, _ => Json(400, "{\"error\":\"invalid_grant\"}"));
    var signedOut = false;
    try { s3.GetAccessTokenAsync(default).Wait(); } catch (AggregateException ex) when (ex.InnerException is NexusSignedOutException) { signedOut = true; }
    Check("a refused refresh (400) means signed out, and the saved sign-in is deleted", signedOut && !s3.IsSignedIn && revoked.Json is null);

    var outage = new MemStore(StoredJson("OLD", "R1", DateTime.UtcNow.AddSeconds(-5)));
    var s4 = Make(outage, _ => Json(503, "down"));
    var transient = false;
    try { s4.GetAccessTokenAsync(default).Wait(); } catch (AggregateException ex) when (ex.InnerException is HttpRequestException) { transient = true; }
    Check("a Nexus outage (5xx) is not a sign-out: the tokens are kept", transient && s4.IsSignedIn && outage.Json is not null);

    // the handler: Bearer for the API host only
    var seen = new List<(string Host, string? Auth, bool ApiKey)>();
    var sBearer = Make(new MemStore(StoredJson("TOK", "R", DateTime.UtcNow.AddHours(1))), _ => Json(200, "{}"));
    var h = new CredentialHandler(sBearer, new FakeHandler(r => { seen.Add((r.RequestUri!.Host, r.Headers.Authorization?.ToString(), r.Headers.Contains("apikey"))); return Json(200, "{}"); }));
    using (var hc = new HttpClient(h))
    {
        hc.GetAsync("https://api.nexusmods.com/v1/users/validate.json").Wait();
        hc.GetAsync("https://cf-files.nexus-cdn.com/file.7z").Wait();
    }
    Check("the Bearer token goes to the API host only, never to a download address", seen.Count == 2 && seen[0].Auth == "Bearer TOK" && seen[1].Auth is null && !seen[0].ApiKey, J(seen.Select(x => x.Host + ":" + x.Auth)));
    var seenKey = new List<(string Host, bool ApiKey)>();
    using (var kc = new HttpClient(new CredentialHandler(new ApiKeyCredential("DEVKEY"), new FakeHandler(r => { seenKey.Add((r.RequestUri!.Host, r.Headers.Contains("apikey"))); return Json(200, "{}"); }))))
    {
        kc.GetAsync("https://api.nexusmods.com/v2/graphql").Wait();
        kc.GetAsync("https://cf-files.nexus-cdn.com/file.7z").Wait();
    }
    Check("a developer key is also only sent to the API host", seenKey.Count == 2 && seenKey[0].ApiKey && !seenKey[1].ApiKey);

    // the whole sign-in, with a pretend browser knocking on the loopback address
    int FreePort() { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    var port = FreePort();
    var cfg2 = new NexusOAuthConfig { ClientId = "test_client", Port = port };
    var store = new MemStore(null);
    calls.Clear();
    var signIn = new NexusSession(cfg2, store, new FakeHandler(r => { calls.Add(r.Content!.ReadAsStringAsync().Result); return Json(200, "{\"access_token\":\"ACC\",\"refresh_token\":\"REF\",\"expires_in\":3600}"); }));
    string? stray = null, good = null;
    signIn.SignInAsync(url =>
    {
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;
        Task.Run(async () =>
        {
            using var browser = new HttpClient();
            stray = (await browser.GetAsync($"http://127.0.0.1:{port}/callback?code=evil&state=wrong")).StatusCode.ToString();
            good = (await browser.GetAsync($"http://127.0.0.1:{port}/callback?code=CODE123&state={state}")).StatusCode.ToString();
        });
    }, default, TimeSpan.FromSeconds(20)).Wait();
    Check("sign-in: a forged answer is refused and the real one finishes it", stray == "BadRequest" && good == "OK" && signIn.IsSignedIn, stray + "/" + good);
    Check("sign-in: the code is swapped with the PKCE verifier and the exact redirect",
        calls.Count == 1 && calls[0].Contains("grant_type=authorization_code") && calls[0].Contains("code=CODE123") && calls[0].Contains("code_verifier=") && calls[0].Contains("redirect_uri=" + Uri.EscapeDataString($"http://127.0.0.1:{port}/callback")), J(calls));
    Check("sign-in: the tokens are stored", store.Json is not null && store.Json.Contains("ACC") && store.Json.Contains("REF"));

    calls.Clear();
    signIn.SignOutAsync().Wait();
    Check("sign-out: forgets the tokens and tells Nexus to revoke", !signIn.IsSignedIn && store.Json is null && calls.Count == 1 && calls[0].Contains("token=REF") && calls[0].Contains("token_type_hint=refresh_token"), J(calls));

    // reports never carry a token
    var rroot = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var inst = Path.Combine(rroot, "inst"); Directory.CreateDirectory(inst);
        File.WriteAllText(Path.Combine(inst, "install-20261022-100000.log"), "auth: Bearer ACCESSTOKEN123 and refresh REFRESHTOKEN456\n");
        var zp = Path.Combine(rroot, "r.zip");
        ReportBuilder.Build(zp, new ReportContext("9.9.9", inst, null, null, true, false, false, null, null, ["ACCESSTOKEN123", "REFRESHTOKEN456"]));
        using var z = ZipFile.OpenRead(zp);
        using var rd = new StreamReader(z.GetEntry("logs/install-20261022-100000.log")!.Open());
        var t = rd.ReadToEnd();
        Check("the report removes the access and refresh tokens", !t.Contains("ACCESSTOKEN123") && !t.Contains("REFRESHTOKEN456") && t.Contains("[key removed]"), t);
    }
    finally { try { Directory.Delete(rroot, true); } catch { } }
}

// ---- audit fixes
{
    // a FOMOD destination must stay inside the mod folder
    var modRoot = Path.Combine(Path.GetTempPath(), "asw-tests-root", "mods", "A.part");
    Check("a path inside the mod folder is inside", Mo2InstanceWriter.IsInsideFolder(modRoot, Path.Combine(modRoot, "Meshes", "x.nif")) && Mo2InstanceWriter.IsInsideFolder(modRoot, modRoot));
    Check("a '..' in the middle can't climb out", !Mo2InstanceWriter.IsInsideFolder(modRoot, Path.GetFullPath(Path.Combine(modRoot, "a", "..", "..", "..", "evil"))) && !Mo2InstanceWriter.IsInsideFolder(modRoot, Path.Combine(modRoot, "..", "B")));
    Check("a sibling folder with the same start is not inside", !Mo2InstanceWriter.IsInsideFolder(modRoot, modRoot + "2"));

    var dir = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);

        // FOMOD XML: a DTD is skipped, never expanded
        var xml = Path.Combine(dir, "ModuleConfig.xml");
        File.WriteAllText(xml, "<!DOCTYPE config [<!ENTITY boom \"BOOM\">]><config><name>fine</name></config>");
        Check("a FOMOD file with a DTD still loads", FomodResolver.LoadXml(xml).Root?.Element("name")?.Value == "fine");
        File.WriteAllText(xml, "<!DOCTYPE config [<!ENTITY boom \"BOOM\">]><config><name>&boom;</name></config>");
        string? expanded = null; try { expanded = FomodResolver.LoadXml(xml).Root?.Element("name")?.Value; } catch (System.Xml.XmlException) { expanded = "refused"; }
        Check("an entity in a FOMOD file is never expanded", expanded != "BOOM", expanded);

        // unfinished staging folders from a cancelled run are swept, real mods are not
        var inst = Path.Combine(dir, "inst");
        foreach (var d in new[] { "Half.part", "Raw Mod.raw", "Real Mod", "Fixes_separator" }) Directory.CreateDirectory(Path.Combine(inst, "mods", d));
        var swept = Mo2InstanceWriter.SweepStaging(inst);
        Check("only .part and .raw folders are swept", swept == 2 && !Directory.Exists(Path.Combine(inst, "mods", "Half.part")) && !Directory.Exists(Path.Combine(inst, "mods", "Raw Mod.raw"))
            && Directory.Exists(Path.Combine(inst, "mods", "Real Mod")) && Directory.Exists(Path.Combine(inst, "mods", "Fixes_separator")));

        // a mod that is only an AAF / Video folder is content, not a wrapper
        var aaf = Path.Combine(dir, "aaf-mod"); Directory.CreateDirectory(Path.Combine(aaf, "AAF"));
        File.WriteAllText(Path.Combine(aaf, "AAF", "theme.xml"), "x");
        Mo2InstanceWriter.StripWrapper(aaf, "t");
        Check("a lone AAF folder stays where it is", File.Exists(Path.Combine(aaf, "AAF", "theme.xml")) && !File.Exists(Path.Combine(aaf, "theme.xml")));
        var wrapped = Path.Combine(dir, "wrapped-mod"); Directory.CreateDirectory(Path.Combine(wrapped, "Some Mod 1.2"));
        File.WriteAllText(Path.Combine(wrapped, "Some Mod 1.2", "plugin.esp"), "x");
        Mo2InstanceWriter.StripWrapper(wrapped, "t");
        Check("a real wrapper folder is still removed", File.Exists(Path.Combine(wrapped, "plugin.esp")));

        // tools with a line break in a field are refused, so they can't add INI keys
        var game = Path.Combine(dir, "Game"); Directory.CreateDirectory(Path.Combine(game, "Data", "Tools"));
        File.WriteAllText(Path.Combine(game, "ok.exe"), "x");
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"), "[General]\ngameName=Fallout 4\n\n[customExecutables]\nsize=0\n");
        Mo2InstanceWriter.WriteCustomExecutables(inst, game, [
            new ToolEntry { Name = "Fine", Exe = "ok.exe" },
            new ToolEntry { Name = "Evil\nsneaky=1", Exe = "ok.exe" },
            new ToolEntry { Name = "EvilArgs", Exe = "ok.exe", Args = ["--x\n[Settings]"] }]);
        var ini = File.ReadAllText(Path.Combine(inst, "ModOrganizer.ini"));
        Check("a tool with a line break is skipped", ini.Contains("title=Fine") && !ini.Contains("sneaky") && !ini.Contains("EvilArgs") && !ini.Contains("[Settings]"), ini);

        // add-on registry: a locked file isn't read as empty, a damaged one is kept as a copy, saves are whole
        var reg = Path.Combine(dir, "reg"); Directory.CreateDirectory(reg);
        AddonRegistry.Save(reg, new AddonRegistryFile { Addons = [new InstalledAddon { Id = "x", Name = "X", Revision = "r1" }] });
        Check("the record saves and loads", AddonRegistry.LoadStrict(reg).Addons.Count == 1 && !File.Exists(Path.Combine(reg, AddonRegistry.FileName + ".tmp")));
        using (var hold = new FileStream(Path.Combine(reg, AddonRegistry.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var threw = false; try { AddonRegistry.LoadStrict(reg); } catch (IOException) { threw = true; }
            Check("a locked record is an error for code that writes, not an empty record", threw);
            Check("showing code still gets an empty record instead of a crash", AddonRegistry.Load(reg).Addons.Count == 0);
        }
        File.WriteAllText(Path.Combine(reg, AddonRegistry.FileName), "{ this is not json");
        Check("a damaged record is kept as a .bak copy", AddonRegistry.LoadStrict(reg).Addons.Count == 0 && File.ReadAllText(Path.Combine(reg, AddonRegistry.FileName + ".bak")).Contains("not json"));

        // install.json: a lock for a moment doesn't move the file away
        var stateDir = Path.Combine(dir, "state"); Directory.CreateDirectory(stateDir);
        InstallState.UseDirectory(stateDir);
        try
        {
            InstallState.SetThreads(new InstallState.ThreadSettings(2, 2));
            var installJson = Path.Combine(stateDir, "install.json");
            File.WriteAllText(installJson, "{\"InstallDir\":\"C:\\\\X\",\"CompletedUtc\":\"2026-10-26\"}");
            using (var hold2 = new FileStream(installJson, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                InstallState.GetResume();
            Check("a locked install.json stays where it is", File.Exists(installJson) && !File.Exists(installJson + ".bak"));
            File.WriteAllText(installJson, "not json at all");
            InstallState.GetResume();
            Check("a damaged install.json is set aside as .bak", File.Exists(installJson + ".bak"));
        }
        finally { InstallState.UseDirectory(null); }

        // the report still reads the mod list while Mod Organizer has it open
        var rinst = Path.Combine(dir, "rinst"); var rprofile = Path.Combine(rinst, "profiles", "A StoryWealth"); Directory.CreateDirectory(rprofile);
        var ml = Path.Combine(rprofile, "modlist.txt");
        File.WriteAllLines(ml, ["# header", "+One", "+Two"]);
        using (var mo2 = new FileStream(ml, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var sum = ReportBuilder.Summary(new ReportContext("1.0", rinst, null, null, true, false, false));
            Check("the report reads a mod list that is open in MO2", sum.Contains("Mod list: 2 mods"), sum);
        }

        // collections: twin mods keep an old folder only when the md5 matches; a plugin the collection doesn't switch off is on
        var cj = Path.Combine(dir, "c.json");
        File.WriteAllText(cj, "{\"info\":{\"name\":\"A Storywealth - Twin Test\"},\"mods\":["
            + "{\"name\":\"Main File\",\"version\":\"1\",\"optional\":false,\"domainName\":\"fallout4\",\"phase\":0,\"source\":{\"type\":\"nexus\",\"modId\":7,\"fileId\":1,\"md5\":\"aaa\",\"fileSize\":1,\"logicalFilename\":\"Main File\"}},"
            + "{\"name\":\"Main File\",\"version\":\"1\",\"optional\":false,\"domainName\":\"fallout4\",\"phase\":0,\"source\":{\"type\":\"nexus\",\"modId\":7,\"fileId\":2,\"md5\":\"bbb\",\"fileSize\":1,\"logicalFilename\":\"Main File\"}}],"
            + "\"plugins\":[{\"name\":\"On.esp\"},{\"name\":\"Off.esp\",\"enabled\":false},{\"name\":\"Yes.esp\",\"enabled\":true}]}");
        var twin = VortexCollectionMapper.Map(cj, "tw");
        Check("a plugin without 'enabled' is on, 'enabled: false' is off",
            twin.Plugins.Single(p => p.Plugin == "On.esp").Enabled && !twin.Plugins.Single(p => p.Plugin == "Off.esp").Enabled && twin.Plugins.Single(p => p.Plugin == "Yes.esp").Enabled);
        var plainName = twin.Mods[0].LegacyInstallAs.FirstOrDefault(l => l.EndsWith(" - Main File"));
        Check("twin mods remember the plain name they had before the clash", plainName is not null && twin.Mods[1].LegacyInstallAs.Contains(plainName), J(twin.Mods[0].LegacyInstallAs));
        var tinst = Path.Combine(dir, "tinst"); Directory.CreateDirectory(Path.Combine(tinst, "mods", plainName!));
        var treg = new AddonRegistryFile { Addons = [new InstalledAddon { Id = "tw", Mods = [new InstalledAddonMod { Name = plainName!, Md5 = "bbb" }] }] };
        AddonPlacement.KeepExistingFolders(twin, treg, tinst);
        Check("only the twin with the matching md5 takes the old folder", twin.Mods[1].InstallAs == plainName && twin.Mods[0].InstallAs != plainName, twin.Mods[0].InstallAs + " | " + twin.Mods[1].InstallAs);

        // Creation Club masters are implicit, other names starting with "cc" are not
        var cc = new Dictionary<string, PluginOrder.Info>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mine.esp"] = new(false, false, ["ccBGSFO4001-PipBoy(Black).esl", "CCTV.esp"]),
            ["Other.esp"] = new(false, false, ["ccSBJFO4002_ManwellRifle.esl"]),
        };
        var ccBad = PluginOrder.MissingMasters(["Mine.esp", "Other.esp"], new Dictionary<string, bool> { ["Mine.esp"] = true, ["Other.esp"] = true }, n => cc.GetValueOrDefault(n)).Select(x => x.Plugin).ToList();
        Check("a missing 'CCTV.esp' master is flagged, Creation Club masters are not", ccBad.Contains("Mine.esp") && !ccBad.Contains("Other.esp"), J(ccBad));
    }
    finally { try { Directory.Delete(dir, true); } catch { } }

    // sign-in: 401 means signed out, 429 does not; the request line may arrive in pieces
    var cfgX = new NexusOAuthConfig { ClientId = "test_client", Port = 18089 };
    HttpResponseMessage Resp(int code) => new((System.Net.HttpStatusCode)code) { Content = new StringContent("{}") };
    string Stored(DateTime exp) => System.Text.Json.JsonSerializer.Serialize(new { access = "OLD", refresh = "R1", expires = exp });
    var s429 = new NexusSession(cfgX, new MemStore(Stored(DateTime.UtcNow.AddSeconds(-5))), new FakeHandler(_ => Resp(429)));
    var is429Transient = false; try { s429.GetAccessTokenAsync(default).Wait(); } catch (AggregateException ex) when (ex.InnerException is HttpRequestException) { is429Transient = true; }
    Check("'too many requests' on refresh is not a sign-out", is429Transient && s429.IsSignedIn);
    var s401 = new NexusSession(cfgX, new MemStore(Stored(DateTime.UtcNow.AddSeconds(-5))), new FakeHandler(_ => Resp(401)));
    var is401Out = false; try { s401.GetAccessTokenAsync(default).Wait(); } catch (AggregateException ex) when (ex.InnerException is NexusSignedOutException) { is401Out = true; }
    Check("401 on refresh is a sign-out", is401Out && !s401.IsSignedIn);

    int PortFree() { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    var pieceport = PortFree();
    var pieceStore = new MemStore(null);
    var pieceSession = new NexusSession(new NexusOAuthConfig { ClientId = "test_client", Port = pieceport }, pieceStore,
        new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"A\",\"refresh_token\":\"R\",\"expires_in\":3600}") }));
    pieceSession.SignInAsync(url =>
    {
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;
        Task.Run(async () =>
        {
            using var idle = new System.Net.Sockets.TcpClient(); await idle.ConnectAsync("127.0.0.1", pieceport);   // says nothing: must not block the real answer
            using var tcp = new System.Net.Sockets.TcpClient(); await tcp.ConnectAsync("127.0.0.1", pieceport);
            var ns = tcp.GetStream();
            var first = Encoding.ASCII.GetBytes("GET /callback?code=PIECES&sta"); await ns.WriteAsync(first); await ns.FlushAsync(); await Task.Delay(300);
            var rest = Encoding.ASCII.GetBytes("te=" + state + " HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"); await ns.WriteAsync(rest); await ns.FlushAsync();
            await Task.Delay(500);
        });
    }, default, TimeSpan.FromSeconds(30)).Wait();
    Check("sign-in works when the request arrives in pieces and another connection stays silent", pieceSession.IsSignedIn);
}

// ---- FOMOD install end to end (needs 7-Zip): finished in a .part folder and renamed; a destination can't climb out
{
    string? sevenZip = null; try { sevenZip = SevenZip.ExePath; } catch (FileNotFoundException) { }
    if (sevenZip is null) Console.WriteLine("SKIP  FOMOD end-to-end test (7-Zip not found)");
    else
    {
        var root = Path.Combine(Path.GetTempPath(), "asw-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(root, "src"); Directory.CreateDirectory(Path.Combine(src, "fomod")); Directory.CreateDirectory(Path.Combine(src, "Meshes")); Directory.CreateDirectory(Path.Combine(src, "Evil"));
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            File.WriteAllText(Path.Combine(src, "Meshes", "x.nif"), "n");
            File.WriteAllText(Path.Combine(src, "Evil", "pwn.txt"), "p");
            File.WriteAllText(Path.Combine(src, "fomod", "ModuleConfig.xml"),
                "<config><moduleName>T</moduleName><requiredInstallFiles>"
                + "<file source=\"a.txt\" destination=\"a.txt\"/>"
                + "<folder source=\"Meshes\" destination=\"Meshes\"/>"
                + "<folder source=\"Evil\" destination=\"x\\..\\..\\..\\escaped\\\"/>"
                + "</requiredInstallFiles></config>");
            var zip = Path.Combine(root, "mod.zip");
            ZipFile.CreateFromDirectory(src, zip);
            var inst = Path.Combine(root, "inst"); Directory.CreateDirectory(Path.Combine(inst, "mods", "T"));
            File.WriteAllText(Path.Combine(inst, "mods", "T", "stale.txt"), "old");   // an earlier, different copy
            Mo2InstanceWriter.MaterializeFomod(inst, "T", zip, "{\"options\":[]}");
            var mod = Path.Combine(inst, "mods", "T");
            Check("FOMOD: the chosen files are in the mod folder", File.Exists(Path.Combine(mod, "a.txt")) && File.Exists(Path.Combine(mod, "Meshes", "x.nif")));
            Check("FOMOD: the finished folder replaces the old one and no .part is left", !File.Exists(Path.Combine(mod, "stale.txt")) && !Directory.Exists(mod + ".part"));
            Check("FOMOD: a destination with '..' wrote nothing outside the mod folder", !Directory.Exists(Path.Combine(inst, "escaped")) && !Directory.Exists(Path.Combine(root, "escaped")) && !File.Exists(Path.Combine(mod, "pwn.txt")) && !Directory.GetFiles(root, "pwn.txt", SearchOption.AllDirectories).Any(f => !f.Contains(Path.Combine("src", "Evil"))));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

// ---- the update settings that ship in the exe
{
    var shipped = UpdateConfig.Load();
    bool KeyParses() { try { using var k = ECDsa.Create(); k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(shipped.PublicKey), out _); return k.KeySize == 256; } catch { return false; } }
    Check("the shipped update settings name a repository and a valid P-256 public key", shipped.Enabled && shipped.Repo == "npc18-projects/aSW-Installer" && KeyParses(), shipped.Repo);
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

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}

sealed class MemStore(string? initial) : INexusTokenStore
{
    public string? Json { get; private set; } = initial;
    public string? Load() => Json;
    public void Save(string json) => Json = json;
    public void Delete() => Json = null;
}
