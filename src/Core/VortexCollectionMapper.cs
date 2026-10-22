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
            entry.InstallAs = Mo2InstanceWriter.SafeName($"A Storywealth - {modName}", "installAs");
            entry.DisplayName = m.TryGetProperty("name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString()! : modName;
            if (m.TryGetProperty("instructions", out var ins) && ins.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ins.GetString()))
                entry.Instructions = ins.GetString()!.Trim();
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

            manifest.Mods.Add(entry);
        }

        // plugins[] -> enabled state (names only; addon plugins must match mods above)
        if (root.TryGetProperty("plugins", out var plugins))
            foreach (var p in plugins.EnumerateArray())
                manifest.Plugins.Add(new PluginEntry
                {
                    Plugin = p.GetProperty("name").GetString() ?? "",
                    Enabled = p.TryGetProperty("enabled", out var en) && en.GetBoolean()
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

    private static string Pascalize(string s)
    {
        var parts = s.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
