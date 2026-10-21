using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Root Builder and Tools-folder support.
///
/// Root Builder (Kezyma) mirrors <c>mods/&lt;mod&gt;/Root/</c> into the game folder at
/// launch. It deliberately IGNORES any mod whose Root folder contains a "Data"
/// folder, so a root mod has to be split in two:
///   mods/&lt;mod&gt;/Root/...   game-folder files (f4se_loader.exe, dll hooks, .bat)
///   mods/&lt;mod&gt;/...        the archive's Data content, as an ordinary MO2 mod
///
/// Tools (FO4Edit, Bethini, BodySlide, ...) are not game content, so they live in
/// <c>&lt;instance&gt;/Tools/</c> instead of the mod list and MO2 executables point there.
/// </summary>
public static partial class Mo2InstanceWriter
{
    public const string ToolsDirName = "Tools";
    private const string ToolsIndexFile = ".asw-tools.json";

    /// <summary>
    /// Extracts a root-layout archive (F4SE, script-extender hooks, BAT files) into
    /// mods/&lt;installAs&gt;/ split for Root Builder. Returns (elapsedSec, bytesWritten).
    /// </summary>
    public static (double sec, long bytes) MaterializeRootMod(string instanceDir, string installAs, string archivePath)
    {
        installAs = SafeName(installAs, "installAs");
        var dest = Path.Combine(instanceDir, "mods", installAs);
        var staging = dest + ".part";
        var raw = dest + ".raw";
        foreach (var d in new[] { staging, raw })
            try { if (Directory.Exists(d)) InstallRunner.DeleteTree(d); } catch { }
        Directory.CreateDirectory(raw);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = SevenZip.ExePath,
            Arguments = $"x \"{archivePath}\" -o\"{raw}\" -y -bso0 -bsp0",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        try
        {
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                var so = proc.StandardOutput.ReadToEnd();
                var se = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    InstallLog.Error("mo2", $"7z exit {proc.ExitCode} extracting {Path.GetFileName(archivePath)}: {se} {so}");
                    throw new InvalidOperationException($"archive extraction failed: {archivePath}");
                }
            }

            // Descend through single-folder wrappers ("f4se_0_06_23\"), but never into Data.
            var root = raw;
            while (true)
            {
                var entries = Directory.GetFileSystemEntries(root);
                if (entries.Length == 1 && Directory.Exists(entries[0])
                    && !Path.GetFileName(entries[0]).Equals("Data", StringComparison.OrdinalIgnoreCase))
                {
                    InstallLog.Info("mo2", $"{installAs}: stripped wrapper folder '{Path.GetFileName(entries[0])}'");
                    root = entries[0];
                    continue;
                }
                break;
            }

            Directory.CreateDirectory(staging);
            var rootTarget = Path.Combine(staging, "Root");
            foreach (var e in Directory.GetFileSystemEntries(root))
            {
                var name = Path.GetFileName(e);
                if (name.Equals("src", StringComparison.OrdinalIgnoreCase)) continue;   // F4SE sources
                if (name.Equals("Data", StringComparison.OrdinalIgnoreCase) && Directory.Exists(e))
                {
                    foreach (var c in Directory.GetFileSystemEntries(e))
                        MoveInto(c, staging);
                }
                else MoveInto(e, rootTarget);
            }

