#!/usr/bin/env pwsh
<#
.SYNOPSIS
    PoMode's extra build-time checks, run by the shared deploy.yml after the unit tests.

.DESCRIPTION
    Runs from the repo root with the solution already built in Release. Any failure exits non-zero.
      1. Whitespace formatting as `dotnet format whitespace` writes it (fix: dotnet format whitespace PoMode.slnx).
      2. Test caps per suite (scripts/test-caps.ps1: 100 Unit / 50 Integration / 25 E2EAPI / 25 E2EUI).
      3. Every infra/*.bicep compiles to ARM (catches template errors; nothing is deployed).
#>
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

Write-Host '== Format (whitespace)'
dotnet format whitespace PoMode.slnx --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) { Write-Error 'Whitespace differs from dotnet format; run: dotnet format whitespace PoMode.slnx'; exit 1 }

Write-Host '== Test caps'
& ./scripts/test-caps.ps1 -Configuration Release
if (-not $?) { exit 1 }

Write-Host '== Bicep compile'
foreach ($file in Get-ChildItem infra -Filter *.bicep -Recurse) {
    Write-Host "-> $($file.FullName)"
    az bicep build --file $file.FullName --stdout | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "Bicep failed to compile: $($file.Name)"; exit 1 }
}

Write-Host 'Repo checks passed.'
