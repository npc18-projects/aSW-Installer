using System.Text.Json;
using System.Text.Json.Serialization;

namespace AStoryWealth.Core;

// ---------- Addon manifest model (spec v0.2) ----------

public sealed class AddonManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("addonId")] public string AddonId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("requires")] public Requires Requires { get; set; } = new();
    [JsonPropertyName("scope")] public AddonScope Scope { get; set; } = AddonScope.Optional;
    [JsonPropertyName("installPriority")] public int InstallPriority { get; set; }
    [JsonPropertyName("capabilities")] public Capabilities Capabilities { get; set; } = new();
    [JsonPropertyName("gameVersion")] public GameVersion? GameVersion { get; set; }
    [JsonPropertyName("mods")] public List<ModEntry> Mods { get; set; } = [];
    [JsonPropertyName("plugins")] public List<PluginEntry> Plugins { get; set; } = [];
    [JsonPropertyName("iniTweaks")] public List<IniTweak> IniTweaks { get; set; } = [];
    [JsonPropertyName("tools")] public List<ToolEntry> Tools { get; set; } = [];
    [JsonPropertyName("conflicts")] public List<Conflict> Conflicts { get; set; } = [];
    /// <summary>Vortex collection modRules (before/after/conflicts) — drives the
    /// MO2 priority (modlist.txt) ordering in WriteLoadFiles.</summary>
    [JsonPropertyName("modRules")] public List<ModRule>? ModRules { get; set; }
    /// <summary>Raw Vortex collection "pluginRules" JSON (load-order constraints), kept verbatim.</summary>
    [JsonPropertyName("pluginRulesJson")] public string? PluginRulesJson { get; set; }
}

/// <summary>Vortex collection modRule: source is the mod DOING something,
/// reference the mod it acts on. before/after are ordering relations,
/// conflicts a version-incompatibility declaration.</summary>
public sealed class ModRule
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("source")] public ModRuleRef? Source { get; set; }
    [JsonPropertyName("reference")] public ModRuleRef? Reference { get; set; }
}

public sealed class ModRuleRef
{
    [JsonPropertyName("fileMD5")] public string? FileMd5 { get; set; }
    [JsonPropertyName("logicalFileName")] public string? LogicalFileName { get; set; }
    [JsonPropertyName("fileExpression")] public string? FileExpression { get; set; }
    [JsonPropertyName("idHint")] public string? IdHint { get; set; }
    [JsonPropertyName("versionMatch")] public string? VersionMatch { get; set; }
}

public sealed class Requires
{
    [JsonPropertyName("baseCollectionId")] public string BaseCollectionId { get; set; } = "";
    /// <summary>Exact revision, or the string "latest" (installer warns).</summary>
    [JsonPropertyName("baseRevision")] public JsonElement BaseRevision { get; set; }
    [JsonPropertyName("installerMin")] public string? InstallerMin { get; set; }

    public bool IsLatest => BaseRevision.ValueKind == JsonValueKind.String;
    public int? RevisionNumber => BaseRevision.ValueKind == JsonValueKind.Number
        ? BaseRevision.GetInt32() : null;
}

[JsonConverter(typeof(JsonStringEnumConverter<AddonScope>))]
public enum AddonScope { Core, Optional, Recommended }

public sealed class Capabilities
{
    [JsonPropertyName("eslFlagging")] public bool EslFlagging { get; set; }
}

public sealed class GameVersion
{
    [JsonPropertyName("target")] public string Target { get; set; } = "1.10.163";
    [JsonPropertyName("f4se")] public string? F4Se { get; set; }
    [JsonPropertyName("method")] public DowngradeMethod Method { get; set; } = DowngradeMethod.Depot;
    [JsonPropertyName("patchPack")] public NexusFileRef? PatchPack { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DowngradeMethod>))]
public enum DowngradeMethod { Depot, PatchPack }

