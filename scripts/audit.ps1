<#
.SYNOPSIS
    Checks FCAT for the failure classes that a green build does not catch.

.DESCRIPTION
    Every one of these checks exists because the thing it looks for actually shipped, or nearly did.
    None of them are style opinions; each maps to a bug that cost an evening.

      1. Unbalanced XAML comments. `DataTemplate` content is parsed when the page is first opened,
         not when the project builds, so an orphaned comment tail passes the build, passes an XML
         parse, launches clean, and throws the instant an FC opens that tab.

      2. XAML that parses as XML at all. Same reason.

      3. Catch blocks that discard the error without a log line or a comment saying why. "No kills
         on zKillboard" and "no doctrine fits" both turned out to be exceptions nobody could see.

      4. Duplicate private helpers. Four view models each grew their own copy of the same name
         cache, and two panels ended up paying ESI separately for the same ids.

      5. Build warnings. The repo builds at zero; one is a regression.

    Run it before a release, or any time a change touched more than one panel.

.EXAMPLE
    .\scripts\audit.ps1
#>
#requires -Version 5
param([switch]$Quiet)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$app  = Join-Path $root "FCAT"
$fail = 0

function Note($text, $colour = "Gray") { if (-not $Quiet) { Write-Host $text -ForegroundColor $colour } }
function Problem($text) { Write-Host "  $text" -ForegroundColor Red; $script:fail++ }

Note "==> XAML comment balance" Cyan
foreach ($f in Get-ChildItem $app -Recurse -Filter *.xaml) {
    $depth = 0; $line = 0
    foreach ($l in Get-Content $f.FullName) {
        $line++
        $depth += ([regex]::Matches($l, '<!--')).Count - ([regex]::Matches($l, '-->')).Count
        if ($depth -lt 0) { Problem "$($f.Name):$line closes a comment that was never opened"; break }
    }
    if ($depth -gt 0) { Problem "$($f.Name) ends inside a comment - a page will throw when opened" }
}

Note "==> XAML is well-formed" Cyan
foreach ($f in Get-ChildItem $app -Recurse -Filter *.xaml) {
    try { [xml](Get-Content $f.FullName -Raw) | Out-Null }
    catch { Problem "$($f.Name) is not valid XML: $($_.Exception.Message)" }
}

Note "==> Swallowed exceptions" Cyan
$swallow = [regex]'catch\s*(?:\([^)]*\))?\s*\{([^{}]*)\}'
foreach ($f in Get-ChildItem $app -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    $text = Get-Content $f.FullName -Raw
    foreach ($m in $swallow.Matches($text)) {
        $body = $m.Groups[1].Value
        # A log line, a rethrow, or a cancellation are all deliberate.
        if ($body -match 'Log\.|throw' -or $m.Value -match 'OperationCanceledException') { continue }
        if ($body -notmatch '^[\s]*$' -and $body -notmatch '^\s*return[^;]*;\s*$') { continue }

        $line = ($text.Substring(0, $m.Index) -split "`n").Count
        # A comment saying why counts wherever it sits: inside the braces, trailing the line, or
        # on the two lines above. That is how the rest of this codebase already writes them.
        $lines = $text -split "`n"
        $near = ($lines[[Math]::Max(0, $line - 3) .. [Math]::Min($lines.Count - 1, $line)] -join ' ')
        if ($body -match '//' -or $near -match '//') { continue }
        if ($true) {
            Problem "$($f.Name):$line discards an exception with no log line and no reason given"
        }
    }
}

Note "==> Duplicate private helpers" Cyan
$bodies = @{}
$method = [regex]'(?ms)\n    (?:private|protected)[^\n(]*\s(\w+)\s*\([^)]*\)\s*\r?\n?\s*\{(.*?)\r?\n    \}'
foreach ($f in Get-ChildItem $app -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    foreach ($m in $method.Matches((Get-Content $f.FullName -Raw))) {
        $body = ($m.Groups[2].Value -replace '\s+', ' ').Trim()
        if ($body.Length -lt 150) { continue }
        if (-not $bodies.ContainsKey($body)) { $bodies[$body] = @() }
        $bodies[$body] += "$($f.Name).$($m.Groups[1].Value)"
    }
}
foreach ($k in $bodies.Keys) {
    if ($bodies[$k].Count -gt 1) { Problem "identical bodies: $($bodies[$k] -join ', ')" }
}

Note "==> Build warnings" Cyan
$build = & dotnet build (Join-Path $app "FCAT.csproj") -v q --nologo 2>&1
$warnings = @($build | Where-Object { $_ -match ': warning ' })
if ($warnings.Count -gt 0) {
    foreach ($w in $warnings | Select-Object -First 10) { Problem $w }
}

Write-Host ""
if ($fail -eq 0) {
    Write-Host "Clean. Note this checks what a build cannot - it is not a substitute for opening every" -ForegroundColor Green
    Write-Host "page you touched, because WPF only applies a page's templates when that page is shown." -ForegroundColor Green
    exit 0
}
Write-Host "$fail problem(s) above." -ForegroundColor Red
exit 1
