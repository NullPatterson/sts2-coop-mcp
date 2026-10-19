Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$scriptPath = Join-Path $repoRoot 'Start-LocalCoopClients.ps1'

if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "Startup script not found: $scriptPath"
}

$scriptContent = Get-Content -Raw -LiteralPath $scriptPath
if ($scriptContent -notmatch '\[int\]\$WindowPlacementStabilizationSeconds\s*=\s*15') {
    throw 'Start-LocalCoopClients.ps1 should keep placing windows during the game startup resize window by default.'
}

if ($scriptContent -notmatch '\[int\]\$WindowPlacementReadinessTimeoutSeconds\s*=\s*90') {
    throw 'Start-LocalCoopClients.ps1 should wait for LocalCoop startup readiness before placing windows by default.'
}

if ($scriptContent -notmatch '\[int\]\$WindowPlacementStartupDelaySeconds\s*=\s*0') {
    throw 'Start-LocalCoopClients.ps1 should not use a blind startup delay by default.'
}

. $scriptPath

function Assert-Equal($actual, $expected, [string]$message) {
    if ($actual -ne $expected) {
        throw "$message Expected '$expected' but got '$actual'."
    }
}

function Assert-SequenceEqual($actual, $expected, [string]$message) {
    $actualText = ($actual -join '|')
    $expectedText = ($expected -join '|')
    if ($actualText -ne $expectedText) {
        throw "$message Expected '$expectedText' but got '$actualText'."
    }
}

