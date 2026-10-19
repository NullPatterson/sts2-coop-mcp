param(
    [string]$RepoRoot = $PSScriptRoot,
    [string]$GameRoot,
    [string]$ConfigRoot,
    [string]$BrokerConfigPath,
    [string]$DefaultSessionId = 'local-test',
    [string]$DefaultHost = '127.0.0.1',
    [int]$DefaultPort = 38989,
    [int]$BrokerStartupTimeoutSeconds = 60,
    [int]$WindowPlacementTimeoutSeconds = 30,
    [int]$WindowPlacementReadinessTimeoutSeconds = 90,
    [int]$WindowPlacementStartupDelaySeconds = 0,
    [int]$WindowPlacementStabilizationSeconds = 15,
    [int]$WindowPlacementRetryIntervalMilliseconds = 1000,
    [switch]$ReuseExistingBroker,
    [switch]$SkipClientLaunch,
    [switch]$SkipWindowPlacement,
    [switch]$NoWaitForBroker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:LocalCoopWindowPlacementDpiAwarenessInitialized = $false

function Read-LocalCoopBrokerConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath,
        [Parameter(Mandatory = $true)]
        [string]$DefaultSessionId,
        [Parameter(Mandatory = $true)]
        [string]$DefaultHost,
        [Parameter(Mandatory = $true)]
        [int]$DefaultPort
    )

    $sessionId = $DefaultSessionId
    $hostName = $DefaultHost
    $port = $DefaultPort
    $source = 'defaults'

    if (Test-Path -LiteralPath $ConfigPath) {
        $source = (Resolve-Path -LiteralPath $ConfigPath).Path
        $values = @{}

        foreach ($line in Get-Content -LiteralPath $ConfigPath) {
            $trimmed = $line.Trim()
            if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
                continue
            }

            $separator = $trimmed.IndexOf('=')
            if ($separator -lt 1) {
                continue
            }

            $key = $trimmed.Substring(0, $separator).Trim()
            $value = $trimmed.Substring($separator + 1).Trim()
            $values[$key] = $value
        }

        if ($values.ContainsKey('sessionId') -and -not [string]::IsNullOrWhiteSpace($values['sessionId'])) {
            $sessionId = $values['sessionId']
        }

        if ($values.ContainsKey('endpoint') -and -not [string]::IsNullOrWhiteSpace($values['endpoint'])) {
            $endpoint = $values['endpoint']
            $portSeparator = $endpoint.LastIndexOf(':')
            if ($portSeparator -gt 0 -and $portSeparator -lt ($endpoint.Length - 1)) {
                $candidateHost = $endpoint.Substring(0, $portSeparator)
                $candidatePortText = $endpoint.Substring($portSeparator + 1)
                $candidatePort = 0
                if ([int]::TryParse($candidatePortText, [ref]$candidatePort) -and $candidatePort -gt 0 -and $candidatePort -le 65535) {
                    $hostName = $candidateHost
                    $port = $candidatePort
                }
            }
        }
    }

    [pscustomobject]@{
        SessionId = $sessionId
        Host = $hostName
        Port = $port
        Source = $source
    }
}

function Test-LocalCoopPackagedInstall {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    Test-Path -LiteralPath (Join-Path $RepoRoot 'broker\LocalCoop.Broker.Cli.exe') -PathType Leaf
}

function Resolve-LocalCoopBrokerRepoRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    if (-not (Test-LocalCoopPackagedInstall -RepoRoot $resolvedRepoRoot)) {
        return $resolvedRepoRoot
    }

    $gameRoot = Resolve-LocalCoopDefaultGameRoot -RepoRoot $resolvedRepoRoot
    $developmentRepoRoot = Join-Path $gameRoot 'STS2CouchCoop'
    $brokerProject = Join-Path $developmentRepoRoot 'src\LocalCoop.Broker.Cli\LocalCoop.Broker.Cli.csproj'
    if (Test-Path -LiteralPath $brokerProject -PathType Leaf) {
        return (Resolve-Path -LiteralPath $developmentRepoRoot).Path
    }

    $resolvedRepoRoot
}

function Resolve-LocalCoopDefaultGameRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    if (Test-LocalCoopPackagedInstall -RepoRoot $resolvedRepoRoot) {
        $parent = Split-Path -Parent $resolvedRepoRoot
        $grandParent = Split-Path -Parent $parent
        if ((Split-Path -Leaf $resolvedRepoRoot) -ieq 'LocalCoop' -and
            (Split-Path -Leaf $parent) -ieq 'mods' -and
            -not [string]::IsNullOrWhiteSpace($grandParent)) {
            return (Resolve-Path -LiteralPath $grandParent).Path
        }
    }

    (Resolve-Path -LiteralPath (Join-Path $resolvedRepoRoot '..')).Path
}

function Ensure-LocalCoopSteamAppIdFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameRoot
    )

    $steamAppIdPath = Join-Path $GameRoot 'steam_appid.txt'
    if (Test-Path -LiteralPath $steamAppIdPath -PathType Leaf) {
        return
    }

    try {
        Set-Content -LiteralPath $steamAppIdPath -Value '2868840'
    }
    catch {
        throw "Steam app id file is missing and could not be created at $steamAppIdPath. Create that file manually with the value 2868840, then run the launcher again. $($_.Exception.Message)"
    }
}

function Get-LocalCoopUserStateRoot {
    [CmdletBinding()]
    param()

    $appData = $env:APPDATA
    if ([string]::IsNullOrWhiteSpace($appData)) {
        $appData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
    }

    if ([string]::IsNullOrWhiteSpace($appData)) {
        throw 'Could not resolve APPDATA for LocalCoop runtime files.'
    }

    Join-Path $appData 'SlayTheSpire2\LocalCoop'
}

function Get-LocalCoopDefaultConfigRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    if (Test-LocalCoopPackagedInstall -RepoRoot $RepoRoot) {
        return Join-Path (Get-LocalCoopUserStateRoot) 'clients'
    }

    Join-Path $RepoRoot '.localcoop-clients'
}

function Get-LocalCoopDefaultRuntimeRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    if (Test-LocalCoopPackagedInstall -RepoRoot $RepoRoot) {
        return Join-Path (Get-LocalCoopUserStateRoot) 'runtime'
    }

    Join-Path $RepoRoot '.localcoop-runtime'
}

function Get-LocalCoopPackagedBrokerExecutablePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $brokerPath = Join-Path $RepoRoot 'broker\LocalCoop.Broker.Cli.exe'
    if (Test-Path -LiteralPath $brokerPath -PathType Leaf) {
        return $brokerPath
    }

    $null
}

function Format-LocalCoopBrokerArgumentList {
    [CmdletBinding()]
    param(
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $brokerExecutablePath = Get-LocalCoopPackagedBrokerExecutablePath -RepoRoot $RepoRoot
        if (-not [string]::IsNullOrWhiteSpace($brokerExecutablePath)) {
            return @($brokerExecutablePath, $SessionId, $Port.ToString())
        }
    }

    @('src\LocalCoop.Broker.Cli\bin\Debug\net9.0-launch\LocalCoop.Broker.Cli.dll', $SessionId, $Port.ToString())
}

