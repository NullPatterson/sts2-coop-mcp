Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$scriptPath = Join-Path $repoRoot 'Start-LocalCoop4Players.bat'

if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "Four-player launcher script not found: $scriptPath"
}

$scriptContent = Get-Content -Raw -LiteralPath $scriptPath
if ($scriptContent -notmatch 'Start-LocalCoopClients\.ps1') {
    throw 'Four-player launcher should invoke Start-LocalCoopClients.ps1.'
}

if ($scriptContent -notmatch '-ClientCount\s+4') {
    throw 'Four-player launcher should pass -ClientCount 4.'
}

if ($scriptContent -match '-ControllerDevices') {
    throw 'Four-player launcher should request dynamic controller assignment without fixed device indices.'
}

if ($scriptContent -notmatch '%~dp0') {
    throw 'Four-player launcher should resolve Start-LocalCoopClients.ps1 relative to its own folder.'
}

Write-Host 'Start-LocalCoop4Players.Tests.ps1 passed.'