function Assert-True($condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Assert-RectangleEqual($actual, [int]$x, [int]$y, [int]$width, [int]$height, [string]$message) {
    Assert-Equal $actual.X $x "$message X mismatch."
    Assert-Equal $actual.Y $y "$message Y mismatch."
    Assert-Equal $actual.Width $width "$message Width mismatch."
    Assert-Equal $actual.Height $height "$message Height mismatch."
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

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('localcoop-script-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    $harnessArgs = Format-LocalCoopHarnessPreparationArgumentList `
        -ConfigRoot '.localcoop-clients' `
        -ClientCount 4 `
        -SessionId 'local-test5' `
        -Port 38993 `
        -GameExecutablePath 'D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe' `
        -ControllerDevices '0,1,none,3'

    Assert-SequenceEqual $harnessArgs @(
        'run',
        '--no-restore',
        '--project',
        'tools\LocalCoop.MultiClientHarness',
        '--',
        'prepare-clients',
        '.localcoop-clients',
        '4',
        'local-test5',
        '38993',
        'D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe',
        '0,1,none,3'
    ) 'Harness preparation should pass client count and controller overrides.'

    $clientDirectories = Get-LocalCoopClientConfigDirectories -ConfigRoot '.localcoop-clients' -ClientCount 4
    Assert-SequenceEqual $clientDirectories @(
        (Join-Path '.localcoop-clients' 'client-0'),
        (Join-Path '.localcoop-clients' 'client-1'),
        (Join-Path '.localcoop-clients' 'client-2'),
        (Join-Path '.localcoop-clients' 'client-3')
    ) 'Client directory list should include every generated client.'

    Assert-Throws { Assert-LocalCoopClientCount -ClientCount 1 } '2 through 4' 'Client count validation should reject values below 2.'
    Assert-Throws { Assert-LocalCoopClientCount -ClientCount 5 } '2 through 4' 'Client count validation should reject values above 4.'

    $screenBounds = [pscustomobject]@{
        X = 0
        Y = 0
        Width = 3840
        Height = 2160
    }

    Assert-RectangleEqual (Get-LocalCoopClientWindowBounds -ScreenBounds $screenBounds -ClientIndex 0) 0 0 1920 1080 'Client 0 should use the top-left quadrant.'
    Assert-RectangleEqual (Get-LocalCoopClientWindowBounds -ScreenBounds $screenBounds -ClientIndex 1) 1920 0 1920 1080 'Client 1 should use the top-right quadrant.'
    Assert-RectangleEqual (Get-LocalCoopClientWindowBounds -ScreenBounds $screenBounds -ClientIndex 2) 0 1080 1920 1080 'Client 2 should use the bottom-left quadrant.'
    Assert-RectangleEqual (Get-LocalCoopClientWindowBounds -ScreenBounds $screenBounds -ClientIndex 3) 1920 1080 1920 1080 'Client 3 should use the bottom-right quadrant.'

    $offsetScreenBounds = [pscustomobject]@{
        X = -1920
        Y = 40
        Width = 1919
        Height = 1039
    }

    Assert-RectangleEqual (Get-LocalCoopClientWindowBounds -ScreenBounds $offsetScreenBounds -ClientIndex 3) -961 559 960 520 'Client bounds should preserve offset screen coordinates and odd pixels.'

    $placementPlan = Get-LocalCoopClientWindowPlacementPlan -ScreenBounds $screenBounds -ClientCount 3
    Assert-SequenceEqual ($placementPlan | ForEach-Object { $_.ClientIndex.ToString() }) @('0', '1', '2') 'Placement plan should keep client index order.'
    Assert-RectangleEqual $placementPlan[2] 0 1080 1920 1080 'Three-client placement should reserve the bottom-left quadrant for client 2.'

    $windowArguments = Format-LocalCoopGameWindowArgumentList -WindowBounds ($placementPlan[2])
    Assert-SequenceEqual $windowArguments @('--windowed', '--position', '0,1080', '--resolution', '1920x1080') 'Game window arguments should request the planned client window at process startup.'

    $clientStartInfo = New-LocalCoopClientStartInfo `
        -GameExecutablePath 'D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe' `
        -GameRoot 'D:\SteamLibrary\steamapps\common\Slay the Spire 2' `
        -ClientConfigDirectory '.localcoop-clients\client-2' `
        -WindowBounds ($placementPlan[2])

    Assert-Equal $clientStartInfo.Arguments '' 'Client start info should not pass Godot window placement flags by default.'
    $clientConfigDirectory = $clientStartInfo.EnvironmentVariables.Get_Item('LOCALCOOP_CONFIG_DIR')
    Assert-Equal $clientConfigDirectory '.localcoop-clients\client-2' 'Client start info should preserve LocalCoop config directory.'

    $clientStartInfoWithGameWindowArguments = New-LocalCoopClientStartInfo `
        -GameExecutablePath 'D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe' `
        -GameRoot 'D:\SteamLibrary\steamapps\common\Slay the Spire 2' `
        -ClientConfigDirectory '.localcoop-clients\client-2' `
        -WindowBounds ($placementPlan[2]) `
        -UseGameWindowArguments

    Assert-Equal $clientStartInfoWithGameWindowArguments.Arguments '--windowed --position 0,1080 --resolution 1920x1080' 'Client start info should pass Godot window placement flags only when requested.'

    Assert-Equal (Get-LocalCoopWindowPlacementAttemptCount -StabilizationSeconds 0 -RetryIntervalMilliseconds 1000) 1 'Zero-second stabilization should place windows once without retrying.'
    Assert-Equal (Get-LocalCoopWindowPlacementAttemptCount -StabilizationSeconds 5 -RetryIntervalMilliseconds 1000) 6 'Stabilization should include the immediate attempt and each retry tick.'
    Assert-Throws { Get-LocalCoopWindowPlacementAttemptCount -StabilizationSeconds 5 -RetryIntervalMilliseconds 0 } 'positive' 'Placement retry interval validation should reject zero.'

    $hostConfigDirectory = Join-Path $tempRoot 'client-0'
    $remoteConfigDirectory = Join-Path $tempRoot 'client-1'
    $readinessGameRoot = Join-Path $tempRoot 'readiness-game'
    New-Item -ItemType Directory -Path $hostConfigDirectory, $remoteConfigDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $hostConfigDirectory 'enable-local-broker.txt') -Value @('role=host', 'clientIndex=0')
    Set-Content -LiteralPath (Join-Path $remoteConfigDirectory 'enable-local-broker.txt') -Value @('role=client', 'clientIndex=1')

    Assert-Equal (Get-LocalCoopClientEventLogPath -GameRoot $readinessGameRoot -ClientConfigDirectory $hostConfigDirectory -ClientIndex 0) (Join-Path $readinessGameRoot 'mods\LocalCoop\localcoop-host-0-events.txt') 'Host event log path should follow the configured broker role.'
    Assert-Equal (Get-LocalCoopClientEventLogPath -GameRoot $readinessGameRoot -ClientConfigDirectory $remoteConfigDirectory -ClientIndex 1) (Join-Path $readinessGameRoot 'mods\LocalCoop\localcoop-client-1-events.txt') 'Client event log path should follow the configured broker role.'

    $readyLogPath = Join-Path $tempRoot 'ready-events.txt'
    Set-Content -LiteralPath $readyLogPath -Value 'Broker mode enabled: clientId=client-0 role=Host endpoint=127.0.0.1:38989 sessionId=local-test.'
    Assert-True (Test-LocalCoopClientStartupLog -Path $readyLogPath) 'Startup log readiness should detect the broker enabled marker.'

    $script:readinessAttempts = 0
    $script:readinessSleeps = @()
    $readinessObserved = Wait-LocalCoopClientStartupLogs `
        -ClientLaunches @([pscustomobject]@{
            ClientIndex = 0
            Process = [pscustomobject]@{ Id = 42 }
            ConfigDirectory = $hostConfigDirectory
        }) `
        -GameRoot $readinessGameRoot `
        -TimeoutSeconds 5 `
        -PollIntervalMilliseconds 250 `
        -TestStartupLog {
            param($path)
            $script:readinessAttempts++
            $script:readinessAttempts -ge 2
        } `
        -SleepMilliseconds {
            param($milliseconds)
            $script:readinessSleeps += $milliseconds.ToString()
        }

    Assert-True $readinessObserved 'Window placement readiness wait should succeed once startup logs are observed.'
    Assert-SequenceEqual $script:readinessSleeps @('250') 'Window placement readiness wait should poll until the startup log marker appears.'

    $script:placementEvents = @()
    Invoke-LocalCoopClientWindowPlacementStabilization `
        -ClientLaunches @([pscustomobject]@{
            ClientIndex = 0
            Process = [pscustomobject]@{ Id = 42 }
            ConfigDirectory = 'client-0'
        }) `
        -ScreenBounds $screenBounds `
        -TimeoutSeconds 0 `
        -InitialDelayMilliseconds 0 `
        -StabilizationSeconds 0 `
        -RetryIntervalMilliseconds 1000 `
        -ResolveWindowHandle {
            $script:placementEvents += 'resolve'
            [IntPtr]::new(101)
        } `
        -MoveWindow {
            param($windowHandle, $bounds)
            $script:placementEvents += 'move'
        } `
        -SleepMilliseconds {
            param($milliseconds)
            $script:placementEvents += ('sleep:{0}' -f $milliseconds)
        }

    Assert-SequenceEqual $script:placementEvents @('resolve', 'move') 'Window placement should resolve and move after startup readiness is handled by the caller.'

    $script:bootstrapEvents = @()
    $bootstrapFocused = Invoke-LocalCoopSteamInputBootstrapFocus `
        -ClientLaunches @(
            [pscustomobject]@{ ClientIndex = 1; Process = [pscustomobject]@{ Id = 43 } },
            [pscustomobject]@{ ClientIndex = 0; Process = [pscustomobject]@{ Id = 42 } }
        ) `
        -TimeoutSeconds 0 `
        -HoldMilliseconds 5000 `
        -ResolveWindowHandle {
            param($process, $timeoutSeconds)
            $script:bootstrapEvents += ('resolve:{0}' -f $process.Id)
            [IntPtr]::new(101)
        } `
        -ActivateWindow {
            param($windowHandle)
            $script:bootstrapEvents += ('activate:{0}' -f $windowHandle.ToInt64())
            $true
        } `
        -SleepMilliseconds {
            param($milliseconds)
            $script:bootstrapEvents += ('sleep:{0}' -f $milliseconds)
        }

    Assert-True $bootstrapFocused 'Steam Input bootstrap should focus the original host client.'
    Assert-SequenceEqual $script:bootstrapEvents @('resolve:42', 'activate:101', 'sleep:5000') 'Steam Input bootstrap should focus client 0 and hold it long enough for discovery.'
    Assert-Throws {
        Invoke-LocalCoopClientWindowPlacementStabilization `
            -ClientLaunches @([pscustomobject]@{
                ClientIndex = 0
                Process = [pscustomobject]@{ Id = 42 }
                ConfigDirectory = 'client-0'
            }) `
            -ScreenBounds $screenBounds `
            -TimeoutSeconds 0 `
            -InitialDelayMilliseconds -1 `
            -StabilizationSeconds 0 `
            -RetryIntervalMilliseconds 1000
    } 'zero or greater' 'Placement initial delay validation should reject negative values.'

    $script:movedWindowHandles = @()
    Invoke-LocalCoopClientWindowPlacementStabilization `
        -ClientLaunches @([pscustomobject]@{
            ClientIndex = 0
            Process = [pscustomobject]@{ Id = 42 }
            ConfigDirectory = 'client-0'
        }) `
        -ScreenBounds $screenBounds `
        -TimeoutSeconds 0 `
        -StabilizationSeconds 0 `
        -RetryIntervalMilliseconds 1000 `
        -ResolveWindowHandle {
            [IntPtr]::new(101)
        } `
        -MoveWindow {
            param($windowHandle, $bounds)
            $script:movedWindowHandles += $windowHandle.ToInt64().ToString()
        } `
        -SleepMilliseconds {
            param($milliseconds)
        }

    Assert-SequenceEqual $script:movedWindowHandles @('101') 'Zero-second stabilization should force exactly one post-launch Win32 resize.'

    $script:resolvedWindowHandles = @([IntPtr]::new(101), [IntPtr]::new(202))
    $script:resolveWindowHandleIndex = 0
    $script:movedWindowHandles = @()
    Invoke-LocalCoopClientWindowPlacementStabilization `
        -ClientLaunches @([pscustomobject]@{
            ClientIndex = 0
            Process = [pscustomobject]@{ Id = 42 }
            ConfigDirectory = 'client-0'
        }) `
        -ScreenBounds $screenBounds `
        -TimeoutSeconds 0 `
        -StabilizationSeconds 1 `
        -RetryIntervalMilliseconds 1000 `
        -ResolveWindowHandle {
            param($process, $timeoutSeconds)
            $handle = $script:resolvedWindowHandles[$script:resolveWindowHandleIndex]
            $script:resolveWindowHandleIndex++
            $handle
        } `
        -MoveWindow {
            param($windowHandle, $bounds)
            $script:movedWindowHandles += $windowHandle.ToInt64().ToString()
        } `
        -SleepMilliseconds {
            param($milliseconds)
        }

    Assert-SequenceEqual $script:movedWindowHandles @('101', '202') 'Placement stabilization should re-resolve the current window handle on every attempt.'

    Enable-LocalCoopDpiAwareness
    Enable-LocalCoopDpiAwareness

    $desiredFrameBounds = [pscustomobject]@{
        X = 0
        Y = 0
        Width = 1920
        Height = 1080
    }

    $currentWindowBounds = [pscustomobject]@{
        X = -8
        Y = -8
        Width = 1936
        Height = 1096
    }

    $currentVisibleFrameBounds = [pscustomobject]@{
        X = 0
        Y = 0
        Width = 1920
        Height = 1080
    }

    Assert-RectangleEqual (ConvertTo-LocalCoopOuterWindowBounds `
        -DesiredFrameBounds $desiredFrameBounds `
        -CurrentWindowBounds $currentWindowBounds `
        -CurrentVisibleFrameBounds $currentVisibleFrameBounds) -8 -8 1936 1096 'Outer window bounds should compensate for invisible resize borders.'

    $logicalWindowBounds = [pscustomobject]@{
        X = -6
        Y = 696
        Width = 1292
        Height = 702
    }

    $physicalVisibleFrameBounds = [pscustomobject]@{
        X = 0
        Y = 1044
        Width = 1920
        Height = 1044
    }

    $normalizedVisibleFrameBounds = ConvertTo-LocalCoopComparableVisibleFrameBounds `
        -VisibleFrameBounds $physicalVisibleFrameBounds `
        -WindowBounds $logicalWindowBounds `
        -Dpi 144

    Assert-RectangleEqual $normalizedVisibleFrameBounds 0 696 1280 696 'Physical DWM frame bounds should be scaled to match logical GetWindowRect bounds.'

    $physicalDesiredFrameBounds = [pscustomobject]@{
        X = 0
        Y = 0
        Width = 1920
        Height = 1044
    }

    $comparableDesiredFrameBounds = ConvertTo-LocalCoopComparableDesiredFrameBounds `
        -DesiredFrameBounds $physicalDesiredFrameBounds `
        -CoordinateScale 1.5

    Assert-RectangleEqual $comparableDesiredFrameBounds 0 0 1280 696 'Desired physical quadrant bounds should be scaled before MoveWindow when the target window uses logical coordinates.'

    $desiredLogicalFrameBounds = [pscustomobject]@{
        X = 0
        Y = 0
        Width = 1280
        Height = 696
    }

    Assert-RectangleEqual (ConvertTo-LocalCoopOuterWindowBounds `
        -DesiredFrameBounds $desiredLogicalFrameBounds `
        -CurrentWindowBounds $logicalWindowBounds `
        -CurrentVisibleFrameBounds $normalizedVisibleFrameBounds) -6 0 1292 702 'DPI-normalized frame margins should produce usable outer bounds.'

    $gameRoot = Join-Path $tempRoot 'game'
    $modDirectory = Join-Path $gameRoot 'mods\LocalCoop'
    New-Item -ItemType Directory -Path $modDirectory -Force | Out-Null
    $ownedLogFiles = @(
        'localcoop-host-0-events.txt',
        'localcoop-client-1-events.txt',
        'localcoop-client-2-events.txt',
        'localcoop-client-3-events.txt',
        'localcoop-transport-probe-client-2.txt',
        'localcoop-transport-probe-client-3.txt'
    )

    foreach ($fileName in $ownedLogFiles) {
        Set-Content -LiteralPath (Join-Path $modDirectory $fileName) -Value 'old log data'
    }

    Clear-LocalCoopLaunchLogs -GameRoot $gameRoot

    foreach ($fileName in $ownedLogFiles) {
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $modDirectory $fileName))) "Launch cleanup should remove $fileName."
    }

    Write-Host 'Start-LocalCoopClients.Tests.ps1 passed.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
