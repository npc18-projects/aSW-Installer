# Third-party software

## In release builds

- **7-Zip** (`7z.exe`, `7z.dll`) by Igor Pavlov unpacks the mod archives. It's under the GNU LGPL, with the unRAR license restriction for the RAR code. The full licence ships next to the exe as `7-Zip-License.txt`. Official binaries and source: https://www.7-zip.org/. `build/publish.ps1` copies the binaries unchanged from a local 7-Zip install. They aren't kept in this repository.
- **.NET runtime** (self-contained build), Microsoft, MIT licence.
- **Avalonia**, with the Fluent theme and Inter font, MIT licence.

## Downloaded when you install

- **Mod Organizer 2** (GPL-3.0) and **Root Builder** come from their official Nexus pages, at the versions pinned in `src/Core/Mo2Installer.cs`. They go into your instance, not into this repository.
- **Mods** come from Nexus Mods through your own account. The installer doesn't host or redistribute them.
