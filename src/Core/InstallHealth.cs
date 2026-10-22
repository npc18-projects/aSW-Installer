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
        items.Add(ResolutionItem(instanceDir, display));

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

    private static HealthItem ResolutionItem(string instanceDir, (int W, int H)? display)
    {
        var set = GameDisplay.ReadResolution(GameDisplay.ProfilePrefs(instanceDir));
        if (set is null) return new("Game resolution", HealthLevel.Warn, "Not set in the profile's Fallout4Prefs.ini.");
        if (display is not { } d) return new("Game resolution", HealthLevel.Ok, $"{set.Value.W}x{set.Value.H}.");
        return set.Value == d ? new("Game resolution", HealthLevel.Ok, $"{d.W}x{d.H}, the size of your main display.")
            : new("Game resolution", HealthLevel.Warn, $"{set.Value.W}x{set.Value.H}. Your main display is {d.W}x{d.H}.", "match-resolution");
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