function Test-LocalCoopTcpPort {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$HostName,
        [Parameter(Mandatory = $true)]
        [int]$Port,
        [int]$TimeoutMilliseconds = 750
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
            return $false
        }

        $client.EndConnect($connect)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Wait-LocalCoopBroker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$HostName,
        [Parameter(Mandatory = $true)]
        [int]$Port,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-LocalCoopTcpPort -HostName $HostName -Port $Port) {
            return $true
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

function ConvertTo-LocalCoopFileToken {
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

function ConvertTo-LocalCoopCommandLineArgument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Argument
    )

    if ($Argument.Length -eq 0) {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    '"' + $Argument.Replace('"', '\"') + '"'
}

function ConvertTo-LocalCoopPowerShellSingleQuoted {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    "'" + $Value.Replace("'", "''") + "'"
}

function Write-LocalCoopBrokerLauncherScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $sessionToken = ConvertTo-LocalCoopFileToken -Value $SessionId
    $runtimeRoot = Get-LocalCoopDefaultRuntimeRoot -RepoRoot $RepoRoot
    New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null

    $launcherPath = Join-Path $runtimeRoot ("start-broker-{0}-{1}.ps1" -f $sessionToken, $Port)
    $logPath = Join-Path $RepoRoot 'broker.log'
    $arguments = Format-LocalCoopBrokerArgumentList -RepoRoot $RepoRoot -SessionId $SessionId -Port $Port

    $content = @(
        'Set-StrictMode -Version Latest'
        '$ErrorActionPreference = ''Stop'''
        ('Set-Location -LiteralPath {0}' -f (ConvertTo-LocalCoopPowerShellSingleQuoted -Value $RepoRoot))
        ('$env:LOCALCOOP_BROKER_LOG = {0}' -f (ConvertTo-LocalCoopPowerShellSingleQuoted -Value $logPath))
    )

    if ($arguments[0].EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
        $brokerExecutable = ConvertTo-LocalCoopPowerShellSingleQuoted -Value $arguments[0]
        $brokerInvocationArguments = @($arguments[1..($arguments.Count - 1)]) + @($logPath)
        $quotedBrokerArguments = ($brokerInvocationArguments | ForEach-Object { ConvertTo-LocalCoopPowerShellSingleQuoted -Value $_ }) -join ', '
        $content += @(
            ('$brokerExecutable = {0}' -f $brokerExecutable)
            ('$brokerArguments = @({0})' -f $quotedBrokerArguments)
            '& $brokerExecutable @brokerArguments'
        )
    }
    else {
        $brokerInvocationArguments = @($arguments) + @($logPath)
        $quotedArguments = ($brokerInvocationArguments | ForEach-Object { ConvertTo-LocalCoopPowerShellSingleQuoted -Value $_ }) -join ', '
        $content += @(
            ('$brokerArguments = @({0})' -f $quotedArguments)
            '& dotnet @brokerArguments'
        )
    }

    Set-Content -LiteralPath $launcherPath -Value $content
    $launcherPath
}

function New-LocalCoopBrokerStartInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $launcherPath = Write-LocalCoopBrokerLauncherScript -RepoRoot $RepoRoot -SessionId $SessionId -Port $Port
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $launcherPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powershell.exe'
    $startInfo.Arguments = (($arguments | ForEach-Object { ConvertTo-LocalCoopCommandLineArgument -Argument $_ }) -join ' ')
    $startInfo.WorkingDirectory = $RepoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    $startInfo
}

function Invoke-LocalCoopBrokerBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    Push-Location -LiteralPath $RepoRoot
    try {
        $buildOutput = & dotnet build 'src\LocalCoop.Broker.Cli\LocalCoop.Broker.Cli.csproj' --no-restore '-p:OutputPath=bin\Debug\net9.0-launch\' 2>&1
        foreach ($line in $buildOutput) {
            Write-Host $line
        }

        if ($LASTEXITCODE -ne 0) {
            throw "Broker CLI build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Get-LocalCoopExistingBrokerProcessIds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$HostName,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $ids = [System.Collections.Generic.HashSet[int]]::new()

    try {
        $connections = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        foreach ($connection in $connections) {
            if ($connection.OwningProcess -gt 0) {
                [void]$ids.Add([int]$connection.OwningProcess)
            }
        }
    }
    catch {
    }

    if ($ids.Count -eq 0) {
        try {
            $netstatOutput = & netstat.exe -ano -p tcp 2>$null
            foreach ($line in $netstatOutput) {
                if ($line -notmatch 'LISTENING') {
                    continue
                }

                $columns = $line -split '\s+' | Where-Object { $_.Length -gt 0 }
                if ($columns.Count -lt 5) {
                    continue
                }

                $localAddress = $columns[1]
                $processIdText = $columns[-1]
                if ($localAddress -notmatch (":{0}$" -f $Port)) {
                    continue
                }

                $processId = 0
                if ([int]::TryParse($processIdText, [ref]$processId) -and $processId -gt 0) {
                    [void]$ids.Add($processId)
                }
            }
        }
        catch {
        }
    }

    $resolvedRepoRoot = $RepoRoot
    try {
        $resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    }
    catch {
    }

    $escapedRepoRoot = [System.Management.Automation.WildcardPattern]::Escape($resolvedRepoRoot)
    $brokerProcesses = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $commandLine = $_.CommandLine
            -not [string]::IsNullOrWhiteSpace($commandLine) -and
                (
                    ($commandLine -like '*LocalCoop.Broker.Cli*' -and $commandLine -like "*$escapedRepoRoot*") -or
                    ($commandLine -like '*start-broker-*' -and $commandLine -like "*$escapedRepoRoot*")
                )
        }

    foreach ($process in $brokerProcesses) {
        if ($process.ProcessId -gt 0) {
            [void]$ids.Add([int]$process.ProcessId)
        }
    }

    foreach ($id in $ids) {
        $id
    }
}

