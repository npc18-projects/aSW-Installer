# aSW Installer — Addon Manifest Specification v0.2 (draft)

Changelog v0.1 → v0.2:
- Mods are NEXUS-ONLY: removed `url` and `builtin` sources. Every archive is
  pulled by modId/fileId from Nexus.
- Removed LodBlock (deferred; reserved for a future schemaVersion).
- Added `gameVersion` block: automated downgrade is a first-class install step.
- Added free-user browser-assist download mode alongside premium direct CDN.
- ESL flagging added as an installer capability, gated per-manifest.

## Purpose

The installer consumes two layers:

- **Layer 0 — Base collection.** The official Nexus collection ("A StoryWealth"),
  consumed via the Nexus Collections API. The manifest is the same
  `collection.json` Vortex consumes (keys: `info`, `mods`, `modRules`,
  `pluginRules`, `collectionConfig`, `tools`). The installer never edits or
  re-hosts this — it fetches the current revision at install time.
- **Layer 1 — Addon manifests.** One manifest per official team addon
  (Atmosphere, Nature, Desolation, Pickman's Passion, ...). Authored and
  versioned by the addon maintainer, resolved against the base collection at
  install time into a single MO2 profile.

Wabbajack cannot express Layer 1; that is the reason this installer exists.

## File format

`addonmanifest` — JSON, UTF-8, no BOM. One file per addon, named
`<addonid>.addonmanifest.json`. A manifest is valid against one specific
schema version; the installer must refuse manifests with an unknown
`schemaVersion` (no forward compatibility, explicit error).

## Resolution model

1. Fetch base collection manifest for the selected revision.
2. Fetch each selected addon manifest, pinned by `requires.baseRevision`
   (exact) or a compatibility rule (below).
3. Apply addons in `installPriority` order. Within an addon, apply
   `mods[]` in array order.
4. Conflicts between addons are resolved by installPriority (higher wins
   for overwrite-type rules) and are ERRORS, not silent wins, unless
   `conflicts[]` declares the pairing explicitly.
5. Emit: MO2 mod folders (or file-batch downloads), `modlist.txt`,
   `plugins.txt`, `archives.txt`, ini tweaks, and the tool list.

## Top-level fields

| Field | Type | Required | Notes |
|---|---|---|---|
| schemaVersion | int | yes | 1 for this draft |
| addonId | string | yes | stable slug, e.g. `atmosphere`. Lowercase a-z0-9-. Permanent once published. |
| name | string | yes | Display name, e.g. `A StoryWealth: Atmosphere` |
| author | string | yes | Team handle |
| version | string | yes | Semver of the ADDON, independent of base revision |
| description | string | no | |
| homepage | uri | no | Nexus mod page or GitHub |
| requires.baseCollectionId | string | yes | Nexus collection id/slug of the base list |
| requires.baseRevision | int \| string | yes | Exact collection revision this manifest was built against, or `"latest"` (installer warns) |
| requires.installerMin | string | no | Minimum installer version (for new rule kinds) |
| scope | enum | yes | `core` (installed with every profile) / `optional` (user checkbox, default off) / `recommended` (checkbox, default on) |
| installPriority | int | yes | Global order across ALL addons. Gap by 100s: Atmosphere=100, Desolation=200, Nature=300, Pickman=400 ... so third parties can slot in between without renumbering |
| mods | ModEntry[] | yes (may be empty) | In apply order |
| plugins | PluginEntry[] | no | Load-order/plugin-state directives |
| modRules | ModRule[] | no | MO2-style rules spanning addon↔base or addon↔addon |
| iniTweaks | IniTweak[] | no | Applied after base collection's tweaks |
| tools | ToolEntry[] | no | MO2-executable tools contributed by the addon |
| conflicts | Conflict[] | no | Declared pairwise resolutions |
| gameVersion | GameVersion | no | Target game/F4SE version; triggers the downgrade step when detected install differs |
| capabilities.eslFlagging | bool | no | Default false. When true, installer MAY ESL-flag this addon's own plugins per PluginEntry.esl directives |

## ModEntry

All mods come from Nexus. `source` is fixed to `"nexus"` (kept as a literal
field for forward compatibility).

```
{
  "source": "nexus",
  "nexus": { "modId": 12345, "fileId": 67890, "game": "fallout4" },
  "hash": "sha256:...",                    // optional; verified when present. Nexus API exposes author-provided MD5 for some files — installer logs a warn when a file has no hash on record.
  "installAs": "Atmosphere Core",          // MO2 mod folder name. MUST be prefixed: "aSW <AddonName> - <what>"
  "mkdir": ["MCM\\Settings\\..."],         // extra empty dirs / loose files staged into the mod folder
  "choose": [ { "fileId": ..., "hash": ..., "label": "..." } ],  // optional: installer offers a choice among variants; hash pins each
  "patchTarget": {                          // when this entry is a compat patch...
    "against": ["modName@nexus", "modName@addonId"],
    "kind": "esp-overwrite"                 // installer wires it as an MO2 overwrite-of-last-resort rule
  }
}
```

Rules:
- `installAs` uniqueness across the resolved profile is enforced.
- A `ModEntry` with `choose[]` is user-facing only when `scope != core`.

## PluginEntry

Mirrors Vortex `pluginRules` semantics but scoped to the addon:

```
{
  "plugin": "Npc18_Whatever.esp",
  "enabled": true,                          // false = install but keep disabled
  "loadAfter": ["SomeBase.esp"],            // explicit LO constraints written into loadorder via LOOT masterlist supplement
  "esl": "as-is" | "force" | "never"        // "force"/"never" require explicit author justification in a comment field
}
```

- The installer runs LOOT after materializing plugins, then applies
  `loadAfter` as hard overrides.

### ESL flagging (installer capability)

The installer CAN apply/strip the ESL flag on installed plugins, but only
under these gates, because flagging a plugin that other plugins reference by
FormID is destructive:

1. The owning addon manifest sets `capabilities.eslFlagging: true`.
2. Only the addon's OWN plugins may be flagged (`esl: force|never` entries
   referencing plugins from other addons or the base list are validation
   errors).
