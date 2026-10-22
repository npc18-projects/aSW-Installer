using System.Diagnostics;

namespace AStoryWealth.Core;

/// <summary>Opens Mod Organizer 2, or a program registered in it (F4SE), from the installer.</summary>
public static class Mo2Launcher
{
    public static string Mo2Exe(string instanceDir) => Path.Combine(instanceDir, "ModOrganizer.exe");

    public static bool IsInstalled(string? instanceDir) => !string.IsNullOrEmpty(instanceDir) && File.Exists(Mo2Exe(instanceDir));

    /// <summary>Title of the program in MO2's list whose file is f4se_loader.exe, or null when there isn't one.</summary>
    public static string? FindF4seTitle(string instanceDir)
    {
        var ini = Path.Combine(instanceDir, "ModOrganizer.ini");
        if (!File.Exists(ini)) return null;
        return FindF4seTitleInIni(File.ReadAllLines(ini));
    }

    public static string? FindF4seTitleInIni(IEnumerable<string> lines)
    {
        var titles = new Dictionary<string, string>();
        var binaries = new Dictionary<string, string>();
        var inSection = false;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith('[')) { inSection = l.Equals("[customExecutables]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection) continue;
            var eq = l.IndexOf('=');
            var slash = l.IndexOf('\\');
            if (eq < 0 || slash < 0 || slash > eq) continue;
            var idx = l[..slash]; var key = l[(slash + 1)..eq]; var val = l[(eq + 1)..];
            if (key.Equals("title", StringComparison.OrdinalIgnoreCase)) titles[idx] = val;
            else if (key.Equals("binary", StringComparison.OrdinalIgnoreCase)) binaries[idx] = val;
        }
        foreach (var (idx, bin) in binaries)
            if (bin.Contains("f4se_loader", StringComparison.OrdinalIgnoreCase) && titles.TryGetValue(idx, out var t) && t.Length > 0)
                return t;
        return null;
    }

    /// <summary>MO2's download folder from its ini (%BASE_DIR% is the install folder), else the Downloads folder in the install.</summary>
    public static string DownloadsDir(string instanceDir)
    {
        var fallback = Path.Combine(instanceDir, "Downloads");
        var ini = Path.Combine(instanceDir, "ModOrganizer.ini");
        if (!File.Exists(ini)) return fallback;
        foreach (var l in File.ReadLines(ini))
            if (l.StartsWith("download_directory=", StringComparison.OrdinalIgnoreCase))
            {
                var v = l["download_directory=".Length..].Trim().Replace("%BASE_DIR%", instanceDir.TrimEnd('/', (char)92), StringComparison.OrdinalIgnoreCase).Replace('/', (char)92);
                return v.Length > 0 ? v : fallback;
            }
        return fallback;
    }

    public static void StartMo2(string instanceDir) =>
        Process.Start(new ProcessStartInfo(Mo2Exe(instanceDir)) { WorkingDirectory = instanceDir, UseShellExecute = true });

    /// <summary>Starts the game through F4SE inside MO2's virtual file system (the same as pressing Run for that program).</summary>
    public static void StartF4se(string instanceDir, string title)
    {
        var psi = new ProcessStartInfo(Mo2Exe(instanceDir)) { WorkingDirectory = instanceDir, UseShellExecute = false };
        psi.ArgumentList.Add($"moshortcut://:{title}");
        Process.Start(psi);
    }
}
