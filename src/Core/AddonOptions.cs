using System.Text.Json;

namespace AStoryWealth.Core;

/// <summary>
/// Curated facts about optional mods that a collection cannot express: which other add-on (or base group) an option only
/// makes sense with. The Optional tab greys such an option out until its requirement is installed. The collection's own
/// per-mod "instructions" text is shown next to every option as well, so hardware or Creation Club notes (AMD patches,
/// "needs next-gen CC content") reach the user without any check on their PC.
/// Shipped embedded; an override at &lt;app&gt;/order/addon-options.json wins, so curators can edit it without a build.
/// </summary>
public sealed class AddonOptionsFile
{
    public int Schema { get; set; } = 1;
    public string Note { get; set; } = "";
    public List<AddonOptionEntry> Entries { get; set; } = [];
    /// <summary>Friendly names for add-on slugs, for messages like "Needs the Thuggyverse add-on" before the add-on list was fetched.</summary>
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AddonOptionEntry
{
    /// <summary>The add-on the optional mod belongs to (its collection slug), or "base".</summary>
    public string Addon { get; set; } = "";
    /// <summary>Match by md5 (preferred) or by the mod's name in the collection.</summary>
    public string Md5 { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Add-on slugs that must be installed for this option to apply.</summary>
    public List<string> NeedsAddons { get; set; } = [];
    /// <summary>Base optional-group ids ("ss2") that must be installed for this option to apply.</summary>
    public List<string> NeedsGroups { get; set; } = [];
    /// <summary>Shown to the user next to the option.</summary>
    public string Note { get; set; } = "";
}

public static class AddonOptions
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const string ResourceName = "addon-options.json";

    public static List<AddonOptionEntry> Load() => LoadFile().Entries;

    /// <summary>Friendly name of an add-on slug (falls back to the slug).</summary>
    public static string NameOf(string slug) => LoadFile().Names.TryGetValue(slug, out var n) ? n : slug;

    public static AddonOptionsFile LoadFile()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "order", ResourceName);
            if (File.Exists(local))
                return JsonSerializer.Deserialize<AddonOptionsFile>(File.ReadAllText(local), Json) ?? new();
            var asm = typeof(AddonOptions).Assembly;
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.OrdinalIgnoreCase));
            if (res is null) return new();
            using var s = asm.GetManifestResourceStream(res)!;
            return JsonSerializer.Deserialize<AddonOptionsFile>(s, Json) ?? new();
        }
        catch { return new(); }
    }

    public static AddonOptionEntry? Find(IReadOnlyList<AddonOptionEntry> entries, string addon, ModEntry mod) =>
        entries.FirstOrDefault(e => e.Addon.Equals(addon, StringComparison.OrdinalIgnoreCase)
            && ((e.Md5.Length > 0 && e.Md5.Equals(mod.Nexus?.Md5, StringComparison.OrdinalIgnoreCase))
                || (e.Name.Length > 0 && e.Name.Equals(mod.DisplayName, StringComparison.OrdinalIgnoreCase))));
}
