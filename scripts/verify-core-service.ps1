[CmdletBinding()]
param([int]$HeartbeatFreshSeconds = 10)

$ErrorActionPreference = 'Stop'

$ServiceName = 'ErGeCore'
$StatusPath = Join-Path $env:ProgramData 'ErGe\runtime-status.json'

$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if (-not $service) {
    throw 'ErGeCore service is not installed.'
}
if ($service.State -ne 'Running') {
    throw "ErGeCore is not running. Current state: $($service.State)"
}
if ($service.StartMode -ne 'Auto') {
    throw "ErGeCore is not configured for automatic startup. Current mode: $($service.StartMode)"
}

$expectedAccount = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-19')).Translate([System.Security.Principal.NTAccount]).Value
if ($service.StartName -ne $expectedAccount) {
    throw "ErGeCore is not using Local Service. Current account: $($service.StartName)"
}

if (-not (Test-Path $StatusPath)) {
    throw "Runtime status does not exist: $StatusPath"
}

$status = Get-Content -Raw -LiteralPath $StatusPath | ConvertFrom-Json
$heartbeat = [DateTimeOffset]$status.HeartbeatAtUtc
$age = ([DateTimeOffset]::UtcNow - $heartbeat).TotalSeconds
if ($age -ge $HeartbeatFreshSeconds) {
    throw "Runtime heartbeat is stale: $([Math]::Round($age, 2)) seconds."
}

$qfailure = cmd.exe /d /c "sc.exe qfailure $ServiceName"
$qfailureText = [string]::Join([Environment]::NewLine, $qfailure)
$qfailureFlag = cmd.exe /d /c "sc.exe qfailureflag $ServiceName"
$qfailureFlagText = [string]::Join([Environment]::NewLine, $qfailureFlag)

foreach ($delay in @('5000', '15000', '60000')) {
    if ($qfailureText -notmatch $delay) {
        throw "SCM recovery policy is missing restart delay $delay ms."
    }
}

if ($qfailureFlagText -notmatch 'TRUE') {
    throw 'SCM failure actions on non-crash failures are not enabled.'
}

[pscustomobject]@{
    Name = $service.Name
    State = $service.State
    StartMode = $service.StartMode
    Account = $service.StartName
    ProcessId = $service.ProcessId
    RuntimeState = $status.RuntimeState
    PersistentMode = $status.PersistentMode
    EffectiveAccess = $status.EffectiveAccess
    ConfigurationHealthy = $status.ConfigurationHealthy
    HeartbeatAgeSeconds = [Math]::Round($age, 2)
    InstanceId = $status.InstanceId
}

Write-Output 'ERGE_CORE_VERIFY_OK'
