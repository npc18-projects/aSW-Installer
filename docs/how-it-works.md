# How it works

## Install pipeline

1. **Collection list.** The latest revision of the base collection is fetched from Nexus (Premium: automatically; free
   accounts: download the collection by hand and choose it) and mapped from Vortex `collection.json` to a manifest.
2. **Game.** Stock Game mode copies the vanilla game into the instance (the Steam install is never touched). A next-gen
   game is downgraded with the collection's Downgrader, applied natively (`GameDowngrader`).
3. **Mod Organizer 2 and Root Builder** are installed at pinned versions (`Mo2Installer.Pin`), size- and hash-checked.
4. **Downloads.** Premium accounts download in parallel (md5/size verified, resumable). Free accounts download by hand:
   the installer opens each mod's file page (mod id + file id from the collection) one at a time, waits for the file in the
   browser's download folder, verifies it and moves it into the downloads folder (`ManualDownloader`). Archives already in a
   downloads or Vortex folder are reused first. When transferring from Vortex, the folder is checked up front and only
   missing mods are downloaded.
5. **Extraction** into `mods/<name>`, FOMOD choices replayed, F4SE and other game-root mods placed in a mod's `Root` folder
   for Root Builder, standalone tools moved to `<instance>/Tools`. Installed content is checked against the collection's
   per-file hashes and plugin CRCs, with hash-guided repair of wrapped layouts.
6. **Profile.** `modlist.txt`, `plugins.txt`, `loadorder.txt`, custom executables.

## Deploy order and load order

Vortex collections ship an install phase per mod plus before/after rules, not a final order.

- **Deploy order** (`DeployOrder`): phase, then collection order, then the rules (stable topological sort, cycles broken).
- **Structured order** (`PriorityOrder`): the mods are grouped under MO2 separators from the shipped `priority.json`.
  Patches and BodySlide sets follow the mod they need; inside a separator mods are A–Z. Rules and real file conflicts
  always win over the shipped placement. An empty "Added mods" separator on top is for the user's own mods and survives
  re-runs.
- **Load order** (`PluginOrder`): the collection's plugin list (already LOOT-sorted), moved only when plugin headers prove it
  (ESM first, masters before dependents, the collection's plugin rules). Plugins whose file is absent are left out.

## Add-ons

Each add-on is a Nexus collection installed on top of the base list.

- **Anchors.** Vortex only emits rules between mods that are in the same collection, so an add-on repeats the few base mods
  it needs rules against. A repeat of an installed mod (matched by md5, folder on disk) is an *anchor*: never downloaded
  again, but its rules place the add-on's own mods. A mod shared with another installed add-on is shared, not duplicated.
- **Placement** (`AddonPlacement`). The add-on's mods form one block under their own separator on top of the base list,
  below "Added mods". A new mod that must deploy before a base mod is hoisted in just below that mod (with the mods that
  must deploy before it). Inside the block: A–Z, patches after parents, never against a rule or file conflict.
- **Record.** `.asw-addons.json` lists what each add-on installed; a base re-run keeps add-ons on top.
- **Versions.** If an add-on contains a different file of a mod the base already has, the base file wins.

## Optional content

Optional mods (the collection's Vortex "optional" flag) and optional groups are chosen in the **Optional** tab, which lists
only the base list and the add-ons that are installed, A–Z in a drop-down per source. Installed options are ticked,
options that need an add-on or group that is not installed are greyed out with the reason (`addon-options.json`), and every
option shows the author's own note (AMD patches, Creation Club requirements). Picking options queues one run per source.

## Not built yet

- Removing an installed add-on or optional mod.
- A remote, signed metadata channel (ordering, add-on rules, curated options updated without a new build) and a Curator
  section to edit and publish it. Publishing collections from the installer depends on what the Nexus API allows.
- Nexus single sign-on: needs the application to be verified by Nexus. Until then the personal API key is used.
