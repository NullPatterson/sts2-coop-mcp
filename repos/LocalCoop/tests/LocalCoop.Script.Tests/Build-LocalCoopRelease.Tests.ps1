Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$scriptPath = Join-Path $repoRoot 'Build-LocalCoopRelease.ps1'

if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "Release build script not found: $scriptPath"
}

. $scriptPath

function Assert-Equal($actual, $expected, [string]$message) {
    if ($actual -ne $expected) {
        throw "$message Expected '$expected' but got '$actual'."
    }
}

function Assert-True($condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Assert-Throws([scriptblock]$scriptBlock, [string]$expectedText, [string]$message) {
    try {
        & $scriptBlock
    }
    catch {
        if ($_.Exception.Message.Contains($expectedText)) {
            return
        }

        throw "$message Expected exception containing '$expectedText' but got '$($_.Exception.Message)'."
    }

    throw "$message Expected exception containing '$expectedText' but no exception was thrown."
}

function Set-TestPublicManifest([string]$manifestPath, [string]$author = 'Bahne', [string]$description = 'Experimental alpha same-machine local co-op launcher and transport bridge for Slay the Spire 2.') {
    [pscustomobject]@{
        id = 'LocalCoop'
        name = 'LocalCoop'
        author = $author
        description = $description
        version = '0.1.0'
        has_pck = $false
        has_dll = $true
        dependencies = @()
        affects_gameplay = $true
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath
}

function Set-TestPublicReadme([string]$readmePath) {
    @'
# LocalCoop for Slay the Spire 2

LocalCoop is an Experimental Alpha tested against Slay the Spire 2 v0.103.3 on Windows x64.

Controller/mouse cross-play is not supported in this alpha because STS2 does not split gameplay input between mouse and controller users.

GitHub Issues and Pull Requests are strongly preferred.

LocalCoop does not include or license Slay the Spire 2 assets or binaries.
'@ | Set-Content -LiteralPath $readmePath
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('localcoop-release-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    $manifestPath = Join-Path $tempRoot 'LocalCoop.json'
    Set-TestPublicManifest -manifestPath $manifestPath

    Update-LocalCoopReleaseManifestVersion -ManifestPath $manifestPath -Version '0.2.3'
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    Assert-Equal $manifest.id 'LocalCoop' 'Manifest id should be preserved.'
    Assert-Equal $manifest.name 'LocalCoop' 'Manifest name should be preserved.'
    Assert-Equal $manifest.version '0.2.3' 'Manifest version should be updated.'
    Assert-Equal $manifest.has_dll $true 'Manifest has_dll flag should be preserved.'

    $stageRoot = Join-Path $tempRoot 'stage'
    $packageRoot = Join-Path $stageRoot 'LocalCoop'
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'broker') -Force | Out-Null
    $packageManifestPath = Join-Path $packageRoot 'LocalCoop.json'
    $packageReadmePath = Join-Path $packageRoot 'README.md'
    Set-TestPublicManifest -manifestPath $packageManifestPath
    Set-TestPublicReadme -readmePath $packageReadmePath
    Set-Content -LiteralPath (Join-Path $packageRoot 'KNOWN_ISSUES.md') -Value 'Known issues'
    Set-Content -LiteralPath (Join-Path $packageRoot 'LocalCoop.dll') -Value 'mod'
    Set-Content -LiteralPath (Join-Path $packageRoot 'LocalCoop.Protocol.dll') -Value 'protocol'
    Set-Content -LiteralPath (Join-Path $packageRoot 'broker\LocalCoop.Broker.Cli.exe') -Value 'broker'
    Set-Content -LiteralPath (Join-Path $packageRoot 'Start-LocalCoop2Players.bat') -Value 'launcher'
    Set-Content -LiteralPath (Join-Path $packageRoot 'Start-LocalCoop3Players.bat') -Value 'launcher'
    Set-Content -LiteralPath (Join-Path $packageRoot 'Start-LocalCoop4Players.bat') -Value 'launcher'
    Set-Content -LiteralPath (Join-Path $packageRoot 'Start-LocalCoopClients.ps1') -Value 'launcher'
    Set-Content -LiteralPath (Join-Path $packageRoot 'Start-LocalCoopTwoClient.ps1') -Value 'launcher'

    Assert-LocalCoopReleaseLayout -StageRoot $stageRoot
    Assert-LocalCoopReleaseFilePolicy -PackageRoot $packageRoot
    Assert-LocalCoopReleasePublicReadiness -PackageRoot $packageRoot

    Set-TestPublicManifest -manifestPath $packageManifestPath -author 'bahne + Codex'
    Assert-Throws {
        Assert-LocalCoopReleasePublicReadiness -PackageRoot $packageRoot
    } "Public release manifest author must be 'Bahne'." 'Public readiness should reject non-public manifest author.'
    Set-TestPublicManifest -manifestPath $packageManifestPath

    Set-TestPublicManifest -manifestPath $packageManifestPath -description 'Portable test manifest.'
    Assert-Throws {
        Assert-LocalCoopReleasePublicReadiness -PackageRoot $packageRoot
    } "Public release manifest description must include 'Experimental alpha'." 'Public readiness should reject non-alpha manifest description.'
    Set-TestPublicManifest -manifestPath $packageManifestPath

    Set-Content -LiteralPath $packageReadmePath -Value '# Current Slice'
    Assert-Throws {
        Assert-LocalCoopReleasePublicReadiness -PackageRoot $packageRoot
    } 'Public release README must include: Experimental Alpha' 'Public readiness should reject developer README content.'
    Set-TestPublicReadme -readmePath $packageReadmePath

    foreach ($forbiddenName in @(
        'sts2.dll',
        '0Harmony.dll',
        'GodotSharp.dll',
        'SlayTheSpire2.exe',
        'SlayTheSpire2.pck',
        'enable-local-broker.txt',
        'enable-local-injection.txt',
        'localcoop-host-0-events.txt',
        'localcoop-transport-probe-client-0.txt',
        '.localcoop-clients',
        'broker.log',
        'LocalCoop.Protocol.deps.json',
        'LocalCoop.Protocol.runtimeconfig.json',
        'LocalCoop.pdb'
    )) {
        $candidate = Join-Path $packageRoot $forbiddenName
        if ($forbiddenName -eq '.localcoop-clients') {
            New-Item -ItemType Directory -Path $candidate -Force | Out-Null
        }
        else {
            Set-Content -LiteralPath $candidate -Value 'forbidden'
        }

        Assert-Throws {
            Assert-LocalCoopReleaseFilePolicy -PackageRoot $packageRoot
        } $forbiddenName "Release file policy should reject $forbiddenName."

        if (Test-Path -LiteralPath $candidate) {
            Remove-Item -LiteralPath $candidate -Recurse -Force
        }
    }

    Set-Content -LiteralPath (Join-Path $packageRoot 'LocalCoop.pdb') -Value 'symbols'
    Assert-LocalCoopReleaseFilePolicy -PackageRoot $packageRoot -IncludeSymbols

    Write-Host 'Build-LocalCoopRelease.Tests.ps1 passed.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
