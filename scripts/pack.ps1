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
    One-time setup:  dotnet tool install -g vpk
    Requires FCAT/AppSecrets.cs to be present locally (gitignored).
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

Write-Host ""
Write-Host "Done. Artifacts are in .\Releases :" -ForegroundColor Green
Write-Host "  - FCAT-win-Setup.exe   <- the installer to share / pin to the release"
Write-Host "  - *-full.nupkg (+ *-delta.nupkg) and RELEASES / releases.*.json"
Write-Host ""
Write-Host "Upload ALL files in .\Releases to a GitHub Release tagged v$Version." -ForegroundColor Yellow
Write-Host "See RELEASING.md for the full checklist." -ForegroundColor Yellow