function Stop-LocalCoopExistingBrokers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$HostName,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $processIds = Get-LocalCoopExistingBrokerProcessIds `
        -RepoRoot $RepoRoot `
        -HostName $HostName `
        -Port $Port

    foreach ($processId in $processIds) {
        if ($processId -eq $PID) {
            continue
        }

        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($process -isnot [System.Diagnostics.Process]) {
            continue
        }

        Write-Host ("Stopping existing LocalCoop broker process id {0}." -f $processId)
        Stop-LocalCoopProcessTree -Process $process
    }

    if (Test-LocalCoopTcpPort -HostName $HostName -Port $Port -TimeoutMilliseconds 250) {
        throw "Could not stop existing process listening on $($HostName):$Port. Close that broker process and try again."
    }
}

function Start-LocalCoopBrokerProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $brokerRepoRoot = Resolve-LocalCoopBrokerRepoRoot -RepoRoot $RepoRoot
    if (-not (Test-LocalCoopPackagedInstall -RepoRoot $brokerRepoRoot)) {
        if ($brokerRepoRoot -ne (Resolve-Path -LiteralPath $RepoRoot).Path) {
            Write-Host ("Using LocalCoop development broker from {0}." -f $brokerRepoRoot)
        }

        Invoke-LocalCoopBrokerBuild -RepoRoot $brokerRepoRoot
    }

    $startInfo = New-LocalCoopBrokerStartInfo -RepoRoot $brokerRepoRoot -SessionId $SessionId -Port $Port
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'Failed to start LocalCoop broker process.'
    }

    $process
}

function Stop-LocalCoopProcessTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process
    )

    if ($Process.HasExited) {
        return
    }

    $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Process.Id)" -ErrorAction SilentlyContinue
    foreach ($child in $children) {
        $childProcess = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
        if ($childProcess -is [System.Diagnostics.Process]) {
            Stop-LocalCoopProcessTree -Process $childProcess
        }
    }

    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }

    if (-not $Process.WaitForExit(5000) -and -not $Process.HasExited) {
        $Process.Kill()
        [void]$Process.WaitForExit(5000)
    }
}

function Wait-LocalCoopBrokerProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process
    )

    $script:LocalCoopBrokerCancellationRequested = $false
    $cancelHandler = [ConsoleCancelEventHandler]{
        param($sender, $eventArgs)
        $eventArgs.Cancel = $true
        $script:LocalCoopBrokerCancellationRequested = $true
    }

    [Console]::add_CancelKeyPress($cancelHandler)
    try {
        while (-not $Process.HasExited) {
            if ($script:LocalCoopBrokerCancellationRequested) {
                Write-Host 'Ctrl+C received; stopping LocalCoop broker and exiting.'
                Stop-LocalCoopProcessTree -Process $Process
                break
            }

            Start-Sleep -Milliseconds 200
        }
    }
    finally {
        [Console]::remove_CancelKeyPress($cancelHandler)
        $script:LocalCoopBrokerCancellationRequested = $false
    }
}

function Assert-LocalCoopClientCount {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ClientCount
    )

    if ($ClientCount -lt 2 -or $ClientCount -gt 4) {
        throw 'ClientCount must be 2 through 4.'
    }
}

function Get-LocalCoopClientConfigDirectories {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigRoot,
        [Parameter(Mandatory = $true)]
        [int]$ClientCount
    )

    Assert-LocalCoopClientCount -ClientCount $ClientCount

    $directories = @()
    for ($index = 0; $index -lt $ClientCount; $index++) {
        $directories += (Join-Path $ConfigRoot ("client-{0}" -f $index))
    }

    $directories
}

function Get-LocalCoopPrimaryScreenWorkingArea {
    [CmdletBinding()]
    param()

    Enable-LocalCoopDpiAwareness
    Add-Type -AssemblyName System.Windows.Forms
    $workingArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

    [pscustomobject]@{
        X = [int]$workingArea.X
        Y = [int]$workingArea.Y
        Width = [int]$workingArea.Width
        Height = [int]$workingArea.Height
    }
}

function Get-LocalCoopClientWindowBounds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$ScreenBounds,
        [Parameter(Mandatory = $true)]
        [int]$ClientIndex
    )

    if ($ClientIndex -lt 0 -or $ClientIndex -gt 3) {
        throw 'ClientIndex must be 0 through 3.'
    }

    if ($ScreenBounds.Width -le 0 -or $ScreenBounds.Height -le 0) {
        throw 'Screen bounds must have positive width and height.'
    }

    $leftWidth = [int][Math]::Floor($ScreenBounds.Width / 2.0)
    $topHeight = [int][Math]::Floor($ScreenBounds.Height / 2.0)
    $rightWidth = [int]$ScreenBounds.Width - $leftWidth
    $bottomHeight = [int]$ScreenBounds.Height - $topHeight

    $isRightColumn = ($ClientIndex % 2) -eq 1
    $isBottomRow = $ClientIndex -ge 2

    [pscustomobject]@{
        ClientIndex = $ClientIndex
        X = $(if ($isRightColumn) { [int]$ScreenBounds.X + $leftWidth } else { [int]$ScreenBounds.X })
        Y = $(if ($isBottomRow) { [int]$ScreenBounds.Y + $topHeight } else { [int]$ScreenBounds.Y })
        Width = $(if ($isRightColumn) { $rightWidth } else { $leftWidth })
        Height = $(if ($isBottomRow) { $bottomHeight } else { $topHeight })
    }
}

function Get-LocalCoopClientWindowPlacementPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$ScreenBounds,
        [Parameter(Mandatory = $true)]
        [int]$ClientCount
    )

    Assert-LocalCoopClientCount -ClientCount $ClientCount

    for ($clientIndex = 0; $clientIndex -lt $ClientCount; $clientIndex++) {
        Get-LocalCoopClientWindowBounds -ScreenBounds $ScreenBounds -ClientIndex $clientIndex
    }
}

function Get-LocalCoopClientEventLogPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameRoot,
        [Parameter(Mandatory = $true)]
        [string]$ClientConfigDirectory,
        [Parameter(Mandatory = $true)]
        [int]$ClientIndex
    )

    $role = if ($ClientIndex -eq 0) { 'host' } else { 'client' }
    $configPath = Join-Path $ClientConfigDirectory 'enable-local-broker.txt'
    if (Test-Path -LiteralPath $configPath) {
        foreach ($line in Get-Content -LiteralPath $configPath) {
            $trimmed = $line.Trim()
            if ($trimmed.StartsWith('role=', [StringComparison]::OrdinalIgnoreCase)) {
                $configuredRole = $trimmed.Substring(5).Trim().ToLowerInvariant()
                if ($configuredRole -eq 'host' -or $configuredRole -eq 'client') {
                    $role = $configuredRole
                }

                break
            }
        }
    }

    Join-Path $GameRoot ("mods\LocalCoop\localcoop-{0}-{1}-events.txt" -f $role, $ClientIndex)
}

function Test-LocalCoopClientStartupLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    try {
        $content = Get-Content -Raw -LiteralPath $Path -ErrorAction Stop
        return $content.IndexOf('Broker mode enabled:', [StringComparison]::Ordinal) -ge 0
    }
    catch {
        return $false
    }
}

function Wait-LocalCoopClientStartupLogs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$ClientLaunches,
        [Parameter(Mandatory = $true)]
        [string]$GameRoot,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds,
        [int]$PollIntervalMilliseconds = 250,
        [scriptblock]$TestStartupLog = {
            param($path)
            Test-LocalCoopClientStartupLog -Path $path
        },
        [scriptblock]$SleepMilliseconds = {
            param($milliseconds)
            Start-Sleep -Milliseconds $milliseconds
        }
    )

    if ($TimeoutSeconds -lt 0) {
        throw 'Window placement readiness timeout must be zero or greater.'
    }

    if ($PollIntervalMilliseconds -le 0) {
        throw 'Window placement readiness poll interval must be positive.'
    }

    $pending = @{}
    foreach ($clientLaunch in $ClientLaunches) {
        $eventLogPath = Get-LocalCoopClientEventLogPath `
            -GameRoot $GameRoot `
            -ClientConfigDirectory $clientLaunch.ConfigDirectory `
            -ClientIndex $clientLaunch.ClientIndex
        $pending[$clientLaunch.ClientIndex] = $eventLogPath
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($pending.Count -gt 0) {
        foreach ($clientIndex in @($pending.Keys)) {
            if (& $TestStartupLog $pending[$clientIndex]) {
                [void]$pending.Remove($clientIndex)
            }
        }

        if ($pending.Count -eq 0) {
            Write-Host 'All LocalCoop client startup logs are ready; placing windows.'
            return $true
        }

        if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            $missing = ($pending.GetEnumerator() | Sort-Object Key | ForEach-Object { "client $($_.Key): $($_.Value)" }) -join '; '
            Write-Warning ("Timed out waiting for LocalCoop client startup logs before window placement: {0}" -f $missing)
            return $false
        }

        & $SleepMilliseconds $PollIntervalMilliseconds
    }

    $true
}

function Get-LocalCoopWindowPlacementAttemptCount {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$StabilizationSeconds,
        [Parameter(Mandatory = $true)]
        [int]$RetryIntervalMilliseconds
    )

    if ($StabilizationSeconds -lt 0) {
        throw 'Window placement stabilization seconds must be zero or greater.'
    }

    if ($RetryIntervalMilliseconds -le 0) {
        throw 'Window placement retry interval must be positive.'
    }

    1 + [int][Math]::Floor(($StabilizationSeconds * 1000.0) / $RetryIntervalMilliseconds)
}

function ConvertTo-LocalCoopOuterWindowBounds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$DesiredFrameBounds,
        [Parameter(Mandatory = $true)]
        [object]$CurrentWindowBounds,
        [Parameter(Mandatory = $true)]
        [object]$CurrentVisibleFrameBounds
    )

    $leftMargin = [int]$CurrentVisibleFrameBounds.X - [int]$CurrentWindowBounds.X
    $topMargin = [int]$CurrentVisibleFrameBounds.Y - [int]$CurrentWindowBounds.Y
    $rightMargin = ([int]$CurrentWindowBounds.X + [int]$CurrentWindowBounds.Width) - ([int]$CurrentVisibleFrameBounds.X + [int]$CurrentVisibleFrameBounds.Width)
    $bottomMargin = ([int]$CurrentWindowBounds.Y + [int]$CurrentWindowBounds.Height) - ([int]$CurrentVisibleFrameBounds.Y + [int]$CurrentVisibleFrameBounds.Height)

    [pscustomobject]@{
        X = [int]$DesiredFrameBounds.X - $leftMargin
        Y = [int]$DesiredFrameBounds.Y - $topMargin
        Width = [int]$DesiredFrameBounds.Width + $leftMargin + $rightMargin
        Height = [int]$DesiredFrameBounds.Height + $topMargin + $bottomMargin
    }
}

