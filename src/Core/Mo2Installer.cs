using System.Security.Cryptography;

namespace AStoryWealth.Core;

/// <summary>
/// Installs Mod Organizer 2 and Root Builder into the instance. Both are PINNED to a
/// specific release: a new upstream version can change behaviour (or break) between the
/// day the list was validated and the day someone installs it. Bumping a pin is a
/// deliberate code change made after testing the new release.
///
/// Each archive is downloaded once into %LOCALAPPDATA%\AStoryWealth\Mo2 and verified by
/// size and, when known, SHA-256. When the pin carries no published hash, the hash of the
/// first verified download is recorded next to the archive and checked on every reuse.
/// </summary>
public static class Mo2Installer
{
    /// <summary>A fixed upstream release asset.</summary>
    public sealed record Pin(string Name, string Url, long Size, string? Sha256);

    /// <summary>Mod Organizer 2 v2.5.2 (github.com/ModOrganizer2/modorganizer, released 2024-08-04).
    /// GitHub publishes no digest for this asset; size is pinned and the hash is recorded on first download.</summary>
    public static readonly Pin Mo2 = new(
        "Mod.Organizer-2.5.2.7z",
        "https://github.com/ModOrganizer2/modorganizer/releases/download/v2.5.2/Mod.Organizer-2.5.2.7z",
        149_660_212, null);

    /// <summary>Root Builder 5.1.1 (github.com/Kezyma/ModOrganizer-Plugins, 'rootbuilder' release);
    /// SHA-256 as published by GitHub for the asset.</summary>
    public static readonly Pin RootBuilder = new(
        "rootbuilder.5.1.1.zip",
        "https://github.com/Kezyma/ModOrganizer-Plugins/releases/download/rootbuilder/rootbuilder.5.1.1.zip",
        957_191, "902f1edf7ccae8fa625d4ea50b8fd36a0540cda121ad584989e4b1ed977046b1");

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    static Mo2Installer()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("aSW-Installer");
    }

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AStoryWealth", "Mo2");

    public static async Task EnsureAsync(string instanceDir, IProgress<InstallRunner.ProgressInfo>? progress, CancellationToken ct)
    {
        var exe = Path.Combine(instanceDir, "ModOrganizer.exe");
        if (File.Exists(exe) && new FileInfo(exe).Length > 0)
        {
            InstallLog.Info("mo2", "Mod Organizer 2 already present in instance");
            return;
        }

        var archive = await EnsureArchiveAsync(Mo2, progress, ct);
        progress?.Report(new(0, 1, "mo2", "unpacking Mod Organizer 2"));
        SevenZip.ExtractAll(archive, instanceDir);

        if (!File.Exists(exe))
            throw new InvalidOperationException($"{Mo2.Name} did not contain ModOrganizer.exe");
        InstallLog.Info("mo2", $"installed {Mo2.Name} into {instanceDir}");
    }

    /// <summary>
    /// Installs the pinned Root Builder (MO2 python plugin) into the instance's plugins
    /// folder. A different or older Root Builder already in the instance is replaced.
    /// </summary>
    public static async Task EnsureRootBuilderAsync(string instanceDir, IProgress<InstallRunner.ProgressInfo>? progress, CancellationToken ct)
    {
        var rbDir = Path.Combine(instanceDir, "plugins", "rootbuilder");
        var versionMarker = Path.Combine(rbDir, ".asw-version");
        if (Directory.Exists(Path.Combine(rbDir, "plugin")) && File.Exists(versionMarker)
            && File.ReadAllText(versionMarker).Trim() == RootBuilder.Name)
        {
            InstallLog.Info("mo2", $"Root Builder already installed ({RootBuilder.Name})");
            return;
        }
        if (Directory.Exists(rbDir))
        {
            InstallLog.Warn("mo2", $"Root Builder in the instance is not {RootBuilder.Name} — replacing it");
            InstallRunner.DeleteTree(rbDir);
        }

        var archive = await EnsureArchiveAsync(RootBuilder, progress, ct);
        var pluginsDir = Path.Combine(instanceDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        SevenZip.ExtractAll(archive, pluginsDir); // zip root is 'rootbuilder/'
        if (!Directory.Exists(Path.Combine(rbDir, "plugin")))
            throw new InvalidOperationException($"{RootBuilder.Name} did not contain a rootbuilder plugin folder");
        File.WriteAllText(versionMarker, RootBuilder.Name);
        InstallLog.Info("mo2", $"Root Builder installed ({RootBuilder.Name}) into {pluginsDir}");
    }

    /// <summary>Returns the path of a verified local copy of the pinned asset, downloading it if needed.</summary>
    private static async Task<string> EnsureArchiveAsync(Pin pin, IProgress<InstallRunner.ProgressInfo>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        var path = Path.Combine(CacheDir, pin.Name);
        var sidecar = path + ".sha256";

        if (File.Exists(path))
        {
            var (ok, why) = await VerifyAsync(pin, path, sidecar);
            if (ok)
            {
                InstallLog.Info("mo2", $"using cached {pin.Name} ({new FileInfo(path).Length / 1_000_000.0:F1} MB, verified)");
                return path;
            }
            InstallLog.Warn("mo2", $"cached {pin.Name} failed verification ({why}) — downloading again");
            File.Delete(path);
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }

        InstallLog.Info("mo2", $"downloading {pin.Name} from {new Uri(pin.Url).Host}");
        progress?.Report(new(0, 1, "mo2", $"downloading {pin.Name}"));
        var part = path + ".part";
        try
        {
            using (var resp = await Http.GetAsync(pin.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(part);
                await src.CopyToAsync(dst, ct);
            }
            var (ok, why) = await VerifyAsync(pin, part, sidecar: null);
            if (!ok) throw new InvalidOperationException($"downloaded {pin.Name} failed verification: {why}");
            if (pin.Sha256 is null)
                File.WriteAllText(sidecar, await Sha256Async(part));   // first-use record
            File.Move(part, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
        return path;
    }

    private static async Task<(bool Ok, string Why)> VerifyAsync(Pin pin, string file, string? sidecar)
    {
        var len = new FileInfo(file).Length;
        if (len != pin.Size) return (false, $"size {len}, expected {pin.Size}");
        var expected = pin.Sha256 ?? (sidecar is not null && File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : null);
        if (expected is null) return (true, "size ok");
        var actual = await Sha256Async(file);
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? (true, "sha256 ok") : (false, $"sha256 {actual}, expected {expected}");
    }

    private static async Task<string> Sha256Async(string file)
    {
        await using var fs = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));
    }
}
