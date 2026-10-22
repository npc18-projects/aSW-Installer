# Changelog

## 0.4.0

- Add-ons install on top of the base list. Each gets its own section. Mods that have to load before a base mod are placed next to it. Cards show installed and update badges, and install is one click.
- New Optional tab for optional mods and groups, A–Z in a drop-down per source. Options that need a missing add-on are greyed out. The collection authors' notes show next to each option, and your picks are remembered.
- Optional groups at setup: Sim Settlements 2 is on by default, HD textures off.
- Free accounts download in the browser, one mod at a time. The installer opens the file page, watches your download folder, checks the file and moves it. Coming from Vortex, it checks your Vortex folder first.
- Downloads, endorsements, rating, mod count and size show on the base list and every add-on. Add-ons can be sorted.
- Structured MO2 order: separators, patches and BodySlide sets after the mods they need, A–Z inside each group, and an "Added mods" section for your own mods.
- Mod Organizer 2 (2.5.2) and Root Builder (5.1.1) are pinned. Root Builder places F4SE and other root mods. Standalone tools go in `<instance>/Tools`. The Downgrader step runs natively.
- Setup checks your paths, game folder and free disk space before you continue. Stopped installs offer Resume and Back. The install log is buffered. You can browse for the game folder (Steam, Bethesda launcher and GOG are detected).
- Release builds include 7-Zip. `build/publish.ps1` copies it from a local install.
- Sign in with a Nexus personal API key, stored encrypted for your Windows user, until Nexus verifies the app for browser sign-in.
