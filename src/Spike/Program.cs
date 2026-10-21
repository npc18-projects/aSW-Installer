using System.Text.Json;
using AStoryWealth.NexusApi;

// Spike: proves the Nexus leg end to end.
//   ASW_NEXUS_KEY must be set (premium key for direct CDN downloads), OR
//   run with arg "sso" to test the browser-based SSO authorization flow.
//   Args: "sso" | [collectionSlug] [modId fileId]
var key = Environment.GetEnvironmentVariable("ASW_NEXUS_KEY");

if (args.Length > 0 && args[0] == "sso")
{
    Console.WriteLine("SSO flow: a browser page will open for Nexus authorization.");
    Console.WriteLine("(Opening in your default browser — approve access there.)");
    using var sso = new NexusSsoClient();
    sso.OnBrowserUrl += url =>
    {
        Console.WriteLine($"\nOpen this URL to authorize:\n  {url}\n");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    };
    sso.OnError += e => Console.WriteLine($"[sso error] {e}");
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var receivedKey = await sso.AuthorizeAsync("astorywealth-installer", cts.Token);
    Console.WriteLine($"API key received ({receivedKey.Length} chars, never printed). Validating...");
    using var api2 = new NexusApiClient(receivedKey);
    var u = await api2.ValidateUserAsync();
    Console.WriteLine($"OK — authorized as {u.Name}, premium={u.IsPremium}");
    return 0;
}

if (args.Length > 0 && args[0] == "install-test")
{
    // install-test <collection.json> <addonId> <instanceDir>
    // Full single-mod leg: validate -> map -> resolve -> scaffold ->
    // download smallest archive -> verify md5+size -> extract -> load files.
    var key2 = key!;
    using var itTApi = new NexusApiClient(key2);
    var itTUser = await itTApi.ValidateUserAsync();
    AStoryWealth.Core.InstallLog.Info("auth", $"user {itTUser.Name}, premium={itTUser.IsPremium}");

    var manifest = AStoryWealth.Core.VortexCollectionMapper.Map(args[1], args[2]);
    var resolver2 = new AStoryWealth.Core.ManifestResolver();
    var plan2 = resolver2.Resolve([manifest]);
    AStoryWealth.Core.InstallLog.Info("plan", $"{manifest.Mods.Count} mods resolved, {plan2.Issues.Count} issues");

    var dir = args[3];
    AStoryWealth.Core.Mo2InstanceWriter.Scaffold(dir, "D:\\Games\\Steam\\steamapps\\common\\Fallout 4");

    // smallest archive (5.5 MB Enclave Gear Pack is smallest entry)
    var smallest = manifest.Mods.OrderBy(m => m.Nexus?.FileSize ?? long.MaxValue).First();
    var fileName = "archive.7z";
    var archivePath = AStoryWealth.Core.Mo2InstanceWriter.ArchivePath(dir, smallest, fileName);
    var uri = await itTApi.GetFileDownloadLinkAsync("fallout4", smallest.Nexus!.ModId, smallest.Nexus.FileId);
    if (uri == null) { AStoryWealth.Core.InstallLog.Error("download", "no CDN link (non-premium key?)"); return 4; }
    await itTApi.DownloadFileAsync(uri, archivePath);
    AStoryWealth.Core.InstallLog.Info("download", $"fetched mod {smallest.Nexus.ModId} file {smallest.Nexus.FileId} -> {archivePath}");

    var (ok, detail) = await AStoryWealth.Core.ManifestResolver.VerifyArchiveAsync(archivePath, smallest);
    AStoryWealth.Core.InstallLog.Info("verify", $"archive check: {detail}");
    if (!ok) return 5;

    AStoryWealth.Core.Mo2InstanceWriter.Materialize(dir, smallest.InstallAs, archivePath);
    AStoryWealth.Core.Mo2InstanceWriter.WriteLoadFiles(dir, plan2);
    AStoryWealth.Core.InstallLog.Info("done", $"instance ready at {dir} — open ModOrganizer.ini there to inspect");
    return 0;
}

if (args.Length > 1 && args[0] == "convert")
{
    // convert <collection.json path> <addonId> [outPath]
    var manifest = AStoryWealth.Core.VortexCollectionMapper.Map(args[1], args[2]);
    var resolver = new AStoryWealth.Core.ManifestResolver();
    var plan = resolver.Resolve([manifest]);
    Console.WriteLine($"Mapped {manifest.Mods.Count} mods, {manifest.Plugins.Count} plugins from '{manifest.Name}'");
    Console.WriteLine($"Resolver: {plan.Issues.Count} issues ({plan.Issues.Count(i => i.Severity == "error")} errors)");
    foreach (var i in plan.Issues.Take(5)) Console.WriteLine($"  [{i.Severity}] {i.Message}");
    if (args.Length > 3)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(args[3], json);
        Console.WriteLine($"Wrote {args[3]}");
    }
    return plan.HasErrors ? 3 : 0;
}

if (string.IsNullOrEmpty(key))
{
    Console.WriteLine("Set ASW_NEXUS_KEY (Nexus personal API key) to run the spike, or run with arg 'sso'.");
    return 1;
}

var slug = args.Length > 0 ? args[0] : throw new ArgumentException("usage: spike <slug> [modId fileId]");

using var api = new NexusApiClient(key);

Console.WriteLine("[0/2] Validating key...");
var user = await api.ValidateUserAsync();
var mode = user.IsPremium ? "premium (direct CDN)" : "free (browser-assist)";
Console.WriteLine($"      OK — {user.Name}, premium={user.IsPremium} → download mode: {mode}");

Console.WriteLine($"[1/2] Fetching latest revision of collection '{slug}' via GraphQL...");
var revision = await api.GetCollectionRevisionAsync(slug);
var revNum = revision.GetProperty("revisionNumber").GetInt32();
var name = revision.GetProperty("collection").GetProperty("name").GetString();
var mods = revision.GetProperty("modFiles");
Console.WriteLine($"      OK — {name}, revision {revNum}, {revision.GetProperty("modCount").GetInt32()} mods, {mods.GetArrayLength()} file entries");

if (args.Length > 2 && int.TryParse(args[1], out var modId) && long.TryParse(args[2], out var fileId))
{
    Console.WriteLine($"[2/2] Download test: mod {modId} file {fileId}...");
    var link = await api.GetFileDownloadLinkAsync("fallout4", modId, fileId);
    if (link is null)
    {
        Console.WriteLine("      403 — key is not premium. Browser-assist mode would engage here.");
        return 2;
    }
    Console.WriteLine("      Got CDN link. Downloading...");
    var dest = Path.Combine(Path.GetTempPath(), "asw-spike", $"{modId}-{fileId}.download");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var hash = await api.DownloadFileAsync(link, dest, progress: new Progress<double>(p => Console.Write($"\r      {p:F1}%")));
    sw.Stop();
    Console.WriteLine($"\n      OK — {new FileInfo(dest).Length:N0} bytes in {sw.Elapsed.TotalSeconds:F1}s, sha256={hash[..16]}...");
}
else
{
    Console.WriteLine("[2/2] No test file supplied. First three entries:");
    int i = 0;
    foreach (var m in mods.EnumerateArray())
    {
        var f = m.GetProperty("file");
        Console.WriteLine($"      - mod {f.GetProperty("modId").GetInt32()} file {f.GetProperty("fileId").GetInt64()}: {f.GetProperty("mod").GetProperty("name").GetString()}");
        if (++i >= 3) break;
    }
}
return 0;
