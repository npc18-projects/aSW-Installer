# Changelog

## 0.3.0 (in development)

- Add-ons: install any of the team's add-on collections on top of the base list. Repeats of installed mods become anchors,
  each add-on gets its own separator block (mods that must deploy before a base mod are placed beside it), and
  `.asw-addons.json` records what was installed. Installed / update-available badges, one-click install.
- Optional tab: optional mods and optional groups of the base list and installed add-ons, A–Z in drop-downs, greyed out
  when they need an add-on that is not installed, with the collection author's notes. Picks are remembered.
- Optional install groups at setup (Sim Settlements 2 on by default, HD textures off).
- Free accounts: mods are downloaded one at a time in the browser (the installer opens the exact file page, watches the
  download folder, verifies and moves the file). Transferring from Vortex checks the Vortex folder first.
- Collection stats (downloads, endorsements, rating, mod count, size) on the base card and every add-on; add-on sorting.
- Structured MO2 order: separators, patches and BodySlide sets after the mods they need, A–Z inside groups, "Added mods"
  separator for your own mods.
- Pinned Mod Organizer 2 (2.5.2) and Root Builder (5.1.1); Root Builder places F4SE and other root mods; standalone tools go
  to `<instance>/Tools`; native Downgrader step.
- Setup is validated before continuing (paths, game found, free disk space per drive); Resume / Back after a stopped run;
  buffered install log; game folder Browse (Steam, Bethesda launcher and GOG detection).
- Personal Nexus API key sign-in (stored encrypted for the current Windows user) until Nexus verifies the application for SSO.