function ConvertTo-LocalCoopComparableVisibleFrameBounds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$VisibleFrameBounds,
        [Parameter(Mandatory = $true)]
        [object]$WindowBounds,
        [Parameter(Mandatory = $true)]
        [int]$Dpi
    )

    if ($Dpi -le 0) {
        throw 'DPI must be positive.'
    }

    $visibleFrameIsLargerThanWindow =
        [int]$VisibleFrameBounds.Width -gt [int]$WindowBounds.Width -or
        [int]$VisibleFrameBounds.Height -gt [int]$WindowBounds.Height

    if (-not $visibleFrameIsLargerThanWindow -or $Dpi -eq 96) {
        return $VisibleFrameBounds
    }

    $scale = Get-LocalCoopWindowCoordinateScale `
        -VisibleFrameBounds $VisibleFrameBounds `
        -WindowBounds $WindowBounds `
        -Dpi $Dpi

    [pscustomobject]@{
        X = [int][Math]::Round([int]$VisibleFrameBounds.X / $scale)
        Y = [int][Math]::Round([int]$VisibleFrameBounds.Y / $scale)
        Width = [int][Math]::Round([int]$VisibleFrameBounds.Width / $scale)
        Height = [int][Math]::Round([int]$VisibleFrameBounds.Height / $scale)
    }
}

function ConvertTo-LocalCoopComparableDesiredFrameBounds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$DesiredFrameBounds,
        [Parameter(Mandatory = $true)]
        [double]$CoordinateScale
    )

    if ($CoordinateScale -le 0) {
        throw 'Coordinate scale must be positive.'
    }

    [pscustomobject]@{
        X = [int][Math]::Round([int]$DesiredFrameBounds.X / $CoordinateScale)
        Y = [int][Math]::Round([int]$DesiredFrameBounds.Y / $CoordinateScale)
        Width = [int][Math]::Round([int]$DesiredFrameBounds.Width / $CoordinateScale)
        Height = [int][Math]::Round([int]$DesiredFrameBounds.Height / $CoordinateScale)
    }
}

function Get-LocalCoopWindowCoordinateScale {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$VisibleFrameBounds,
        [Parameter(Mandatory = $true)]
        [object]$WindowBounds,
        [Parameter(Mandatory = $true)]
        [int]$Dpi
    )

    if ($Dpi -le 0) {
        throw 'DPI must be positive.'
    }

    $visibleFrameIsLargerThanWindow =
        [int]$VisibleFrameBounds.Width -gt [int]$WindowBounds.Width -or
        [int]$VisibleFrameBounds.Height -gt [int]$WindowBounds.Height

    if ($visibleFrameIsLargerThanWindow -and $Dpi -ne 96) {
        return ($Dpi / 96.0)
    }

    1.0
}

function Initialize-LocalCoopWin32WindowApi {
    [CmdletBinding()]
    param()

    if (([System.Management.Automation.PSTypeName]'LocalCoop.WindowPlacementWin32').Type) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace LocalCoop
{
    public static class WindowPlacementWin32
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT rect, int attributeSize);

        public static bool ForceForegroundWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            var currentThreadId = GetCurrentThreadId();
            uint ignoredProcessId;
            var targetThreadId = GetWindowThreadProcessId(hWnd, out ignoredProcessId);
            var foregroundWindow = GetForegroundWindow();
            var foregroundThreadId = foregroundWindow == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foregroundWindow, out ignoredProcessId);
            var attachedToTarget = targetThreadId != 0 && targetThreadId != currentThreadId
                && AttachThreadInput(currentThreadId, targetThreadId, true);
            var attachedToForeground = foregroundThreadId != 0
                && foregroundThreadId != currentThreadId
                && foregroundThreadId != targetThreadId
                && AttachThreadInput(currentThreadId, foregroundThreadId, true);

            try
            {
                ShowWindow(hWnd, 9);
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                SetFocus(hWnd);
                return GetForegroundWindow() == hWnd;
            }
            finally
            {
                if (attachedToForeground)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }

                if (attachedToTarget)
                {
                    AttachThreadInput(currentThreadId, targetThreadId, false);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
'@
}

function Enable-LocalCoopDpiAwareness {
    [CmdletBinding()]
    param()

    if ($script:LocalCoopWindowPlacementDpiAwarenessInitialized) {
        return
    }

    Initialize-LocalCoopWin32WindowApi

    $perMonitorAwareV2 = [IntPtr]::new(-4)
    if (-not [LocalCoop.WindowPlacementWin32]::SetProcessDpiAwarenessContext($perMonitorAwareV2)) {
        [void][LocalCoop.WindowPlacementWin32]::SetProcessDPIAware()
    }

    $script:LocalCoopWindowPlacementDpiAwarenessInitialized = $true
}

function Wait-LocalCoopMainWindowHandle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($Process.HasExited) {
            return ([IntPtr]::Zero)
        }

        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $Process.MainWindowHandle
        }

        Start-Sleep -Milliseconds 250
    }

    [IntPtr]::Zero
}

function ConvertFrom-LocalCoopWin32Rect {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Rect
    )

    [pscustomobject]@{
        X = [int]$Rect.Left
        Y = [int]$Rect.Top
        Width = [int]$Rect.Right - [int]$Rect.Left
        Height = [int]$Rect.Bottom - [int]$Rect.Top
    }
}

function Get-LocalCoopWindowBounds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle
    )

    Initialize-LocalCoopWin32WindowApi

    $rect = New-Object LocalCoop.WindowPlacementWin32+RECT
    if (-not [LocalCoop.WindowPlacementWin32]::GetWindowRect($WindowHandle, [ref]$rect)) {
        $lastError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "GetWindowRect failed with Win32 error $lastError."
    }

    ConvertFrom-LocalCoopWin32Rect -Rect $rect
}

function Get-LocalCoopWindowDpi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle
    )

    Initialize-LocalCoopWin32WindowApi

    try {
        $dpi = [int][LocalCoop.WindowPlacementWin32]::GetDpiForWindow($WindowHandle)
        if ($dpi -gt 0) {
            return $dpi
        }
    }
    catch {
    }

    96
}

function Get-LocalCoopVisibleFrameBounds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)]
        [object]$FallbackWindowBounds
    )

    Initialize-LocalCoopWin32WindowApi

    $rect = New-Object LocalCoop.WindowPlacementWin32+RECT
    $extendedFrameBounds = 9
    $result = [LocalCoop.WindowPlacementWin32]::DwmGetWindowAttribute(
        $WindowHandle,
        $extendedFrameBounds,
        [ref]$rect,
        [Runtime.InteropServices.Marshal]::SizeOf([type]'LocalCoop.WindowPlacementWin32+RECT'))

    if ($result -ne 0) {
        return $FallbackWindowBounds
    }

    $visibleFrameBounds = ConvertFrom-LocalCoopWin32Rect -Rect $rect
    $dpi = Get-LocalCoopWindowDpi -WindowHandle $WindowHandle
    ConvertTo-LocalCoopComparableVisibleFrameBounds `
        -VisibleFrameBounds $visibleFrameBounds `
        -WindowBounds $FallbackWindowBounds `
        -Dpi $dpi
}

function Get-LocalCoopRawVisibleFrameBounds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle
    )

    Initialize-LocalCoopWin32WindowApi

    $rect = New-Object LocalCoop.WindowPlacementWin32+RECT
    $extendedFrameBounds = 9
    $result = [LocalCoop.WindowPlacementWin32]::DwmGetWindowAttribute(
        $WindowHandle,
        $extendedFrameBounds,
        [ref]$rect,
        [Runtime.InteropServices.Marshal]::SizeOf([type]'LocalCoop.WindowPlacementWin32+RECT'))

    if ($result -ne 0) {
        return $null
    }

    ConvertFrom-LocalCoopWin32Rect -Rect $rect
}

