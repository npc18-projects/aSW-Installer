# Decisions

Short notes on why things are done this way, and what was rejected.

## Portable Mod Organizer 2, not Vortex
The installer builds a portable MO2 instance with its own copy of the game. Your Steam install is not touched, and one folder holds everything. Vortex is still the reference: a collection that installs cleanly in Vortex must install cleanly here. When it doesn't, the installer is wrong.

## Nexus only
Collections and add-ons come from Nexus, and downloads use the Nexus API. Other sites need logins or have no API, so they stay manual (see the roadmap).

## Sign-in is Nexus OAuth (PKCE), not a pasted key
Nexus requires OAuth for apps that download files, and personal keys aren't allowed in public apps. The installer is a public desktop app, so it uses OAuth 2.0 with PKCE and no client secret. Sign in opens the browser, Nexus answers on `http://127.0.0.1:<port>/callback`, and the app swaps the code for an access token and a refresh token. The client id and port are in `src/Core/Data/nexus-oauth.json`. Until Nexus issues our own client id, the guide's `public_test` is in it.

The tokens are encrypted with Windows' per-user encryption (DPAPI) and kept in `%LOCALAPPDATA%\AStoryWealth\nexus-oauth.secret`. They only decrypt for the same Windows user on the same PC, and they are only sent to the Nexus API host (never to a download address). The access token is renewed with the refresh token when it runs out. If Nexus refuses the refresh (400), the user is signed out. A Nexus outage is not a sign-out. Sign out deletes the tokens and asks Nexus to revoke them. They are removed from reports. An old saved personal key is deleted when the app starts. `ASW_NEXUS_KEY` still works for developers.

## Updates are signed
The app updates itself from GitHub releases. Each zip has a signature made with a private key that is never committed, and the app only accepts updates that match the public key shipped inside it. Test builds are GitHub pre-releases, and only people who tick "Include test builds" see them.

## Zero disabled mods and plugins
A finished install must have none. A plugin whose master is missing is switched off and logged as a fallback, but that still counts as a failure to fix.

## Add-on folder names
Add-on mods are named `<Add-on title> - <mod>`, with "A StoryWealth" / "aSW" dropped from the title, for example `Atmosphere - Mod`. The base list keeps `A Storywealth - <mod>`. Older names are remembered so an existing install keeps its folders. Two mods with the same file name get their Nexus id added.

## Reports are made on the PC and sent by the person
Nothing is uploaded. The report zip holds the logs, the lists and a summary. The key is removed, and the Windows user, PC name and Nexus account are hidden.

## Always the dark theme
The page colours are fixed, so the controls are too. Following Windows' light mode gave black text on dark buttons.

## Release rhythm
Patch releases wait until there are about 10 fixes. Features raise the middle number. Order: local alpha, then beta (a GitHub pre-release), then the release.
