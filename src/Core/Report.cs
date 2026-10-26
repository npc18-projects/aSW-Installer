using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace AStoryWealth.Core;

/// <summary>What the report says about this run of the app. Anything the app doesn't know is left null.</summary>
public sealed record ReportContext(string AppVersion, string? InstallDir, string? GameDir, string? DownloadsDir,
    bool? StockGame, bool? Premium, bool ResumePending, string? Secret = null, string? AccountName = null);

/// <summary>
/// One zip a tester can send when something goes wrong: the latest install logs, the mod and plugin lists, the add-on
/// record, and a short summary of the PC and the setup. Nothing is uploaded. The Nexus API key is replaced by
/// "[key removed]" wherever it shows up in the text files, and the names that identify a person (Windows user, PC name,
/// Nexus account) are hidden in the logs and the summary.
/// </summary>
public static class ReportBuilder
{
    private const long MaxLogBytes = 8L * 1024 * 1024;

    public static string Build(string zipPath, ReportContext ctx)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var who = LocalNames(ctx.AccountName);
        // Names are hidden in the logs and the summary. The mod, plugin and load-order lists hold mod names only, so they stay as they are.
        void AddText(string entry, string text, bool hideNames = true)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            var t = Redact(text, ctx.Secret);
            w.Write(hideNames ? Anonymize(t, who) : t);
        }
        void AddFile(string entry, string path, long maxBytes = MaxLogBytes, bool hideNames = true)
        {
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);   // the end of a log is the useful part
            using var r = new StreamReader(fs);
            AddText(entry, r.ReadToEnd(), hideNames);
        }

        AddText("report.txt", Summary(ctx));
        if (ctx.InstallDir is { } inst && Directory.Exists(inst))
        {
            foreach (var log in Directory.GetFiles(inst, "install-*.log").OrderByDescending(f => f).Take(3))
                AddFile("logs/" + Path.GetFileName(log), log);
            if (!Directory.GetFiles(inst, "install-*.log").Any()) AddFile("logs/install.log", Path.Combine(inst, "install.log"), 2L * 1024 * 1024);
            var profile = Path.Combine(inst, "profiles", "A StoryWealth");
            foreach (var f in new[] { "modlist.txt", "plugins.txt", "loadorder.txt" }) AddFile("profile/" + f, Path.Combine(profile, f), 4L * 1024 * 1024, hideNames: false);
            AddFile("instance/.asw-addons.json", Path.Combine(inst, AddonRegistry.FileName), 1L * 1024 * 1024);
        }
        var updateLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth", "update.log");
        AddFile("logs/update.log", updateLog, 512 * 1024);
        return zipPath;
    }

    /// <summary>What in a report points at a person: the profile folder, the Windows user name, the PC name and the Nexus account.</summary>
    public sealed record LocalIdentity(string? ProfileDir, string? UserName, string? MachineName, string? AccountName);

    public static LocalIdentity LocalNames(string? accountName) => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName, accountName);

    /// <summary>
    /// Replaces the profile folder with %USERPROFILE%, and the user name, PC name and Nexus account with &lt;user&gt;, &lt;pc&gt; and
    /// &lt;nexus-user&gt;. Names are matched as whole words, ignoring case, and only when they are 4+ characters (3+ for the Nexus
    /// account), so a short name can't mangle ordinary words.
    /// </summary>
    public static string Anonymize(string text, LocalIdentity who)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (who.ProfileDir is { Length: > 3 } pd)
            foreach (var form in new[] { pd, pd.Replace('\\', '/') }.Distinct())
                text = text.Replace(form, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = HideWord(text, who.UserName, 4, "<user>");
        text = HideWord(text, who.MachineName, 4, "<pc>");
        text = HideWord(text, who.AccountName, 3, "<nexus-user>");
        return text;
    }

    private static string HideWord(string text, string? name, int minLength, string label) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length < minLength ? text
        : System.Text.RegularExpressions.Regex.Replace(text,
            @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(name.Trim()) + @"(?![\p{L}\p{N}])",
            label, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string Redact(string text, string? secret) =>
        string.IsNullOrEmpty(secret) || secret.Length < 8 ? text : text.Replace(secret, "[key removed]", StringComparison.Ordinal);

    public static string Summary(ReportContext c)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.AppendLine(s);
        L($"aSW Installer report, {DateTime.Now:yyyy-MM-dd HH:mm}");
        L("Names that identify you (Windows user, PC name, Nexus account) are hidden in this report.");
        L($"App version:      {c.AppVersion}");
        L($"Windows:          {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        L($"Processors:       {Environment.ProcessorCount}");
        L($"Memory:           {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F1} GB");
        L($"Nexus account:    {(c.Premium is null ? "not signed in" : c.Premium == true ? "Premium" : "free")}");
        L($"Install folder:   {c.InstallDir ?? "none recorded"}");
        L($"Game folder:      {c.GameDir ?? "not found"}");
        L($"Downloads folder: {c.DownloadsDir ?? "default"}");
        L($"Game mode:        {(c.StockGame is null ? "unknown" : c.StockGame == true ? "separate copy" : "existing install")}");
        L($"Unfinished install waiting to resume: {(c.ResumePending ? "yes" : "no")}");
        foreach (var (label, path) in new[] { ("install", c.InstallDir), ("downloads", c.DownloadsDir) })
        {
            try
            {
                if (path is { Length: > 0 } && Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root)
                    L($"Free space on the {label} drive ({root}): {new DriveInfo(root).AvailableFreeSpace / 1e9:F0} GB");
            }
            catch { /* drive not readable */ }
        }
        if (c.InstallDir is { } inst && Directory.Exists(inst))
        {
            var ml = Path.Combine(inst, "profiles", "A StoryWealth", "modlist.txt");
            if (File.Exists(ml))
            {
                var lines = File.ReadAllLines(ml).Where(l => l.Length > 1 && l[0] != '#').ToList();
                var seps = lines.Count(l => l.EndsWith("_separator", StringComparison.Ordinal));
                L($"Mod list: {lines.Count - seps} mods ({lines.Count(l => l[0] == '+' && !l.EndsWith("_separator", StringComparison.Ordinal))} enabled), {seps} separators");
            }
            var reg = AddonRegistry.Load(inst);
            L($"Base groups installed: {(reg.BaseGroups is null ? "not recorded" : reg.BaseGroups.Count == 0 ? "none" : string.Join(", ", reg.BaseGroups))}");
            L($"Add-ons: {(reg.Addons.Count == 0 ? "none" : string.Join("; ", reg.Addons.Select(a => $"{a.Name.Trim()} {a.Revision}")))}");
        }
        return sb.ToString();
    }
}
