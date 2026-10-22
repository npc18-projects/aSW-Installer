using System.Text;

namespace AStoryWealth.Core;

/// <summary>Small, format-keeping edits of the game's INI files: only the one line changes, comments and order stay.</summary>
public static class IniEdit
{
    public static string? GetValue(string text, string section, string key)
    {
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith('[') && l.EndsWith(']')) { inSection = l[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection) continue;
            var eq = l.IndexOf('=');
            if (eq > 0 && l[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return l[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Sets one value, adding the key (or the whole section) when it isn't there. Line endings are kept.</summary>
    public static string SetValue(string text, string section, string key, string value)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var inSection = false;
        var sectionEnd = -1;   // index just after the last line of the wanted section
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith('[') && l.EndsWith(']'))
            {
                if (inSection) break;
                inSection = l[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                if (inSection) sectionEnd = i + 1;
                continue;
            }
            if (!inSection) continue;
            var eq = l.IndexOf('=');
            if (eq > 0 && l[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return string.Join(nl, lines);
            }
            if (l.Length > 0) sectionEnd = i + 1;
        }
        if (sectionEnd >= 0) lines.Insert(sectionEnd, $"{key}={value}");
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        return string.Join(nl, lines);
    }
}

/// <summary>The game's screen size in the profile's Fallout4Prefs.ini.</summary>
public static class GameDisplay
{
    public const string BackupSuffix = ".asw-backup";

    public static (int W, int H)? ReadResolution(string prefsPath)
    {
        if (!File.Exists(prefsPath)) return null;
        var text = File.ReadAllText(prefsPath);
        return int.TryParse(IniEdit.GetValue(text, "Display", "iSize W"), out var w) && int.TryParse(IniEdit.GetValue(text, "Display", "iSize H"), out var h) ? (w, h) : null;
    }

    /// <summary>Sets iSize W / iSize H in [Display]. The file is copied to Fallout4Prefs.ini.asw-backup the first time.</summary>
    public static bool ApplyResolution(string prefsPath, int width, int height)
    {
        if (width < 640 || height < 480 || !File.Exists(prefsPath)) return false;
        var text = File.ReadAllText(prefsPath);
        var backup = prefsPath + BackupSuffix;
        if (!File.Exists(backup)) File.Copy(prefsPath, backup);
        text = IniEdit.SetValue(text, "Display", "iSize W", width.ToString());
        text = IniEdit.SetValue(text, "Display", "iSize H", height.ToString());
        File.WriteAllText(prefsPath, text, new UTF8Encoding(false));
        return true;
    }

    public static string ProfilePrefs(string instanceDir) => Path.Combine(InstallHealth.ProfileDir(instanceDir), "fallout4prefs.ini");
}