            try { if (Directory.Exists(dest)) InstallRunner.DeleteTree(dest); } catch { }
            Directory.Move(staging, dest);
        }
        finally
        {
            foreach (var d in new[] { staging, raw })
                try { if (Directory.Exists(d)) InstallRunner.DeleteTree(d); } catch { }
        }
        sw.Stop();
        var bytes = CountBytes(dest);
        var sec = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var hasData = Directory.EnumerateFileSystemEntries(dest).Any(e => !Path.GetFileName(e).Equals("Root", StringComparison.OrdinalIgnoreCase));
        InstallLog.Info("mo2", $"materialized root mod '{installAs}' → Root/{(hasData ? " + Data content" : "")} ({bytes / 1_000_000.0:F1} MB in {sec:F1}s)");
        return (sec, bytes);
    }

    private static void MoveInto(string entry, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, Path.GetFileName(entry));
        if (Directory.Exists(entry))
        {
            if (!Directory.Exists(target)) { Directory.Move(entry, target); return; }
            CopyDirectory(entry, target);
        }
        else File.Move(entry, target, overwrite: true);
    }

    // ---- Tools folder -------------------------------------------------------------

    public static string ToolsDir(string instanceDir) => Path.Combine(instanceDir, ToolsDirName);

    /// <summary>installAs names already relocated into the Tools folder.</summary>
    public static HashSet<string> InstalledTools(string instanceDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var idx = Path.Combine(ToolsDir(instanceDir), ToolsIndexFile);
            if (File.Exists(idx))
                foreach (var n in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(idx)) ?? []) set.Add(n);
        }
        catch { }
        return set;
    }

    private static readonly object ToolIndexLock = new();

    private static bool HasExecutable(string dir) =>
        Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
            .Any(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".bat" or ".cmd");

    /// <summary>
    /// A "tool mod" is one whose content is entirely under a top-level Tools folder
    /// (the collection stages FO4Edit, Bethini, ... as Tools\&lt;name&gt;\). Its content moves
    /// to &lt;instance&gt;/Tools/ and the mod folder is removed, so it never enters the mod list.
    /// Returns true when the mod was relocated.
    /// </summary>
    public static bool RelocateToolMod(string instanceDir, string installAs)
    {
        var modDir = Path.Combine(instanceDir, "mods", SafeName(installAs, "installAs"));
        if (!Directory.Exists(modDir)) return false;
        var entries = Directory.GetFileSystemEntries(modDir)
            .Where(e => !Path.GetFileName(e).Equals("meta.ini", StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count != 1 || !Directory.Exists(entries[0])
            || !Path.GetFileName(entries[0]).Equals(ToolsDirName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Only standalone tools move: every tool folder must carry its own executable, and BodySlide
        // stays a normal mod — it and its preset mods (Tools\BodySlide\SliderSets...) must merge
        // through the virtual file system, which a physical Tools folder would break.
        var children = Directory.GetFileSystemEntries(entries[0]);
        if (children.Length == 0 || !children.All(c => Directory.Exists(c) && HasExecutable(c)
                && !Path.GetFileName(c).Contains("BodySlide", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(c).Contains("OutfitStudio", StringComparison.OrdinalIgnoreCase)))
            return false;

        var toolsDir = ToolsDir(instanceDir);
        Directory.CreateDirectory(toolsDir);
        foreach (var e in Directory.GetFileSystemEntries(entries[0]))
            MoveInto(e, toolsDir);
        InstallRunner.DeleteTree(modDir);

        lock (ToolIndexLock)
        {
            var set = InstalledTools(instanceDir);
            set.Add(installAs);
            File.WriteAllText(Path.Combine(toolsDir, ToolsIndexFile),
                JsonSerializer.Serialize(set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        InstallLog.Info("mo2", $"{installAs}: moved to the Tools folder ({toolsDir})");
        return true;
    }

    // ---- profile files + Root Builder executables --------------------------------------

    /// <summary>
    /// Files a self-contained MO2 profile needs (the shape Wabbajack lists ship): profile-local
    /// settings.ini and INIs, so the user's Documents\My Games\Fallout4 is never touched, plus the
    /// loose-file invalidation every MO2 Fallout 4 setup requires. Existing files are kept.
    /// </summary>
    public static void EnsureProfileFiles(string instanceDir, string gameRoot)
    {
        var profile = Path.Combine(instanceDir, "profiles", "A StoryWealth");
        Directory.CreateDirectory(profile);
        void Write(string name, Func<string> content)
        {
            var path = Path.Combine(profile, name);
            if (File.Exists(path)) return;
            File.WriteAllText(path, content(), new System.Text.UTF8Encoding(false));
        }
        Write("settings.ini", () => "[General]\nLocalSaves=true\nLocalSettings=true\nAutomaticArchiveInvalidation=true\n");
        Write("fallout4.ini", () =>
        {
            var def = Path.Combine(gameRoot, "Fallout4_Default.ini");
            return File.Exists(def) ? File.ReadAllText(def) : "[General]\n";
        });
        Write("fallout4prefs.ini", () =>
        {
            var prefs = Path.Combine(gameRoot, "Fallout4", "Fallout4Prefs.ini");
            return File.Exists(prefs) ? File.ReadAllText(prefs) : "[Launcher]\nbEnableFileSelection=1\n";
        });
        Write("fallout4custom.ini", () => "[Launcher]\nbEnableFileSelection=1\n\n[Archive]\nbInvalidateOlderFiles=1\nsResourceDataDirsFinal=\n");
        Write("initweaks.ini", () => "[Archive]\nbInvalidateOlderFiles=1\n");
    }

    /// <summary>
    /// A game-folder file shipped by a Root Builder mod (mods/&lt;m&gt;/Root/&lt;rel&gt;), highest-priority
    /// folder first is not needed here: file names such as f4se_loader.exe exist in one mod.
    /// </summary>
    private static string? FindRootFile(string instanceDir, string rel)
    {
        var mods = Path.Combine(instanceDir, "mods");
        if (!Directory.Exists(mods)) return null;
        foreach (var d in Directory.EnumerateDirectories(mods))
        {
            var p = Path.Combine(d, "Root", rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Points the scaffolded F4SE launcher at mods/&lt;F4SE&gt;/Root/f4se_loader.exe (working directory
    /// stays the game folder), the way Wabbajack lists do: MO2 can start it before Root Builder
    /// has copied anything, and Root Builder's redirect then runs the game-folder copy.
    /// </summary>
    public static void PointF4seAtRootMod(string instanceDir, string gameRoot)
    {
        var iniPath = Path.Combine(instanceDir, "ModOrganizer.ini");
        var loader = FindRootFile(instanceDir, "f4se_loader.exe");
        if (!File.Exists(iniPath) || loader is null) return;
        var want = Path.Combine(gameRoot, "f4se_loader.exe").Replace(Path.DirectorySeparatorChar, '/');
        var lines = File.ReadAllLines(iniPath);
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var eq = lines[i].IndexOf('=');
            if (eq < 0 || !lines[i][..eq].EndsWith("\\binary", StringComparison.Ordinal)) continue;
            if (!lines[i][(eq + 1)..].Trim().Replace(Path.DirectorySeparatorChar, '/').Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = lines[i][..(eq + 1)] + loader.Replace(Path.DirectorySeparatorChar, '/');
            changed = true;
        }
        if (changed)
        {
            File.WriteAllLines(iniPath, lines, new System.Text.UTF8Encoding(false));
            InstallLog.Info("mo2", $"F4SE launcher points at {loader}");
        }
    }
}
