[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Root = Join-Path $env:RUNNER_TEMP ('erge-phase7-' + [guid]::NewGuid().ToString('N'))
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
$OwnerCliProject = Join-Path $RepoRoot 'src\ErGe.OwnerControl.Cli\ErGe.OwnerControl.Cli.csproj'

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

    $output = & dotnet run --no-build --project $OwnerCliProject --configuration $Configuration -- $Argument 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Owner command $Argument failed. Output: $output"
    }

    if (($output | Out-String) -notmatch 'ERGE_OWNER_CONTROL_OK') {
        throw "Owner command marker missing for $Argument. Output: $output"
    }
}

function Wait-KeepAwake {
    param(
        [bool]$Expected,
        [int]$TimeoutSeconds = 12
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path $SessionStatusPath) {
            try {
                $status = Get-Content -Raw -LiteralPath $SessionStatusPath | ConvertFrom-Json
                if ($status.connected -and $status.authenticated -and $null -ne $status.keepAwakeApplied) {
                    if ([bool]$status.keepAwakeApplied -eq $Expected) {
                        return
                    }
                }
            } catch { }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    $current = if (Test-Path $SessionStatusPath) {
        Get-Content -Raw -LiteralPath $SessionStatusPath
    } else {
        '<missing>'
    }

    throw "Timed out waiting for KeepAwakeApplied=$Expected. Current status: $current"
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

    Wait-KeepAwake -Expected $true

    Invoke-Owner '--always-off'
    Wait-KeepAwake -Expected $false

    Invoke-Owner '--connect-now'
    Wait-KeepAwake -Expected $true

    Invoke-Owner '--emergency-block'
    Wait-KeepAwake -Expected $false

    Invoke-Owner '--clear-override'
    Wait-KeepAwake -Expected $false

    Write-Output 'ERGE_PHASE7_AVAILABILITY_E2E_OK'
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