function Set-LocalCoopWindowBounds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)]
        [object]$Bounds
    )

    if ($WindowHandle -eq [IntPtr]::Zero) {
        throw 'Window handle must not be zero.'
    }

    Initialize-LocalCoopWin32WindowApi
    Enable-LocalCoopDpiAwareness

    $restoreWindow = 9
    [void][LocalCoop.WindowPlacementWin32]::ShowWindow($WindowHandle, $restoreWindow)

    $currentWindowBounds = Get-LocalCoopWindowBounds -WindowHandle $WindowHandle
    $rawVisibleFrameBounds = Get-LocalCoopRawVisibleFrameBounds -WindowHandle $WindowHandle
    $dpi = Get-LocalCoopWindowDpi -WindowHandle $WindowHandle
    if ($null -eq $rawVisibleFrameBounds) {
        $rawVisibleFrameBounds = $currentWindowBounds
    }

    $coordinateScale = Get-LocalCoopWindowCoordinateScale `
        -VisibleFrameBounds $rawVisibleFrameBounds `
        -WindowBounds $currentWindowBounds `
        -Dpi $dpi
    $currentVisibleFrameBounds = ConvertTo-LocalCoopComparableVisibleFrameBounds `
        -VisibleFrameBounds $rawVisibleFrameBounds `
        -WindowBounds $currentWindowBounds `
        -Dpi $dpi
    $desiredFrameBounds = ConvertTo-LocalCoopComparableDesiredFrameBounds `
        -DesiredFrameBounds $Bounds `
        -CoordinateScale $coordinateScale
    $outerBounds = ConvertTo-LocalCoopOuterWindowBounds `
        -DesiredFrameBounds $desiredFrameBounds `
        -CurrentWindowBounds $currentWindowBounds `
        -CurrentVisibleFrameBounds $currentVisibleFrameBounds

    $moved = [LocalCoop.WindowPlacementWin32]::MoveWindow(
        $WindowHandle,
        [int]$outerBounds.X,
        [int]$outerBounds.Y,
        [int]$outerBounds.Width,
        [int]$outerBounds.Height,
        $true)

    if (-not $moved) {
        $lastError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "MoveWindow failed with Win32 error $lastError."
    }
}

function Invoke-LocalCoopClientWindowPlacementStabilization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$ClientLaunches,
        [Parameter(Mandatory = $true)]
        [object]$ScreenBounds,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds,
        [int]$InitialDelayMilliseconds = 0,
        [Parameter(Mandatory = $true)]
        [int]$StabilizationSeconds,
        [Parameter(Mandatory = $true)]
        [int]$RetryIntervalMilliseconds,
        [scriptblock]$ResolveWindowHandle = {
            param($process, $timeoutSeconds)
            Wait-LocalCoopMainWindowHandle -Process $process -TimeoutSeconds $timeoutSeconds
        },
        [scriptblock]$MoveWindow = {
            param($windowHandle, $bounds)
            Set-LocalCoopWindowBounds -WindowHandle $windowHandle -Bounds $bounds
        },
        [scriptblock]$SleepMilliseconds = {
            param($milliseconds)
            Start-Sleep -Milliseconds $milliseconds
        }
    )

    if ($InitialDelayMilliseconds -lt 0) {
        throw 'Window placement initial delay must be zero or greater.'
    }

    $placements = @()
    foreach ($clientLaunch in $ClientLaunches) {
        $bounds = Get-LocalCoopClientWindowBounds -ScreenBounds $ScreenBounds -ClientIndex $clientLaunch.ClientIndex
        $placements += [pscustomobject]@{
            ClientIndex = $clientLaunch.ClientIndex
            ProcessId = $clientLaunch.Process.Id
            Process = $clientLaunch.Process
            Bounds = $bounds
        }
    }

    $attemptCount = Get-LocalCoopWindowPlacementAttemptCount `
        -StabilizationSeconds $StabilizationSeconds `
        -RetryIntervalMilliseconds $RetryIntervalMilliseconds

    if ($InitialDelayMilliseconds -gt 0) {
        & $SleepMilliseconds $InitialDelayMilliseconds
    }

    for ($attempt = 0; $attempt -lt $attemptCount; $attempt++) {
        foreach ($placement in $placements) {
            try {
                $windowHandle = & $ResolveWindowHandle $placement.Process $TimeoutSeconds
                if ($windowHandle -eq [IntPtr]::Zero) {
                    if ($attempt -eq 0) {
                        Write-Warning ("Could not find a main window for STS2 client process id {0} within {1} seconds." -f $placement.ProcessId, $TimeoutSeconds)
                    }

                    continue
                }

                & $MoveWindow $windowHandle $placement.Bounds
                if ($attempt -eq 0) {
                    Write-Host ("Placed STS2 client {0} process id {1} at {2},{3} {4}x{5}." -f $placement.ClientIndex, $placement.ProcessId, $placement.Bounds.X, $placement.Bounds.Y, $placement.Bounds.Width, $placement.Bounds.Height)
                }
            }
            catch {
                Write-Warning ("Could not place STS2 client {0} process id {1}: {2}" -f $placement.ClientIndex, $placement.ProcessId, $_.Exception.Message)
            }
        }

        if ($attempt -lt ($attemptCount - 1)) {
            & $SleepMilliseconds $RetryIntervalMilliseconds
        }
    }
}

function Invoke-LocalCoopSteamInputBootstrapFocus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$ClientLaunches,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds,
        [int]$HoldMilliseconds = 5000,
        [scriptblock]$ResolveWindowHandle = {
            param($process, $timeoutSeconds)
            Wait-LocalCoopMainWindowHandle -Process $process -TimeoutSeconds $timeoutSeconds
        },
        [scriptblock]$ActivateWindow = {
            param($windowHandle)
            Initialize-LocalCoopWin32WindowApi
            [LocalCoop.WindowPlacementWin32]::ForceForegroundWindow($windowHandle)
        },
        [scriptblock]$SleepMilliseconds = {
            param($milliseconds)
            Start-Sleep -Milliseconds $milliseconds
        }
    )

    if ($HoldMilliseconds -lt 0) {
        throw 'Steam Input bootstrap hold must be zero or greater.'
    }

    $hostLaunch = $ClientLaunches | Where-Object ClientIndex -eq 0 | Select-Object -First 1
    if ($null -eq $hostLaunch) {
        Write-Warning 'Could not bootstrap Steam Input because client 0 was not launched.'
        return $false
    }

    $windowHandle = & $ResolveWindowHandle $hostLaunch.Process $TimeoutSeconds
    if ($windowHandle -eq [IntPtr]::Zero) {
        Write-Warning 'Could not bootstrap Steam Input because the client 0 window was unavailable.'
        return $false
    }

    if (-not (& $ActivateWindow $windowHandle)) {
        Write-Warning 'Windows declined the client 0 foreground request; Steam Input may initialize after client 0 is focused manually.'
        return $false
    }

    Write-Host ("Focused STS2 client 0 for Steam Input discovery ({0} ms)." -f $HoldMilliseconds)
    if ($HoldMilliseconds -gt 0) {
        & $SleepMilliseconds $HoldMilliseconds
    }

    return $true
}

function Set-LocalCoopClientWindowPlacement {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [int]$ClientIndex,
        [Parameter(Mandatory = $true)]
        [object]$ScreenBounds,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $bounds = Get-LocalCoopClientWindowBounds -ScreenBounds $ScreenBounds -ClientIndex $ClientIndex
    $windowHandle = Wait-LocalCoopMainWindowHandle -Process $Process -TimeoutSeconds $TimeoutSeconds
    if ($windowHandle -eq [IntPtr]::Zero) {
        Write-Warning ("Could not find a main window for STS2 client process id {0} within {1} seconds." -f $Process.Id, $TimeoutSeconds)
        return $false
    }

    try {
        Set-LocalCoopWindowBounds -WindowHandle $windowHandle -Bounds $bounds
        Write-Host ("Placed STS2 client {0} process id {1} at {2},{3} {4}x{5}." -f $ClientIndex, $Process.Id, $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height)
        return $true
    }
    catch {
        Write-Warning ("Could not place STS2 client {0} process id {1}: {2}" -f $ClientIndex, $Process.Id, $_.Exception.Message)
        return $false
    }
}

function ConvertFrom-LocalCoopControllerDeviceList {
    [CmdletBinding()]
    param(
        [string]$ControllerDevices,
        [Parameter(Mandatory = $true)]
        [int]$ClientCount
    )

    Assert-LocalCoopClientCount -ClientCount $ClientCount

    if ([string]::IsNullOrWhiteSpace($ControllerDevices)) {
        $defaults = @()
        for ($index = 0; $index -lt $ClientCount; $index++) {
            $defaults += $index.ToString()
        }

        return $defaults
    }

    $parts = $ControllerDevices.Split(',') | ForEach-Object { $_.Trim() }
    if ($parts.Count -ne $ClientCount) {
        throw 'controllerDevices must provide one value per client.'
    }

    foreach ($part in $parts) {
        if ([string]::Equals($part, 'none', [StringComparison]::OrdinalIgnoreCase)) {
            'none'
            continue
        }

        $device = 0
        if (-not [int]::TryParse($part, [ref]$device) -or $device -lt 0 -or $device -gt 3) {
            throw 'controllerDevices values must be none or integers from 0 through 3.'
        }

        $device.ToString()
    }
}

function Get-LocalCoopDetectedControllerCount {
    [CmdletBinding()]
    param()

    $xinputSlots = @(Get-LocalCoopXInputControllerSlots)
    $connectedXInputSlots = @($xinputSlots | Where-Object { $_.Connected })
    if ($connectedXInputSlots.Count -gt 0) {
        return $connectedXInputSlots.Count
    }

    try {
        $controllers = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
            Where-Object {
                $name = $_.Name
                -not [string]::IsNullOrWhiteSpace($name) -and
                    ($name -match '(?i)(xinput|gamepad|controller|xbox|dualsense|dualshock|playstation|steam)')
            } |
            Select-Object -ExpandProperty PNPDeviceID -Unique

        return @($controllers).Count
    }
    catch {
        return 0
    }
}

function Get-LocalCoopXInputControllerSlots {
    [CmdletBinding()]
    param()

    if ($null -eq ('LocalCoopXInputProbe' -as [type])) {
        $code = @'
using System;
using System.Runtime.InteropServices;

public static class LocalCoopXInputProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct XInputGamepad {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputState {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    public static extern int XInputGetState(int userIndex, out XInputState state);
}
'@

        try {
            Add-Type -TypeDefinition $code
        }
        catch {
            Write-Warning ("Could not initialize XInput controller probe: {0}" -f $_.Exception.Message)
            return @()
        }
    }

    for ($slot = 0; $slot -lt 4; $slot++) {
        $state = New-Object LocalCoopXInputProbe+XInputState
        $result = [LocalCoopXInputProbe]::XInputGetState($slot, [ref]$state)
        [pscustomobject]@{
            Slot = $slot
            Connected = ($result -eq 0)
            Result = $result
            PacketNumber = $state.PacketNumber
        }
    }
}

function Write-LocalCoopControllerAssignmentDiagnostics {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ClientCount,
        [string]$ControllerDevices
    )

    $resolvedDevices = @(ConvertFrom-LocalCoopControllerDeviceList -ControllerDevices $ControllerDevices -ClientCount $ClientCount)
    $requestedControllerCount = @($resolvedDevices | Where-Object { -not [string]::Equals($_, 'none', [StringComparison]::OrdinalIgnoreCase) }).Count
    $xinputSlots = @(Get-LocalCoopXInputControllerSlots)
    $connectedXInputSlots = @($xinputSlots | Where-Object { $_.Connected })
    $detectedControllerCount = if ($connectedXInputSlots.Count -gt 0) {
        $connectedXInputSlots.Count
    }
    else {
        Get-LocalCoopDetectedControllerCount
    }

    Write-Host ("Controller diagnostics: requestedAutoClients={0} connectedXInputSlots={1} slots={2}" -f `
        $requestedControllerCount, `
        $connectedXInputSlots.Count, `
        (($connectedXInputSlots | ForEach-Object { $_.Slot }) -join ','))

    if ($requestedControllerCount -gt $detectedControllerCount) {
        Write-Warning ("Requested {0} controller-backed LocalCoop clients, but Windows prelaunch detection found {1} controller candidate(s). Steam Input may still resolve additional handles at runtime; configure unavailable clients as none if the mod does not log a Steam handle assignment." -f $requestedControllerCount, $detectedControllerCount)
    }

    for ($clientIndex = 0; $clientIndex -lt $resolvedDevices.Count; $clientIndex++) {
        $device = $resolvedDevices[$clientIndex]
        if ([string]::Equals($device, 'none', [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host ("Controller assignment: client={0} playerSlot={0} inputMode=none fallback=keyboard-only" -f $clientIndex)
            continue
        }

        Write-Host ("Controller assignment: client={0} playerSlot={0} inputMode=auto resolvedController=runtime SteamHandle=runtime SteamInputType=runtime XInputSlot=runtime GodotJoyId=runtime fallback=SteamInput,XInput,Godot" -f $clientIndex)
    }
}

function Format-LocalCoopClientBrokerConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ClientIndex,
        [Parameter(Mandatory = $true)]
        [string]$ControllerDevice,
        [Parameter(Mandatory = $true)]
        [int]$ControllerClientCount,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [string]$HostName,
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    if ([string]::IsNullOrWhiteSpace($SessionId)) {
        throw 'Session id must not be blank.'
    }

    if ([string]::IsNullOrWhiteSpace($HostName)) {
        throw 'Broker host must not be blank.'
    }

    if ($Port -le 0 -or $Port -gt 65535) {
        throw 'Broker port must be 1 through 65535.'
    }

    $role = if ($ClientIndex -eq 0) { 'host' } else { 'client' }
    $playerSlot = $ClientIndex.ToString()
    $inputMode = if ([string]::Equals($ControllerDevice, 'none', [StringComparison]::OrdinalIgnoreCase)) {
        'none'
    }
    else {
        'auto'
    }

    @(
        "role=$role"
        "clientIndex=$ClientIndex"
        "playerSlot=$playerSlot"
        "inputMode=$inputMode"
        "controllerClientCount=$ControllerClientCount"
        "endpoint=$($HostName):$Port"
        "sessionId=$SessionId"
        ''
    ) -join [Environment]::NewLine
}

