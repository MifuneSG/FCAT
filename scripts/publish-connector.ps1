<#
.SYNOPSIS
    Builds and publishes the Alliance Auth connector to PyPI as `aa-fcat-connector`.

.DESCRIPTION
    Alliance admins install the connector the way they install every other auth plugin:

        pip install aa-fcat-connector

    That only works once a version is on PyPI. This script builds the wheel and sdist in a throwaway
    virtualenv, validates the metadata, and uploads. It never stores your token - twine prompts for
    it, or reads TWINE_PASSWORD if you would rather set it for one shell.

.PARAMETER DryRun
    Build and validate, then stop. Nothing is uploaded and no credentials are needed. Use this to
    check a version before committing to it, because PyPI will not let you replace one.

.PARAMETER TestPyPI
    Upload to test.pypi.org instead of the real index. Worth doing once, the first time.

.EXAMPLE
    .\scripts\publish-connector.ps1 -DryRun
    .\scripts\publish-connector.ps1

.NOTES
    You need a PyPI account with 2FA on, and an API token:
      https://pypi.org/manage/account/token/
    For the FIRST upload the project does not exist yet, so the token has to be account-scoped.
    Afterwards, replace it with one scoped to aa-fcat-connector alone.

    At the prompt: username is literally  __token__  and the password is the whole token,
    including the  pypi-  prefix.
#>
#requires -Version 5
param(
    [switch]$DryRun,
    [switch]$TestPyPI
)

$ErrorActionPreference = "Stop"
$root      = Split-Path $PSScriptRoot -Parent
$connector = Join-Path $root "aa-connector"
$venv      = Join-Path $connector ".publish-venv"

if (-not (Test-Path (Join-Path $connector "pyproject.toml"))) {
    throw "aa-connector/pyproject.toml not found - run this from the FCAT repo."
}

$version = (Select-String -Path (Join-Path $connector "fcatconnector\__init__.py") `
                          -Pattern '__version__\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
Write-Host "==> Connector version $version" -ForegroundColor Cyan

# PyPI refuses to replace a version that already exists, and yanking is the only undo. Check first.
try {
    $published = Invoke-RestMethod "https://pypi.org/pypi/aa-fcat-connector/json" -ErrorAction Stop
    if ($published.releases.PSObject.Properties.Name -contains $version) {
        throw "aa-fcat-connector $version is already on PyPI. Bump __version__ in fcatconnector/__init__.py."
    }
    Write-Host "    already published: $($published.releases.PSObject.Properties.Name -join ', ')" -ForegroundColor DarkGray
} catch [System.Net.WebException] {
    Write-Host "    not on PyPI yet - this will be the first release" -ForegroundColor DarkGray
} catch {
    if ($_.Exception.Message -like "*already on PyPI*") { throw }
    Write-Host "    not on PyPI yet - this will be the first release" -ForegroundColor DarkGray
}

Write-Host "==> Preparing a build environment..." -ForegroundColor Cyan
if (Test-Path $venv) { Remove-Item $venv -Recurse -Force }
python -m venv $venv
if ($LASTEXITCODE -ne 0) { throw "could not create a virtualenv - is Python on PATH?" }

$py = Join-Path $venv "Scripts\python.exe"
& $py -m pip install --quiet --upgrade pip build twine
if ($LASTEXITCODE -ne 0) { throw "could not install build/twine into the virtualenv." }

Write-Host "==> Building..." -ForegroundColor Cyan
Push-Location $connector
try {
    foreach ($stale in @("dist", "build")) {
        if (Test-Path $stale) { Remove-Item $stale -Recurse -Force }
    }
    Get-ChildItem -Filter "*.egg-info" -Directory | Remove-Item -Recurse -Force

    # python -m build writes progress to stderr; PowerShell would call that a failure on its own.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $py -m build 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray } }
    finally { $ErrorActionPreference = $prevEap }
    if ($LASTEXITCODE -ne 0) { throw "python -m build failed." }

    Write-Host "==> Checking the package..." -ForegroundColor Cyan
    & $py -m twine check (Join-Path "dist" "*")
    if ($LASTEXITCODE -ne 0) { throw "twine check failed - do not upload this." }

    # The wheel has to carry the Django template, or the connector page 500s on first open.
    $wheel = Get-ChildItem dist -Filter "*.whl" | Select-Object -First 1
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($wheel.FullName)
    try {
        $needed = @("fcatconnector/views.py", "fcatconnector/templates/fcatconnector/keys.html")
        foreach ($entry in $needed) {
            if (-not ($zip.Entries | Where-Object { $_.FullName -eq $entry })) {
                throw "$entry is missing from the wheel - the plugin would install broken."
            }
        }
        Write-Host "    wheel carries views.py and the keys template" -ForegroundColor DarkGray
    } finally { $zip.Dispose() }

    Get-ChildItem dist | ForEach-Object { Write-Host ("    {0,8:N1} KB  {1}" -f ($_.Length / 1KB), $_.Name) }

    if ($DryRun) {
        Write-Host ""
        Write-Host "Dry run. Nothing uploaded. Artifacts are in aa-connector\dist\." -ForegroundColor Green
        return
    }

    Write-Host "==> Uploading to $(if ($TestPyPI) { 'TestPyPI' } else { 'PyPI' })..." -ForegroundColor Cyan
    Write-Host "    username is  __token__  and the password is your whole API token." -ForegroundColor Yellow
    if ($TestPyPI) { & $py -m twine upload --repository testpypi (Join-Path "dist" "*") }
    else           { & $py -m twine upload (Join-Path "dist" "*") }
    if ($LASTEXITCODE -ne 0) { throw "twine upload failed." }
}
finally {
    Pop-Location
    if (Test-Path $venv) { Remove-Item $venv -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "Published. Admins can now run:" -ForegroundColor Green
Write-Host "    pip install aa-fcat-connector" -ForegroundColor White
Write-Host ""
Write-Host "Tag the repo connector-v$version (NOT v$version - that pings the FC Discord)." -ForegroundColor Yellow
