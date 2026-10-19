Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$scriptPath = Join-Path $repoRoot 'Start-LocalCoopTwoClient.ps1'

if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "Startup script not found: $scriptPath"
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

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse('127.0.0.1'), 0)
    try {
        $listener.Start()
        $listener.LocalEndpoint.Port
    }
    finally {
        $listener.Stop()
    }
}

function Start-TestTcpListenerProcess([int]$port) {
    $listenerCommand = @"
`$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse('127.0.0.1'), $port)
`$listener.Start()
try {
    Start-Sleep -Seconds 60
}
finally {
    `$listener.Stop()
}
"@

    Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-Command', $listenerCommand) `
        -WindowStyle Hidden `
        -PassThru
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('localcoop-script-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    $missingConfig = Join-Path $tempRoot 'missing-enable-local-broker.txt'
    $fallback = Read-LocalCoopBrokerConfig `
        -ConfigPath $missingConfig `
        -DefaultSessionId 'local-test' `
        -DefaultHost '127.0.0.1' `
        -DefaultPort 38989

    Assert-Equal $fallback.SessionId 'local-test' 'Missing config should use default session id.'
    Assert-Equal $fallback.Host '127.0.0.1' 'Missing config should use default host.'
    Assert-Equal $fallback.Port 38989 'Missing config should use default port.'
    Assert-Equal $fallback.Source 'defaults' 'Missing config should identify defaults as source.'

    $configPath = Join-Path $tempRoot 'enable-local-broker.txt'
    @(
        'role=host'
        'clientIndex=0'
        'endpoint=127.0.0.1:38993'
        'sessionId=local-test5'
    ) | Set-Content -LiteralPath $configPath

    $configured = Read-LocalCoopBrokerConfig `
        -ConfigPath $configPath `
        -DefaultSessionId 'local-test' `
        -DefaultHost '127.0.0.1' `
        -DefaultPort 38989

    Assert-Equal $configured.SessionId 'local-test5' 'Existing config should provide session id.'
    Assert-Equal $configured.Host '127.0.0.1' 'Existing config should provide endpoint host.'
    Assert-Equal $configured.Port 38993 'Existing config should provide endpoint port.'
    Assert-Equal $configured.Source $configPath 'Existing config should identify config path as source.'

    $args = Format-LocalCoopBrokerArgumentList -SessionId 'local-test5' -Port 38993
    Assert-SequenceEqual $args @('src\LocalCoop.Broker.Cli\bin\Debug\net9.0-launch\LocalCoop.Broker.Cli.dll', 'local-test5', '38993') 'Broker argument list should launch the launch-specific built CLI directly.'

    $fakeGameRoot = Join-Path $tempRoot 'game'
    $fakePackageRoot = Join-Path $fakeGameRoot 'mods\LocalCoop'
    $fakeBrokerDirectory = Join-Path $fakePackageRoot 'broker'
    New-Item -ItemType Directory -Path $fakeBrokerDirectory -Force | Out-Null
    $fakeBrokerExe = Join-Path $fakeBrokerDirectory 'LocalCoop.Broker.Cli.exe'
    Set-Content -LiteralPath $fakeBrokerExe -Value 'broker'
    Set-Content -LiteralPath (Join-Path $fakeGameRoot 'SlayTheSpire2.exe') -Value 'game'

    Assert-Equal (Resolve-LocalCoopDefaultGameRoot -RepoRoot $fakePackageRoot) $fakeGameRoot 'Packaged install should resolve the game root from mods\LocalCoop.'
    Assert-Equal (Resolve-LocalCoopDefaultGameRoot -RepoRoot $repoRoot) (Resolve-Path (Join-Path $repoRoot '..')).Path 'Dev checkout should resolve the game root from the repo parent.'
    Assert-Equal (Resolve-LocalCoopBrokerRepoRoot -RepoRoot $fakePackageRoot) $fakePackageRoot 'Packaged install without a source checkout should use its bundled broker.'

    $fakeDevelopmentRepo = Join-Path $fakeGameRoot 'STS2CouchCoop'
    $fakeDevelopmentBrokerProject = Join-Path $fakeDevelopmentRepo 'src\LocalCoop.Broker.Cli\LocalCoop.Broker.Cli.csproj'
    New-Item -ItemType Directory -Path (Split-Path -Parent $fakeDevelopmentBrokerProject) -Force | Out-Null
    Set-Content -LiteralPath $fakeDevelopmentBrokerProject -Value '<Project />'
    Assert-Equal (Resolve-LocalCoopBrokerRepoRoot -RepoRoot $fakePackageRoot) $fakeDevelopmentRepo 'Packaged development install should use the adjacent source broker.'
    Assert-Equal (Resolve-LocalCoopBrokerRepoRoot -RepoRoot $repoRoot) (Resolve-Path -LiteralPath $repoRoot).Path 'Dev checkout should continue using its own broker source.'

    $steamAppIdPath = Join-Path $fakeGameRoot 'steam_appid.txt'
    Assert-True (-not (Test-Path -LiteralPath $steamAppIdPath)) 'Fake target game root should start without steam_appid.txt.'
    Ensure-LocalCoopSteamAppIdFile -GameRoot $fakeGameRoot
    Assert-Equal (Get-Content -Raw -LiteralPath $steamAppIdPath).Trim() '2868840' 'Launcher should create the STS2 Steam app id file when missing.'

    Set-Content -LiteralPath $steamAppIdPath -Value 'existing-app-id'
    Ensure-LocalCoopSteamAppIdFile -GameRoot $fakeGameRoot
    Assert-Equal (Get-Content -Raw -LiteralPath $steamAppIdPath).Trim() 'existing-app-id' 'Launcher should preserve an existing steam_appid.txt.'

    $oldAppData = $env:APPDATA
    $env:APPDATA = Join-Path $tempRoot 'appdata'
    try {
        Assert-Equal (Get-LocalCoopDefaultConfigRoot -RepoRoot $fakePackageRoot) (Join-Path $env:APPDATA 'SlayTheSpire2\LocalCoop\clients') 'Packaged install should store generated client config under APPDATA.'
        Assert-Equal (Get-LocalCoopDefaultRuntimeRoot -RepoRoot $fakePackageRoot) (Join-Path $env:APPDATA 'SlayTheSpire2\LocalCoop\runtime') 'Packaged install should store broker launcher logs under APPDATA.'

        $packagedArgs = Format-LocalCoopBrokerArgumentList -RepoRoot $fakePackageRoot -SessionId 'portable-test' -Port 39001
        Assert-SequenceEqual $packagedArgs @($fakeBrokerExe, 'portable-test', '39001') 'Packaged broker argument list should launch the broker executable directly.'

        $packagedLauncherScript = Write-LocalCoopBrokerLauncherScript -RepoRoot $fakePackageRoot -SessionId 'portable-test' -Port 39001
        $packagedLauncherContent = Get-Content -Raw -LiteralPath $packagedLauncherScript
        Assert-True ($packagedLauncherContent.Contains($fakeBrokerExe)) 'Packaged broker launcher should reference the broker executable.'
        Assert-True (-not $packagedLauncherContent.Contains('& dotnet @brokerArguments')) 'Packaged broker launcher should not invoke dotnet.'

        $portableConfigRoot = Join-Path $tempRoot 'portable-configs'
        Invoke-LocalCoopClientPreparation `
            -RepoRoot $fakePackageRoot `
            -ConfigRoot $portableConfigRoot `
            -ClientCount 3 `
            -SessionId 'portable-test' `
            -Port 39001 `
            -GameExecutablePath (Join-Path $fakeGameRoot 'SlayTheSpire2.exe') `
            -ControllerDevices '0,none,2'

        Assert-True (Test-Path -LiteralPath (Join-Path $portableConfigRoot 'client-0\enable-local-broker.txt')) 'Release mode should write host config without source harness.'
        Assert-True (Test-Path -LiteralPath (Join-Path $portableConfigRoot 'client-1\enable-local-broker.txt')) 'Release mode should write client config without source harness.'
        $portableClientConfig = Get-Content -Raw -LiteralPath (Join-Path $portableConfigRoot 'client-1\enable-local-broker.txt')
        Assert-True ($portableClientConfig.Contains('role=client')) 'Release mode client config should mark nonzero clients as client role.'
        Assert-True ($portableClientConfig.Contains('clientIndex=1')) 'Release mode client config should include the client index.'
        Assert-True ($portableClientConfig.Contains('playerSlot=1')) 'Release mode client config should preserve keyboard-only player slot.'
        Assert-True ($portableClientConfig.Contains('inputMode=none')) 'Release mode client config should preserve keyboard-only input mode.'
        Assert-True ($portableClientConfig.Contains('controllerClientCount=2')) 'Release mode client config should include controller-backed client count.'
        Assert-True (-not $portableClientConfig.Contains('controllerDevice=')) 'Release mode client config should omit fixed controller device indices.'
        Assert-True ($portableClientConfig.Contains('endpoint=127.0.0.1:39001')) 'Release mode client config should include the broker endpoint.'
        Assert-True ($portableClientConfig.Contains('sessionId=portable-test')) 'Release mode client config should include the session id.'
    }
    finally {
        $env:APPDATA = $oldAppData
    }

    $harnessArgs = Format-LocalCoopHarnessPreparationArgumentList `
        -ConfigRoot '.localcoop-clients' `
        -SessionId 'local-test5' `
        -Port 38993 `
        -GameExecutablePath 'D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe'

    Assert-SequenceEqual $harnessArgs @(
        'run',
        '--no-restore',
        '--project',
        'tools\LocalCoop.MultiClientHarness',
        '--',
        'prepare-clients',
        '.localcoop-clients',
        '2',
        'local-test5',
        '38993',
        'D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe'
    ) 'Harness preparation should avoid implicit restore and use the generalized client command.'

    $brokerStartInfo = New-LocalCoopBrokerStartInfo `
        -RepoRoot $tempRoot `
        -SessionId 'local-test5' `
        -Port 38993

    Assert-Equal $brokerStartInfo.FileName 'powershell.exe' 'Broker start info should run a detached PowerShell launcher.'
    Assert-Equal $brokerStartInfo.WorkingDirectory $tempRoot 'Broker start info should run from the repo root.'
    Assert-Equal $brokerStartInfo.UseShellExecute $false 'Broker start info should avoid Start-Process shell environment handling.'
    Assert-Equal $brokerStartInfo.CreateNoWindow $true 'Broker start info should run without opening an extra console window.'
    Assert-Equal $brokerStartInfo.RedirectStandardOutput $false 'Broker launcher should redirect broker output itself.'
    Assert-Equal $brokerStartInfo.RedirectStandardError $false 'Broker launcher should redirect broker errors itself.'

    $launcherScript = Join-Path $tempRoot '.localcoop-runtime\start-broker-local-test5-38993.ps1'
    if (-not (Test-Path -LiteralPath $launcherScript)) {
        throw "Expected broker launcher script to exist at $launcherScript."
    }

    $gameRoot = Join-Path $tempRoot 'game'
    $modDirectory = Join-Path $gameRoot 'mods\LocalCoop'
    New-Item -ItemType Directory -Path $modDirectory -Force | Out-Null
    $ownedLogFiles = @(
        'localcoop-events.txt',
        'localcoop-host-0-events.txt',
        'localcoop-client-1-events.txt',
        'localcoop-probe.txt',
        'localcoop-transport-probe-client-0.txt',
        'localcoop-transport-probe-client-1.txt'
    )

    foreach ($fileName in $ownedLogFiles) {
        Set-Content -LiteralPath (Join-Path $modDirectory $fileName) -Value 'old log data'
    }

    $preservedFiles = @(
        'enable-local-broker.txt',
        'LocalCoop.dll',
        'notes.txt'
    )

    foreach ($fileName in $preservedFiles) {
        Set-Content -LiteralPath (Join-Path $modDirectory $fileName) -Value 'keep me'
    }

    Clear-LocalCoopLaunchLogs -GameRoot $gameRoot

    foreach ($fileName in $ownedLogFiles) {
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $modDirectory $fileName))) "Launch cleanup should remove $fileName."
    }

    foreach ($fileName in $preservedFiles) {
        Assert-True (Test-Path -LiteralPath (Join-Path $modDirectory $fileName)) "Launch cleanup should preserve $fileName."
    }

    $sleepProcess = Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60') `
        -WindowStyle Hidden `
        -PassThru

    try {
        Stop-LocalCoopProcessTree -Process $sleepProcess
        Assert-True ($sleepProcess.WaitForExit(5000)) 'Stop-LocalCoopProcessTree should stop the supplied process.'
        Wait-LocalCoopBrokerProcess -Process $sleepProcess
    }
    finally {
        if (-not $sleepProcess.HasExited) {
            Stop-Process -Id $sleepProcess.Id -Force
        }
    }

    $listenerPort = Get-FreeTcpPort
    $listenerProcess = Start-TestTcpListenerProcess -port $listenerPort
    try {
        if (-not (Wait-LocalCoopBroker -HostName '127.0.0.1' -Port $listenerPort -TimeoutSeconds 5)) {
            throw "Test listener did not start on port $listenerPort."
        }

        $cleanupIds = Get-LocalCoopExistingBrokerProcessIds `
            -RepoRoot $tempRoot `
            -HostName '127.0.0.1' `
            -Port $listenerPort

        Assert-True (($cleanupIds | Measure-Object).Count -gt 0) 'Broker cleanup should include the process listening on the configured broker port.'

        Stop-LocalCoopExistingBrokers `
            -RepoRoot $tempRoot `
            -HostName '127.0.0.1' `
            -Port $listenerPort

        Assert-True (-not (Test-LocalCoopTcpPort -HostName '127.0.0.1' -Port $listenerPort -TimeoutMilliseconds 250)) 'Broker cleanup should release the configured broker port.'
    }
    finally {
        if (-not $listenerProcess.HasExited) {
            Stop-Process -Id $listenerProcess.Id -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host 'Start-LocalCoopTwoClient.Tests.ps1 passed.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
