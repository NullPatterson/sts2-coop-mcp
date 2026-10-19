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

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$startupParameters = @{} + $PSBoundParameters

. (Join-Path $PSScriptRoot 'Start-LocalCoopTwoClient.ps1')

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-LocalCoopClientStartup @startupParameters
}
