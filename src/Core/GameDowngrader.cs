using System.Diagnostics;
using System.Security.Cryptography;

namespace AStoryWealth.Core;

/// <summary>
/// Native version of the "patch files" mode of the collection's Fallout 4 Downgrader
/// (zerratar/fallout4-downgrader, Nexus 81630). FO4Down.exe cannot be used directly: it
/// finds the game by walking up from its working directory looking for a folder literally
/// named "Fallout 4", then falls back to the real Steam install — so it would never target
/// our private "Stock Game" copy, and could patch the user's own installation instead.
///
/// The mod's Assets folder holds, per file to downgrade (Fallout4.exe, steam_api64.dll,
/// Fallout4Launcher.exe):
///   Keys/&lt;file&gt;.key           "&lt;id&gt;\t&lt;base64 AES key&gt;"
///   Patch/&lt;id&gt;_0..N.patch     chunks; each = 16-byte IV + AES-CBC (PKCS7) ciphertext of a
///                             slice of the complete 1.10.163 file, concatenated in order.
/// So "patching" is decrypt-and-replace; nothing is diffed. The result is verified by the
/// file's version resource before it replaces the original.
/// </summary>
public static class GameDowngrader
{
    public sealed record Target(string FileName, string ExpectedVersion);

    /// <summary>The files FO4Down patches and the versions it expects afterwards.</summary>
    public static readonly Target[] Targets =
    [
        new("Fallout4.exe", "1.10.163.0"),
        new("steam_api64.dll", "2.89.45.4"),
        new("Fallout4Launcher.exe", "1.3.22.0"),
    ];

    /// <summary>File version of an executable/dll, normalised to "a.b.c.d"; null when unreadable.</summary>
    public static string? VersionOf(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}";
        }
        catch { return null; }
    }

    /// <summary>
    /// Replaces each target in <paramref name="gameDir"/> that is not already at its expected
    /// version, using the extracted downgrader <paramref name="assetsDir"/>. Returns the number of
    /// files replaced. Throws (leaving the original untouched) if a rebuilt file fails its version check.
    /// </summary>
    public static int Patch(string gameDir, string assetsDir, Func<string, string?>? versionOf = null,
        IEnumerable<Target>? targets = null, CancellationToken ct = default)
    {
        versionOf ??= VersionOf;
        var replaced = 0;
        foreach (var t in targets ?? Targets)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(gameDir, t.FileName);
            if (!File.Exists(path))
            {
                InstallLog.Warn("downgrade", $"{t.FileName}: not found in {gameDir} — skipped");
                continue;
            }
            var current = versionOf(path);
            if (current == t.ExpectedVersion)
            {
                InstallLog.Info("downgrade", $"{t.FileName}: already {current}");
                continue;
            }

            var keyFile = Path.Combine(assetsDir, "Keys", t.FileName + ".key");
            if (!File.Exists(keyFile))
                throw new InvalidOperationException($"downgrader has no key for {t.FileName} ({keyFile})");
            var parts = File.ReadAllText(keyFile).Trim().Split('\t');
            if (parts.Length < 2 || !ulong.TryParse(parts[0], out var id))
                throw new InvalidOperationException($"unreadable key file for {t.FileName}");
            var key = Convert.FromBase64String(parts[1]);

            var chunks = Directory.GetFiles(Path.Combine(assetsDir, "Patch"), $"{id}_*.patch")
                .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)[(Path.GetFileNameWithoutExtension(f).LastIndexOf('_') + 1)..]))
                .ToList();
            if (chunks.Count == 0)
                throw new InvalidOperationException($"downgrader has no patch data for {t.FileName} (id {id})");

            InstallLog.Info("downgrade", $"{t.FileName}: {current ?? "unknown version"} → {t.ExpectedVersion} ({chunks.Count} chunk(s))");
            var tmp = path + ".asw-new";
            try
            {
                using (var output = File.Create(tmp))
                    foreach (var chunk in chunks)
                    {
                        ct.ThrowIfCancellationRequested();
                        var plain = DecryptChunk(File.ReadAllBytes(chunk), key);
                        output.Write(plain, 0, plain.Length);
                    }
                var made = versionOf(tmp);
                if (made != t.ExpectedVersion)
                    throw new InvalidOperationException(
                        $"{t.FileName}: rebuilt file reports version {made ?? "unknown"}, expected {t.ExpectedVersion} — original left untouched");
                File.Move(tmp, path, overwrite: true);
                replaced++;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        return replaced;
    }

    internal static byte[] DecryptChunk(byte[] chunk, byte[] key)
    {
        if (chunk.Length <= 16) throw new InvalidOperationException("patch chunk too small");
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        var iv = chunk.AsSpan(0, 16).ToArray();
        using var dec = aes.CreateDecryptor(aes.Key, iv);
        return dec.TransformFinalBlock(chunk, 16, chunk.Length - 16);
    }
}
