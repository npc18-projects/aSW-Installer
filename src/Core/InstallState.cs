using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Tracks whether the base A StoryWealth pack is installed (marker file written on
/// successful install, path recorded). Add-on lists only show when this says yes.
/// </summary>
public static class InstallState
{
    private static string? _dirOverride;

    /// <summary>Tests only: keep the state files in this folder instead of %LOCALAPPDATA%\AStoryWealth. Pass null to go back.</summary>
    public static void UseDirectory(string? dir) => _dirOverride = dir;

    private static string StatePath
    {
        get
        {
            var dir = _dirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "install.json");
        }
    }

    /// <summary>Reads install.json, tolerating corruption. On any read/parse
    /// failure the corrupt file is moved aside to install.json.bak and null is
    /// returned (empty state). Deserialized records are normalized so Resume
    /// list fields are never null.</summary>
    private static InstallRecord? ReadRecord()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            string text;
            for (var attempt = 1; ; attempt++)
            {
                try { text = File.ReadAllText(StatePath); break; }
                catch (IOException) when (attempt < 4) { Thread.Sleep(100 * attempt); }
            }
            var rec = JsonSerializer.Deserialize<InstallRecord>(text);
            if (rec?.Resume is { } r)
            {
                var dd = r.DoneDownloads ?? new List<string>();
                var di = r.DoneInstalls ?? new List<string>();
                if (!ReferenceEquals(dd, r.DoneDownloads) || !ReferenceEquals(di, r.DoneInstalls))
                    rec = rec with { Resume = r with { DoneDownloads = dd, DoneInstalls = di } };
            }
            return rec;
        }
        catch (IOException) { return null; }   // locked for now: leave the file where it is
        catch (UnauthorizedAccessException) { return null; }
        catch
        {
            try { if (File.Exists(StatePath)) File.Move(StatePath, StatePath + ".bak", overwrite: true); } catch { }
            return null;
        }
    }

    /// <summary>Atomic write: serialize to a temp file, then move it over the target so a crash mid-write never
    /// leaves a half-written install.json. File.Replace can't be used here: it fails when the target doesn't exist
    /// yet, which is exactly the first write on a new machine.</summary>
    private static void WriteRecord(InstallRecord rec)
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(dir);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rec));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // state persistence must never throw into the install flow, but a failure has to leave a trace
            try { InstallLog.Warn("state", $"couldn't save install.json: {ex.Message}"); } catch { }
        }
    }

    public sealed record InstallRecord(string InstallDir, string CompletedUtc,
        string? LastDir = null, string? LastReuseDir = null, string? LastDownloadsDir = null, bool? LastStockGame = null,
        ResumeInfo? Resume = null);

    /// <summary>Checkpoint of an in-flight install. Written by InstallRunner after
    /// each completed step so an interrupted run resumes instead of restarting.</summary>
    public sealed record ResumeInfo(
        string JsonPath, string AddonId, string InstallDir, string GamePath, bool StockGame,
        string? ReuseDir, string? DownloadsDir,
        List<string> DoneDownloads, List<string> DoneInstalls, bool GameCopied, Dictionary<string,string>? DownloadedFiles = null, bool IncludeOptional = false, List<string>? OptionalGroups = null, string? BaseJsonPath = null, List<string>? OptionalMods = null);

    public static ResumeInfo? GetResume() => ReadRecord()?.Resume;

    public static void SetResume(ResumeInfo resume)
    {
        var old = ReadRecord();
        var dir = old?.InstallDir ?? resume.InstallDir;
        var done = old?.CompletedUtc ?? "";
        WriteRecord(new InstallRecord(dir, done,
            LastDir: old?.LastDir ?? resume.InstallDir,
            LastReuseDir: old?.LastReuseDir ?? resume.ReuseDir,
            LastDownloadsDir: old?.LastDownloadsDir ?? resume.DownloadsDir,
            LastStockGame: old?.LastStockGame ?? resume.StockGame,
            Resume: resume));
    }

    /// <summary>User-tuned thread counts for downloads/extraction. Defaults to
    /// one less than the logical core count; persisted in threads.json.</summary>
    public sealed record ThreadSettings(int Download, int Extract);

    public static ThreadSettings DefaultThreads()
    {
        var t = Math.Max(1, Environment.ProcessorCount - 1);
        return new ThreadSettings(t, t);
    }

    private static string ThreadsPath => Path.Combine(Path.GetDirectoryName(StatePath)!, "threads.json");

    public static ThreadSettings GetThreads()
    {
        try
        {
            if (File.Exists(ThreadsPath))
            {
                var s = JsonSerializer.Deserialize<ThreadSettings>(File.ReadAllText(ThreadsPath));
                if (s is { Download: > 0, Extract: > 0 }) return s;
            }
        }
        catch { }
        return DefaultThreads();
    }

    public static void SetThreads(ThreadSettings t)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThreadsPath)!);
            File.WriteAllText(ThreadsPath, JsonSerializer.Serialize(
                new ThreadSettings(Math.Max(1, t.Download), Math.Max(1, t.Extract))));
        }
        catch { }
    }

    public static void ClearResume()
    {
        var rec = ReadRecord();
        if (rec is null) return;
        WriteRecord(rec with { Resume = null });
    }

    /// <summary>Install dir if a completed install is recorded AND ModOrganizer.exe is really there; otherwise null.</summary>
    public static string? GetInstallDir()
    {
        var rec = ReadRecord();
        if (rec?.InstallDir is not { } d) return null;
        return File.Exists(Path.Combine(d, "ModOrganizer.exe")) ? d : null;
    }

    public static void SetInstallDir(string installDir)
    {
        // copy-on-write: carry every other field through, only update InstallDir + CompletedUtc
        var old = ReadRecord();
        WriteRecord(new InstallRecord(installDir, DateTime.UtcNow.ToString("O"),
            LastDir: old?.LastDir, LastReuseDir: old?.LastReuseDir,
            LastDownloadsDir: old?.LastDownloadsDir, LastStockGame: old?.LastStockGame,
            Resume: old?.Resume));
    }

    /// <summary>Last install dir used, even if that install never finished — remembered so the field pre-fills next launch.</summary>
    public static string? GetLastDir()
    {
        var rec = ReadRecord();
        return rec?.LastDir is { } d && Directory.Exists(d) ? d : rec?.InstallDir is { } d2 && Directory.Exists(d2) ? d2 : null;
    }

    /// <summary>Writes the last-used install options at install START (not completion) so a cancelled run is still remembered.</summary>
    public static void SetLastPath(string installDir, string? reuseDir, string? downloadsDir, bool stockGame)
    {
        // copy-on-write: preserve Resume and any other fields, only update the Last* options
        var old = ReadRecord();
        var dir = old?.InstallDir ?? installDir;
        var done = old?.CompletedUtc ?? "";
        WriteRecord(new InstallRecord(dir, done,
            LastDir: installDir, LastReuseDir: reuseDir, LastDownloadsDir: downloadsDir,
            LastStockGame: stockGame, Resume: old?.Resume));
    }

    public static (string? Dir, string? ReuseDir, string? DownloadsDir, bool StockGame) GetLastPath()
    {
        var rec = ReadRecord();
        if (rec is null) return (null, null, null, false);
        return (rec.LastDir, rec.LastReuseDir, rec.LastDownloadsDir, rec.LastStockGame ?? false);
    }
}
