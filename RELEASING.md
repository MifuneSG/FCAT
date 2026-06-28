# Releasing FCAT (installer + auto-update)

FCAT ships as a Windows installer with in-app auto-update, powered by
[Velopack](https://velopack.io). Users install once via `Setup.exe`; every later launch the app
checks this repo's **GitHub Releases** for a newer version, downloads it in the background, and
offers a one-click "Restart & update".

## One-time setup

```powershell
dotnet tool install -g vpk      # the Velopack CLI
```

Make sure `FCAT/AppSecrets.cs` exists locally (it's gitignored — copy `AppSecrets.example.cs`
and fill in the real ESI client id/secret). It gets baked into the published build.

## Cut a release

1. **Bump the version** in `FCAT/FCAT.csproj` (`<Version>` / `<FileVersion>`).
2. **Pack** (from the repo root):

   ```powershell
   # First ever release:
   .\scripts\pack.ps1 -Version 0.12.0-beta

   # Every release after that — pull the previous one first so users get small delta updates:
   .\scripts\pack.ps1 -Version 0.13.0-beta -Delta
   ```

   Output lands in `.\Releases\`:
   - `FCAT-win-Setup.exe` — the installer to share
   - `*-full.nupkg` (+ `*-delta.nupkg`), `RELEASES`, `releases.*.json` — the updater's feed

3. **Publish the GitHub Release.** Tag it `v<version>` (e.g. `v0.12.0-beta`), mark it
   **pre-release** while the version is `-beta` (the app's updater is prerelease-aware), and
   **upload every file from `.\Releases\`**. The updater reads these assets directly.

   Either do it on github.com (no `gh` CLI needed), or let Velopack push it for you:

   ```powershell
   vpk upload github --repoUrl https://github.com/MifuneSG/FCAT `
       --publish --releaseName "FCAT 0.12.0-beta" --tag v0.12.0-beta `
       --token <github-personal-access-token> --pre
   ```

That's it. Installed clients pick up the new version on their next launch.

## Verifying the update flow

The updater is a **no-op when run from source** (`dotnet run`) — `UpdateManager.IsInstalled`
is false. To test it for real: install an older version via its `Setup.exe`, publish a newer
release, launch the installed app, and confirm the **UPDATE** pill appears in the top bar
(also surfaced under **Settings → Updates**).

## Code signing & SmartScreen (pending)

Unsigned installers trigger a **Windows SmartScreen** warning ("unrecognized app"). It's
dismissable (More info → Run anyway) but looks alarming. There is no free fix — self-signed
certs do **not** clear SmartScreen. The realistic options:

- **Azure Trusted Signing** (~$10/mo, requires identity/org validation) — cheapest legit path.
- **EV code-signing certificate** — pricier, instant SmartScreen reputation.

Once you have a cert, `vpk pack` can sign during packing via `--signParams` (signtool args).
Until then we ship unsigned and tell beta users to click through the warning.