function Write-LocalCoopClientBrokerConfigs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigRoot,
        [Parameter(Mandatory = $true)]
        [int]$ClientCount,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [string]$HostName,
        [Parameter(Mandatory = $true)]
        [int]$Port,
        [string]$ControllerDevices
    )

    Assert-LocalCoopClientCount -ClientCount $ClientCount
    $resolvedDevices = @(ConvertFrom-LocalCoopControllerDeviceList -ControllerDevices $ControllerDevices -ClientCount $ClientCount)
    $controllerClientCount = @($resolvedDevices | Where-Object { -not [string]::Equals($_, 'none', [StringComparison]::OrdinalIgnoreCase) }).Count

    for ($clientIndex = 0; $clientIndex -lt $ClientCount; $clientIndex++) {
        $clientDirectory = Join-Path $ConfigRoot ("client-{0}" -f $clientIndex)
        New-Item -ItemType Directory -Path $clientDirectory -Force | Out-Null
        $content = Format-LocalCoopClientBrokerConfig `
            -ClientIndex $clientIndex `
            -ControllerDevice $resolvedDevices[$clientIndex] `
            -ControllerClientCount $controllerClientCount `
            -SessionId $SessionId `
            -HostName $HostName `
            -Port $Port

        Set-Content -LiteralPath (Join-Path $clientDirectory 'enable-local-broker.txt') -Value $content
    }
}

function Invoke-LocalCoopClientPreparation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$ConfigRoot,
        [Parameter(Mandatory = $true)]
        [int]$ClientCount,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [int]$Port,
        [Parameter(Mandatory = $true)]
        [string]$GameExecutablePath,
        [string]$ControllerDevices
    )

    Assert-LocalCoopClientCount -ClientCount $ClientCount
    $harnessProjectPath = Join-Path $RepoRoot 'tools\LocalCoop.MultiClientHarness\LocalCoop.MultiClientHarness.csproj'
    if (-not (Test-Path -LiteralPath $harnessProjectPath)) {
        Write-LocalCoopClientBrokerConfigs `
            -ConfigRoot $ConfigRoot `
            -ClientCount $ClientCount `
            -SessionId $SessionId `
            -HostName '127.0.0.1' `
            -Port $Port `
            -ControllerDevices $ControllerDevices

        Write-Host ("{0}-client config prepared." -f $ClientCount)
        return
    }

    $arguments = Format-LocalCoopHarnessPreparationArgumentList `
        -ConfigRoot $ConfigRoot `
        -ClientCount $ClientCount `
        -SessionId $SessionId `
        -Port $Port `
        -GameExecutablePath $GameExecutablePath `
        -ControllerDevices $ControllerDevices

    Push-Location -LiteralPath $RepoRoot
    try {
        $prepareOutput = & dotnet @arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            foreach ($line in $prepareOutput) {
                Write-Host $line
            }

            throw "LocalCoop client harness preparation failed with exit code $LASTEXITCODE."
        }

        Write-Host ("{0}-client config prepared." -f $ClientCount)
    }
    finally {
        Pop-Location
    }
}

