using System.Xml.Linq;

namespace AStoryWealth.Core;

/// <summary>
/// Applies a Vortex collection "choices" block (type "fomod") to a mod archive:
/// parses fomod/ModuleConfig.xml, resolves the selected options to concrete
/// (source, destination) file mappings, including conditionFlags-driven
/// conditionalFileInstall patterns. A mod folder materialized this way contains
/// only the selected option files — never the raw variant trees.
/// </summary>
public static class FomodResolver
{
    public sealed record FileMapping(string Source, string Destination);

    public sealed record InstallPlan(List<FileMapping> Files, List<string> Issues);

    /// <summary>
    /// ModuleConfig files are often UTF-16 (byte-order mark) while their declaration says UTF-8; the XML reader refuses that
    /// mix, so decode by the byte-order mark ourselves and parse the text.
    /// </summary>
    public static XDocument LoadXml(string path)
    {
        using var sr = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        // The file comes from a mod archive: skip any DTD (no entity tricks) and never fetch anything.
        using var xr = System.Xml.XmlReader.Create(new StringReader(sr.ReadToEnd()),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(xr);
    }

    /// <summary>Evaluates the ModuleConfig against the collection's choices.
    /// choicesJson is the raw "choices" object from the collection mod entry.</summary>
    public static InstallPlan Resolve(string archivePath, string choicesJson,
        ISet<string>? installedFiles = null, string? gameVersion = null)
    {
        var issues = new List<string>();
        var (selected, legacy) = ParseChoices(choicesJson, issues);

        // Temp root (asw-fomod/<guid>). Created up front so the finally block can
        // always delete the whole root, including when the config is not found.
        var tmp = Path.Combine(Path.GetTempPath(), "asw-fomod", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var cfgPath = ExtractModuleConfig(archivePath, tmp);
            if (cfgPath == null)
            {
                issues.Add("no fomod/ModuleConfig.xml in archive");
                return new InstallPlan([], issues);
            }

            var root = LoadXml(cfgPath).Root
                ?? throw new InvalidDataException("empty ModuleConfig.xml");
            var ns = root.Name.Namespace;
            var files = new List<FileMapping>();
            var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // root-level <requiredInstallFiles>: always installed regardless of
            // choices (Enable-style FOMODs put the whole base mod there and the
            // selected "Enable/Default" plugin itself has no <files>).
            var req = root.Element(ns + "requiredInstallFiles");
            if (req != null)
                foreach (var f in req.Elements().Where(e => e.Name.LocalName is "file" or "folder"))
                {
                    var src = (string?)f.Attribute("source") ?? "";
                    files.Add(new FileMapping(src, NormalizeDestination(src, (string?)f.Attribute("destination"), f.Name.LocalName == "folder")));
                }

            // walk installSteps: required plugins + their files + flags.
            // Selections are keyed by (step name, group name); a step-agnostic
            // group-name lookup is only used as a fallback when the choices blob
            // carried no step info at all — never to merge same-named groups
            // across distinct steps.
            var steps = root.Descendants(ns + "installStep");
            foreach (var step in steps)
            {
                var stepName = (string?)step.Attribute("name") ?? "";
                foreach (var group in step.Descendants(ns + "group"))
                {
                    var groupName = (string?)group.Attribute("name") ?? "";
                    bool found;
                    HashSet<string>? chosen = null;
                    if (selected.TryGetValue(stepName, out var byGroup))
                        found = byGroup.TryGetValue(groupName, out chosen);
                    else if (legacy != null && legacy.TryGetValue(groupName, out chosen))
                        found = true;
                    else
                        found = false;
                    if (!found) continue;

                    // seenPlugins is scoped PER GROUP so plugins named
                    // 'Yes'/'Default'/'None' in different groups never collide.
                    var seenPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var plugin in group.Descendants(ns + "plugin"))
                    {
                        var pname = (string?)plugin.Attribute("name") ?? "";
                        if (!chosen!.Contains(pname, StringComparer.OrdinalIgnoreCase)) continue;
                        if (!seenPlugins.Add(pname)) continue;
                        CollectFiles(plugin, ns, files);
                        foreach (var flag in plugin.Descendants(ns + "flag"))
                        {
                            var fname = (string?)flag.Attribute("name");
                            if (fname != null)
                                flags[fname] = (string?)flag.Value ?? "true";
                        }
                    }
                }
            }

            // The installed-files universe used for fileDependency gating. When the
            // caller provides one it is authoritative (cross-mod), but we always
            // union this mod's own resolved files so intra-mod deps still resolve.
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (installedFiles != null)
                foreach (var f in installedFiles) installed.Add(NormDest(f));
            foreach (var f in files) installed.Add(NormDest(f.Destination));

            // conditional file install: patterns gated on flags / installed files /
            // game version. Containers seen in the wild: <conditionalFileInstall>
            // and <conditionalFileInstalls><patterns>… (Darker Nights and friends).
            var condContainers = root.Elements().Where(e =>
                e.Name.LocalName is "conditionalFileInstall" or "conditionalFileInstalls").ToList();
            if (condContainers.Count > 0)
            {
                foreach (var pattern in condContainers.SelectMany(c => c.Descendants(ns + "pattern")))
                {
                    var deps = pattern.Element(ns + "dependencies");
                    if (deps != null && !EvaluateDependencies(deps, ns, flags, installed, gameVersion, issues)) continue;
                    foreach (var f in pattern.Descendants().Where(e => e.Name.LocalName is "file" or "folder"))
                    {
                        var src = (string?)f.Attribute("source") ?? "";
                        files.Add(new FileMapping(src, NormalizeDestination(src, (string?)f.Attribute("destination"), f.Name.LocalName == "folder")));
                    }
                }
            }

            if (files.Count == 0)
                issues.Add("selected options produced no file mappings");

            return new InstallPlan(files, issues);
        }
        finally
        {
            // Delete the entire asw-fomod/<guid> root, not just the config's parent.
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ---- choice parsing ---------------------------------------------------

    /// <summary>step name → (group name → selected plugin names). Also returns a
    /// step-agnostic group-name → names map for legacy flat choice blobs.</summary>
    private static (Dictionary<string, Dictionary<string, HashSet<string>>> ByStep,
        Dictionary<string, HashSet<string>>? Legacy)
        ParseChoices(string choicesJson, List<string> issues)
    {
        var byStep = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var legacy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(choicesJson).RootElement;
            if (!json.TryGetProperty("options", out var opts)) return (byStep, null);
            foreach (var opt in opts.EnumerateArray())
            {
                var stepName = opt.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                if (!opt.TryGetProperty("groups", out var groups)) continue;
                var stepMap = byStep.TryGetValue(stepName, out var sm)
                    ? sm : new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in groups.EnumerateArray())
                {
                    var gname = g.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : "";
                    var set = stepMap.TryGetValue(gname, out var s)
                        ? s : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var lset = legacy.TryGetValue(gname, out var ls)
                        ? ls : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (g.TryGetProperty("choices", out var choices))
                        foreach (var c in choices.EnumerateArray())
                        {
                            var cn = c.TryGetProperty("name", out var cnn) ? cnn.GetString() ?? "" : "";
                            set.Add(cn);
                            lset.Add(cn);
                        }
                    stepMap[gname] = set;
                    legacy[gname] = lset;
                }
                byStep[stepName] = stepMap;
            }
        }
        catch (Exception ex) { issues.Add($"choices parse failed: {ex.Message}"); }
        return (byStep, legacy.Count > 0 ? legacy : null);
    }

