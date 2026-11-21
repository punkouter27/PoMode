#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the E2EUI browser tier against the trimmed Release publish, the same build that deploys.

.DESCRIPTION
    Publishes src/PoMode.Api -c Release (client trimmed, as in deploy.yml) to a temp folder, then runs
    tests/PoMode.E2EUI with POMODE_E2EUI_APP_DIR pointing at it, so AppFixture boots the published
    PoMode.API.dll instead of the Debug build. .NET 10 bakes the WASM environment in at publish time,
    so the client is published as Development to match the server the fixture starts. The fixture
    stops every server it started; the publish folder is deleted at the end.

.PARAMETER Filter
    Optional dotnet test --filter expression.

.EXAMPLE
    pwsh scripts/run-e2eui.ps1
#>
[CmdletBinding()]
param([string]$Filter)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path ([IO.Path]::GetTempPath()) "pomode-e2eui-publish-$PID"

Get-Process -Name 'PoMode.API' -ErrorAction SilentlyContinue | Stop-Process -Force

try {
    dotnet build (Join-Path $repo 'tests/PoMode.E2EUI') -c Debug
    if ($LASTEXITCODE) { throw 'build failed' }

    $playwright = Join-Path $repo 'tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1'
    & pwsh $playwright install chromium
    if ($LASTEXITCODE) { throw 'playwright install failed' }

    dotnet publish (Join-Path $repo 'src/PoMode.Api') -c Release -o $publishDir -p:WasmApplicationEnvironmentName=Development
    if ($LASTEXITCODE) { throw 'publish failed' }

    $env:POMODE_E2EUI_APP_DIR = $publishDir
    $testArgs = @('test', (Join-Path $repo 'tests/PoMode.E2EUI'), '--no-build')
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    dotnet @testArgs
    $exit = $LASTEXITCODE
}
finally {
    Remove-Item Env:POMODE_E2EUI_APP_DIR -ErrorAction SilentlyContinue
    Get-Process -Name 'PoMode.API' -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $exit
