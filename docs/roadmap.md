# Roadmap

What the installer is for, what it needs, and what comes next. Small things and bugs are tracked as GitHub issues. This page is the bigger picture.

## What it needs

- Windows 10 or 11, Fallout 4 on Steam, and a Nexus Mods account.
- Premium downloads automatically. A free account works, but each mod is downloaded in the browser.
- Space: the mods folder of a base install with two add-ons is about 107 GB. The game copy and the download archives come on top.

## Goal

A finished install has no disabled mods and no disabled plugins, and the Install health list on Home is all green. Anything else is a bug.

## Now (0.5.0)

- Fix what the beta testers find.
- Report zip hides the Windows user, PC name and Nexus account.
- Nexus registration, so people can sign in with Nexus instead of pasting a key.
- The LK Modular Beret rule for the Faction add-on, once the curator answers.

## Next (0.6)

- A "what is wrong with this install" view that extends Install health: missing masters, mod conflicts, mods whose requirements are missing.
- Profiles: male, female, and no-SS2. Needs the add-on curators to say which mods belong to which profile.
- Fixes and tools that run after an install, only from a list we ship and sign. The installer never runs a program a collection supplies.

## Maybe

- Endorse mods and collections after an install, one button at a time. Nexus's rules come first.
- BA2 archive patcher, like CM Tools. Probably better as its own tool.
- Files from outside Nexus: a "put the file here" step with an md5 check, like the manual flow for free accounts. No automatic downloads from other sites.

## Not planned

- Automatic downloads from LoversLab, Discord or Google Drive. They need logins or have no API.
- Running arbitrary programs from a collection.

Ideas from testers go in the issues. See [decisions](decisions.md) for why things are the way they are.
