using System.Diagnostics;

namespace AStoryWealth.Core;

/// <summary>
/// Downloads for free Nexus accounts. Nexus only hands out direct links to Premium members, so for everyone else
/// the installer opens the exact file's page (mod id + file id from the collection) one mod at a time, then waits
/// for the archive to land in the browser's download folder, checks size and md5 against the collection and moves
/// it into the downloads folder. Nothing is fetched automatically.
/// </summary>
public static class ManualDownloader
{
    /// <summary>The page currently being waited on, so the UI can offer to open it again.</summary>
    public static string? CurrentUrl { get; private set; }

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly string[] TempExt = [".crdownload", ".part", ".tmp", ".download", ".opdownload", ".partial"];

    public static string PageUrl(ModEntry mod) =>
        $"https://www.nexusmods.com/{mod.Nexus!.Game}/mods/{mod.Nexus.ModId}?tab=files&file_id={mod.Nexus.FileId}";

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { InstallLog.Warn("download", $"couldn't open the browser: {ex.Message}"); }
    }

    /// <summary>The browser's usual download folder (Windows "Downloads").</summary>
    public static string DefaultWatchDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// Opens the file's page and waits until a verified copy of it appears in <paramref name="watchDir"/>.
    /// Returns the path it was moved to (inside the downloads folder via <paramref name="destFor"/>).
    /// A matching file already sitting in the watch folder is taken straight away without opening the browser.
    /// </summary>
    public static async Task<string> FetchAsync(ModEntry mod, string watchDir,
        Func<string, string> destFor,
        Func<string, Task<(bool ok, string detail)>> verify,
        Action<string> announce, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            var size = mod.Nexus!.FileSize ?? 0;
            var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            async Task<string?> Scan()
            {
                if (!Directory.Exists(watchDir)) return null;
                foreach (var f in Directory.EnumerateFiles(watchDir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (TempExt.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || rejected.Contains(f)) continue;
                    long len;
                    try { len = new FileInfo(f).Length; } catch { continue; }
                    if (size > 0 && len != size) continue;
                    // still being written? wait for the size to hold still
                    await Task.Delay(1200, ct);
                    try { if (new FileInfo(f).Length != len) continue; } catch { continue; }
                    var (ok, detail) = await verify(f);
                    if (ok) return f;
                    rejected.Add(f);
                    InstallLog.Warn("download", $"{Path.GetFileName(f)} has the right size but isn't the file for {mod.InstallAs} ({detail})");
                }
                return null;
            }

            var found = await Scan();
            if (found is null)
            {
                CurrentUrl = PageUrl(mod);
                announce(CurrentUrl);
                InstallLog.Info("download", $"waiting for you to download {mod.InstallAs} (mod {mod.Nexus.ModId}, file {mod.Nexus.FileId}) into {watchDir}");
                OpenPage(CurrentUrl);
                while (found is null)
                {
                    await Task.Delay(1500, ct);
                    found = await Scan();
                }
            }
            CurrentUrl = null;

            var dest = destFor(Path.GetFileName(found));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!string.Equals(Path.GetFullPath(found), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest)) File.Delete(found);
                else File.Move(found, dest);
            }
            InstallLog.Info("download", $"received: {Path.GetFileName(dest)}");
            return dest;
        }
        finally { CurrentUrl = null; OneAtATime.Release(); }
    }
}
