# Releasing and updates

The app can update itself from GitHub releases. It stays off until you set it up, because it needs a repository to look at and a key to check updates against.

## One-time setup

1. **Make a public GitHub repository** for the releases. The app asks GitHub without logging in, so a private repository looks empty to it.
2. **Make the signing key.** Run this once and keep the private key file somewhere safe. Never commit it. Anyone who has it can sign an update that every installed copy will accept.
   ```
   pwsh build/new-update-key.ps1 -OutPrivate D:\secrets\asw-update-private-key.pem
   ```
3. **Fill in `src/Core/Data/update.json`** with the repository and the public key the script printed:
   ```json
   { "Repo": "your-name/your-repo", "PublicKey": "MFkwEwYH…" }
   ```
4. Build and send out this version by hand. Copies built before this step can't update themselves.

## Each release

1. Change the number in `version.txt` (for example `0.4.1`) and add a section to `CHANGELOG.md`.
2. Build, with the signing key:
   ```
   pwsh build/publish.ps1 -SevenZipDir "D:\Apps\7-Zip" -SigningKey D:\secrets\asw-update-private-key.pem
   ```
   This makes `dist/AStoryWealth-Installer-<version>-win-x64.zip` and `…zip.sig`.
3. Create a GitHub release. The tag must be `v` plus the version from `version.txt`, and both files must be attached:
   ```
   gh release create v0.4.1 dist/AStoryWealth-Installer-0.4.1-win-x64.zip dist/AStoryWealth-Installer-0.4.1-win-x64.zip.sig --title "0.4.1" --notes-file notes.md
   ```
   Don't tick "pre-release": the app ignores those.

## What the app does

- At startup (and when you press Check now in Settings) it asks GitHub for the latest release.
- If it's newer, the app downloads the zip and the signature, and checks them. The update is thrown away if any of these fail:
  - the signature doesn't match the public key built into the app
  - the zip holds paths outside its own folder
  - the program inside doesn't report the version the release claims, or isn't newer than the running one
  - the files aren't served from github.com
- A bar appears: "Version X is ready. Restarting in 20 s to install it." Restart now installs it straight away. Later skips it until the next start. If an install is running, it waits until that finishes.
- To install, the app starts the new exe as a helper and closes. The helper waits for the old app to exit, backs up the files it replaces, copies the new ones over, and starts the app again. If anything fails it puts the old files back. Your `order\` folder is never touched.
- If the install folder is read-only (for example under Program Files), the app says so and doesn't try.
- The helper writes `%LOCALAPPDATA%\AStoryWealth\update.log`.

## Limits

- Users can turn off the startup check in Settings. Auto-update needs a folder the app can write to.
- Only the zip's own signature protects users. If the private key leaks, make a new key pair and ship a build with the new public key by hand.
- Tested: the checks, the signature handling and the file replacement without the network, and a real update on a copy of the 0.4.0 app, which found 0.4.1 on GitHub, downloaded it, verified it, installed it and restarted in about 30 seconds.
