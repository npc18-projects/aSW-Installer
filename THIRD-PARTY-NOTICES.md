# Third-party software

## Bundled in release builds

- **7-Zip** (`7z.exe`, `7z.dll`) © Igor Pavlov. Used to unpack mod archives. Licensed under the GNU LGPL, with the
  "unRAR license restriction" for the RAR code; the full licence text ships next to the exe as `7-Zip-License.txt`.
  Source and the unmodified official binaries: https://www.7-zip.org/. The binaries are copied unchanged from an official
  7-Zip installation at build time (`build/publish.ps1`); they are not kept in this repository.
- **.NET runtime** (self-contained build) © Microsoft, MIT licence.
- **Avalonia** (UI framework, with Fluent theme and Inter font) — MIT licence.

## Downloaded at install time (not redistributed)

- **Mod Organizer 2** (GPL-3.0) and **Root Builder** are fetched from their official Nexus Mods pages into the user's own
  instance, at the versions pinned in `src/Core/Mo2Installer.cs`.
- **All mods** are fetched from Nexus Mods by the user's own account; the installer never hosts or redistributes them.
