# A StoryWealth Installer

Installs the A StoryWealth Fallout 4 collection from Nexus Mods, plus its add-on collections, into a portable Mod Organizer 2 instance. It downloads and checks every mod, makes its own copy of the game, and builds the mod order and load order for you. Windows only. Built with Avalonia on .NET 10.

## What it does

- Builds a ready-to-play MO2 instance with Root Builder, F4SE and your tools set up.
- Groups mods under MO2 separators. Patches and BodySlide sets sit next to the mods they need, and each group is A–Z. An empty "Added mods" section on top is for your own mods.
- Copies the vanilla game into the instance, so your Steam install is never touched. A next-gen game gets downgraded with the collection's Downgrader.
- Installs the team's add-ons on top, each in its own section.
- Has an Optional tab for optional mods and groups (Sim Settlements 2, HD textures and so on). Options that need an add-on you don't have are greyed out.
- Downloads automatically with Premium. With a free account it opens each mod's page, waits for your download, checks the file and moves on.
- If you're coming from Vortex, it checks your Vortex downloads first and only fetches what's missing.

## Using it

1. Start the app and paste your Nexus personal API key on the Nexus page (or in Settings). Get it at `nexusmods.com/settings/api-keys`. It's stored encrypted for your Windows user. Browser sign-in comes once Nexus verifies the app.
2. Pick the install, downloads and game folders, tick the optional groups you want, and continue.
3. When it's done, open Mod Organizer 2, choose **F4SE** in the drop-down at the top right and press Run. The game only starts through F4SE.

If an install is interrupted, press Resume or start it again. It picks up where it stopped.

## Requirements

- Windows 10 or 11 and a copy of Fallout 4 (Steam, Bethesda launcher or GOG).
- About 150 GB free for the full list. Less without HD textures and Sim Settlements 2.
- 7-Zip. Release builds include it. When running from source, put `7z.exe` on PATH or set `ASW_7Z` to its path.

## Building

```
dotnet build AStoryWealth.slnx
dotnet run --project src/Tests
dotnet run --project app
pwsh build/publish.ps1
```

`build/publish.ps1` runs the tests, then makes a self-contained win-x64 build and a zip in `dist/`.

The tests also check against `samples/base-r284.collection.json` if it's there. It isn't in the repository, so that check is skipped without it.

## Layout

| Path | What's in it |
|---|---|
| `app/` | The desktop app |
| `src/Core/` | Install steps, MO2 writer, ordering, add-ons, optional content, shipped data |
| `src/NexusApi/` | Nexus API client |
| `src/OrderTool/` | Generates the shipped order and optional-group files |
| `src/Tests/` | Test runner |
| `docs/` | [How it works](docs/how-it-works.md) and [data formats](docs/formats.md) |
| `build/` | Publish script (it bundles 7-Zip) |

## Status

Tested on real installs: the full list (915 mods), add-ons, optional picks, and MO2 plus F4SE launching with the profile intact. The free-account download pickup was tested with a fake file.

Not tested yet: a downgrade of a real next-gen game, the HD textures group from start to finish, and the free-account flow against live Nexus.

Not built yet: removing an add-on or optional mod, remote metadata updates, and a Curator section. See [how it works](docs/how-it-works.md).

Changes are in [CHANGELOG.md](CHANGELOG.md). Third-party licences are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