function Invoke-LocalCoopTwoClientPreparation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$ConfigRoot,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [int]$Port,
        [Parameter(Mandatory = $true)]
        [string]$GameExecutablePath
    )

    Invoke-LocalCoopClientPreparation `
        -RepoRoot $RepoRoot `
        -ConfigRoot $ConfigRoot `
        -ClientCount 2 `
        -SessionId $SessionId `
        -Port $Port `
        -GameExecutablePath $GameExecutablePath
}

function Format-LocalCoopHarnessPreparationArgumentList {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigRoot,
        [int]$ClientCount = 2,
        [Parameter(Mandatory = $true)]
        [string]$SessionId,
        [Parameter(Mandatory = $true)]
        [int]$Port,
        [Parameter(Mandatory = $true)]
        [string]$GameExecutablePath,
        [string]$ControllerDevices
    )

    Assert-LocalCoopClientCount -ClientCount $ClientCount

    $arguments = @(
        'run',
        '--no-restore',
        '--project',
        'tools\LocalCoop.MultiClientHarness',
        '--',
        'prepare-clients',
        $ConfigRoot,
        $ClientCount.ToString(),
        $SessionId,
        $Port.ToString(),
        $GameExecutablePath
    )

    if (-not [string]::IsNullOrWhiteSpace($ControllerDevices)) {
        $arguments += $ControllerDevices
    }

    $arguments
}

function Format-LocalCoopGameWindowArgumentList {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$WindowBounds
    )

    @(
        '--windowed',
        '--position',
        ('{0},{1}' -f [int]$WindowBounds.X, [int]$WindowBounds.Y),
        '--resolution',
        ('{0}x{1}' -f [int]$WindowBounds.Width, [int]$WindowBounds.Height)
    )
}

function Set-LocalCoopStartInfoEnvironmentVariable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $environmentVariables = $StartInfo.EnvironmentVariables
    if ($null -eq $environmentVariables) {
        $environmentVariables = $StartInfo.EnvironmentVariables
    }

    if ($null -ne $environmentVariables) {
        $environmentVariables.Set_Item($Name, $Value)
        return
    }

    $environment = $StartInfo.Environment
    if ($null -eq $environment) {
        $environment = $StartInfo.Environment
    }

    if ($null -ne $environment) {
        $environment.Set_Item($Name, $Value)
        return
    }

    throw 'Could not initialize process environment collection.'
}

