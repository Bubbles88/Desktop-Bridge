[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$TempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:TEMP) { $env:TEMP } else { [System.IO.Path]::GetTempPath() }
$Root = Join-Path $TempRoot ('erge-phase10-' + [guid]::NewGuid().ToString('N'))
$PolicyPath = Join-Path $Root 'policy.json'
$StatusPath = Join-Path $Root 'runtime-status.json'
$SessionStatusPath = Join-Path $Root 'session-agent-status.json'
$OwnerPath = Join-Path $Root 'session-owner.json'
$CoreOut = Join-Path $Root 'core.out.log'
$CoreErr = Join-Path $Root 'core.err.log'
$AgentOut = Join-Path $Root 'agent.out.log'
$AgentErr = Join-Path $Root 'agent.err.log'

$CoreProject = Join-Path $RepoRoot 'src\ErGe.Core.Service\ErGe.Core.Service.csproj'
$AgentProject = Join-Path $RepoRoot 'src\ErGe.SessionAgent\ErGe.SessionAgent.csproj'
$RemoteProject = Join-Path $RepoRoot 'src\ErGe.RemoteProvider.Cli\ErGe.RemoteProvider.Cli.csproj'
$LocalProject = Join-Path $RepoRoot 'src\ErGe.LocalProvider.Cli\ErGe.LocalProvider.Cli.csproj'
$OwnerProject = Join-Path $RepoRoot 'src\ErGe.OwnerControl.Cli\ErGe.OwnerControl.Cli.csproj'

$Core = $null
$Agent = $null

function Stop-Tree {
    param([System.Diagnostics.Process]$Process)
    if (-not $Process) { return }
    try {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            taskkill.exe /PID $Process.Id /T /F 2>$null | Out-Null
        }
    } catch { }
}

function Invoke-Owner {
    param([string]$Argument)

    $output = & dotnet run --no-build --project $OwnerProject --configuration $Configuration -- $Argument 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Owner command $Argument failed. Output: $output"
    }
    if (($output | Out-String) -notmatch 'ERGE_OWNER_CONTROL_OK') {
        throw "Owner command marker missing for $Argument. Output: $output"
    }
}

function Invoke-RemoteWindowsSuccess {
    $output = & dotnet run --no-build --project $RemoteProject --configuration $Configuration -- --probe-windows 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Remote provider windows.list probe failed. Output: $output"
    }
    $joined = $output | Out-String
    if ($joined -notmatch 'ERGE_REMOTE_PROVIDER_WINDOWS_LIST_OK') {
        throw "Remote windows.list success marker missing. Output: $joined"
    }
    if ($joined -notmatch '"success":true' -or $joined -notmatch '"windows":') {
        throw "Remote windows.list response was incomplete. Output: $joined"
    }
}

function Invoke-LocalWindowsSuccess {
    $output = & dotnet run --no-build --project $LocalProject --configuration $Configuration -- --probe-windows 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Local provider windows.list probe failed. Output: $output"
    }
    $joined = $output | Out-String
    if ($joined -notmatch 'ERGE_LOCAL_PROVIDER_WINDOWS_LIST_OK') {
        throw "Local windows.list success marker missing. Output: $joined"
    }
    if ($joined -notmatch '"success":true' -or $joined -notmatch '"windows":') {
        throw "Local windows.list response was incomplete. Output: $joined"
    }
}

function Invoke-ProviderDenied {
    param(
        [ValidateSet('remote','local')]
        [string]$Provider
    )

    $project = if ($Provider -eq 'remote') { $RemoteProject } else { $LocalProject }
    $output = & dotnet run --no-build --project $project --configuration $Configuration -- --probe-windows 2>&1
    $code = $LASTEXITCODE
    $joined = $output | Out-String

    if ($code -eq 0) {
        throw "$Provider windows.list unexpectedly succeeded. Output: $joined"
    }
    if ($joined -notmatch '"errorCode":"policy_denied"') {
        throw "$Provider windows.list denial was not policy_denied. Output: $joined"
    }
}

