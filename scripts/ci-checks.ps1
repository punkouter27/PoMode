#!/usr/bin/env pwsh
<#
.SYNOPSIS
    PoMode's checks that the deploy workflow does not run; run locally before a push.

.DESCRIPTION
    Runs from the repo root with the solution already built in Release. Any failure exits non-zero.
    (Formatting is checked by the deploy itself: dotnet format PoMode.slnx --verify-no-changes.)
      1. Test caps per suite (scripts/test-caps.ps1: 100 Unit / 50 Integration / 25 E2EAPI / 25 E2EUI).
      2. Every infra/*.bicep compiles to ARM (catches template errors; nothing is deployed).
      3. The mobile app builds for Android (it is outside the deploy, so nothing else would notice a
         Shared change that breaks it). Skipped when the MAUI workload is not installed.
#>
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

Write-Host '== Test caps'
& ./scripts/test-caps.ps1 -Configuration Release
if (-not $?) { exit 1 }

Write-Host '== Bicep compile'
foreach ($file in Get-ChildItem infra -Filter *.bicep -Recurse) {
    Write-Host "-> $($file.FullName)"
    az bicep build --file $file.FullName --stdout | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "Bicep failed to compile: $($file.Name)"; exit 1 }
}

Write-Host '== Mobile build'
if ((dotnet workload list) -match '^\s*maui') {
    dotnet build src/PoMode.Mobile/PoMode.Mobile.csproj -c Release -f net10.0-android -v q
    if ($LASTEXITCODE -ne 0) { Write-Error 'PoMode.Mobile does not build.'; exit 1 }
} else {
    Write-Host 'MAUI workload not installed; skipped.'
}

Write-Host 'Repo checks passed.'
