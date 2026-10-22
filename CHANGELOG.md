# Changelog

## Unreleased

- Archive list shows each mod's own name instead of the install folder name.
- Modlist page: the archive list is always shown (the Archives button is gone).
- Game settings: a new install sets the game's resolution to your main display. Install health has an INI settings line. It reads your Fallout4.ini, Fallout4Prefs.ini and Fallout4Custom.ini each time and names any setting that differs from the recommended one (volume, controls, window mode and similar are ignored). It also shows when the resolution differs from your display, with a Match display button.
- Install health: Use recommended INIs replaces those three files with the recommended ones and sets the resolution. Your old files are kept as .asw-backup.
- A new install gets the recommended Fallout4.ini, Fallout4Prefs.ini and Fallout4Custom.ini (tuned in BethINI). A profile that already has its own is never overwritten.
- Home tab: banner with the base list's revision and stats, an install health check (disabled mods or plugins, missing masters, game version, F4SE), an install summary, and links to the Nexus page and the install, mods, downloads and logs folders.
- Settings > Updates > Include test builds: testers get pre-release builds (such as 0.5.0-beta.1) automatically. Off by default.
- Latest changelog from Nexus: "What's new" on the Home tab for the base list, and on each add-on's page.
- Home tab (shown once the base list is installed): open Mod Organizer 2, or launch the game with F4SE through MO2.
- Fixed: plugins inside installed mods that the collection's plugin list leaves out (for example KCI.esp) were added by MO2 switched off. They are now switched on.
- Plugins turned off for a missing master are turned back on on a later run once the master is there.
- Fixed: FOMOD configs saved as UTF-16 (Strong Power Armor) failed to read, so their plugins went missing.
- Fixed: mods with a folder inside a wrapper folder (Simple Esp Replacer) installed one level too deep, so their plugin was missing.

## 0.4.6

- Faction add-on: the SS2 mods (IT_SS2, SS2 and ITOv2 Integration Patch, RGE Concord SS2 Patch) are optional. They install when the SS2 group is installed, and otherwise show greyed out on the Optional tab.

## 0.4.5

- Plugins whose master isn't installed (an optional mod, or the SS2 group left out) are now turned off, so MO2 no longer warns about missing masters. The log lists them.
- Add-ons page: installed add-ons get a green border and an Installed badge.

## 0.4.4

- Add-on mods are named after their add-on, without the A Storywealth / aSW prefix (for example "Atmosphere - Mod name"). Each add-on uses its own title. Folders from earlier versions are still recognised.

## 0.4.3

- Settings > Save report: one zip with the install logs, mod list and basic PC details to send when something goes wrong. Your Nexus key is left out.
- Remove an add-on (Add-Ons page) or an installed optional mod (Optional tab). Mods another add-on needs are kept.
- App icon
- Fixed: installing a new add-on after a failed one resumed the failed install instead.
- Add-on mods are named after their add-on. Add-ons already installed keep their current folders.
- Fixed: a download failed verification when a collection listed the file a few bytes off its real size (Nightmare Deathclaw redux in Creature Madness). The md5 now decides.

## 0.4.1

- Fixed: a finished install wasn't recorded on a PC with no settings file, so the app acted as if nothing was installed
- Fixed: an interrupted install couldn't resume on a fresh PC
- Added: Settings > Existing install, to point the app at an install it lost track of

## 0.4.0

- Add-ons install on top of the base list, each in its own section
- Optional tab for optional mods and groups, greyed out when an add-on is missing
- Optional groups at setup: Sim Settlements 2 on, HD textures off
- Free accounts download in the browser, one mod at a time
- Vortex downloads are used first
- Downloads, endorsements, rating and size shown on the base list and add-ons
- Structured MO2 order with separators and an "Added mods" section
- Mod Organizer 2 2.5.2 and Root Builder 5.1.1 pinned
- F4SE and root mods go through Root Builder; tools go in `<instance>/Tools`
- Built-in Downgrader step
- Setup checks paths, game folder and free disk space
- Resume and Back after a stopped install
- Game folder picker (Steam, Bethesda launcher and GOG detected)
- 7-Zip bundled
- Updates itself from GitHub releases
- Sign-in with a Nexus personal API key