function Invoke-RemoteScreenRegression {
    $output = & dotnet run --no-build --project $RemoteProject --configuration $Configuration -- --probe-screen 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Existing remote screen.info regression probe failed. Output: $output"
    }
    if (($output | Out-String) -notmatch 'ERGE_REMOTE_PROVIDER_SCREEN_INFO_OK') {
        throw "Existing screen.info marker missing. Output: $output"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    [ordered]@{
        SchemaVersion = 1
        PersistentMode = 'AlwaysOn'
    } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath $PolicyPath

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.User) { throw 'Unable to resolve Windows runner SID.' }

    [ordered]@{
        schemaVersion = 1
        userSid = $identity.User.Value
        userName = $identity.Name
    } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath $OwnerPath

    $env:ERGE_POLICY_PATH = $PolicyPath
    $env:ERGE_STATUS_PATH = $StatusPath
    $env:ERGE_SESSION_STATUS_PATH = $SessionStatusPath
    $env:ERGE_SESSION_OWNER_PATH = $OwnerPath

    $coreArgs = @('run','--no-build','--project',$CoreProject,'--configuration',$Configuration)
    $Core = Start-Process -FilePath 'dotnet' -ArgumentList $coreArgs -RedirectStandardOutput $CoreOut -RedirectStandardError $CoreErr -PassThru -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds(20)
    do {
        $Core.Refresh()
        if ($Core.HasExited) { throw "Core exited early with code $($Core.ExitCode)." }
        if (Test-Path $StatusPath) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    if (-not (Test-Path $StatusPath)) { throw 'Core did not create runtime status.' }

    $agentArgs = @('run','--no-build','--project',$AgentProject,'--configuration',$Configuration)
    $Agent = Start-Process -FilePath 'dotnet' -ArgumentList $agentArgs -RedirectStandardOutput $AgentOut -RedirectStandardError $AgentErr -PassThru -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds(20)
    do {
        $Agent.Refresh()
        if ($Agent.HasExited) { throw "Session Agent exited early with code $($Agent.ExitCode)." }
        if (Test-Path $SessionStatusPath) {
            $sessionStatus = Get-Content -Raw -LiteralPath $SessionStatusPath | ConvertFrom-Json
            if ($sessionStatus.connected -and $sessionStatus.authenticated) { break }
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    $sessionStatus = Get-Content -Raw -LiteralPath $SessionStatusPath | ConvertFrom-Json
    if (-not ($sessionStatus.connected -and $sessionStatus.authenticated)) {
        throw 'Session Agent did not reach authenticated connected state.'
    }

    Invoke-RemoteWindowsSuccess
    Invoke-LocalWindowsSuccess
    Invoke-RemoteScreenRegression

    Invoke-Owner '--local-only'
    Invoke-ProviderDenied -Provider remote
    Invoke-LocalWindowsSuccess

    Invoke-Owner '--emergency-block'
    Invoke-ProviderDenied -Provider remote
    Invoke-ProviderDenied -Provider local

    Invoke-Owner '--clear-override'
    Invoke-RemoteWindowsSuccess
    Invoke-RemoteScreenRegression

    Invoke-Owner '--always-off'
    Invoke-ProviderDenied -Provider remote
    Invoke-ProviderDenied -Provider local

    Invoke-Owner '--always-on'
    Invoke-RemoteWindowsSuccess

    Write-Output 'ERGE_PHASE10_WINDOWS_LIST_E2E_OK'
}
catch {
    Write-Host '--- Core stdout ---'
    if (Test-Path $CoreOut) { Get-Content -LiteralPath $CoreOut }
    Write-Host '--- Core stderr ---'
    if (Test-Path $CoreErr) { Get-Content -LiteralPath $CoreErr }
    Write-Host '--- Agent stdout ---'
    if (Test-Path $AgentOut) { Get-Content -LiteralPath $AgentOut }
    Write-Host '--- Agent stderr ---'
    if (Test-Path $AgentErr) { Get-Content -LiteralPath $AgentErr }
    throw
}
finally {
    Stop-Tree $Agent
    Stop-Tree $Core

    Remove-Item Env:ERGE_POLICY_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:ERGE_STATUS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:ERGE_SESSION_STATUS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:ERGE_SESSION_OWNER_PATH -ErrorAction SilentlyContinue

    Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
}