3. `esl: force` runs the standard safety checks first: no FormID ≥ 0x800
   conflicts with records referenced by other plugins, plugin is a light
   candidate (small master count / no large FormID ranges). On failure the
   flag step aborts for that plugin with `error`.
4. The base collection's plugins are never touched — flagging the base list
   is not this installer's job (Vortex/aSW already manage that).
5. `esl: never` strips a flag if present (also gated to own plugins).

ESL flagging ships as an OPTIONAL install step (checkbox, default off) even
when all manifests declare the capability.

## IniTweak

```
{ "file": "Fallout4Custom.ini", "section": "Archive", "key": "sResourceDataDirsFinal", "value": "...", "action": "set" }
```
`action` ∈ set | append-csv | remove. Applied in addon installPriority order
after base collectionConfig tweaks.

## Conflict

```
{ "with": "<other addonId | base>", "resolution": "wins" | "loses" | "incompatible",
  "note": "human-readable" }
```
`incompatible` = installer blocks the combination with the note.

## GameVersion (automated downgrade)

The collection targets a pinned runtime. The installer reads the detected
game version (Fallout4.exe product version) and, when it differs from the
target, runs the downgrade BEFORE any mod is materialized.

```
"gameVersion": {
  "target": "1.10.163",
  "f4se": "0.6.23",                 // informational; F4SE version matched to target runtime
  "method": "depot" | "patch-pack", // depot = Steam depotdownload of pre-update depots (cleanest, no third-party files); patch-pack = community downgrade archive fetched from Nexus by modId/fileId
  "patchPack": { "modId": ..., "fileId": ... }   // required when method=patch-pack
}
```

Rules:
- Downgrade runs before Stock Game assembly and before any download of mods
  (so downloads never sit idle behind a version decision... except the patch
  pack itself, which downloads first).
- Per-file checksums (exe/dlls and version-matched BA2s) are verified after
  the swap; a failed checksum aborts with the offending file listed.
- If the install is ALREADY at or below target, the step is skipped with a
  log line. Upgrades are never performed silently — explicit user consent.
- The base collection manifest pins its own gameVersion expectation in
  `collectionConfig`; a mismatch between addon manifests' targets and the
  base's is a validation error.

## Download modes (premium and free)

- **Premium (direct CDN):** API key → `/files/:id/link.json` → resumable,
  chunked, parallel download with hash verification. Default when a valid
  premium key is present.
- **Free (browser-assist):** the installer cannot fetch CDN links, so it
  queues downloads in the user's browser (Nexus allows this flow) and then
  WATCHES the user's Vortex/other download directory for completed archives,
  verifying each against manifest hash/size and importing into the package
  store. The installer must show a live checklist (file → state: waiting /
  detected / verified / wrong-file) and accept manual "point me at the
  archive" selection as fallback. Install proceeds only when every required
  archive is verified.

## Validation levels

- `error` blocks install: unknown schemaVersion, installAs collision,
  undeclared addon↔addon override, baseRevision mismatch, gameVersion
  target mismatch with the base collection, esl directive on a non-owned
  plugin.
- `warn` proceeds with log: baseRevision `"latest"`, `choose[]` in a core
  addon, loadAfter referencing a plugin not present in the resolved profile.
