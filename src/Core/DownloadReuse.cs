using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AStoryWealth.Core;

/// <summary>
/// Reuses archives from an existing Vortex download folder so migrating users
/// don't re-download ~120 GB. Two filename styles exist:
///   CDN/Nexus:  <modId>-<fileId>-<name>-version-<ts>.<ext>  — matched exactly
///   Vortex:     <name>-<modId>-<version>-<ts>.<ext>         — no fileId, matched by
///               modId + file size, then confirmed by MD5 before use.
/// A wrong candidate is rejected by verification and the next one is tried.
/// </summary>
public static class DownloadReuse
{
    private static readonly Regex Rx = new(@"(?:^|[^0-9])(?<mod>\d+)-(?<file>\d+)-", RegexOptions.Compiled);

    // one snapshot per reuse dir per run; 1893 files × 921 mods must not rescan every time
    private static readonly ConcurrentDictionary<string, string[]> _cache = new();

    public static async Task<string?> FindArchiveAsync(string dir, ModEntry mod,
        Func<string, Task<(bool ok, string detail)>> verify, Action<string, string>? onRejected = null)
    {
        if (mod.Nexus is null || !Directory.Exists(dir)) return null;
        int modId = mod.Nexus.ModId;
        long fileId = mod.Nexus.FileId;
        long size = mod.Nexus.FileSize ?? 0;

        var key = Path.GetFullPath(dir);
        var files = _cache.GetOrAdd(key, _ =>
            Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .ToArray());

        string? exact = null;
        var byModId = new List<string>();
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var m = Rx.Match(name);
            if (m.Success &&
                int.TryParse(m.Groups["mod"].Value, out var gm) && gm == modId &&
                long.TryParse(m.Groups["file"].Value, out var gf) && gf == fileId)
            {
                exact = f;
                break;
            }
            if (NameHasModId(name, modId)) byModId.Add(f);
        }

        if (exact is not null && SizeOk(exact))
            return (await verify(exact)).ok ? exact : null;

        foreach (var c in byModId)
        {
            if (!SizeOk(c)) { onRejected?.Invoke(c, "size differs from the collection's pinned file"); continue; }
            var (ok, detail) = await verify(c);
            if (ok) return c;
            onRejected?.Invoke(c, $"md5 differs from the collection's pinned file ({detail})");
        }
        return null;

        bool SizeOk(string p) =>
            size <= 0 || new FileInfo(p).Length == size;
    }

    /// <summary>Parses the modId out of a download filename. The modId is a
    /// dash- or space-delimited token somewhere in the stem (Vortex:
    /// "Name-<modId>-<ver>-<ts>", CDN: "<modId>-<fileId>-...", Vortex renames
    /// like "eXoPatch 72452 1.2.2 ..."). Version segments ("v1-3-2") and
    /// timestamps parse as numbers too, so the token must EQUAL the wanted
    /// modId — no heuristic "first numeric token" guess.
    /// </summary>
    private static bool NameHasModId(string fileName, int modId)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var tokens = stem.Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(t => t == modId.ToString());
    }

    /// <summary>Default Vortex downloads folder, if it exists.</summary>
    public static string? DefaultVortexDir()
    {
        var p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Vortex", "fallout4", "downloads");
        if (Directory.Exists(p)) return p;
        // Vortex staging / downloads are often redirected to another drive.
        // Known layouts: <drive>:\Games\Vortex Mods\fallout4\downloads, <drive>:\Vortex Downloads\fallout4.
        foreach (var drv in Directory.GetLogicalDrives())
        {
            var q = Path.Combine(drv, "Games", "Vortex Mods", "fallout4", "downloads");
            if (Directory.Exists(q)) return q;
            q = Path.Combine(drv, "Vortex Downloads", "fallout4");
            if (Directory.Exists(q)) return q;
        }
        return null;
    }
}
