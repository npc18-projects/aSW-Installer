using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Maps a Vortex-format collection.json (as found in Vortex staging folders,
/// or downloadable from Nexus) into an aSW AddonManifest.
/// Vortex mod entries: { name, version, optional, phase, domainName,
///   source:{ type:"nexus", modId, fileId, md5, fileSize, logicalFilename,
///            updatePolicy, tag }, author, details }
/// </summary>
public static class VortexCollectionMapper
{
    public static AddonManifest Map(string collectionJsonPath, string addonId, int installPriority = 0)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(collectionJsonPath));
        var root = doc.RootElement;
        var info = root.GetProperty("info");

        var manifest = new AddonManifest
        {
            AddonId = addonId,
            Name = info.TryGetProperty("name", out var n) ? n.GetString() ?? addonId : addonId,
            Author = info.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "",
            Description = info.TryGetProperty("description", out var d) ? d.GetString() : null,
            // A collection.json IS a base list, not an addon pinning another —
            // the mapper emits requires pointing at itself, which the resolver
            // skips when baseCollectionId == addonId.
            Requires = new Requires
            {
                BaseCollectionId = addonId,
                BaseRevision = JsonSerializer.SerializeToElement(0)
            },
            InstallPriority = installPriority
        };

        var prefix = FolderPrefix(manifest.Name);
        var forcedOptional = AddonOptions.Load().Where(e => e.MakeOptional && e.Addon.Equals(addonId, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var m in root.GetProperty("mods").EnumerateArray())
        {
            var src = m.GetProperty("source");
            if (src.GetProperty("type").GetString() != "nexus")
                throw new NotSupportedException(
                    $"Non-nexus source '{src.GetProperty("type").GetString()}' in {collectionJsonPath} — spec v0.2 is Nexus-only");

            var detType = m.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Object
                && det.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;
            var entry = new ModEntry
            {
                Type = detType,
                IsOptional = m.TryGetProperty("optional", out var opt) && opt.ValueKind == JsonValueKind.True,
                Nexus = new NexusFileRef
                {
                    ModId = src.GetProperty("modId").GetInt32(),
                    FileId = src.GetProperty("fileId").GetInt64(),
                    Game = m.TryGetProperty("domainName", out var dom) ? dom.GetString() ?? "fallout4" : "fallout4",
                    Md5 = src.TryGetProperty("md5", out var md5) ? md5.GetString() : null,
                    FileSize = src.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : null
                }
            };

            // installAs: prefix rule — "aSW <Addon> - <mod name>" (folder names; the UI
            // strips the prefix for display)
            var modName = SanitizeFolder(m.TryGetProperty("logicalFilename", out var lf) && lf.ValueKind == JsonValueKind.String
                ? lf.GetString()! : m.GetProperty("name").GetString()!);
            // The folder on disk goes through SafeName (it turns ".." into "_"). The name used everywhere else has to be that
            // same name, or modlist.txt lists a mod MO2 can't find and the real folder shows up disabled at the top.
            entry.InstallAs = Mo2InstanceWriter.SafeName($"{prefix} - {modName}", "installAs");
            entry.DisplayName = m.TryGetProperty("name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString()! : modName;
            if (m.TryGetProperty("instructions", out var ins) && ins.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ins.GetString()))
                entry.Instructions = ins.GetString()!.Trim();
            if (prefix != BasePrefix)
            {
                var v043 = PrefixV043(manifest.Name);
                if (v043 != BasePrefix) entry.LegacyInstallAs.Add(Mo2InstanceWriter.SafeName($"{v043} - {modName}", "installAs"));
                entry.LegacyInstallAs.Add(Mo2InstanceWriter.SafeName($"{BasePrefix} - {modName}", "installAs"));
            }
            entry.LegacyInstallAs.Add($"aSW AStorywealth - {modName}");
            entry.LegacyInstallAs.Add($"aSW {Pascalize(addonId)} - {modName}");
            if (m.TryGetProperty("details", out var det2) && det2.ValueKind == JsonValueKind.Object
                && det2.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String)
                entry.Category = cat.GetString();
            if (m.TryGetProperty("phase", out var ph) && ph.ValueKind == JsonValueKind.Number)
                entry.Phase = ph.GetInt32();
            entry.LogicalFileName =
                src.TryGetProperty("logicalFilename", out var slf) && slf.ValueKind == JsonValueKind.String ? slf.GetString()
                : m.TryGetProperty("logicalFilename", out var mlf) && mlf.ValueKind == JsonValueKind.String ? mlf.GetString()
                : null;
            if (m.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Object)
                entry.ChoicesJson = ch.GetRawText();
            if (m.TryGetProperty("hashes", out var hs) && hs.ValueKind == JsonValueKind.Array)
                entry.HashesJson = hs.GetRawText();
            if (m.TryGetProperty("patches", out var pt) && pt.ValueKind == JsonValueKind.Object)
                entry.PatchesJson = pt.GetRawText();

            if (!entry.IsOptional && forcedOptional.Any(e => e.Md5.Length > 0 && e.Md5.Equals(entry.Nexus.Md5, StringComparison.OrdinalIgnoreCase)))
                entry.IsOptional = true;
            manifest.Mods.Add(entry);
        }

        // Two mods can share a file name ("Main File"); each would land in the same folder. Tell them apart by Nexus mod id,
        // and by file id when even that matches. Every mod in a clashing group is renamed, so the result doesn't depend on order.
        // The plain name stays a legacy name: a mod already installed under it (before a revision added its twin) keeps its folder.
        foreach (var group in manifest.Mods.GroupBy(e => e.InstallAs, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
            foreach (var e in group)
            {
                e.LegacyInstallAs.Insert(0, e.InstallAs);
                e.InstallAs = Mo2InstanceWriter.SafeName($"{e.InstallAs} ({e.Nexus.ModId})", "installAs");
            }
        foreach (var group in manifest.Mods.GroupBy(e => e.InstallAs, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
            foreach (var e in group) e.InstallAs = Mo2InstanceWriter.SafeName($"{e.InstallAs} ({e.Nexus.FileId})", "installAs");

        // plugins[] -> enabled state (names only; addon plugins must match mods above)
        if (root.TryGetProperty("plugins", out var plugins))
            foreach (var p in plugins.EnumerateArray())
                manifest.Plugins.Add(new PluginEntry
                {
                    Plugin = p.GetProperty("name").GetString() ?? "",
                    // a plugin the collection doesn't mark as off is on: nothing should end up disabled by accident
                    Enabled = !p.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False
                });

        // tools[] -> custom executables registered in MO2 (WriteCustomExecutables)
        if (root.TryGetProperty("tools", out var tools))
            foreach (var t in tools.EnumerateArray())
                manifest.Tools.Add(new ToolEntry
                {
                    Name = t.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "",
                    Exe = t.TryGetProperty("exe", out var te) ? te.GetString() : null,
                    Args = t.TryGetProperty("args", out var ta) && ta.ValueKind == JsonValueKind.Array
                        ? ta.EnumerateArray().Select(a => a.GetString() ?? "").ToList() : null,
                    Cwd = t.TryGetProperty("cwd", out var tc) ? tc.GetString() : null,
                });

        // modRules -> ordering relations for modlist.txt priority (WriteLoadFiles)
        if (root.TryGetProperty("modRules", out var mr) && mr.ValueKind == JsonValueKind.Array)
            manifest.ModRules = JsonSerializer.Deserialize<List<ModRule>>(mr.GetRawText());

        if (root.TryGetProperty("pluginRules", out var prules) && prules.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            manifest.PluginRulesJson = prules.GetRawText();

        // Phase is carried per mod (ModEntry.Phase) and drives deploy order; the
        // optional flag is separate and handled by the install runner.

        return manifest;
    }

    /// <summary>Replaces characters Windows forbids in folder names (colons in
    /// titles like "Sim Settlements 2: Chapter 2", trailing dots, etc.).</summary>
    private static string SanitizeFolder(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.TrimEnd())
        {
            if (Array.IndexOf(invalid, ch) < 0) { sb.Append(ch); continue; }
            if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            sb.Append("- ");
        }
        var s = sb.ToString().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(s) ? "mod" : s;
    }

    public const string BasePrefix = "A Storywealth";

    private static readonly System.Text.RegularExpressions.Regex BrandRx =
        new(@"^\s*(A\s*Story\s*Wealth|aSW)\s*[-:–—]*\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Folder-name prefix for a collection's mods: the base list uses "A Storywealth"; an add-on uses its own title
    /// without the "A Storywealth" / "aSW" brand ("Atmosphere"), so a mod's folder says which collection it came from.
    /// </summary>
    public static string FolderPrefix(string collectionName)
    {
        var n = SanitizeFolder(collectionName ?? "").Trim();
        var rest = BrandRx.Replace(n, "").Trim();
        return rest.Length == 0 ? BasePrefix : rest;
    }

    /// <summary>The prefix 0.4.3 used for add-ons ("A Storywealth - Atmosphere"), kept so those folders are still found.</summary>
    private static string PrefixV043(string collectionName)
    {
        var n = SanitizeFolder(collectionName ?? "").Trim();
        if (n.Length == 0 || n.Equals(BasePrefix, StringComparison.OrdinalIgnoreCase)) return BasePrefix;
        return n.StartsWith(BasePrefix + " - ", StringComparison.OrdinalIgnoreCase) ? BasePrefix + n[BasePrefix.Length..] : $"{BasePrefix} - {n}";
    }

    private static string Pascalize(string s)
    {
        var parts = s.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
