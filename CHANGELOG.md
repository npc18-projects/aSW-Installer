# Changelog

## Unreleased

- Fixed: dark text on buttons and boxes when Windows is in light mode. The app is now always dark.
- Fixed: an add-on with two mods that share a file name (Pickman's Passion has two "Main File") stopped at "installAs collision". Each now gets its own folder.
- The resume log no longer calls a file "missing" when it is only being looked up again in the Vortex folder.
- Nexus account: the API key box is gone. Sign in to Nexus opens the Nexus key page and takes the key in a small dialog; Sign out forgets it. Browser sign-in replaces the dialog once Nexus approves the app.
- New app icon: the exe, the window and the taskbar use the aSW artwork.
- Fewer Nexus requests: the add-on listing is one request instead of 16, the team's member ids are looked up once, and the listing is reused for an hour. Requests now carry the real app name, version and repository link.
- Fixed: the Archives title was hidden behind the sort and filter boxes on a narrow window. The controls now sit under the title.
- A StoryWealth page: dropped the repeated mod count and download size (they are in the numbers row), and the ~ in front of the download and storage sizes.
- A green dot next to Modlist when the base list has an update (the latest list differs from the one you installed), and next to Add-Ons when an installed add-on has one. The A StoryWealth page then offers Update.
- Add-Ons: the redundant All filter is gone.
- Play moved to the bottom of the left rail, with a short install summary above it, so Launch with F4SE and Open Mod Organizer 2 are on every page once the base list is installed.
- Home: What's new is at the top right; the Nexus link button is now just Nexus page.
- UI pass: files under 1 KB show as <1 KB, long archive names have a tooltip, the install progress bar is taller with a percentage, Remove buttons are red, the Optional page uses the card colour and its button shows the selection count, and Install health names what needs attention.
- Layout: the Add-Ons cards fill the page width, the banners share one height, and Settings uses the full page width.
- The Done page has a summary (mods, plugins, add-ons, time taken, log path) and a Go to Home button.
- The Modlist tab opens the A StoryWealth page directly (no card to click first).
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