    private static void CollectFiles(XElement plugin, XNamespace ns, List<FileMapping> files)
    {
        var filesEl = plugin.Element(ns + "files");
        if (filesEl == null) return;
        foreach (var f in filesEl.Elements().Where(e => e.Name.LocalName is "file" or "folder"))
        {
            var src = (string?)f.Attribute("source") ?? "";
            files.Add(new FileMapping(src, NormalizeDestination(src, (string?)f.Attribute("destination"), f.Name.LocalName == "folder")));
        }
    }

    // ---- dependency evaluation -------------------------------------------

    /// <summary>Evaluates a <dependencies> operator (And/Or), recursing into
    /// nested operators. A dependency of an unsupported/unknown type fails
    /// closed (pattern not installed) so ignored dep types can never cause
    /// over-installation.</summary>
    private static bool EvaluateDependencies(XElement deps, XNamespace ns,
        Dictionary<string, string> flags, ISet<string> installed, string? gameVersion, List<string> issues)
    {
        var isOr = ((string?)deps.Attribute("operator") ?? "And")
            .Equals("Or", StringComparison.OrdinalIgnoreCase);
        var result = !isOr; // identity for And=true, Or=false
        var anyReal = false;
        foreach (var child in deps.Elements())
        {
            var local = child.Name.LocalName;
            // nested operator → recurse
            if (local == "dependencies")
            {
                anyReal = true;
                var sub = EvaluateDependencies(child, ns, flags, installed, gameVersion, issues);
                result = isOr ? result || sub : result && sub;
                continue;
            }
            // resolve the dependency type from the element name (real FOMOD) or
            // from a type attribute (the <dependency type="…"> form).
            string type = local switch
            {
                "fileDependency" => "file",
                "flagDependency" => "flag",
                "gameDependency" => "game",
                "dependency" => (string?)child.Attribute("type") ?? "flag",
                _ => "unknown"
            };
            anyReal = true;
            var ok = EvaluateOne(type, child, flags, installed, gameVersion, issues);
            result = isOr ? result || ok : result && ok;
        }
        // <dependencies> with no real children: And ⇒ true, Or ⇒ false.
        return anyReal ? result : !isOr;
    }

