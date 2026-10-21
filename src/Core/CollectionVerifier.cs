using System.Security.Cryptography;
using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Post-install verification against Vortex collection ground truth:
/// - hashes[]: per-file md5 of the materialized mod folder (14 collection mods)
/// - patches{}: expected CRC32 of installed plugins (39 collection mods)
/// Both are warn-only: a mismatch never aborts the run, it is logged so the
/// user can re-materialize. Absence of the referenced file counts as a mismatch.
/// </summary>
public static class CollectionVerifier
{
    public sealed record VerifyResult(int Checked, List<string> Mismatches)
    {
        public bool Ok => Mismatches.Count == 0;
    }

    /// <summary>Verifies a materialized mod folder. Cheap no-op when the mod
    /// entry carries neither hashes nor patches.</summary>
    public static VerifyResult Verify(string modDir, ModEntry mod)
    {
        var bad = new List<string>();
        var checkedCount = 0;
        try
        {
            RepairLayout(modDir, mod);
        }
        catch (Exception rex) { InstallLog.Warn("verify", $"{mod.InstallAs}: layout repair failed: {rex.Message}"); }
        try
        {
            if (mod.HashesJson != null)
            {
                var json = JsonDocument.Parse(mod.HashesJson).RootElement;
                foreach (var h in json.EnumerateArray())
                {
                    var rel = h.TryGetProperty("path", out var p) ? p.GetString() : null;
                    var want = h.TryGetProperty("md5", out var md) ? md.GetString() : null;
                    if (rel == null || want == null) continue;
                    checkedCount++;
                    var file = Path.Combine(modDir, rel.Replace('/', '\\'));
                    var got = File.Exists(file) ? Md5(file) : null;
                    if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
                        bad.Add($"hash {rel}: expected {want}, got {got ?? "<missing>"}");
                }
            }
            if (mod.PatchesJson != null)
            {
                var json = JsonDocument.Parse(mod.PatchesJson).RootElement;
                foreach (var pt in json.EnumerateObject())
                {
                    var plugin = pt.Name;
                    var want = pt.Value.GetString();
                    if (string.IsNullOrEmpty(want)) continue;
                    checkedCount++;
                    var file = Path.Combine(modDir, plugin.Replace('/', '\\'));
                    var got = File.Exists(file) ? Crc32Hex(file) : null;
                    if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
                        bad.Add($"crc {plugin}: expected {want}, got {got ?? "<missing>"}");
                }
            }
        }
        catch (Exception ex)
        {
            bad.Add($"verify failed: {ex.Message}");
        }
        return new VerifyResult(checkedCount, bad);
    }

    /// <summary>
    /// Vortex's hashes[] describe the mod's staged layout exactly (including
    /// folders Vortex's installer added or kept). When files are missing from
    /// the materialized folder, put them where the collection says:
    ///  1. all paths share a leading folder (e.g. "Tools\FO4Edit 4.1.5f\") that
    ///     the extracted content lacks -> move the content under that folder;
    ///  2. otherwise, per missing file, copy a same-named file whose md5 matches.
    /// Returns the number of files placed.
    /// </summary>
    public static int RepairLayout(string modDir, ModEntry mod)
    {
        if (mod.HashesJson == null || !Directory.Exists(modDir)) return 0;
        List<(string Rel, string Md5)> wanted;
        try
        {
            wanted = JsonDocument.Parse(mod.HashesJson).RootElement.EnumerateArray()
                .Select(h => (Rel: (h.TryGetProperty("path", out var p) ? p.GetString() : null) ?? "",
                              Md5: (h.TryGetProperty("md5", out var m) ? m.GetString() : null) ?? ""))
                .Where(x => x.Rel.Length > 0 && x.Md5.Length > 0)
                .Select(x => (x.Rel.Replace('/', Path.DirectorySeparatorChar), x.Md5)).ToList();
        }
        catch { return 0; }
        if (wanted.Count == 0) return 0;
        var missing = wanted.Where(w => !File.Exists(Path.Combine(modDir, w.Rel))).ToList();
        if (missing.Count == 0) return 0;
        var placed = 0;

        // 1. shared leading folder
        var parts = wanted.Select(w => w.Rel.Split(Path.DirectorySeparatorChar)).ToList();
        var common = new List<string>();
        for (var i = 0; ; i++)
        {
            if (parts.Any(p => p.Length <= i + 1)) break;           // keep the file name out of the prefix
            var seg = parts[0][i];
            if (parts.Any(p => !p[i].Equals(seg, StringComparison.OrdinalIgnoreCase))) break;
            common.Add(seg);
        }
        if (common.Count > 0 && missing.Count == wanted.Count)
        {
            var prefix = string.Join(Path.DirectorySeparatorChar, common);
            var rests = wanted.Select(w => w.Rel[(prefix.Length + 1)..]).ToList();
            if (rests.Count(r => File.Exists(Path.Combine(modDir, r))) >= Math.Max(1, rests.Count * 9 / 10))
            {
                var target = Path.Combine(modDir, prefix);
                var holding = Path.Combine(modDir, ".relayout-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(holding);
                foreach (var e in Directory.GetFileSystemEntries(modDir).Where(e => e != holding && !Path.GetFileName(e).Equals("meta.ini", StringComparison.OrdinalIgnoreCase)))
                    Directory.Move(e, Path.Combine(holding, Path.GetFileName(e)));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(holding, target);
                placed = rests.Count;
                InstallLog.Info("verify", $"{mod.InstallAs}: relocated content under '{prefix}' to match the collection's layout");
                missing = wanted.Where(w => !File.Exists(Path.Combine(modDir, w.Rel))).ToList();   // fall through: place stragglers by md5
            }
        }

        // 2. per-file placement by md5
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories))
        {
            var n = Path.GetFileName(f);
            if (!byName.TryGetValue(n, out var l)) byName[n] = l = [];
            l.Add(f);
        }
        foreach (var (rel, md5) in missing)
        {
            if (!byName.TryGetValue(Path.GetFileName(rel), out var cands)) continue;
            var src = cands.FirstOrDefault(c => string.Equals(Md5(c), md5, StringComparison.OrdinalIgnoreCase));
            if (src is null) continue;
            var dest = Path.Combine(modDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: false);
            placed++;
        }
        if (placed > 0)
            InstallLog.Info("verify", $"{mod.InstallAs}: placed {placed} file(s) where the collection's hashes expect them");
        return placed;
    }

    private static string Md5(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexStringLower(MD5.HashData(fs));
    }

    /// <summary>Standard CRC-32 (IEEE 802.3, reflected, poly 0xEDB88320) as
    /// 8 uppercase hex chars — the format Vortex collections use in patches{}.</summary>
    private static string Crc32Hex(string file)
    {
        uint crc = 0xFFFFFFFF;
        using var fs = File.OpenRead(file);
        Span<byte> buf = stackalloc byte[81920];
        int n;
        while ((n = fs.Read(buf)) > 0)
            for (var i = 0; i < n; i++)
            {
                crc ^= buf[i];
                for (var b = 0; b < 8; b++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        return (~crc).ToString("X8");
    }
}
