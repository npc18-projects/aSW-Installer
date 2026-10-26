namespace AStoryWealth.Core;

/// <summary>7-Zip helpers (hidden window, same binary as Mo2InstanceWriter).</summary>
public static class SevenZip
{
    private static string? _exePath;   // only a found path is kept: a failed search is tried again

    /// <summary>
    /// 7-Zip console binary. Search order: ASW_7Z env var, a copy bundled next to
    /// the app (7z.exe / 7zz / 7za), PATH, then the usual per-OS install spots.
    /// </summary>
    public static string ExePath => _exePath ??= Locate();

    private static string Locate()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "7z.exe", "7za.exe" } : new[] { "7zz", "7z", "7za" };

        if (Environment.GetEnvironmentVariable("ASW_7Z") is { Length: > 0 } env)
        {
            if (File.Exists(env)) return env;
            throw new FileNotFoundException($"ASW_7Z points to '{env}', which does not exist.");
        }

        foreach (var n in names)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, n);
            if (File.Exists(bundled)) return bundled;
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
            foreach (var n in names)
            {
                try
                {
                    var p = Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
                if (root.Length > 0 && Path.Combine(root, "7-Zip", "7z.exe") is var p && File.Exists(p))
                    return p;
        }
        else
        {
            foreach (var p in new[] { "/usr/bin/7zz", "/usr/bin/7z", "/usr/local/bin/7zz", "/usr/local/bin/7z" })
                if (File.Exists(p)) return p;
        }

        throw new FileNotFoundException(
            "7-Zip not found. Install 7-Zip, put 7z on PATH, drop 7z.exe next to the installer, or set ASW_7Z to its full path.");
    }

    /// <summary>
    /// Runs 7-Zip with these arguments and waits for it. Output is read while it runs, so a chatty run can't fill a pipe and hang.
    /// An argument is quoted only when it needs it (7-Zip reads plain quotes, no escapes). With a <paramref name="timeout"/>
    /// the process is stopped when it takes longer.
    /// </summary>
    public static (int ExitCode, string Error, string Output) Run(IEnumerable<string> args, TimeSpan? timeout = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = string.Join(" ", args.Select(a => a.Length == 0 || a.Contains(' ') ? "\"" + a + "\"" : a)),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start 7-Zip.");
        var error = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEndAsync();
        if (timeout is { } limit && !proc.WaitForExit(limit))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("7-Zip took too long.");
        }
        proc.WaitForExit();   // also finishes reading the output
        return (proc.ExitCode, error.Result, output.Result);
    }

    /// <summary>Extracts a whole archive into <paramref name="outDir"/>; any extra switches (for example a wildcard to extract only part) go in <paramref name="extra"/>.</summary>
    public static (int ExitCode, string Error, string Output) Extract(string archive, string outDir, params string[] extra) =>
        Run(["x", archive, "-o" + outDir, .. extra, "-y", "-bso0", "-bsp0"]);

    public static void ExtractSingleFile(string archive, string entryName, string outDir)
    {
        entryName = Mo2InstanceWriter.SafeName(entryName, "archive entry");
        var (code, error, _) = Run(["e", archive, entryName, "-o" + outDir, "-y", "-bso0", "-bsp0"]);
        if (code != 0)
            throw new InvalidOperationException($"7z extract '{entryName}' failed (exit {code}): {error}");
        if (!File.Exists(Path.Combine(outDir, entryName)))
            throw new InvalidOperationException($"'{entryName}' not found inside {archive}");
    }

    /// <summary>Extracts the entries matching a wildcard (recursive, paths kept) into outDir.</summary>
    public static void ExtractMatching(string archive, string pattern, string outDir)
    {
        var (code, error, _) = Extract(archive, outDir, pattern, "-r");
        if (code != 0)
            throw new InvalidOperationException($"7z extract '{pattern}' from '{Path.GetFileName(archive)}' failed (exit {code}): {error}");
    }

    /// <summary>Extracts every entry of an archive into outDir.</summary>
    public static void ExtractAll(string archive, string outDir)
    {
        var (code, error, _) = Extract(archive, outDir);
        if (code != 0)
            throw new InvalidOperationException($"7z extract-all '{Path.GetFileName(archive)}' failed (exit {code}): {error}");
    }
}
