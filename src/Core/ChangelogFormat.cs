using System.Text.RegularExpressions;

namespace AStoryWealth.Core;

/// <summary>Turns the Markdown of a Nexus collection changelog into plain text for a TextBlock.</summary>
public static class ChangelogFormat
{
    public static string ToPlain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var l = raw.TrimEnd().TrimEnd('\\').TrimEnd();   // a trailing backslash is a Markdown line break
            l = Regex.Replace(l, @"\[([^\]]*)\]\([^)]*\)", "$1");              // [text](url) -> text
            l = Regex.Replace(l, @"(\*\*|__)(.+?)\1", "$2");                     // bold
            l = Regex.Replace(l, @"(?<![\w*])[*_]([^*_\s][^*_]*?)[*_](?![\w*])", "$1"); // italic
            l = l.Replace("`", "");
            var h = Regex.Match(l, @"^\s*#{1,6}\s*(.*)$");
            if (h.Success) { lines.Add(""); lines.Add(h.Groups[1].Value.Trim()); continue; }
            var b = Regex.Match(l, @"^(\s*)[-*+]\s+(.*)$");
            if (b.Success) { lines.Add($"{b.Groups[1].Value}•  {b.Groups[2].Value}"); continue; }
            lines.Add(l);
        }
        var text = Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
        // the card title already says the revision
        return Regex.Replace(text, @"^Revision \d+[ \t]*\n+", "").Trim();
    }
}
