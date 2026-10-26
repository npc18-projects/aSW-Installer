# How it works

## The install

1. **Collection list.** The installer gets the latest revision of the base collection from Nexus and maps it to a manifest. Premium accounts fetch it automatically. With a free account you download the collection by hand and pick the file.
2. **Game.** Stock Game mode copies the vanilla game into the instance. A next-gen copy is downgraded with the collection's Downgrader (`GameDowngrader`).
3. **MO2 and Root Builder** are installed at pinned versions (`Mo2Installer.Pin`) and checked by size and hash.
4. **Downloads.** Premium accounts download in parallel, resumable, with md5 and size checks. Free accounts download by hand: the installer opens each mod's file page, waits for the file in your browser's download folder, checks it and moves it into the downloads folder (`ManualDownloader`). Archives already in a downloads or Vortex folder are used first, so a Vortex user only downloads what's missing.
5. **Extraction** goes into `mods/<name>`. FOMOD choices are replayed. F4SE and other game-root mods go in a mod's `Root` folder for Root Builder. Standalone tools go in `<instance>/Tools`. Results are checked against the collection's per-file hashes and plugin CRCs, and wrapped layouts are repaired by hash.
6. **Profile.** `modlist.txt`, `plugins.txt`, `loadorder.txt` and the MO2 executables.

## Deploy order and load order

A Vortex collection has no final order. It has an install phase per mod plus before/after rules.

- **Deploy order** (`DeployOrder`): phase first, then collection order, then the rules (a stable topological sort, with cycles broken).
- **Structured order** (`PriorityOrder`): mods are grouped under MO2 separators from the shipped `priority.json`. Patches and BodySlide sets follow the mod they need, and each separator is A–Z. Rules and real file conflicts always win over the shipped placement. The empty "Added mods" separator on top is yours and survives re-runs.
- **Load order** (`PluginOrder`): the collection's plugin list is already LOOT-sorted. The installer only moves a plugin when its header proves it has to (ESMs first, masters before dependents, the collection's plugin rules). Plugins with no file on disk are left out.

## Add-ons

Each add-on is a Nexus collection installed on top of the base list.

- **Anchors.** Vortex only writes rules between mods in the same collection, so an add-on repeats the few base mods it needs rules against. If a repeat matches an installed mod (by md5, with its folder on disk), it's an anchor. It isn't downloaded again, but its rules decide where the add-on's own mods go. A mod shared with another installed add-on is installed once.
- **Placement** (`AddonPlacement`). The add-on's mods form one block under their own separator, above the base list and below "Added mods". A mod that must deploy before a base mod is placed right below that mod instead, along with anything that must deploy before it. Inside the block it's A–Z with patches after their parents, never against a rule or file conflict.
- **Record.** `.asw-addons.json` lists what each add-on installed. A base re-run keeps add-ons on top.
- **Different versions.** If an add-on has a different file of a mod the base already has, the base file wins.

## Optional content

The Optional tab lists the optional mods and groups of the base list and of installed add-ons only, A–Z in a drop-down per source. Installed options are ticked. Options that need an add-on or group you don't have are greyed out with the reason (`addon-options.json`). Each option shows the author's own note, which covers things like AMD patches and Creation Club requirements. Picking options queues one run per source.

## Not built yet

- Removing an installed add-on or optional mod.
- A remote, signed metadata channel, so ordering, add-on rules and curated options can change without a new build. A Curator section would edit and publish it. Publishing collections from the installer depends on what the Nexus API allows.
- Nexus sign-in is OAuth in the browser. It uses Nexus's test client until the app is registered and gets its own client id.

## Where things are

- `app/`: the Avalonia window. `MainWindow` is split by page: `.Home`, `.Modlist` (the base list and the add-ons), `.Setup` (setup and the Nexus page, sign-in), `.Sync` (fetching the mod list), `.Install`, `.Optional`, `.Removal`, `.SettingsPage` (report, existing install, updates) and `.DevTools` (the development-only icon and screenshot hooks).
- `src/Core/`: everything that isn't a window. `InstallRunner` runs an install; `Mo2InstanceWriter` and `Mo2RootAndTools` build the MO2 instance; `VortexCollectionMapper` reads a collection; `PriorityOrder`, `DeployOrder` and `PluginOrder` decide the order; `AddonPlacement`, `AddonRegistry` and `Removal` handle add-ons; `NexusOAuth` is the sign-in; `SevenZip` runs 7-Zip; `InstallState` remembers the install; `Update` is the self-updater.
- `src/NexusApi/`: the Nexus API client and the credential types it uses.
- `src/Tests/`: the dependency-free test runner (`dotnet run --project src/Tests`). Set `ASW_7Z` to a 7z.exe to include the tests that unpack archives.
- `src/OrderTool/`: makes the shipped order files (see formats).
