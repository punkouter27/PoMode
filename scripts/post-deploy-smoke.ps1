#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks the live PoMode site after the shared deploy.yml health gate has passed.

.DESCRIPTION
    Every API check asserts JSON, not just a 200: MapFallbackToFile serves the SPA shell for anything
    unmatched, so a status-only check passes against a route that does not exist and against any typo.
    Each check retries, because the container being replaced can still answer while the new one warms
    (roughly 20 to 25 seconds per the platform log). Exits non-zero on any failure.

.EXAMPLE
    ./scripts/post-deploy-smoke.ps1 -BaseUrl https://<app>.azurewebsites.net
#>
param([Parameter(Mandatory)] [string] $BaseUrl)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')

function Test-Json([string] $Path) {
    $body = ''
    foreach ($attempt in 1..12) {
        try {
            $body = (Invoke-WebRequest "$BaseUrl$Path" -Headers @{ Accept = 'application/json' } -TimeoutSec 20 -SkipHttpErrorCheck).Content
        } catch { $body = '' }
        if ($body -is [byte[]]) { $body = [Text.Encoding]::UTF8.GetString($body) }
        if ("$body".TrimStart().StartsWith('{') -or "$body".TrimStart().StartsWith('[')) {
            Write-Host "$Path OK after $attempt attempt(s)"
            return
        }
        Start-Sleep -Seconds 10
    }
    $tail = "$body"; if ($tail.Length -gt 80) { $tail = $tail.Substring(0, 80) }
    Write-Host "::error::$Path never returned JSON. Last bytes: $tail"
    exit 1
}

Test-Json '/diag'                              # diagnostics report
Test-Json '/api/analysis/executors'            # pipeline executors
Test-Json '/api/modal-melodies/progressions'   # Mode Lab data the page cannot render without

# SPA shell still serves its routes
try { $code = (Invoke-WebRequest "$BaseUrl/modes" -TimeoutSec 20 -SkipHttpErrorCheck).StatusCode } catch { $code = 0 }
if ($code -ne 200) { Write-Host "::error::/modes returned $code, expected 200"; exit 1 }

Write-Host 'Smoke tests passed.'
