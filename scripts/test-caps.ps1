#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails when a test suite holds more tests than its cap: 100 Unit, 50 Integration, 25 E2EAPI,
    25 E2EUI (CLAUDE.md, "Test suites are capped").

.DESCRIPTION
    Counts the way the runner counts: `dotnet test --list-tests`, so every InlineData row is a test.
    Nothing runs, so Azurite and the Playwright browsers are not needed. Build first; the script
    passes --no-build so it lists exactly what was just built.

.PARAMETER Configuration
    The build configuration to list from. CI builds Release; a local `dotnet build` is Debug.
#>
param([string] $Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$caps = [ordered]@{ Unit = 100; Integration = 50; E2EAPI = 25; E2EUI = 25 }
$over = 0

foreach ($suite in $caps.Keys) {
    $listed = dotnet test (Join-Path $root "tests/PoMode.$suite") --no-build -c $Configuration --list-tests 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Listing PoMode.$suite failed:`n$($listed -join "`n")" }
    $count = @($listed | Where-Object { $_ -match '^\s{4}\S' }).Count
    $cap = $caps[$suite]
    $mark = if ($count -gt $cap) { $over++; 'OVER' } else { 'ok' }
    '{0,-12} {1,4} / {2,-4} {3}' -f $suite, $count, $cap, $mark
}

if ($over -gt 0) { Write-Error "$over suite(s) over their cap; a new test has to earn its place against an existing one." }
