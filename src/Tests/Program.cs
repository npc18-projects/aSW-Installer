using System.IO.Compression;
using System.Security.Cryptography;
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
                if (u.EndsWith(".zip")) return Ok(rq, new ByteArrayContent(zip));
                if (u.EndsWith(".sig")) return Ok(rq, new StringContent(Convert.ToBase64String(Sign(signer, zip))));
                return new(System.Net.HttpStatusCode.NotFound) { RequestMessage = rq };
            })), Path.Combine(root, "staging"), _ => exeVersion);

        var info = await Updater(Release("v0.5.0"), ecdsa).CheckAsync("0.4.0");
        Check("a newer signed release is found", info is not null && info.Version == "0.5.0" && info.ZipName == zipName && info.Notes == "notes", info?.Version);
        Check("the same or an older version is not offered", await Updater(Release("v0.4.0"), ecdsa).CheckAsync("0.4.0") is null && await Updater(Release("v0.3.9"), ecdsa).CheckAsync("0.4.0") is null);
        Check("a pre-release is ignored", await Updater(Release("v0.5.0", pre: true), ecdsa).CheckAsync("0.4.0") is null);
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
