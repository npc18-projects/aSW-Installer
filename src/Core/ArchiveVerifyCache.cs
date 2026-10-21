using System.Collections.Concurrent;
using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Remembers which archives already passed size+md5 verification, so a crashed
/// run doesn't re-hash the same multi-GB files from scratch. Keyed by full path,
/// size and mtime — any change to the file invalidates its entry.
/// </summary>
public static class ArchiveVerifyCache
{
    private sealed record Entry(string PinnedKey, long Size, long MtimeUtcTicks);

    private sealed record CacheFile(Dictionary<string, Entry> Verified);

    private static readonly ConcurrentDictionary<string, Entry> Map = new();
    private static readonly object SaveLock = new();
    private static bool _loaded;
    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AStoryWealth", "verify-cache.json");

    private static void Load()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(CachePath))
            {
                var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
                if (c?.Verified is { } m)
                    foreach (var (k, v) in m) Map[k] = v;
            }
        }
        catch { /* corrupt cache: rebuild */ }
        _loaded = true;
    }

    private static void Save()
    {
        try
        {
            lock (SaveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath,
                    JsonSerializer.Serialize(new CacheFile(new Dictionary<string, Entry>(Map))));
            }
        }
        catch { /* best effort */ }
    }

    private static string Key(string path)
    {
        var fi = new FileInfo(path);
        return $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
    }

    private static string PinnedKey(ModEntry mod) =>
        mod.Nexus is { } nx ? $"{nx.ModId}:{nx.FileId}" : mod.InstallAs;

    /// <summary>Verifies via <paramref name="verify"/>; on success the result is
    /// cached and identical future checks return instantly.</summary>
    public static async Task<(bool ok, string detail)> VerifyAsync(
        string path, ModEntry mod, Func<string, Task<(bool ok, string detail)>> verify)
    {
        Load();
        if (Map.TryGetValue(Key(path), out var e) && e.PinnedKey == PinnedKey(mod))
            return (true, "ok (cached)");
        var (ok, detail) = await verify(path);
        if (ok)
        {
            Map[Key(path)] = new Entry(PinnedKey(mod), new FileInfo(path).Length,
                new FileInfo(path).LastWriteTimeUtc.Ticks);
            Save();
        }
        return (ok, detail);
    }
}