    private static bool EvaluateOne(string type, XElement dep,
        Dictionary<string, string> flags, ISet<string> installed, string? gameVersion, List<string> issues)
    {
        switch (type)
        {
            case "file":
            {
                var f = (string?)dep.Attribute("file") ?? "";
                var wantActive = ((string?)dep.Attribute("state") ?? "Active")
                    .Equals("Active", StringComparison.OrdinalIgnoreCase);
                return installed.Contains(NormDest(f)) == wantActive;
            }
            case "flag":
            case "flagDependency":
            {
                var flagName = (string?)dep.Attribute("flag") ?? (string?)dep.Attribute("name") ?? "";
                // value attr absent = any value; value="" = flag must be UNSET
                if (dep.Attribute("value") == null)
                    return flags.ContainsKey(flagName);
                var v = (string?)dep.Attribute("value") ?? "";
                if (v.Length == 0)
                    return !flags.TryGetValue(flagName, out var unset) || unset.Length == 0;
                return flags.TryGetValue(flagName, out var val) &&
                       val.Equals(v, StringComparison.OrdinalIgnoreCase);
            }
            case "game":
            case "gameDependency":
            {
                // version-ranged deps: gate only when a game version is supplied and
                // both sides parse; otherwise ignore gracefully (do not fail closed).
                if (gameVersion == null) return true;
                var req = (string?)dep.Attribute("version") ?? (string?)dep.Attribute("gameVersion") ?? "";
                if (req.Length == 0) return true;
                return GameVersionSatisfied(req, gameVersion);
            }
            default:
                issues.Add($"unknown dependency type '{type}'");
                return false; // fail closed
        }
    }

    private static bool GameVersionSatisfied(string required, string actual)
    {
        if (!Version.TryParse(NormalizeVersion(required), out var req)) return true;
        if (!Version.TryParse(NormalizeVersion(actual), out var act)) return true;
        return act >= req;
    }

    private static string NormalizeVersion(string v)
    {
        // strip a trailing build qualifier if present; Version.Parse wants dotted ints
        var i = v.IndexOf('-');
        return i >= 0 ? v[..i] : v;
    }

    private static string NormDest(string p)
    {
        p = p.Replace('\\', '/').TrimStart('/');
        return p.ToLowerInvariant();
    }

    // ---- destination / path helpers ---------------------------------------

    /// <summary>Normalizes a <file>/<folder> destination relative to its source.
    /// - empty destination or "/" ⇒ the source file's own name
    /// - trailing slash/backslash ⇒ treated as a directory (source name appended)
    /// - leading "./", ".", "/", "\" trimmed without eating dot-file names
    /// - result is a relative path using backslash separators.</summary>
    private static string NormalizeDestination(string source, string? destination, bool isFolder = false)
    {
        var dest = (destination ?? "").Trim();
        // FOMOD semantics: an empty destination on a FILE = root, same name;
        // on a FOLDER = the folder's CONTENTS go to the root. Returning the
        // source name for folders would re-create "01 Curvy" etc. in the mod.
        if (dest.Length == 0 || dest is "/" or "\\")
            return isFolder ? "" : SourceName(source);

        // trim a leading "./" or ".\\"
        if (dest.Length >= 2 && dest[0] == '.' && (dest[1] == '/' || dest[1] == '\\'))
            dest = dest[2..];
        // a bare "." means the current directory ⇒ empty
        if (dest == ".")
            dest = "";
        // strip leading separators (must not eat a dot-file name like ".esm")
        dest = dest.TrimStart('/', '\\');

        // trailing separator ⇒ directory: append the source's own file name
        if (dest.Length > 0 && (dest[^1] == '/' || dest[^1] == '\\'))
            dest = dest + SourceName(source);

        dest = dest.Replace('/', '\\').TrimStart('\\');
        return dest.Length == 0 ? SourceName(source) : dest;
    }

    private static string SourceName(string source)
        => Path.GetFileName(source ?? "");

    private static string? ExtractModuleConfig(string archivePath, string tmp)
    {
        var sevenZip = SevenZip.ExePath;
        // ModuleConfig.xml is not always at fomod/ — it can sit under a wrapper
        // folder (NanoSuit 6.2\FOMod\, BetterSettlers2.0\fomod\, data\fomod\…).
        // Extract every case-insensitive match, keep paths, take the shallowest.
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = sevenZip,
            Arguments = $"x \"{archivePath}\" -o\"{tmp}\" -ir!*ModuleConfig.xml -y -bso0 -bsp0",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("cannot start 7z");
        proc.WaitForExit(120_000);
        proc.WaitForExit(); // timeout overload doesn't finalize exited state — ExitCode would throw
        if (proc.ExitCode != 0) return null;
        var matches = Directory.EnumerateFiles(tmp, "ModuleConfig.xml", SearchOption.AllDirectories).ToList();
        return matches.OrderBy(p => p.Count(c => c is '/' or '\\')).FirstOrDefault();
    }
}
