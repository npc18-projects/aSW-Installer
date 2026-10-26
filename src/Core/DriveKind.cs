using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AStoryWealth.Core;

/// <summary>
/// Tells a hard drive from an SSD, using the flag Windows keeps for each drive ("incurs a seek penalty"). A hard drive's head has
/// to move between files, so several things reading and writing at once make it much slower than doing them one at a time.
/// </summary>
public static class DriveKind
{
    private static readonly Dictionary<string, bool?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for a hard drive, false for an SSD or other flash, null when it can't be told (network share, a drive Windows gives no
    /// answer for, a path that isn't on a drive letter). Unknown is treated as "no limit" by callers.
    /// </summary>
    public static bool? IsRotational(string? path) => IsRotational(path, 0);

    private static bool? IsRotational(string? path, int depth)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsWindows() || depth > 4) return null;
        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch { return null; }
        if (root.Length < 2 || root[1] != ':') return null;   // \\server\share and the like
        lock (Cache)
            if (Cache.TryGetValue(root, out var known)) return known;
        // A drive made with "subst" (a folder shown as a drive letter) has no disk of its own: look at the folder it points to.
        bool? kind;
        if (SubstTarget(char.ToUpperInvariant(root[0])) is { } target)
            kind = target.Length == 0 ? null : IsRotational(Path.Combine(target, path.Length > root.Length ? path[root.Length..] : ""), depth + 1);
        else kind = Query(char.ToUpperInvariant(root[0]));
        lock (Cache) Cache[root] = kind;
        return kind;
    }

    /// <summary>The drive root of a path ("F:\"), for showing to the user. Empty when there isn't one.</summary>
    public static string RootOf(string? path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? "" : Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch { return ""; }
    }

    /// <summary>For a "subst" drive, the folder it stands for (empty when the target is a network path); null for an ordinary drive.</summary>
    private static string? SubstTarget(char letter)
    {
        try
        {
            var buf = new StringBuilder(1024);
            if (QueryDosDevice($"{letter}:", buf, buf.Capacity) == 0) return null;
            var target = buf.ToString().Split((char)0)[0];
            if (!target.StartsWith(@"\??\", StringComparison.Ordinal)) return null;   // \Device\HarddiskVolume3 and the like: a real volume
            target = target[4..];
            return target.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ? "" : target;
        }
        catch { return null; }
    }

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    /// <summary>Asks the volume first. Some drives only answer when asked as a physical disk, so otherwise it finds the disk(s) the volume sits on and asks those.</summary>
    private static bool? Query(char letter)
    {
        try
        {
            using var volume = CreateFile($@"\\.\{letter}:", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);   // access 0: asking about properties needs no rights
            if (volume.IsInvalid) return null;
            if (SeekPenalty(volume) is { } direct) return direct;

            var extents = new byte[8 + 24 * 16];                      // VOLUME_DISK_EXTENTS: count, then 24 bytes per extent starting with the disk number
            if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, [], 0, extents, extents.Length, out var returned, IntPtr.Zero) || returned < 32)
                return null;
            var count = Math.Min(BitConverter.ToInt32(extents, 0), 16);
            bool? result = null;
            for (var i = 0; i < count; i++)
            {
                var disk = BitConverter.ToInt32(extents, 8 + 24 * i);
                using var physical = CreateFile($@"\\.\PhysicalDrive{disk}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (physical.IsInvalid) return null;
                if (SeekPenalty(physical) is not { } rotational) return null;
                if (rotational) return true;                           // one hard drive in the set is enough
                result = false;
            }
            return result;
        }
        catch { return null; }
    }

    private static bool? SeekPenalty(SafeFileHandle device)
    {
        var query = new byte[12];                                      // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (0 = standard), 1 spare byte
        BitConverter.GetBytes(StorageDeviceSeekPenaltyProperty).CopyTo(query, 0);
        var answer = new byte[16];                                     // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, IncursSeekPenalty
        if (!DeviceIoControl(device, IoctlStorageQueryProperty, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero) || returned < 9)
            return null;
        return answer[8] != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder target, int max);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);
}

/// <summary>How many things may run at once, given which folders sit on a hard drive.</summary>
public static class ThreadLimits
{
    public sealed record Result(int Download, int Extract, List<string> Notes);

    /// <summary>
    /// Anything that reads or writes a hard drive runs on one thread: downloads write to the downloads folder; unpacking reads
    /// the archives (downloads folder, or the folder of archives being reused) and writes the mods (install folder). Nothing
    /// changes for SSDs, network drives or drives Windows can't tell about.
    /// </summary>
    public static Result Apply(int download, int extract, bool downloadsOnHdd, bool installOnHdd, bool reuseOnHdd)
    {
        var notes = new List<string>();
        if (downloadsOnHdd && download > 1) { download = 1; notes.Add("the downloads folder is on a hard drive: one download at a time"); }
        if ((downloadsOnHdd || installOnHdd || reuseOnHdd) && extract > 1)
        {
            extract = 1;
            var where = new List<string>();
            if (downloadsOnHdd) where.Add("downloads");
            if (installOnHdd) where.Add("install");
            if (reuseOnHdd) where.Add("reused archives");
            notes.Add($"a hard drive holds the {string.Join(" and ", where)} folder: one unpack at a time");
        }
        return new Result(download, extract, notes);
    }
}
