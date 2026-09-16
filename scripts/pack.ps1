<#
.SYNOPSIS
    Builds an installable, self-updating FCAT release with Velopack.

.DESCRIPTION
    Publishes a self-contained win-x64 build, then runs `vpk pack` to produce the
    installer (Setup.exe) + release assets under .\Releases. Upload ALL of those
    assets to a GitHub Release so the in-app updater can find them.

.EXAMPLE
    # First release (no previous version to delta against):
    .\scripts\pack.ps1 -Version 0.12.0-beta

    # Later release (pull the previous release first so Velopack can ship small delta updates):
    .\scripts\pack.ps1 -Version 0.13.0-beta -Delta

.NOTES
    One-time setup:
      dotnet tool install -g vpk
      Rust, for the dogma bridge:  https://rustup.rs  (the x86_64-pc-windows-gnu toolchain)

    Requires FCAT/AppSecrets.cs to be present locally (gitignored).

    The dogma bridge (dogma-bridge/) is a native DLL built from Rust. It is NOT committed - only
    its source is - so this script builds it before publishing. Without it the app still runs and
    still packages; fit statistics simply switch themselves off, which is a quiet way to ship a
    broken release, so this treats a missing cargo as a hard stop rather than a warning.
#>
#requires -Version 5
param(
    [Parameter(Mandatory = $true)][string]$Version,                # SemVer, e.g. 0.12.0-beta
    [string]$Repo = "https://github.com/MifuneSG/FCAT",
    [switch]$Delta                                                 # download previous release for delta generation
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$publish = Join-Path $root "publish"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk not found. Install it once with:  dotnet tool install -g vpk"
}

# The dogma bridge: EVEShipFit's engine as a native DLL. FCAT.csproj copies it into the build
# output, so it has to exist BEFORE the publish below or the release ships without fit statistics.
Write-Host "==> Building the dogma bridge (Rust)..." -ForegroundColor Cyan
$cargo = Get-Command cargo -ErrorAction SilentlyContinue
if (-not $cargo) {
    $cargoHome = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"
    if (Test-Path $cargoHome) { $cargo = $cargoHome } else {
        throw "cargo not found. Install Rust from https://rustup.rs, then: rustup default stable-x86_64-pc-windows-gnu"
    }
}
# cargo writes its progress to stderr, and with $ErrorActionPreference = Stop PowerShell turns any
# native stderr into a terminating error - so a SUCCESSFUL build would abort the release. Drop the
# preference across the call and judge it by its exit code, which is the only honest signal.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    & $cargo build --release --manifest-path (Join-Path $root "dogma-bridge\Cargo.toml") 2>&1 |
        ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
} finally {
    $ErrorActionPreference = $prevEap
}
if ($LASTEXITCODE -ne 0) { throw "cargo build failed - the dogma bridge did not build." }

$bridge = Join-Path $root "dogma-bridge\target\release\fcat_dogma.dll"
if (-not (Test-Path $bridge)) { throw "fcat_dogma.dll missing after a successful cargo build." }
Write-Host ("    fcat_dogma.dll  {0:N0} KB" -f ((Get-Item $bridge).Length / 1KB))

# sde.dat is CCP's static data, which the engine reads to know what every item does. It IS
# committed, so a clone builds a working app - but it goes stale when CCP patches, so check it.
$sde = Join-Path $root "FCAT\Assets\sde.dat"
if (-not (Test-Path $sde)) {
    throw "FCAT/Assets/sde.dat missing. See RELEASING.md for how to refresh it."
}
$sdeAge = (Get-Date) - (Get-Item $sde).LastWriteTime
if ($sdeAge.Days -gt 60) {
    Write-Host ("    WARNING: sde.dat is {0} days old - EVE has almost certainly patched since." -f $sdeAge.Days) -ForegroundColor Yellow
    Write-Host "             Refresh it before release; see RELEASING.md." -ForegroundColor Yellow
}

Write-Host "==> Publishing FCAT $Version (self-contained win-x64)..." -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish FCAT/FCAT.csproj -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

if ($Delta) {
    Write-Host "==> Downloading previous release for delta generation..." -ForegroundColor Cyan
    vpk download github --repoUrl $Repo --pre
}

Write-Host "==> Packing installer + release assets ($Version)..." -ForegroundColor Cyan
vpk pack `
    --packId FCAT `
    --packVersion $Version `
    --packDir $publish `
    --mainExe FCAT.exe `
    --packTitle "FCAT - Fleet Commander Assistance Tool" `
    --packAuthors "MifuneSG" `
    --icon "FCAT/fcat.ico"
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

# A release that packaged without the engine looks identical from the outside and silently has no
# fit statistics, so confirm rather than assume.
$packed = Get-ChildItem (Join-Path $root "Releases") -Filter "*-full.nupkg" |
          Sort-Object LastWriteTime | Select-Object -Last 1
if ($packed) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($packed.FullName)
    try {
        foreach ($need in @("fcat_dogma.dll", "sde.dat")) {
            if (-not ($zip.Entries | Where-Object { $_.Name -eq $need })) {
                throw "$need is NOT in $($packed.Name) - the release would ship with fit statistics off."
            }
            Write-Host "    packaged: $need" -ForegroundColor DarkGray
        }
    } finally { $zip.Dispose() }
}

Write-Host ""
Write-Host "Done. Artifacts are in .\Releases :" -ForegroundColor Green
Write-Host "  - FCAT-win-Setup.exe   <- the installer to share / pin to the release"
Write-Host "  - *-full.nupkg (+ *-delta.nupkg) and RELEASES / releases.*.json"
Write-Host ""
Write-Host "Upload ALL files in .\Releases to a GitHub Release tagged v$Version." -ForegroundColor Yellow
Write-Host "See RELEASING.md for the full checklist." -ForegroundColor Yellow
