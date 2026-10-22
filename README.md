# A StoryWealth Installer

An installer for the **A StoryWealth** Fallout 4 collection on Nexus Mods and its official add-on collections. It sets up a
portable **Mod Organizer 2** instance: downloads and verifies every mod, builds a private copy of the game, and writes a
structured mod order and load order, in the spirit of Wabbajack and the Nolvus launcher. Windows, built with Avalonia
(.NET 10).

## What you get

- **A ready-to-play MO2 instance** with Root Builder (F4SE and other root mods), your tools in a `Tools` folder, and a
  profile with MO2 separators, patches and BodySlide sets next to the mods they need, mods A–Z inside each group, and an
  empty **Added mods** section on top for your own mods.
- **Stock Game mode:** the vanilla game is copied into the instance; your Steam install is only read from. A next-gen game
  is downgraded with the collection's own Downgrader.
- **Add-ons:** install any of the team's add-on collections on top. Each gets its own section; mods that have to load
  before a base mod are placed beside it.
- **Optional tab:** optional mods and groups (Sim Settlements 2, HD textures, …) of the base list and of the add-ons you
  installed, with the authors' notes. Options that need an add-on you don't have are greyed out.
- **Premium or free:** Premium accounts download automatically. Free accounts download each mod in the browser, one at a
  time; the installer opens the right file page, watches your download folder and verifies the file.
- **Coming from Vortex:** point the installer at your Vortex downloads and only what is missing is downloaded.
- **Collection stats** (downloads, endorsements, rating, size) on the base list and every add-on.

## Using it

1. Start the app and paste your Nexus **personal API key** on the Nexus page (or in Settings). Get it at
   `nexusmods.com/settings/api-keys`. It is stored encrypted for your Windows user. Browser sign-in will replace this once
   Nexus has verified the application.
2. Choose the install folder, downloads folder and game folder, tick the optional groups you want, and press Continue.
3. After the install, open Mod Organizer 2, pick **F4SE** in the drop-down at the top right and press Run. The game has to
   start through F4SE.

An interrupted install resumes where it stopped (Resume on the install page, or start it again).

## Requirements

- Windows 10/11, a legitimate Fallout 4 install (Steam, Bethesda launcher or GOG)
- 7-Zip (`7z.exe` on PATH, in Program Files, next to the installer, or its path in `ASW_7Z`)
- About 150 GB free for the full list (less without HD textures and Sim Settlements 2)

## Building

```
dotnet build AStoryWealth.slnx            # everything
dotnet run --project src/Tests            # test suite (dependency-free runner)
dotnet run --project app                  # start the app
pwsh build/publish.ps1                    # tests, then a self-contained win-x64 build + zip in dist/
```

The test suite reads `samples/base-r284.collection.json` when present (a real collection list; not in the repository) and
skips that check otherwise.

## Repository layout

| Path | What |
|---|---|
| `app/` | the Avalonia desktop app |
| `src/Core/` | install pipeline, MO2 writer, ordering, add-ons, optional content, metadata (`Data/`) |
| `src/NexusApi/` | Nexus API client (GraphQL, file links, resumable downloads, SSO client) |
| `src/OrderTool/` | maintainer tool that generates the shipped order and optional-group files |
| `src/Tests/` | test runner |
| `docs/` | [how it works](docs/how-it-works.md) and [data formats](docs/formats.md) |
| `build/` | publish script |

## Status

Verified on real installs: the full list (915 mods), add-ons, optional picks, free-account file pickup (with a test file),
MO2 and F4SE launching with the profile intact. **Not yet tested:** a downgrade of a real next-gen game copy, the HD
textures group end to end, and the free-account flow against live Nexus. Not built yet: removing an add-on or optional mod,
remote metadata updates and a Curator section (see [how it works](docs/how-it-works.md)).

Changes are listed in [CHANGELOG.md](CHANGELOG.md).
