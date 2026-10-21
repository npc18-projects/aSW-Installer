using System.Diagnostics;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AStoryWealth.NexusApi;

namespace AStoryWealth.Core;

/// <summary>
/// Drives a full install of one addon manifest into a MO2 instance
/// (premium CDN path). Cancel-safe; every step goes through InstallLog.
/// </summary>
public sealed class InstallRunner
{

    /// <summary>
    /// Hard recursive delete: clears attributes first, retries a few times.
    /// Plain Directory.Delete(recursive) throws "Access to the path" on
    /// read-only files, handles held by AV/Explorer, or >MAX_PATH leaves.
    /// </summary>
    internal static void DeleteTree(string dir)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(d, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
                return;
            }
            catch when (attempt < 4)
            {
                Thread.Sleep(300 * (attempt + 1));
            }
        }
    }

    public sealed record ProgressInfo(int Current, int Total, string Phase, string CurrentItem, string? Speed = null);

    /// <summary>Raised per log line — UI hooks this to show a live log tail.</summary>
    public static event Action<string>? UiLogLine;

    static InstallRunner()
    {
        InstallLog.Written += line => UiLogLine?.Invoke(line);
    }

    public async Task RunAsync(string collectionJsonPath, string addonId, string apiKey,
        string instanceDir, string gamePath, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default, bool stockGame = false, string? reuseDir = null,
        string? downloadsDir = null, InstallState.ResumeInfo? resume = null,
        int downloadThreads = 0, int extractThreads = 0, bool includeOptional = false)
    {
        InstallLog.Init(instanceDir);
        InstallLog.Info("auth", "validating Nexus account...");
        using var api = new NexusApiProxy(apiKey, progress, ct);
        var user = await api.ValidateUserAsync();
        InstallLog.Info("auth", $"user {user.Name}, premium={user.IsPremium}");

        // Resume checkpoint: written after every completed step, cleared on success.
        resume ??= new InstallState.ResumeInfo(collectionJsonPath, addonId, instanceDir, gamePath,
            stockGame, reuseDir, downloadsDir, [], [], false, IncludeOptional: includeOptional);
        includeOptional = resume.IncludeOptional;   // a resumed run keeps the choice it started with
        InstallState.SetResume(resume);
        if (resume.DoneDownloads.Count > 0 || resume.DoneInstalls.Count > 0 || resume.GameCopied)
            InstallLog.Info("resume", $"resuming: {resume.DoneDownloads.Count} downloaded, {resume.DoneInstalls.Count} installed, game copy {(resume.GameCopied ? "done" : "pending")}");

        // Stock game: the instance gets its own private copy of the game, so the
        // user's Steam install is never touched. Source is the local Steam install.
        InstallLog.Info("game", stockGame
            ? $"game mode: separate stock copy (source {gamePath})"
            : $"game mode: attached install ({gamePath})");
        var effectiveGamePath = gamePath;
        if (stockGame)
        {
            if (!Directory.Exists(gamePath))
                throw new DirectoryNotFoundException(
                    "Couldn't find a Fallout 4 install. Install the game through Steam once; the installer can then make its own private copy.");
            effectiveGamePath = Path.Combine(instanceDir, "Stock Game");
            var stockExe = Path.Combine(effectiveGamePath, "Fallout4.exe");
            var stockData = Path.Combine(effectiveGamePath, "Data", "Fallout4.esm");
            if (File.Exists(stockExe) && File.Exists(stockData))
                InstallLog.Info("game", $"complete stock game copy already exists: {effectiveGamePath}");
            else if (Directory.Exists(effectiveGamePath))
            {
                // Folder exists but the copy never actually completed (missing
                // exe or Data master) — a broken copy would poison the install.
                InstallLog.Warn("game", "incomplete stock game copy found — recopying");
                DeleteTree(effectiveGamePath);
                CopyGame(gamePath, effectiveGamePath, progress, ct);
            }
            else
                CopyGame(gamePath, effectiveGamePath, progress, ct);
            // NOTE: GamePath in the checkpoint stays the SOURCE (Steam) path —
            // never overwritten with the Stock Game destination, or a later
            // resume would recopy Stock Game onto itself.
            resume = resume with { GameCopied = true };
            InstallState.SetResume(resume);
        }

        progress?.Report(new(0, 1, "mo2", "installing Mod Organizer 2"));
        await Mo2Installer.EnsureAsync(instanceDir, progress, ct);
        await Mo2Installer.EnsureRootBuilderAsync(instanceDir, progress, ct);

        progress?.Report(new(0, 1, "plan", "reading collection manifest"));
        var manifest = VortexCollectionMapper.Map(collectionJsonPath, addonId);
        var plan = new ManifestResolver().Resolve([manifest]);
        foreach (var i in plan.Issues)
        {
            if (i.Severity == "error") InstallLog.Error("plan", i.Message);
            else InstallLog.Warn("plan", i.Message);
        }
        if (plan.HasErrors) throw new InvalidOperationException("manifest failed resolution — see log");

        Mo2InstanceWriter.Scaffold(instanceDir, effectiveGamePath, downloadsDir);
        Mo2InstanceWriter.EnsureProfileFiles(instanceDir, effectiveGamePath);

        // Classification (from collection.json details.type):
        // - gameRootMods: the Downgrader — patches the game itself,
        //   applied to the game folder, excluded from the mod list.
        // - rootFolderMods: Vortex "dinput" mods (BAT files, preloader hooks) —
        //   (F4SE included) split into mods/<name>/Root/ + Data content, deployed by Root Builder.
        const int downgraderModId = 81630;
        var rootMods = manifest.Mods
            .Where(m => m.Nexus is { } nx && nx.ModId == downgraderModId)
            .Select(m => m.InstallAs).ToHashSet();
        var rootFolderMods = manifest.Mods
            .Where(m => m.Type == "dinput" && !rootMods.Contains(m.InstallAs))
            .Select(m => m.InstallAs).ToHashSet();
        var downgrader = manifest.Mods.FirstOrDefault(m => m.Nexus is { } nx && nx.ModId == downgraderModId);
        foreach (var rf in rootFolderMods)
            InstallLog.Info("plan", $"root-layout mod (Root Builder): {rf}");

        // Downgrade check: next-gen game copies are downgraded by the collection's
        // own Downgrader mod — either inside the private Stock Game copy, or in
        // place on the real game install (allowed: users on tight drives, e.g.
        // Steam Deck, expect the downgrade). Only fatal if there's no Downgrader.
        var needsDowngrade = GameVersionAbove163(effectiveGamePath);
        if (needsDowngrade && downgrader is null)
            throw new InvalidOperationException(
                "your Fallout 4 is next-gen but this collection manifest has no Downgrader mod — it needs the 1.10.163 runtime");
        if (needsDowngrade && downgrader is not null)
        {
            var where = stockGame ? "Stock Game copy" : "REAL game install (in place)";
            InstallLog.Warn("game", $"next-gen game detected — Downgrader will patch the {where}");
            if (!stockGame)
                InstallLog.Warn("game", "downgrading in place: your Steam copy of Fallout 4 will be modified to 1.10.163");
        }
        if (downgrader is not null && stockGame && !needsDowngrade)
            InstallLog.Info("game", "game already 1.10.163 — Downgrader will be skipped");

        if (reuseDir is { } rdir)
            InstallLog.Info("reuse", $"checking existing archives in {rdir} ({Directory.EnumerateFiles(rdir).Count()} files)");

        var dlThreads = downloadThreads > 0 ? downloadThreads : DownloadThreads;
        var exThreads = extractThreads > 0 ? extractThreads : ExtractThreads;
        InstallLog.Info("config", $"threads: {dlThreads} download / {exThreads} extract");

        // ---- Phase 1: download and verify EVERY archive first (no extraction).
        // Downgrader first so the downgrade can run while the rest downloads.
        // Optional mods stay out of the run until the Optionals tab ships.
        var coreMods = manifest.Mods.Where(m => includeOptional || !m.IsOptional).ToList();
        InstallLog.Info("config", includeOptional
            ? $"optional mods: INCLUDED ({manifest.Mods.Count(m => m.IsOptional)} mods)"
            : $"optional mods: skipped ({manifest.Mods.Count(m => m.IsOptional)} mods, e.g. the HD textures)");
        var ordered = needsDowngrade && downgrader is not null
            ? coreMods.OrderByDescending(m => m.Nexus is { } nx && nx.ModId == downgraderModId).ToList()
            : coreMods;
        var total = ordered.Count();
        int done = 0;
        Task? downgradeTask = null;
        var toolsDone = Mo2InstanceWriter.InstalledTools(instanceDir);
        var archiveByInstall = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stateLock = new object();
        // Disk-bound: md5 hashing vs concurrent download writes on an HDD is
        // pure seek thrashing — verify one file at a time, never two.
        var reuseGate = new SemaphoreSlim(1, 1);

        void Bump()
        {
            var d = Interlocked.Increment(ref done);
            progress?.Report(new(d, total, "download", $"{d}/{total} archives ready"));
        }

        void SaveResume()
        {
            lock (stateLock) InstallState.SetResume(resume);
        }

        // Parallel archive downloads: each worker fetches + md5-verifies its own
        // archive; reuse (downloads folder / Vortex folder) happens per mod.
        await Parallel.ForEachAsync(ordered,
            new ParallelOptions { MaxDegreeOfParallelism = dlThreads, CancellationToken = ct },
            async (mod, ct2) =>
        {
            ct2.ThrowIfCancellationRequested();
            try
            {
            var dlKey = $"{mod.Nexus!.ModId}:{mod.Nexus.FileId}";
            // Audit H9: never call the Nexus API for an archive we already have.
            // Checkpoint mapping → reuse folder → only then request a CDN link.
            string link = ""; string fileName = ""; string archivePath = ""; var reused = false;
            string? stored = null;
            if (resume.DownloadedFiles is { } df) df.TryGetValue(dlKey, out stored);
            if (stored is not null)
            {
                var storedPath = Mo2InstanceWriter.ArchivePath(instanceDir, mod, stored, downloadsDir);
                if (File.Exists(storedPath))
                {
                    InstallLog.Info("download", $"reused: {stored} (already in the downloads folder)");
                    archivePath = storedPath;
                    reused = true;
                }
            }
            if (!reused && reuseDir is { } rd)
            {
                bool checkpointed2;
                lock (stateLock) checkpointed2 = resume.DoneDownloads.Contains(dlKey);
                if (checkpointed2)
                    InstallLog.Warn("download", $"checkpointed file missing, re-procuring: {mod.InstallAs}");
                // Disk-bound: only a couple of concurrent scans/md5 passes or
                // they thrash the drive and the whole scan slows down.
                await reuseGate.WaitAsync(ct2);
                try
                {
                    var rejected = new List<string>();
                    var candidate = await DownloadReuse.FindArchiveAsync(rd, mod,
                        p => ArchiveVerifyCache.VerifyAsync(p, mod, q => ManifestResolver.VerifyArchiveAsync(q, mod)),
                        (p, why) => rejected.Add($"{Path.GetFileName(p)}: {why}"));
                    if (candidate is not null)
                    {
                        progress?.Report(new(done, total, "download", $"reusing {mod.InstallAs}"));
                        // Reuse the Vortex archive in place — never copy a duplicate
                        // into the downloads folder (it may BE the reuse folder).
                        InstallLog.Info("download", $"reused: {Path.GetFileName(candidate)} (from Vortex)");
                        archiveByInstall[mod.InstallAs] = candidate;
                        lock (stateLock)
                        {
                            if (!resume.DoneDownloads.Contains(dlKey))
                                resume = resume with { DoneDownloads = [.. resume.DoneDownloads, dlKey] };
                        }
                        SaveResume();
                        Bump();
                        return;
                    }
                    if (rejected.Count > 0)
                        InstallLog.Warn("download",
                            $"no reusable archive for {mod.InstallAs} ({rejected.Count} candidates in the Vortex folder failed size/md5 checks); downloading the pinned archive");
                }
                finally { reuseGate.Release(); }
            }
            if (!reused)
            {
                link = await RetryAsync(
                    () => api.GetDownloadLinkAsync(mod.Nexus!.Game, mod.Nexus!.ModId, mod.Nexus.FileId),
                    $"link for {mod.InstallAs}", ct2)
                    ?? throw new InvalidOperationException($"no CDN link for {mod.InstallAs} — non-premium key?");
                fileName = Uri.UnescapeDataString(new Uri(link).Segments[^1]);
                archivePath = Mo2InstanceWriter.ArchivePath(instanceDir, mod, fileName, downloadsDir);
                if (File.Exists(archivePath))
                {
                    InstallLog.Info("download", $"reused: {fileName} (already in the downloads folder)");
                    reused = true;
                }
            }

            if (!reused)
            {
                // A crash can leave a truncated file — if verification fails on a
                // freshly downloaded archive, give it one clean re-download.
                for (var pass = 1; ; pass++)
                {
                    if (pass > 1) File.Delete(archivePath);
                    try
                    {
                        await RetryAsync(
                            () => api.DownloadAsync(link, archivePath, mod.InstallAs, mod.Nexus!.FileSize ?? 0),
                            $"download of {mod.InstallAs}", ct2);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        // Never leave a partial file behind — a retry would see it
                        // on disk and treat it as an already-downloaded archive.
                        try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { }
                        throw;
                    }
                    var (ok2, detail2) = await ArchiveVerifyCache.VerifyAsync(archivePath, mod, q => ManifestResolver.VerifyArchiveAsync(q, mod));
                    if (ok2) break;
                    InstallLog.Error("verify", $"{mod.InstallAs}: {detail2}");
                    if (pass >= 2)
                    {
                        File.Delete(archivePath);
                        throw new InvalidOperationException($"archive verification failed: {mod.InstallAs} ({detail2})");
                    }
                    InstallLog.Warn("verify", "re-downloading once");
                }
            }
            else
            {
                // Same gate as the reuse scan: one md5 at a time across the run.
                await reuseGate.WaitAsync(ct2);
                try
                {
                var (ok, detail) = await ArchiveVerifyCache.VerifyAsync(archivePath, mod, q => ManifestResolver.VerifyArchiveAsync(q, mod));
                if (!ok)
                {
                    InstallLog.Error("verify", $"{mod.InstallAs}: {detail} — deleting bad archive");
                    File.Delete(archivePath);
                    throw new InvalidOperationException($"archive verification failed: {mod.InstallAs} ({detail})");
                }
                }
                finally { reuseGate.Release(); }
            }
            archiveByInstall[mod.InstallAs] = archivePath;
            lock (stateLock)
            {
                var dfs = resume.DownloadedFiles is { } m
                    ? new Dictionary<string, string>(m)
                    : new Dictionary<string, string>();
                dfs[dlKey] = Path.GetFileName(archivePath);
                resume = resume with { DoneDownloads = [.. resume.DoneDownloads, dlKey],
                    DownloadedFiles = dfs };
            }
            SaveResume();

            // Kick off the downgrade as soon as its archive is verified, then
            // keep downloading — patching.bat is local work, downloads are network.
            if (downgrader is { } dg && ReferenceEquals(mod, dg) && needsDowngrade)
            {
                var dgArchive = archivePath;
                var dgGame = effectiveGamePath;
                downgradeTask = Task.Run(() => RunDowngrader(dgGame, dgArchive, progress, ct), ct);
            }
            Bump();
            }
            // One failed mod must not kill the run: record it, keep going, and
            // stop at the end of the download step so the user can retry.
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                InstallLog.Error("download", $"{mod.InstallAs}: {ex.Message}");
                failures[mod.InstallAs] = ex.Message;
            }
        });

        if (!failures.IsEmpty)
        {
            InstallLog.Error("download", $"{failures.Count} download(s) failed — install stopped before extraction. Failed: {string.Join("; ", failures.Keys)}");
            progress?.Report(new(Volatile.Read(ref done), total, "download", $"{failures.Count} downloads failed"));
            throw new DownloadIncompleteException(new Dictionary<string, string>(failures));
        }

        // ---- Phase 2: game prep that ran during downloads finishes first.
        // The downgrade normally kicks off inside the download worker, but the
        // Vortex-reuse path returns early — make sure it runs regardless.
        if (needsDowngrade && downgradeTask is null &&
            downgrader is { } dg2 && archiveByInstall.TryGetValue(dg2.InstallAs, out var dgArchive2))
            downgradeTask = Task.Run(() => RunDowngrader(effectiveGamePath, dgArchive2, progress, ct), ct);
        if (downgradeTask is not null) await downgradeTask;

        // ---- Phase 3: materialize everything into the instance.
        done = 0;
        await Parallel.ForEachAsync(coreMods,
            new ParallelOptions { MaxDegreeOfParallelism = exThreads, CancellationToken = ct },
            async (mod, ct2) =>
        {
            ct2.ThrowIfCancellationRequested();
            // The Downgrader is applied to the game root by RunDowngrader
            // (patching.bat during downloads) — never extract it into mods/.
            if (mod.Nexus is { } dnx && dnx.ModId == downgraderModId)
            {
                InstallLog.Info("install", $"skipping mods-folder materialization for Downgrader (applied to the game root)");
                Interlocked.Increment(ref done);
                return;
            }
            // Tool mods (FO4Edit, Bethini, ...) were relocated to <instance>/Tools by an earlier run.
            if (toolsDone.Contains(mod.InstallAs))
            {
                InstallLog.Info("install", $"already in the Tools folder: {mod.InstallAs}");
                Interlocked.Increment(ref done);
                return;
            }
            // Root mods from an older build kept the whole archive (including Data) under Root/,
            // which Root Builder ignores. Re-materialize them in the split layout.
            if (rootFolderMods.Contains(mod.InstallAs))
            {
                var oldRoot = Path.Combine(instanceDir, "mods", Mo2InstanceWriter.SafeName(mod.InstallAs, "installAs"));
                if (Directory.Exists(Path.Combine(oldRoot, "Root", "Data")))
                {
                    InstallLog.Warn("mo2", $"{mod.InstallAs}: old Root/Data layout — re-materializing for Root Builder");
                    try { DeleteTree(oldRoot); } catch (Exception rex) { InstallLog.Warn("mo2", $"{mod.InstallAs}: {rex.Message}"); }
                    lock (stateLock) resume = resume with { DoneInstalls = resume.DoneInstalls.Where(x => x != mod.InstallAs).ToList() };
                }
            }
            progress?.Report(new(Volatile.Read(ref done), total, "install", mod.InstallAs));
            var archivePath = archiveByInstall.TryGetValue(mod.InstallAs, out var p)
                ? p : throw new InvalidOperationException($"archive for {mod.InstallAs} missing after download phase");
            bool already;
            lock (stateLock) already = resume.DoneInstalls.Contains(mod.InstallAs);
            // FOMOD mods whose on-disk folder is a raw dump (whole-archive
            // fallback from an earlier build) must be re-materialized even
            // though the checkpoint marks them done.
            var isFomod = mod.ChoicesJson != null;
            if (already && isFomod)
            {
                var dumpDir = Path.Combine(instanceDir, "mods", Mo2InstanceWriter.SafeName(mod.InstallAs, "installAs"));
                // Dump signatures: the raw installer's "fomod" folder, or the
                // option-folder layout (two+ top-level dirs numbered like
                // "00 ...", "01 ...") that a folder-mapping bug leaves behind.
                // Neither appears in a correctly materialized FOMOD install.
                bool dumpSignature = Directory.Exists(Path.Combine(dumpDir, "fomod"));
                if (!dumpSignature && Directory.Exists(dumpDir))
                {
                    try
                    {
                        var numbered = Directory.EnumerateDirectories(dumpDir, "*")
                            .Count(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^\d{1,3}(\b|\s|[-_.])"));
                        // one numbered top-level dir is already a dump signature in practice:
// a correctly mapped FOMOD never installs option folders (verified across
// all 79 collection FOMOD mods — only whole-archive dumps have them).
if (numbered >= 1) dumpSignature = true;
                    }
                    catch { }
                }
                if (!dumpSignature)
                {
                    InstallLog.Info("install", $"already installed: {mod.InstallAs}");
                    Interlocked.Increment(ref done);
                    return;
                }
                InstallLog.Warn("fomod", $"{mod.InstallAs}: whole-archive fallback dump — re-materializing with the collection's option selections");
                try { DeleteTree(dumpDir); }
                catch (Exception dex)
                {
                    InstallLog.Warn("fomod", $"{mod.InstallAs}: raw-dump delete failed ({dex.Message}) — re-materializing anyway");
                }
            }
            else if (already)
            {
                // Existing dumps from an earlier build may still carry a single
                // wrapper folder ("Main") — strip it in place, Vortex-style.
                try { Mo2InstanceWriter.StripWrapper(Path.Combine(instanceDir, "mods", Mo2InstanceWriter.SafeName(mod.InstallAs, "installAs")), mod.InstallAs); }
                catch (Exception wex) { InstallLog.Warn("mo2", $"{mod.InstallAs}: wrapper strip failed: {wex.Message}"); }
                InstallLog.Info("install", $"already installed: {mod.InstallAs}");
                Interlocked.Increment(ref done);
                return;
            }
            // Materialized into the mods folder before (under this or a legacy
            // folder name)? Never extract twice — applies on fresh starts too.
            // FOMOD mods are exempt when the existing folder is a raw dump
            // (unresolved variant trees) — re-materialize them properly.
            foreach (var name in NamesForSkip(mod))
            {
                var modDir = Path.Combine(instanceDir, "mods", Mo2InstanceWriter.SafeName(name, "installAs"));
                if (name != mod.InstallAs && !Directory.Exists(modDir)) continue;
                if (Directory.Exists(modDir) && Directory.EnumerateFileSystemEntries(modDir, "*", SearchOption.AllDirectories).Any())
                {
                    bool rawDump = false;   // a FOMOD folder is a raw dump only if it shows the option-folder signature below
                    if (isFomod)
                    {
                        try
                        {
                            if (Directory.EnumerateDirectories(modDir).Any(d =>
                                    System.Text.RegularExpressions.Regex.IsMatch(
                                        Path.GetFileName(d), @"^\d{1,3}(\b|\s|[-_.])")))
                                rawDump = true;
                        }
                        catch { }
                    }
                    if (rawDump)
                    {
                        InstallLog.Warn("fomod",
                            $"{name}: raw installer dump from an earlier build — deleting and re-materializing with the collection's option selections");
                        try { DeleteTree(modDir); }
                        catch (Exception dex)
                        {
                            InstallLog.Warn("fomod",
                                $"{name}: raw-dump delete failed ({dex.Message}) — re-materializing anyway");
                        }
                        continue;
                    }
                    InstallLog.Info("install", $"already installed on disk: {name}");
                    if (mod.HashesJson != null || mod.PatchesJson != null)
                    {
                        var vr = CollectionVerifier.Verify(modDir, mod);
                        if (!vr.Ok)
                        {
                            foreach (var mm in vr.Mismatches)
                                InstallLog.Warn("verify", $"{name}: {mm}");
                            if (!isFomod && File.Exists(archivePath))
                            {
                                InstallLog.Warn("verify",
                                    $"{name}: content does not match the collection's hashes/patches — deleting and re-materializing from the archive");
                                try { DeleteTree(modDir); }
                                catch (Exception dex)
                                    { InstallLog.Warn("verify", $"{name}: delete failed ({dex.Message}) — keeping existing install"); }
                                continue;
                            }
                            InstallLog.Warn("verify", $"{name}: archive unavailable — cannot re-materialize");
                        }
                    }
                    // Installs from an earlier build keep tool mods in mods/ — move them to <instance>/Tools now.
                    if (!rootFolderMods.Contains(mod.InstallAs))
                        try { Mo2InstanceWriter.RelocateToolMod(instanceDir, mod.InstallAs); }
                        catch (Exception tex) { InstallLog.Warn("mo2", $"{mod.InstallAs}: tool relocation failed: {tex.Message}"); }
                    lock (stateLock)
                        resume = resume with { DoneInstalls = [.. resume.DoneInstalls, mod.InstallAs] };
                    SaveResume();
                    Interlocked.Increment(ref done);
                    return;
                }
            }
            double exSec; long exBytes;
            // F4SE and other "dinput" mods: game-folder files go to mods/<n>/Root/ (Root Builder
            // copies them into the game at launch), Data content stays an ordinary mod.
            if (rootFolderMods.Contains(mod.InstallAs))
                (exSec, exBytes) = Mo2InstanceWriter.MaterializeRootMod(instanceDir, mod.InstallAs, archivePath);
            else if (isFomod)
                (exSec, exBytes) = Mo2InstanceWriter.MaterializeFomod(instanceDir, mod.InstallAs, archivePath, mod.ChoicesJson!);
            else
                (exSec, exBytes) = Mo2InstanceWriter.Materialize(instanceDir, mod.InstallAs, archivePath);
            var exSpeed = exBytes / 1_000_000.0 / Math.Max(exSec, 0.001);
            // Collection ground truth: per-file md5s / plugin CRC32s (warn-only).
            if (mod.HashesJson != null || mod.PatchesJson != null)
            {
                var modDir = Path.Combine(instanceDir, "mods", Mo2InstanceWriter.SafeName(mod.InstallAs, "installAs"));
                var vr = CollectionVerifier.Verify(modDir, mod);
                if (!vr.Ok)
                    foreach (var mm in vr.Mismatches)
                        InstallLog.Warn("verify", $"{mod.InstallAs}: {mm}");
                else
                    InstallLog.Info("verify", $"{mod.InstallAs}: {vr.Checked} hashes/CRCs match the collection");
            }
            // Tools go to <instance>/Tools instead of the mod list (after hash-guided layout repair above).
            if (!rootFolderMods.Contains(mod.InstallAs))
                try { Mo2InstanceWriter.RelocateToolMod(instanceDir, mod.InstallAs); }
                catch (Exception tex) { InstallLog.Warn("mo2", $"{mod.InstallAs}: tool relocation failed: {tex.Message}"); }
            var d1 = Interlocked.Increment(ref done);
            progress?.Report(new(d1, total, "install", mod.InstallAs,
                $"extracted {exBytes / 1_000_000.0:F1} MB @ {exSpeed:F0} MB/s"));
            lock (stateLock)
                resume = resume with { DoneInstalls = [.. resume.DoneInstalls, mod.InstallAs] };
            SaveResume();
        });

        progress?.Report(new(Volatile.Read(ref done), total, "finalize", "writing modlist.txt / plugins.txt"));
        // MO2 holds profile state in memory and rewrites modlist.txt/plugins.txt
        // from it on exit — an open MO2 would silently revert the profile.
        if (Process.GetProcesses().Any(p => p.ProcessName.StartsWith("ModOrganizer", StringComparison.OrdinalIgnoreCase)))
            InstallLog.Warn("mo2",
                "Mod Organizer 2 is running and will overwrite modlist.txt/plugins.txt with its stale state. Close MO2 and re-run the install finalize (or restart the install) afterwards.");
        var optionalNames = plan.InstallOrder
            .Where(t => t.Mod.IsOptional && !includeOptional).Select(t => t.Mod.InstallAs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        optionalNames.UnionWith(rootMods);
        optionalNames.UnionWith(Mo2InstanceWriter.InstalledTools(instanceDir));   // live in <instance>/Tools, not the mod list
        Mo2InstanceWriter.WriteLoadFiles(instanceDir, plan, optionalNames, effectiveGamePath);
        // Tools are registered last: their executables live in mod folders and the game root (F4SE), which exist only now.
        Mo2InstanceWriter.WriteCustomExecutables(instanceDir, effectiveGamePath, manifest.Tools);
        Mo2InstanceWriter.PointF4seAtRootMod(instanceDir, effectiveGamePath);
        InstallState.ClearResume();
        InstallLog.Info("done", $"install complete: {done}/{total} mods → {instanceDir}");
        progress?.Report(new(done, total, "done", "install complete"));
    }

    private const int DownloadThreads = 6;

    /// <summary>Some archives failed to download. The run stopped at the end of
    /// the download step; the user can retry and only the failures re-fetch.</summary>
    public sealed class DownloadIncompleteException(Dictionary<string, string> failures)
        : Exception($"{failures.Count} download(s) failed")
    {
        public IReadOnlyDictionary<string, string> Failures { get; } = failures;
    }
    private const int ExtractThreads = 4;

    /// <summary>Folder names that count as "already installed": the current
    /// install name plus legacy prefixes from older builds.</summary>
    private static IEnumerable<string> NamesForSkip(ModEntry mod)
    {
        yield return mod.InstallAs;
        foreach (var legacy in mod.LegacyInstallAs) yield return legacy;
    }

    /// <summary>
    /// Retries transient network failures (dropped connections, timeouts,
    /// socket resets) with increasing backoff. 3 retries, then the error
    /// propagates. Cancellation is never retried.
    /// </summary>
    private static Task RetryAsync(Func<Task> op, string what, CancellationToken ct)
        => RetryAsync<object?>(async () => { await op(); return null; }, what, ct);

    private static async Task<T> RetryAsync<T>(Func<Task<T>> op, string what, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await op(); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt <= 3)
            {
                await BackoffAsync(attempt, what, ct);
            }
            catch (Exception ex) when (attempt <= 3 && ex is IOException or HttpRequestException or SocketException)
            {
                await BackoffAsync(attempt, what, ct);
            }
        }
    }

    private static async Task BackoffAsync(int attempt, string what, CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(attempt * 4);
        InstallLog.Warn("download", $"{what} failed — retry {attempt}/3 in {wait.TotalSeconds}s");
        await Task.Delay(wait, ct);
    }

    /// <summary>True when Fallout4.exe is newer than the 1.10.163 runtime.</summary>
    private static bool GameVersionAbove163(string gamePath)
    {
        var exe = Path.Combine(gamePath, "Fallout4.exe");
        if (!File.Exists(exe))
        {
            InstallLog.Warn("game", $"no Fallout4.exe found at {gamePath}");
            return false;
        }
        var info = FileVersionInfo.GetVersionInfo(exe);
        var parts = (info.FileVersion ?? "").Split('.').Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
        var (maj, min, patch) = parts.Length >= 3 ? (parts[0], parts[1], parts[2]) : (0, 0, 0);
        InstallLog.Info("game", $"game version {info.FileVersion ?? "unknown"} ({gamePath})");
        return maj > 1 || min > 10 || patch > 163;
    }

    /// <summary>
    /// Downgrades the game folder to 1.10.163 using the collection's Fallout 4 Downgrader
    /// (Nexus 81630). Its Assets are applied natively (GameDowngrader) because FO4Down.exe
    /// locates the game by itself and would not target the private Stock Game copy. Only the
    /// executables are replaced; the next-gen data files stay, and the collection's Backported
    /// Archive2 Support System plugin lets the old runtime read them.
    /// Runs on a worker thread while the rest of the archives download.
    /// </summary>
    private static void RunDowngrader(string gamePath, string downgraderArchive,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(0, 1, "game", "downgrading game to 1.10.163"));
        var scratch = gamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + ".downgrader-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(scratch);
            SevenZip.ExtractMatching(downgraderArchive, "Assets/*", scratch);
            var assets = Path.Combine(scratch, "Assets");
            if (!Directory.Exists(Path.Combine(assets, "Patch")))
                throw new InvalidOperationException("the Downgrader archive has no Assets/Patch folder — cannot downgrade the next-gen game");
            var n = GameDowngrader.Patch(gamePath, assets, ct: ct);
            InstallLog.Info("game", $"downgrader replaced {n} file(s) in {gamePath}");
        }
        finally
        {
            try { DeleteTree(scratch); } catch { }
        }
        if (GameVersionAbove163(gamePath))
            throw new InvalidOperationException("Fallout4.exe is still newer than 1.10.163 after the downgrade — see the 'downgrade' lines in the install log");
        InstallLog.Info("game", $"downgrade complete, new version: {FileVersionInfo.GetVersionInfo(Path.Combine(gamePath, "Fallout4.exe")).FileVersion ?? "unknown"}");
    }

    /// <summary>
    /// Makes a private copy of the Steam game inside the instance ("Stock Game").
    /// Copies everything so MO2 + F4SE work offline from the copy; version prep
    /// (downgrade to 1.10.163) hooks in here afterwards.
    /// </summary>
    /// <summary>
    /// Copies a clean, vanilla-only Stock Game: engine files at the game root
    /// (whitelist — the source may carry CK, ENB, xSE logs, user configs) and
    /// the vanilla Data payload (BA2/ESM/CDX/CSG + videos). Loose mod content
    /// in the source Data folder (Scripts, MCM, F4SE, user esps...) is left out;
    /// USVFS serves all of that from the mods folder at run time.
    /// </summary>
    // Exact root files from the Steam depots (steamdb.info depots 377161/377162/377163,
    // Fallout 4 app 377160). Anything else at the game root is mod residue and must
    // never reach the Stock Game folder.
    private static readonly string[] RootInclude =
    [
        "Fallout4.exe", "Fallout4Launcher.exe", "bink2w64.dll", "steam_api64.dll",
        "msvcp110.dll", "msvcr110.dll", "cudart64_75.dll", "nvToolsExt64_1.dll",
        "flexExtRelease_x64.dll", "flexRelease_x64.dll", "GFSDK_GodraysLib.x64.dll",
        "GFSDK_SSAO_D3D11.win64.dll", "Fallout4.ccc", "Fallout4IDs.ccc",
        "Fallout4_Default.ini", "High.ini", "Low.ini", "Medium.ini", "Ultra.ini",
        "nvdebris.txt",
    ];

    // Data: vanilla names only — base archives ("Fallout4 - X.ba2", Fallout4.esm/.cdx/csg),
    // DLC archives (DLCrobot/DLCCoast/DLCPitt/DLCNukaWorld/DLCworkshopNN, English depots
    // 435880-435885 style) and Creation Club payloads (ccXXXXFO...*.ba2/.esl). Loose mod
    // archives/esp/mcm/f4se/scripts folders can never match.
    private static readonly System.Text.RegularExpressions.Regex[] DataInclude =
    [
        new(@"^Fallout4(\s-\s.+)?\.(ba2|esm|csg|cdx)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        new(@"^DLC[A-Za-z0-9]+(\s-\s.+)?\.(ba2|esm|csg|cdx)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        new(@"^cc[A-Z].*\.(ba2|esl)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
    ];

    private static bool DataAllowed(string name) =>
        DataInclude.Any(rx => rx.IsMatch(Path.GetFileName(name)));

    private static void CopyGame(string srcDir, string destDir, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var files = new List<string>();
        foreach (var f in Directory.EnumerateFiles(srcDir, "*", SearchOption.TopDirectoryOnly))
            if (RootInclude.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                files.Add(f);
        var dataDir = Path.Combine(srcDir, "Data");
        if (Directory.Exists(dataDir))
        {
            files.AddRange(Directory.EnumerateFiles(dataDir, "*", SearchOption.TopDirectoryOnly)
                .Where(DataAllowed));
            var skipped = Directory.EnumerateFiles(dataDir, "*", SearchOption.TopDirectoryOnly)
                .Count(f => !DataAllowed(f) &&
                    (f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)));
            if (skipped > 0)
                InstallLog.Warn("game", $"stock game copy: skipped {skipped} non-vanilla ba2/esm in Data (not matching the Steam depot whitelist)");
            var videoDir = Path.Combine(dataDir, "Video");
            if (Directory.Exists(videoDir))
                files.AddRange(Directory.EnumerateFiles(videoDir, "*.bk2", SearchOption.TopDirectoryOnly));
        }
        var prefs = Path.Combine(srcDir, "Fallout4", "Fallout4Prefs.ini");
        if (File.Exists(prefs))
            files.Add(prefs);
        if (!files.Any(f => Path.GetFileName(f).Equals("Fallout4.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"stock game copy: no Fallout4.exe at '{srcDir}' — source install missing or unreadable");
        progress?.Report(new(0, files.Count, "game", "copying game files into the Stock Game folder"));
        InstallLog.Info("game", $"stock game copy: {srcDir} → {destDir} ({files.Count} files, vanilla payload only)");
        Directory.CreateDirectory(destDir);
        int i = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(srcDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // A read-only dest from an earlier copy makes File.Copy(overwrite)
            // throw "Access to the path ..." — clear the attribute and retry.
            try { File.Copy(file, dest, overwrite: true); }
            catch (IOException) when (File.Exists(dest))
            {
                File.SetAttributes(dest, FileAttributes.Normal);
                File.Copy(file, dest, overwrite: true);
            }
            i++;
            if (i % 500 == 0)
                progress?.Report(new(i, files.Count, "game", $"copying game files ({i * 100 / files.Count}%)"));
        }
        InstallLog.Info("game", $"stock game copy finished: {i} files");
    }

/// <summary>Thin wrapper adding per-file progress and logging to the API client.</summary>
    private sealed class NexusApiProxy : IDisposable
    {
        private readonly NexusApiClient _inner;
        private readonly IProgress<ProgressInfo>? _progress;
        private readonly CancellationToken _ct;

        public NexusApiProxy(string key, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            _inner = new NexusApiClient(key);
            _progress = progress;
            _ct = ct;
        }

        public Task<NexusApiClient.NexusUser> ValidateUserAsync() => _inner.ValidateUserAsync(_ct);

        public Task<string?> GetDownloadLinkAsync(string domain, int modId, long fileId)
            => _inner.GetFileDownloadLinkAsync(domain, modId, fileId, _ct);

        public async Task DownloadAsync(string url, string destPath, string label, long expectedBytes)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var last = -1;
            var prog = new Progress<double>(p =>
            {
                var pc = (int)p;
                if (pc != last)
                {
                    last = pc;
                    var mbDone = expectedBytes * p / 100.0 / 1_000_000.0;
                    var speed = mbDone / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                    _progress?.Report(new(0, 0, "download", $"{label} — {pc}%",
                        $"{mbDone:F1} MB @ {speed:F0} MB/s"));
                }
            });
            await _inner.DownloadFileAsync(url, destPath, progress: prog, ct: _ct);
            InstallLog.Info("download", $"fetched {Path.GetFileName(destPath)}");
        }

        public void Dispose() => _inner.Dispose();
    }
}
