# Releasing FCAT (installer + auto-update)

FCAT ships as a Windows installer with in-app auto-update, powered by
[Velopack](https://velopack.io). Users install once via `Setup.exe`; every later launch the app
checks this repo's **GitHub Releases** for a newer version, downloads it in the background, and
offers a one-click "Restart & update".

## One-time setup

```powershell
dotnet tool install -g vpk      # the Velopack CLI
```

Install **Rust** from [rustup.rs](https://rustup.rs), then pick the GNU toolchain:

```powershell
rustup default stable-x86_64-pc-windows-gnu
```

GNU rather than MSVC on purpose: it brings its own linker, so you don't need Visual Studio or the
Windows SDK just to build one small DLL. `pack.ps1` builds it for you; you never run cargo by hand.

Make sure `FCAT/AppSecrets.cs` exists locally (it's gitignored, so copy `AppSecrets.example.cs`
and fill in the real ESI client id/secret). It gets baked into the published build.

## The two pieces behind fit statistics

Damage, effective hitpoints and mass come from
[EVEShipFit's dogma engine](https://github.com/EVEShipFit/dogma-engine) (MIT). FCAT does not
implement EVE's dogma itself and should not start. Two things make it work, and a release that
loses either one **still builds, still installs, and silently shows no fit statistics** - so
`pack.ps1` treats both as hard failures rather than warnings.

**`dogma-bridge/`** is a small Rust crate wrapping that engine as `fcat_dogma.dll`, which FCAT
calls in-process. Only the source is committed; `pack.ps1` runs `cargo build --release` before
publishing. Nothing here touches the network - the engine is a pure function over local data.

**`FCAT/Assets/sde.dat`** is CCP's static data: every item, its attributes, and the dogma rules
that turn a fit into numbers. ~9MB, and it **is** committed, so a fresh clone builds a working
app. It compresses to about 2MB inside the installer.

It goes stale when CCP patches - new ships and modules simply will not resolve. Refresh it before
a release if it has been a while (`pack.ps1` warns past 60 days):

```powershell
# EVEShipFit publishes it on npm; no Node needed, it is just a tarball.
$meta = Invoke-RestMethod https://registry.npmjs.org/@eveshipfit/sde
$ver  = $meta.'dist-tags'.latest                       # e.g. 3.3503375.1 - the digits are the EVE build
Invoke-WebRequest $meta.versions.$ver.dist.tarball -OutFile sde.tgz
tar -xzf sde.tgz
Copy-Item package/dist/sde.dat FCAT/Assets/sde.dat -Force
Remove-Item sde.tgz, package -Recurse -Force
```

The app logs the build it loaded at startup, so you can confirm the refresh took:
`INFO [dogma] engine loaded, EVE build 3503375`.

Take `sde.dat` only. The package also carries `names.dat` (15MB) for non-English item names,
which FCAT does not use.

Bumping the engine itself is separate and rarer: change the `esf-dogma-engine` / `esf-data`
versions in `dogma-bridge/Cargo.toml`. If you do, re-check the derived attribute ids in
`DogmaService` against
[sde-patched's `patches/ids.yaml`](https://github.com/EVEShipFit/sde-patched) - they are negative
numbers like `-12` for DPS, and they are assigned per name rather than fixed by the engine.

## Cut a release

1. **Bump the version** in `FCAT/FCAT.csproj` (`<Version>` / `<FileVersion>` /
   `<AssemblyVersion>` - keep all three together).
2. **Pack** (from the repo root):

   ```powershell
   # First ever release:
   .\scripts\pack.ps1 -Version 0.12.0-beta

   # Every release after that, pull the previous one first so users get small delta updates:
   .\scripts\pack.ps1 -Version 0.13.0-beta -Delta
   ```

   Output lands in `.\Releases\`:
   - `FCAT-win-Setup.exe`, the installer to share
   - `*-full.nupkg` (+ `*-delta.nupkg`), `RELEASES`, `releases.*.json`, the updater's feed

3. **Publish the GitHub Release.** Tag it `v<version>` (e.g. `v0.12.0-beta`), mark it
   **pre-release** while the version is `-beta` (the app's updater is prerelease-aware), and
   **upload every file from `.\Releases\`**. The updater reads these assets directly.

   Either do it on github.com (no `gh` CLI needed), or let Velopack push it for you:

   ```powershell
   vpk upload github --repoUrl https://github.com/MifuneSG/FCAT `
       --publish --releaseName "FCAT 0.12.0-beta" --tag v0.12.0-beta `
       --token <github-personal-access-token> --pre
   ```

`pack.ps1` opens the packed `.nupkg` afterwards and fails if `fcat_dogma.dll` or `sde.dat` is
missing, because a release without them looks identical from the outside.

That's it. Installed clients pick up the new version on their next launch.

## The Alliance Auth connector releases separately

`aa-connector/` is a Django plugin alliances install on their own auth. It has its own version in
`fcatconnector/__init__.py` and ships to **PyPI as `aa-fcat-connector`**, so admins install it the
same way they install every other auth plugin:

```bash
pip install aa-fcat-connector
```

Tag the repo **`connector-vX.Y.Z`**, not `vX.Y.Z` - the Discord release workflow only fires on
`v`-prefixed tags, so connector releases don't ping the FC Discord.

### Publishing a connector version

One-time: a PyPI account with an API token, and `pip install build twine`.

```powershell
cd aa-connector
Remove-Item dist, build, *.egg-info -Recurse -Force -ErrorAction SilentlyContinue
python -m build                 # -> dist/*.whl and dist/*.tar.gz
python -m twine check dist/*    # must say PASSED before uploading
python -m twine upload dist/*   # username __token__, password is the API token
```

Bump `__version__` first; PyPI refuses to overwrite a version that already exists, and there is no
way to take one back - only to yank it.

Worth testing the upload against [TestPyPI](https://test.pypi.org) first the very first time:
`python -m twine upload --repository testpypi dist/*`.

### Compatibility

FCAT degrades gracefully against an older connector. The index endpoint advertises which sources
an auth can serve, so anything a connector doesn't know about simply isn't offered and the panels
that read it hide themselves. An FC on connector 0.1.0 gets doctrines and structures; fleet
attendance and SRP appear when their auth is upgraded, with nothing to change in FCAT.

## Verifying the update flow

The updater is a no-op when run from source (`dotnet run`), because `UpdateManager.IsInstalled`
is false. To test it for real: install an older version via its `Setup.exe`, publish a newer
release, launch the installed app, and confirm the UPDATE pill appears in the top bar
(also surfaced under Settings, Updates).

## Code signing & SmartScreen (pending)

Unsigned installers trigger a Windows SmartScreen warning ("unrecognized app"). It's
dismissable (More info, then Run anyway) but looks alarming. There is no free fix: self-signed
certs do not clear SmartScreen. The realistic options:

- Azure Trusted Signing (~$10/mo, requires identity/org validation), the cheapest legit path.
- An EV code-signing certificate, pricier but with instant SmartScreen reputation.

Once you have a cert, `vpk pack` can sign during packing via `--signParams` (signtool args).
Until then we ship unsigned and tell beta users to click through the warning.