public sealed class NexusFileRef
{
    [JsonPropertyName("modId")] public int ModId { get; set; }
    [JsonPropertyName("fileId")] public long FileId { get; set; }
    [JsonPropertyName("game")] public string Game { get; set; } = "fallout4";
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("fileSize")] public long? FileSize { get; set; }
}

public sealed class ModEntry
{
    [JsonPropertyName("source")] public string Source { get; set; } = "nexus";
    /// <summary>Vortex collection "optional" flag — skipped installs until the
    /// Optionals tab ships; recorded so the flag survives manifest round-trips.</summary>
    [JsonPropertyName("optional")] public bool IsOptional { get; set; }
    [JsonPropertyName("nexus")] public NexusFileRef? Nexus { get; set; }
    /// <summary>sha256 hex (64 chars) — verified post-download when present.</summary>
    [JsonPropertyName("hash")] public string? Sha256 { get; set; }
    [JsonPropertyName("installAs")] public string InstallAs { get; set; } = "";

    /// <summary>Vortex install phase. Lower phases install (and so deploy) first;
    /// the baseline for deploy order before modRules are applied.</summary>
    [JsonPropertyName("phase")] public int Phase { get; set; }

    /// <summary>Vortex source.logicalFilename — what modRules reference mods by
    /// when no md5 is given.</summary>
    [JsonPropertyName("logicalFileName")] public string? LogicalFileName { get; set; }

    /// <summary>Nexus category from the collection (details.category), e.g. "Armour"; used to place the mod in a priority-order separator.</summary>
    [JsonPropertyName("category")] public string? Category { get; set; }

    /// <summary>Raw Vortex collection "choices" JSON for FOMOD mods (type "fomod");
    /// null for plain mods. Consumed by FomodResolver at materialization.</summary>
    [JsonPropertyName("choicesJson")] public string? ChoicesJson { get; set; }

    /// <summary>Raw Vortex collection "hashes" array ([{path, md5}]) — per-file
    /// md5s of the expected materialized content. Verified post-install (warn).</summary>
    [JsonPropertyName("hashesJson")] public string? HashesJson { get; set; }

    /// <summary>Raw Vortex collection "patches" object ({plugin: crc32hex}) —
    /// expected CRC32 of installed plugins. Verified post-install (warn).</summary>
    [JsonPropertyName("patchesJson")] public string? PatchesJson { get; set; }

    /// <summary>InstallAs names used by older builds — for skipping mods
    /// already materialized under a previous folder naming scheme.</summary>
    [JsonIgnore] public List<string> LegacyInstallAs { get; } = new();
    /// <summary>Vortex mod type (collection.json details.type). "dinput" = game-root layout.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("mkdir")] public List<string>? Mkdir { get; set; }
    [JsonPropertyName("instructions")] public string? Instructions { get; set; }
    [JsonPropertyName("patchTarget")] public PatchTarget? PatchTarget { get; set; }
}

public sealed class PatchTarget
{
    [JsonPropertyName("against")] public List<string> Against { get; set; } = [];
    [JsonPropertyName("kind")] public string Kind { get; set; } = "esp-overwrite";
}

public sealed class PluginEntry
{
    [JsonPropertyName("plugin")] public string Plugin { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("loadAfter")] public List<string>? LoadAfter { get; set; }
    [JsonPropertyName("esl")] public string Esl { get; set; } = "as-is";
}

public sealed class IniTweak
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("section")] public string Section { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "set";
}

public sealed class ToolEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    /// <summary>Vortex collection tools[] use "exe" (path relative to game root).</summary>
    [JsonPropertyName("exe")] public string? Exe { get; set; }
    [JsonPropertyName("args")] public List<string>? Args { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("detach")] public bool? Detach { get; set; }
    [JsonPropertyName("shell")] public bool? Shell { get; set; }
}

public sealed class Conflict
{
    [JsonPropertyName("with")] public string With { get; set; } = "";
    [JsonPropertyName("resolution")] public string Resolution { get; set; } = "wins";
    [JsonPropertyName("note")] public string? Note { get; set; }
}