function New-LocalCoopClientStartInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameExecutablePath,
        [Parameter(Mandatory = $true)]
        [string]$GameRoot,
        [Parameter(Mandatory = $true)]
        [string]$ClientConfigDirectory,
        [object]$WindowBounds,
        [switch]$UseGameWindowArguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.UseShellExecute = $false
    $startInfo.FileName = $GameExecutablePath
    $startInfo.WorkingDirectory = $GameRoot
    $startInfo.Arguments = ''
    if ($UseGameWindowArguments -and $null -ne $WindowBounds) {
        $gameWindowArguments = Format-LocalCoopGameWindowArgumentList -WindowBounds $WindowBounds
        $startInfo.Arguments = (($gameWindowArguments | ForEach-Object { ConvertTo-LocalCoopCommandLineArgument -Argument $_ }) -join ' ')
    }

    Set-LocalCoopStartInfoEnvironmentVariable `
        -StartInfo $startInfo `
        -Name 'LOCALCOOP_CONFIG_DIR' `
        -Value $ClientConfigDirectory
    $startInfo
}

function Start-LocalCoopClientProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameExecutablePath,
        [Parameter(Mandatory = $true)]
        [string]$GameRoot,
        [Parameter(Mandatory = $true)]
        [string]$ClientConfigDirectory,
        [object]$WindowBounds
    )

    $startInfo = New-LocalCoopClientStartInfo `
        -GameExecutablePath $GameExecutablePath `
        -GameRoot $GameRoot `
        -ClientConfigDirectory $ClientConfigDirectory `
        -WindowBounds $WindowBounds

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start STS2 client for config $ClientConfigDirectory."
    }

    $process
}

function Clear-LocalCoopLaunchLogs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameRoot
    )

    $modDirectory = Join-Path $GameRoot 'mods\LocalCoop'
    if (-not (Test-Path -LiteralPath $modDirectory)) {
        return
    }

    $logPatterns = @(
        'localcoop-events.txt',
        'localcoop-*-events.txt',
        'localcoop-probe.txt',
        'localcoop-transport-probe-*.txt'
    )

    foreach ($pattern in $logPatterns) {
        foreach ($logFile in Get-ChildItem -LiteralPath $modDirectory -Filter $pattern -File -ErrorAction SilentlyContinue) {
            try {
                Remove-Item -LiteralPath $logFile.FullName -Force
            }
            catch {
                Write-Warning ("Could not clear LocalCoop log {0}: {1}" -f $logFile.FullName, $_.Exception.Message)
            }
        }
    }
}

function Invoke-LocalCoopClientStartup {
    [CmdletBinding()]
    param(
        [string]$RepoRoot = $PSScriptRoot,
        [string]$GameRoot,
        [string]$ConfigRoot,
        [string]$BrokerConfigPath,
        [int]$ClientCount = 4,
        [string]$ControllerDevices,
        [string]$DefaultSessionId = 'local-test',
        [string]$DefaultHost = '127.0.0.1',
        [int]$DefaultPort = 38989,
        [int]$BrokerStartupTimeoutSeconds = 60,
        [int]$WindowPlacementTimeoutSeconds = 30,
        [int]$WindowPlacementReadinessTimeoutSeconds = 90,
        [int]$WindowPlacementStartupDelaySeconds = 0,
        [int]$WindowPlacementStabilizationSeconds = 15,
        [int]$WindowPlacementRetryIntervalMilliseconds = 1000,
        [switch]$ReuseExistingBroker,
        [switch]$SkipClientLaunch,
        [switch]$SkipWindowPlacement,
        [switch]$NoWaitForBroker
    )

    Assert-LocalCoopClientCount -ClientCount $ClientCount

    $resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    if ([string]::IsNullOrWhiteSpace($GameRoot)) {
        $GameRoot = Resolve-LocalCoopDefaultGameRoot -RepoRoot $resolvedRepoRoot
    }
    else {
        $GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
    }

    if ([string]::IsNullOrWhiteSpace($ConfigRoot)) {
        $ConfigRoot = Get-LocalCoopDefaultConfigRoot -RepoRoot $resolvedRepoRoot
    }

    if ([string]::IsNullOrWhiteSpace($BrokerConfigPath)) {
        $BrokerConfigPath = Join-Path $GameRoot 'mods\LocalCoop\enable-local-broker.txt'
    }

    $gameExecutablePath = Join-Path $GameRoot 'SlayTheSpire2.exe'
    if (-not (Test-Path -LiteralPath $gameExecutablePath)) {
        throw "Game executable not found: $gameExecutablePath"
    }

    Ensure-LocalCoopSteamAppIdFile -GameRoot $GameRoot

    $broker = Read-LocalCoopBrokerConfig `
        -ConfigPath $BrokerConfigPath `
        -DefaultSessionId $DefaultSessionId `
        -DefaultHost $DefaultHost `
        -DefaultPort $DefaultPort

    Write-Host ("Broker config: sessionId={0} endpoint={1}:{2} source={3}" -f $broker.SessionId, $broker.Host, $broker.Port, $broker.Source)

    $startedBrokerProcess = $null
    if ($ReuseExistingBroker) {
        $brokerUp = Test-LocalCoopTcpPort -HostName $broker.Host -Port $broker.Port
        if ($brokerUp) {
            Write-Host ("Broker already listening on {0}:{1}; reusing it because -ReuseExistingBroker was supplied." -f $broker.Host, $broker.Port)
        }
        else {
            Write-Host ("Broker is not listening on {0}:{1}; starting it now." -f $broker.Host, $broker.Port)
            $startedBrokerProcess = Start-LocalCoopBrokerProcess -RepoRoot $resolvedRepoRoot -SessionId $broker.SessionId -Port $broker.Port
            Write-Host ("Started broker process id {0}." -f $startedBrokerProcess.Id)

            if (-not (Wait-LocalCoopBroker -HostName $broker.Host -Port $broker.Port -TimeoutSeconds $BrokerStartupTimeoutSeconds)) {
                throw "Broker did not start listening on $($broker.Host):$($broker.Port) within $BrokerStartupTimeoutSeconds seconds."
            }
        }
    }
    else {
        Stop-LocalCoopExistingBrokers `
            -RepoRoot $resolvedRepoRoot `
            -HostName $broker.Host `
            -Port $broker.Port

        Write-Host ("Starting fresh broker on {0}:{1}." -f $broker.Host, $broker.Port)
        $startedBrokerProcess = Start-LocalCoopBrokerProcess -RepoRoot $resolvedRepoRoot -SessionId $broker.SessionId -Port $broker.Port
        Write-Host ("Started broker process id {0}." -f $startedBrokerProcess.Id)

        if (-not (Wait-LocalCoopBroker -HostName $broker.Host -Port $broker.Port -TimeoutSeconds $BrokerStartupTimeoutSeconds)) {
            throw "Broker did not start listening on $($broker.Host):$($broker.Port) within $BrokerStartupTimeoutSeconds seconds."
        }
    }

    Write-LocalCoopControllerAssignmentDiagnostics `
        -ClientCount $ClientCount `
        -ControllerDevices $ControllerDevices

    Invoke-LocalCoopClientPreparation `
        -RepoRoot $resolvedRepoRoot `
        -ConfigRoot $ConfigRoot `
        -ClientCount $ClientCount `
        -SessionId $broker.SessionId `
        -Port $broker.Port `
        -GameExecutablePath $gameExecutablePath `
        -ControllerDevices $ControllerDevices

    if ($SkipClientLaunch) {
        Write-Host ("Prepared {0}-client config; client launch skipped." -f $ClientCount)
        if ($startedBrokerProcess -is [System.Diagnostics.Process] -and -not $NoWaitForBroker) {
            Write-Host 'Broker was started by this script. Leave this window open; press Ctrl+C to stop it.'
            Wait-LocalCoopBrokerProcess -Process $startedBrokerProcess
        }

        return
    }

    Clear-LocalCoopLaunchLogs -GameRoot $GameRoot

    $clientDirectories = @(Get-LocalCoopClientConfigDirectories -ConfigRoot $ConfigRoot -ClientCount $ClientCount)
    $clientLaunches = @()
    $screenBounds = $null
    $windowPlacementPlan = @()
    if (-not $SkipWindowPlacement) {
        $screenBounds = Get-LocalCoopPrimaryScreenWorkingArea
        $windowPlacementPlan = @(Get-LocalCoopClientWindowPlacementPlan -ScreenBounds $screenBounds -ClientCount $ClientCount)
    }

    for ($clientIndex = 0; $clientIndex -lt $clientDirectories.Count; $clientIndex++) {
        $clientDirectory = $clientDirectories[$clientIndex]
        if (-not (Test-Path -LiteralPath (Join-Path $clientDirectory 'enable-local-broker.txt'))) {
            throw "Client config missing: $clientDirectory"
        }

        $windowBounds = $null
        if ($windowPlacementPlan.Count -gt $clientIndex) {
            $windowBounds = $windowPlacementPlan[$clientIndex]
        }

        $clientProcess = Start-LocalCoopClientProcess `
            -GameExecutablePath $gameExecutablePath `
            -GameRoot $GameRoot `
            -ClientConfigDirectory $clientDirectory `
            -WindowBounds $windowBounds

        Write-Host ("Started STS2 client process id {0} using config {1}." -f $clientProcess.Id, $clientDirectory)
        $clientLaunches += [pscustomobject]@{
            ClientIndex = $clientIndex
            Process = $clientProcess
            ConfigDirectory = $clientDirectory
        }
    }

    if (-not $SkipWindowPlacement) {
        try {
            [void](Wait-LocalCoopClientStartupLogs `
                -ClientLaunches $clientLaunches `
                -GameRoot $GameRoot `
                -TimeoutSeconds $WindowPlacementReadinessTimeoutSeconds)

            Invoke-LocalCoopClientWindowPlacementStabilization `
                -ClientLaunches $clientLaunches `
                -ScreenBounds $screenBounds `
                -TimeoutSeconds $WindowPlacementTimeoutSeconds `
                -InitialDelayMilliseconds ($WindowPlacementStartupDelaySeconds * 1000) `
                -StabilizationSeconds $WindowPlacementStabilizationSeconds `
                -RetryIntervalMilliseconds $WindowPlacementRetryIntervalMilliseconds

            [void](Invoke-LocalCoopSteamInputBootstrapFocus `
                -ClientLaunches $clientLaunches `
                -TimeoutSeconds $WindowPlacementTimeoutSeconds)
        }
        catch {
            Write-Warning ("Could not place LocalCoop client windows: {0}" -f $_.Exception.Message)
        }
    }

    if ($startedBrokerProcess -is [System.Diagnostics.Process] -and -not $NoWaitForBroker) {
        Write-Host 'Broker was started by this script. Leave this window open while the clients are running; press Ctrl+C to stop it.'
        Wait-LocalCoopBrokerProcess -Process $startedBrokerProcess
    }
}

function Invoke-LocalCoopTwoClientStartup {
    [CmdletBinding()]
    param(
        [string]$RepoRoot = $PSScriptRoot,
        [string]$GameRoot,
        [string]$ConfigRoot,
        [string]$BrokerConfigPath,
        [string]$DefaultSessionId = 'local-test',
        [string]$DefaultHost = '127.0.0.1',
        [int]$DefaultPort = 38989,
        [int]$BrokerStartupTimeoutSeconds = 60,
        [int]$WindowPlacementTimeoutSeconds = 30,
        [int]$WindowPlacementReadinessTimeoutSeconds = 90,
        [int]$WindowPlacementStartupDelaySeconds = 0,
        [int]$WindowPlacementStabilizationSeconds = 15,
        [int]$WindowPlacementRetryIntervalMilliseconds = 1000,
        [switch]$ReuseExistingBroker,
        [switch]$SkipClientLaunch,
        [switch]$SkipWindowPlacement,
        [switch]$NoWaitForBroker
    )

    Invoke-LocalCoopClientStartup `
        -RepoRoot $RepoRoot `
        -GameRoot $GameRoot `
        -ConfigRoot $ConfigRoot `
        -BrokerConfigPath $BrokerConfigPath `
        -ClientCount 2 `
        -DefaultSessionId $DefaultSessionId `
        -DefaultHost $DefaultHost `
        -DefaultPort $DefaultPort `
        -BrokerStartupTimeoutSeconds $BrokerStartupTimeoutSeconds `
        -WindowPlacementTimeoutSeconds $WindowPlacementTimeoutSeconds `
        -WindowPlacementReadinessTimeoutSeconds $WindowPlacementReadinessTimeoutSeconds `
        -WindowPlacementStartupDelaySeconds $WindowPlacementStartupDelaySeconds `
        -WindowPlacementStabilizationSeconds $WindowPlacementStabilizationSeconds `
        -WindowPlacementRetryIntervalMilliseconds $WindowPlacementRetryIntervalMilliseconds `
        -ReuseExistingBroker:$ReuseExistingBroker `
        -SkipClientLaunch:$SkipClientLaunch `
        -SkipWindowPlacement:$SkipWindowPlacement `
        -NoWaitForBroker:$NoWaitForBroker
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-LocalCoopTwoClientStartup @PSBoundParameters
}
