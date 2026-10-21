# A StoryWealth Installer

External installer for the A StoryWealth Fallout 4 collection and its official
addon collections. Targets a portable Mod Organizer 2 instance. Avalonia UI
(Windows first; Linux / Steam Deck are targets).

## What it does today

- Syncs the base collection (`CollectionSync.BaseSlug`) from Nexus and tracks revisions.
- Auto-discovers addon collections from the team's Nexus accounts (`AddonGrabber`).
- Maps each Vortex `collection.json` to an addon manifest (`VortexCollectionMapper`).
- Premium path: downloads every archive (parallel, resumable, md5/size verified),
  reusing archives already present in a downloads or Vortex folder.
- Optionally builds a private "Stock Game" copy and runs the collection's
  Downgrader (or patches the real install in place).
- Extracts into MO2 mod folders, replays collection FOMOD choices, installs F4SE
  into the game root, and routes `dinput` mods through Root Builder.
- Verifies installed content against the collection's per-file hashes and plugin CRCs.
- Writes `modlist.txt` / `plugins.txt` (ordered from the collection's `modRules`),
  custom executables, and a resumable checkpoint.

## Not implemented yet

- Layered addon manifests (conflict declarations, `loadAfter`, install-priority
  resolution across addons) — see `spec/`.
- Free-account browser-assist downloads (premium only today).
- ESL flagging, ini tweaks from manifests, optional-mod selection UI.
- Verifying deploy/load order against a real Vortex install (logic is in place, see below).

## Layout

- `app/` — `AStoryWealth.Installer`, the Avalonia desktop app (net10.0)
- `src/Core/` — manifest model, collection sync, resolver, MO2 writer, install runner
- `src/NexusApi/` — Nexus API client (GraphQL, file links, resumable download)
- `src/Spike/` — console spike for the Nexus download leg
- `spec/` — addon manifest spec and JSON Schema (draft v0.2; ahead of the code)
- `examples/` — example addon manifests

## Requirements

- .NET 10 SDK
- 7-Zip. Located via the `ASW_7Z` env var, a `7z.exe` next to the installer, `PATH`,
  or the standard install folders.

## Build

```
cd app
dotnet build
```

## Spike

```
cd src/Spike
export ASW_NEXUS_KEY=<your Nexus personal API key>   # premium for direct downloads
dotnet run -- fallout4 <collection-slug> [revision] [testModId testFileId]
```

## Deploy order and load order

Vortex collections ship no final order, only per-mod install `phase` plus
`before`/`after` `modRules` (and a plugin list). The installer derives the order:

- **Deploy order** (`DeployOrder`): phase, then collection array order, then
  before/after rules (matched by md5, logical file name, or file expression) as a
  stable topological sort. Unresolved rules and broken cycles are logged.
  Computed in ascending priority (last wins); `modlist.txt` is written
  highest-priority-first, as MO2 expects. Headerless `modlist.txt` files from
  older builds are read as ascending and migrated on the next write.
- **Load order** (`PluginOrder`): the collection's plugin list, adjusted only
  where the engine requires it (ESM before non-masters, masters before
  dependents, `pluginRules` load-after). Writes `plugins.txt` and `loadorder.txt`.

## Tests

```
dotnet run --project src/Tests
```

## Instance layout

- `Stock Game/` — private vanilla copy of the game (your Steam install is only read from).
  If it is next-gen, the collection's Downgrader (`FO4Down.exe -patch-files`) swaps the
  executables for 1.10.163 here before anything else is installed; success is judged by the
  resulting `Fallout4.exe` version. (Untested against a real next-gen copy so far.)
- `mods/` — one folder per collection mod. Root mods (F4SE, preloader hooks, BAT files) are split:
  game-folder files in `Root/`, the archive's Data content at the mod root — the layout Root
  Builder (latest 5.x, installed automatically) expects.
- `Tools/` — FO4Edit, Bethini and other tool mods (anything staged under `Tools\` by the
  collection). They stay out of the mod list; MO2 executables point here.
- `profiles/A StoryWealth/` — `modlist.txt`, `plugins.txt`, `loadorder.txt`, local INIs.
