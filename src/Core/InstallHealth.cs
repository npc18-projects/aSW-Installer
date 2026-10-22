using System.Diagnostics;

namespace AStoryWealth.Core;

public enum HealthLevel { Ok, Warn, Fail }

public sealed record HealthItem(string Label, HealthLevel Level, string Detail, string? Action = null);

public sealed record InstallSummary(int Mods, int EnabledMods, int Plugins, int EnabledPlugins, int AddOns, string Groups);

/// <summary>
/// What the Home tab reports about an install. A finished install has no disabled mods or plugins and no plugin missing a
/// master, so anything else is shown as a failure the person can screenshot.
/// </summary>
public static class InstallHealth
{
    public static string ProfileDir(string instanceDir)
    {
        var profiles = Path.Combine(instanceDir, "profiles");
        if (!Directory.Exists(profiles)) return Path.Combine(profiles, "A StoryWealth");
        var named = Path.Combine(profiles, "A StoryWealth");
        return Directory.Exists(named) ? named : Directory.GetDirectories(profiles).FirstOrDefault() ?? named;
    }

    /// <summary>modlist.txt entries, highest priority first, as written ("+Name" on, "-Name" off). Separators are left out.</summary>
    public static List<(string Name, bool Enabled)> ReadMods(string instanceDir)
    {
        var path = Path.Combine(ProfileDir(instanceDir), "modlist.txt");
        var list = new List<(string, bool)>();
        if (!File.Exists(path)) return list;
        foreach (var raw in File.ReadLines(path))
        {
            var l = raw.TrimEnd();
            if (l.Length < 2 || l[0] == '#' || l.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            if (l[0] is '+' or '-') list.Add((l[1..], l[0] == '+'));
        }
        return list;
    }

    /// <summary>plugins.txt entries in load order: (name, enabled).</summary>
    public static List<(string Name, bool Enabled)> ReadPlugins(string instanceDir)
    {
        var path = Path.Combine(ProfileDir(instanceDir), "plugins.txt");
        var list = new List<(string, bool)>();
        if (!File.Exists(path)) return list;
        foreach (var raw in File.ReadLines(path))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            list.Add(l[0] is '*' or '+' ? (l[1..].Trim(), true) : (l, false));
        }
        return list;
    }

    public static InstallSummary Summary(string instanceDir)
    {
        var mods = ReadMods(instanceDir);
        var plugins = ReadPlugins(instanceDir);
        var reg = AddonRegistry.Load(instanceDir);
        var groups = reg.BaseGroups is null ? "not recorded" : reg.BaseGroups.Count == 0 ? "none" : string.Join(", ", reg.BaseGroups);
        return new InstallSummary(mods.Count, mods.Count(m => m.Enabled), plugins.Count, plugins.Count(p => p.Enabled), reg.Addons.Count, groups);
    }

