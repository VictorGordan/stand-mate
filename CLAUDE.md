# Instructions for AI assistants working on StandMate

README.md is for users. This file covers maintainer-only work: publishing releases.

## Releasing an update

Only release when the user asks for it. Pushing and publishing are public and hard to undo, so confirm the version
number and the release notes with the user first.

### Requirements (check, don't redo)
- Release signing key: `%USERPROFILE%\.standmate\release-key.xml`. Its public half is compiled in as
  `src/StandMate/Platform/ReleaseKey.cs`.
  - Never commit, print or copy the private key.
  - Never regenerate it with `tools/new-release-key.ps1 -Force`. A new key makes every installed copy reject future
    updates, and users would have to reinstall manually.
  - If the key is missing, stop and ask the user to restore it from their backup.
- `<UpdateRepo>` in `src/StandMate/StandMate.csproj` must be the real GitHub `owner/repo`. The placeholder
  `OWNER/stand-mate` disables updates, and `release.ps1` refuses to run with it.
- The GitHub CLI must be installed and signed in (`gh auth status`). If it isn't, ask the user to run `gh auth login`
  themselves, because it's interactive.

### Steps
1. **Bump `<Version>`** in `src/StandMate/StandMate.csproj`. It's the single source; the installer gets it from
   `build.ps1`. Use MAJOR.MINOR.PATCH:
   - PATCH or MINOR: installed copies keep their running timer when they update.
   - MAJOR: everyone's timer starts fresh on update. Only bump MAJOR if the user wants that, or if the saved state
     (`Core/EngineSnapshot.cs`, mode `ExportState` formats) changed incompatibly.
2. **Run the tests:** `build.ps1` runs them too.
3. **Commit and push** the version bump, so the release tag points at the released code.
4. **Publish:** `powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "<one or two plain sentences>"`.
   - It builds `dist\StandMate-Setup.exe`, writes `dist\latest.json` (version, URL, SHA-256, size, notes) and signs it
     into `latest.json.sig`.
   - It then creates the GitHub release `vX.Y.Z` with those three files.
   - The app reads `https://github.com/<UpdateRepo>/releases/latest/download/latest.json`, so the newest release must
     always contain all three files.
5. **Tell the user** the release URL.

### winget (package `VictorGordan.StandMate`)
- Ask the user whether a release should go to winget. Every winget PR is approved by a human moderator, and the user
  doesn't want to spend their time on trivial releases. If yes, add `-Winget` to the `release.ps1` call. It runs
  `wingetcreate update ... --submit` with the `gh` token passed through `WINGET_CREATE_GITHUB_TOKEN` (never `--token`,
  which can end up in logs).
- Follow microsoft/winget-pkgs' own rules (`AGENTS.md`, `CONTRIBUTING.md`, `doc/FirstContribution.md`):
  - one package version per PR, manifest files only;
  - check that no open PR exists for the same version;
  - `winget validate --manifest <dir>` and a real `winget install --manifest <dir>` before submitting by hand;
  - never recursively search their `manifests/` folder.
- If a winget validation check fails on a PR, read `doc/ValidationFailureGuide.md` there and fix it promptly. Don't
  leave a failing PR for moderators.
- The installer's publisher ("Victor Gordan") and display name ("StandMate") must keep matching the manifest's
  `Publisher` and `PackageName`. Don't change the Inno `AppId`.

### Testing an update locally (no GitHub)
1. Build a throwaway newer version. Temporarily bump `<Version>` and restore it afterwards. Edit the csproj without
   adding a UTF-8 BOM, which PowerShell 5.1's `Set-Content -Encoding utf8` adds.
2. Run `release.ps1 -Local -BaseUrl http://127.0.0.1:8765`.
3. Copy `dist\StandMate-Setup.exe`, `latest.json` and `latest.json.sig` to a scratch folder, and serve it with
   `python -m http.server 8765 --bind 127.0.0.1 --directory <folder>`.
4. Run the installed older app with `--update-url http://127.0.0.1:8765/latest.json`, then use Settings › General.
   - The signature check still applies. Plain `http` is accepted only for loopback addresses.
5. Afterwards, reinstall the real current version so the user isn't left on the throwaway build.

**UI testing:** the user may be at the PC, for example in a fullscreen game. Don't move or click the real mouse.
Press buttons by finding them with UI Automation, then sending `BM_CLICK` (`0xF5`) to their `NativeWindowHandle`.

## Other rules
- No copyrighted material in the repo or installer. The default mate's art is the user's own work
  (`assets/mates/default/`).
- The only network code is `src/StandMate/Platform/UpdateClient.cs`, and it runs only on a button press. Keep it
  that way: no background checks, no telemetry.
- Build with `build.ps1`. The `dotnet` on PATH may be runtime-only. The script finds an SDK, which can be the per-user
  one at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`.
