# Changelog

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