    /// <summary>Total size of a folder in bytes; slow for a big mods folder, so call it off the UI thread.</summary>
    public static long FolderSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            try { total += f.Length; } catch { }
        return total;
    }

    /// <summary>
    /// True when the list this install was made from differs from the latest one, false when they are the same, null when the
    /// install kept no copy (or the latest isn't available). The install keeps a copy at .asw\a-storywealth.collection.json.
    /// </summary>
    public static bool? BaseUpdateAvailable(string instanceDir, string latestJsonPath)
    {
        var installed = Path.Combine(instanceDir, ".asw", "a-storywealth.collection.json");
        if (!File.Exists(installed) || !File.Exists(latestJsonPath)) return null;
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var a = File.OpenRead(installed); using var b = File.OpenRead(latestJsonPath);
            return !sha.ComputeHash(a).AsSpan().SequenceEqual(sha.ComputeHash(b));
        }
        catch { return null; }
    }

    public static List<HealthItem> Check(string instanceDir, (int W, int H)? display = null)
    {
        var items = new List<HealthItem>();
        items.Add(Mo2Launcher.IsInstalled(instanceDir)
            ? new("Mod Organizer 2", HealthLevel.Ok, "Found.")
            : new("Mod Organizer 2", HealthLevel.Fail, "ModOrganizer.exe isn't in the install folder."));

        var mods = ReadMods(instanceDir);
        var offMods = mods.Where(m => !m.Enabled).Select(m => m.Name).ToList();
        items.Add(mods.Count == 0 ? new("Mods", HealthLevel.Fail, "No mod list found.")
            : offMods.Count == 0 ? new("Mods", HealthLevel.Ok, $"All {mods.Count:N0} are enabled.")
            : new("Mods", HealthLevel.Fail, $"{offMods.Count:N0} disabled: {Names(offMods)}"));

        var plugins = ReadPlugins(instanceDir);
        var offPlugins = plugins.Where(p => !p.Enabled).Select(p => p.Name).ToList();
        items.Add(plugins.Count == 0 ? new("Plugins", HealthLevel.Fail, "No plugin list found.")
            : offPlugins.Count == 0 ? new("Plugins", HealthLevel.Ok, $"All {plugins.Count:N0} are enabled.")
            : new("Plugins", HealthLevel.Fail, $"{offPlugins.Count:N0} disabled: {Names(offPlugins)}"));

        var gameDir = Path.Combine(instanceDir, "Stock Game");
        items.Add(MissingMastersItem(instanceDir, mods, plugins, Directory.Exists(gameDir) ? gameDir : null));
        items.Add(GameVersionItem(gameDir));
        items.Add(IniItem(instanceDir, display));

        var f4se = Mo2Launcher.FindF4seTitle(instanceDir);
        items.Add(f4se is null ? new("F4SE", HealthLevel.Fail, "Not in Mod Organizer 2's program list.")
            : new("F4SE", HealthLevel.Ok, $"Ready as \"{f4se}\"."));
        return items;
    }

    private static string Names(List<string> names) =>
        string.Join(", ", names.Take(4)) + (names.Count > 4 ? $" and {names.Count - 4} more" : "");

    private static HealthItem MissingMastersItem(string instanceDir, List<(string Name, bool Enabled)> mods,
        List<(string Name, bool Enabled)> plugins, string? gameDir)
    {
        try
        {
            // MO2's file is highest priority first; the lookup wants lowest first so the winning mod's plugin is found.
            var ascending = mods.AsEnumerable().Reverse().Select(m => (m.Name, m.Enabled ? '+' : '-')).ToList();
            var paths = Mo2InstanceWriter.LocatePlugins(instanceDir, ascending, gameDir);
            var enabled = plugins.ToDictionary(p => p.Name, p => p.Enabled, StringComparer.OrdinalIgnoreCase);
            var order = plugins.Select(p => p.Name).ToList();
            var bad = PluginOrder.MissingMasters(order, enabled,
                n => paths.TryGetValue(n, out var p) ? PluginOrder.ReadHeader(p) : null);
            return bad.Count == 0 ? new("Missing masters", HealthLevel.Ok, "None.")
                : new("Missing masters", HealthLevel.Fail, $"{bad.Count:N0} plugin(s): " + string.Join(", ", bad.Take(3).Select(b => $"{b.Plugin} needs {b.Master}")) + (bad.Count > 3 ? $" and {bad.Count - 3} more" : ""));
        }
        catch (Exception ex) { return new("Missing masters", HealthLevel.Warn, "Couldn't check: " + ex.Message); }
    }

    /// <summary>
    /// The profile's INIs against the recommended ones shipped in the exe: which settings were changed, plus whether the game's
    /// screen size is the main display's. Reading the files each time is what makes it track changes made later.
    /// </summary>
    private static HealthItem IniItem(string instanceDir, (int W, int H)? display)
    {
        var profile = ProfileDir(instanceDir);
        var diffs = new List<IniDifference>();
        var missing = new List<string>();
        var compared = 0;
        foreach (var (profileName, shippedName) in new[] { ("fallout4.ini", "Fallout4.ini"), ("fallout4prefs.ini", "Fallout4Prefs.ini"), ("fallout4custom.ini", "Fallout4Custom.ini") })
        {
            if (Mo2InstanceWriter.ShippedIni(shippedName) is not { } recommended) continue;
            compared++;
            var path = Path.Combine(profile, profileName);
            if (!File.Exists(path)) { missing.Add(shippedName); continue; }
            diffs.AddRange(IniCompare.Diff(shippedName, recommended, File.ReadAllText(path)));
        }
        var set = GameDisplay.ReadResolution(GameDisplay.ProfilePrefs(instanceDir));
        var resolutionOff = display is { } d && set is { } r && r != d;
        string res = resolutionOff ? $" The game is set to {set!.Value.W}x{set.Value.H}; your main display is {display!.Value.W}x{display.Value.H}." : "";

        if (missing.Count > 0)
            return new("INI settings", HealthLevel.Fail, $"{string.Join(", ", missing)} missing from the profile.", "use-recommended");
        if (diffs.Count > 0)
        {
            string One(IniDifference x) => $"{x.Key} ({x.Current ?? "missing"}, recommended {x.Recommended})";
            var shown = string.Join("; ", diffs.Take(4).Select(One)) + (diffs.Count > 4 ? $"; and {diffs.Count - 4} more" : "");
            return new("INI settings", HealthLevel.Warn, $"{diffs.Count} setting{(diffs.Count == 1 ? "" : "s")} differ from the recommended: {shown}.{res}", "use-recommended");
        }
        if (resolutionOff) return new("INI settings", HealthLevel.Warn, $"Matches the recommended settings.{res}", "match-resolution");
        return new("INI settings", HealthLevel.Ok, compared == 0 ? $"This build ships no recommended INIs.{res}" : $"Matches the recommended settings.{res}");
    }

    private static HealthItem GameVersionItem(string gameDir)
    {
        var exe = Path.Combine(gameDir, "Fallout4.exe");
        if (!File.Exists(exe)) return new("Game version", HealthLevel.Warn, "No Stock Game copy in the install folder.");
        var v = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "";
        var parts = v.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var above = parts.Length >= 3 && (parts[0] > 1 || parts[1] > 10 || parts[2] > 163);
        return above ? new("Game version", HealthLevel.Fail, $"{v}: this is the next-gen version. The collection needs 1.10.163.")
            : new("Game version", HealthLevel.Ok, $"{v} (the version the collection needs).");
    }
}
