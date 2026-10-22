# Data formats

These are the files the installer reads or writes, apart from the Nexus collections. The ones that ship with the app live in `src/Core/Data/` and are embedded. To change one without a rebuild, put a file with the same name in `<app folder>/order/`. It takes precedence.

## Shipped metadata

### `<id>.priority.json`: structured mod order

```json
{
  "Schema": 1,
  "AddonId": "a-storywealth",
  "Note": "...",
  "Separators": [
    { "Name": "Fixes", "Categories": ["Bug Fixes"], "NamePatterns": [], "AlwaysShow": false,
      "Mods": [ { "Md5": "…", "Name": "A Storywealth - Some Mod", "Parent": null } ] }
  ]
}
```

Separators run lowest priority first. Mods inside a separator are in ascending deploy order, so the last one wins. `Parent` marks a patch or BodySlide set that must follow the mod it needs. `AlwaysShow` keeps an empty separator. The last one, "Added mods", is for your own mods. At install time every before/after rule and every real file conflict still beats this file (`PriorityOrder.Apply`). `OrderTool` generates it.

### `<id>.optional.json`: optional install groups

```json
{ "Schema": 1, "AddonId": "a-storywealth",
  "Groups": [ { "Id": "ss2", "Name": "Sim Settlements 2", "Description": "…", "DefaultOn": true,
                "FromOptionalFlag": false, "Source": "collection 4rfeex r47", "Mods": [ { "Md5": "…", "Name": "…" } ] } ] }
```

A group you leave unticked at setup skips its downloads, extraction, mod-list entries and plugins. `FromOptionalFlag` means the group is the collection's own Vortex "optional" mods, not a listed set.

### `addon-options.json`: curated facts about optional mods

```json
{ "Schema": 1, "Names": { "yilurh": "Thuggyverse" },
  "Entries": [ { "Addon": "wrbrop", "Md5": "…", "Name": "Project Valkyrie",
                 "NeedsAddons": ["yilurh"], "NeedsGroups": [], "Note": "" } ] }
```

The Optional tab greys an option out until every add-on in `NeedsAddons` is installed and every group in `NeedsGroups` came with the base list. Entries match by `Md5` (preferred) or `Name`. The collection author's own note for the mod always shows next to the option. The entries so far come only from notes the collection authors wrote.

## Written into an instance

| File | What it's for |
|---|---|
| `ModOrganizer.ini`, `profiles/A StoryWealth/` | The MO2 instance. `modlist.txt` is highest priority first. |
| `mods/<name>_separator/meta.ini` | MO2 separators, marked `aSW-installer-separator` |
| `.asw-addons.json` | Installed add-ons (id, revision, separator, mod folders, hoisted mods, optional picks), plus the base list's installed groups and optional picks |
| `.asw/<id>.collection.json` | A copy of each collection list that was installed. The Optional tab uses these, so it works offline. |
| `.asw-tools.json` | Tools moved to `<instance>/Tools` |
| `install-*.log` | One log per run |
| `Tools/` | FO4Edit, Bethini and other tools that MO2 executables point to |

## Environment variables

| Variable | What it does |
|---|---|
| `ASW_NEXUS_KEY` | Your Nexus personal API key. Overrides the key saved in Settings. |
| `ASW_7Z` | Full path to `7z.exe` when it isn't in a standard place |
| `ASW_UI_SHOT`, `ASW_UI_SIZE` | Development only. Renders every page to PNG files in that folder, then exits. `WxH` sets the window size. |

## OrderTool

For maintainers.

```
dotnet run --project src/OrderTool -- <collection.json> <instanceDir> <addonId> <out.priority.json>
dotnet run --project src/OrderTool -- optional <base.json> <filter.json> <instanceDir> <addonId> <out.optional.json>
```

The first builds the structured order for a collection from its rules and the file conflicts of an installed instance, and prints an A–Z audit. The second builds an optional group from a Nexus filter collection, like the Sim Settlements 2 group from `4rfeex`.


## Profile INIs (Data/ini)

Put `Fallout4.ini`, `Fallout4Prefs.ini` and/or `Fallout4Custom.ini` (tuned in BethINI) in `src/Core/Data/ini/`. They are embedded in the exe. When a new profile is made, a shipped file is used instead of the game's own default. A profile that already has the file is never changed.

After that the installer sets `iSize W` and `iSize H` in `[Display]` of `Fallout4Prefs.ini` to the main display's real size (read with Avalonia's screen information, nothing else is probed). Home > "Match the game's resolution to my display" does it again later and keeps the old file as `Fallout4Prefs.ini.asw-backup`.
