using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Discovers add-on manifests in %LOCALAPPDATA%\AStoryWealth\Addons and works out
/// which ones have an update (pinned base revision older than the installed base).
/// </summary>
public static class AddonStore
{
    public static string AddonsDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AStoryWealth", "Addons");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public sealed record AddonEntry(AddonManifest Manifest, string Path, bool UpdateAvailable);

    /// <summary>One add-on manifest by slug, or null if not cached.</summary>
    public static AddonManifest? Get(string slug)
    {
        try
        {
            var file = Path.Combine(AddonsDir, $"{slug}.addonmanifest.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(file)) : null;
        }
        catch { return null; }
    }

    /// <summary>All installed add-on manifests with their update state. baseRevision = installed base collection revision.</summary>
    public static List<AddonEntry> List(int? baseRevision)
    {
        var result = new List<AddonEntry>();
        foreach (var file in Directory.EnumerateFiles(AddonsDir, "*.addonmanifest.json"))        {
            try
            {
                var m = JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(file));
                if (m is null) continue;
                // update available when the add-on pins an exact base revision older
                // than the installed one, or when a newer manifest revision was
                // published for the same addonId later (auto-grab overwrites the file,
                // so file presence alone means current).
                var update = m.Requires.RevisionNumber is int r && baseRevision is int b && r < b;
                result.Add(new AddonEntry(m, file, update));
            }
            catch { /* bad manifest — skip */ }
        }
        return result.OrderBy(a => a.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
