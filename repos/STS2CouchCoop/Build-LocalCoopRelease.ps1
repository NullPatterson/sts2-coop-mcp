param(
    [string]$GameRoot = (Join-Path $PSScriptRoot '..'),
    [string]$Version = '0.1.0',
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts\release'),
    [switch]$IncludeSymbols,
    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-LocalCoopReleaseFileToken {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $invalid = [System.IO.Path]::GetInvalidFileNameChars()
    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $Value.ToCharArray()) {
        if ($invalid -contains $character) {
            [void]$builder.Append('_')
        }
        else {
            [void]$builder.Append($character)
        }
    }

    $builder.ToString()
}

function Resolve-LocalCoopReleaseGameRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameRoot
    )

    $resolvedGameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
    $requiredFiles = @(
        'release_info.json',
        'data_sts2_windows_x86_64\sts2.dll',
        'data_sts2_windows_x86_64\0Harmony.dll'
    )

    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $resolvedGameRoot $relativePath
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required STS2 file not found: $path"
        }
    }

    $resolvedGameRoot
}

function Get-LocalCoopReleaseInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameRoot
    )

    $releaseInfoPath = Join-Path $GameRoot 'release_info.json'
    Get-Content -Raw -LiteralPath $releaseInfoPath | ConvertFrom-Json
}

function Update-LocalCoopReleaseManifestVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw "Manifest not found: $ManifestPath"
    }

    $manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
    $manifest.version = $Version
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath
}

function Assert-LocalCoopReleaseLayout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$StageRoot
    )

    $packageRoot = Join-Path $StageRoot 'LocalCoop'
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "Release stage must contain a LocalCoop directory: $packageRoot"
    }

    $requiredFiles = @(
        'LocalCoop.json',
        'LocalCoop.dll',
        'LocalCoop.Protocol.dll',
        'KNOWN_ISSUES.md',
        'broker\LocalCoop.Broker.Cli.exe',
        'Start-LocalCoop2Players.bat',
        'Start-LocalCoop3Players.bat',
        'Start-LocalCoop4Players.bat',
        'Start-LocalCoopClients.ps1',
        'Start-LocalCoopTwoClient.ps1'
    )

    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $packageRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required release file not found: $relativePath"
        }
    }
}

function Assert-LocalCoopReleaseFilePolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageRoot,
        [switch]$IncludeSymbols
    )

    if (-not (Test-Path -LiteralPath $PackageRoot -PathType Container)) {
        throw "Release package root not found: $PackageRoot"
    }

    $forbiddenExactNames = @(
        'sts2.dll',
        '0Harmony.dll',
        'GodotSharp.dll',
        'SlayTheSpire2.exe',
        'SlayTheSpire2.pck'
    )

    $forbiddenPatterns = @(
        'enable-local*.txt',
        'localcoop-*.txt',
        '.localcoop-*',
        '*.log',
        '*.deps.json',
        '*.runtimeconfig.json'
    )

    if (-not $IncludeSymbols) {
        $forbiddenPatterns += '*.pdb'
    }

    $resolvedPackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\', '/')
    foreach ($item in Get-ChildItem -LiteralPath $PackageRoot -Recurse -Force) {
        $relativePath = $item.FullName
        if ($item.FullName.StartsWith($resolvedPackageRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $relativePath = $item.FullName.Substring($resolvedPackageRoot.Length).TrimStart('\', '/')
        }

        foreach ($name in $forbiddenExactNames) {
            if ($item.Name -eq $name) {
                throw "Forbidden release artifact: $relativePath"
            }
        }

        foreach ($pattern in $forbiddenPatterns) {
            if ($item.Name -like $pattern) {
                throw "Forbidden release artifact: $relativePath"
            }
        }
    }
}

function Invoke-LocalCoopDotNet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FailureMessage Exit code: $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-LocalCoopReleasePublicReadiness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageRoot
    )

    $manifestPath = Join-Path $PackageRoot 'LocalCoop.json'
    $readmePath = Join-Path $PackageRoot 'README.md'

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Public release manifest not found: $manifestPath"
    }

    if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
        throw "Public release README not found: $readmePath"
    }

    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.author -ne 'Bahne') {
        throw "Public release manifest author must be 'Bahne'."
    }

    $description = [string]$manifest.description
    if ($description.IndexOf('Experimental alpha', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Public release manifest description must include 'Experimental alpha'."
    }

    $readme = Get-Content -Raw -LiteralPath $readmePath
    $requiredReadmeText = @(
        'Experimental Alpha',
        'Slay the Spire 2 v0.103.3',
        'Controller/mouse cross-play is not supported in this alpha because STS2 does not split gameplay input between mouse and controller users',
        'GitHub Issues and Pull Requests are strongly preferred',
        'LocalCoop does not include or license Slay the Spire 2 assets or binaries'
    )

    foreach ($requiredText in $requiredReadmeText) {
        if ($readme.IndexOf($requiredText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Public release README must include: $requiredText"
        }
    }

    $forbiddenReadmeText = @(
        'orphan branch',
        'Current Slice',
        'hold/paneling-ui-wip',
        'Manual Four-Client Smoke',
        'transport seam probe'
    )

    foreach ($forbiddenText in $forbiddenReadmeText) {
        if ($readme.IndexOf($forbiddenText, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Public release README must not include developer-only text: $forbiddenText"
        }
    }
}

function Invoke-LocalCoopReleaseBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameRoot,
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$OutputRoot,
        [switch]$IncludeSymbols,
        [switch]$SkipRestore
    )

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'Version must not be blank.'
    }

    $repoRoot = $PSScriptRoot
    $resolvedGameRoot = Resolve-LocalCoopReleaseGameRoot -GameRoot $GameRoot
    $releaseInfo = Get-LocalCoopReleaseInfo -GameRoot $resolvedGameRoot
    $sts2Version = [string]$releaseInfo.version
    if ([string]::IsNullOrWhiteSpace($sts2Version)) {
        throw 'release_info.json does not contain a version.'
    }

    $resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    $stageRoot = Join-Path $resolvedOutputRoot 'stage'
    $packageRoot = Join-Path $stageRoot 'LocalCoop'
    $brokerRoot = Join-Path $packageRoot 'broker'
    $zipName = 'LocalCoop-v{0}-sts2-{1}-win-x64.zip' -f `
        (ConvertTo-LocalCoopReleaseFileToken -Value $Version), `
        (ConvertTo-LocalCoopReleaseFileToken -Value $sts2Version)
    $zipPath = Join-Path $resolvedOutputRoot $zipName

    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $brokerRoot -Force | Out-Null

    $modOutputPath = $packageRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $SkipRestore) {
        $restorePlans = @(
            @('src\LocalCoop.Mod\LocalCoop.Mod.csproj'),
            @('src\LocalCoop.Protocol\LocalCoop.Protocol.csproj'),
            @('src\LocalCoop.Broker.Cli\LocalCoop.Broker.Cli.csproj', '-r', 'win-x64')
        )
        foreach ($restorePlan in $restorePlans) {
            $project = $restorePlan[0]
            $restoreArguments = @('restore') + $restorePlan + @('--disable-parallel')
            Invoke-LocalCoopDotNet `
                -WorkingDirectory $repoRoot `
                -FailureMessage "LocalCoop restore failed for $project." `
                -Arguments $restoreArguments
        }
    }

    Invoke-LocalCoopDotNet `
        -WorkingDirectory $repoRoot `
        -FailureMessage 'LocalCoop mod build failed.' `
        -Arguments @(
            'build',
            'src\LocalCoop.Mod\LocalCoop.Mod.csproj',
            '-c',
            'Release',
            '--no-restore',
            "-p:Sts2GameRoot=$resolvedGameRoot",
            "-p:LocalCoopModOutputPath=$modOutputPath"
        )

    Invoke-LocalCoopDotNet `
        -WorkingDirectory $repoRoot `
        -FailureMessage 'LocalCoop protocol build failed.' `
        -Arguments @(
            'build',
            'src\LocalCoop.Protocol\LocalCoop.Protocol.csproj',
            '-c',
            'Release',
            '--no-restore',
            "-p:OutputPath=$modOutputPath"
        )

    Invoke-LocalCoopDotNet `
        -WorkingDirectory $repoRoot `
        -FailureMessage 'LocalCoop broker publish failed.' `
        -Arguments @(
            'publish',
            'src\LocalCoop.Broker.Cli\LocalCoop.Broker.Cli.csproj',
            '-c',
            'Release',
            '-r',
            'win-x64',
            '--no-restore',
            '--self-contained',
            'true',
            '-p:PublishSingleFile=true',
            '-p:PublishTrimmed=false',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-o',
            $brokerRoot
        )

    Copy-Item -LiteralPath (Join-Path $repoRoot 'Start-LocalCoopClients.ps1') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Start-LocalCoopTwoClient.ps1') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Start-LocalCoop2Players.bat') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Start-LocalCoop3Players.bat') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Start-LocalCoop4Players.bat') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'KNOWN_ISSUES.md') -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'release\LocalCoop.json') -Destination (Join-Path $packageRoot 'LocalCoop.json') -Force
    Update-LocalCoopReleaseManifestVersion -ManifestPath (Join-Path $packageRoot 'LocalCoop.json') -Version $Version

    if (-not $IncludeSymbols) {
        Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -Filter '*.pdb' |
            Remove-Item -Force
    }

    Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -Filter '*.deps.json' |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -Filter '*.runtimeconfig.json' |
        Remove-Item -Force

    Assert-LocalCoopReleaseLayout -StageRoot $stageRoot
    Assert-LocalCoopReleaseFilePolicy -PackageRoot $packageRoot -IncludeSymbols:$IncludeSymbols
    Assert-LocalCoopReleasePublicReadiness -PackageRoot $packageRoot

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -Force

    [pscustomobject]@{
        StageRoot = $stageRoot
        PackageRoot = $packageRoot
        ZipPath = $zipPath
        Version = $Version
        Sts2Version = $sts2Version
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-LocalCoopReleaseBuild `
        -GameRoot $GameRoot `
        -Version $Version `
        -OutputRoot $OutputRoot `
        -IncludeSymbols:$IncludeSymbols `
        -SkipRestore:$SkipRestore
}
