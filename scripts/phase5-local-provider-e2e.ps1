[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$TempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:TEMP) { $env:TEMP } else { [System.IO.Path]::GetTempPath() }
$Root = Join-Path $TempRoot ('erge-phase5-' + [guid]::NewGuid().ToString('N'))
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
$ProviderProject = Join-Path $RepoRoot 'src\ErGe.LocalProvider.Cli\ErGe.LocalProvider.Cli.csproj'

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
    }
    catch {
        # Best-effort cleanup after the test result is already known.
    }
}

try {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    [ordered]@{
        SchemaVersion = 1
        PersistentMode = 'AlwaysOn'
    } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath $PolicyPath

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.User) {
        throw 'Unable to resolve Windows runner SID.'
    }

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

    $coreDeadline = (Get-Date).AddSeconds(20)
    do {
        $Core.Refresh()
        if ($Core.HasExited) {
            throw "Core exited early with code $($Core.ExitCode)."
        }

        if (Test-Path $StatusPath) {
            break
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $coreDeadline)

    if (-not (Test-Path $StatusPath)) {
        throw 'Core did not create runtime status.'
    }

    $agentArgs = @('run','--no-build','--project',$AgentProject,'--configuration',$Configuration)
    $Agent = Start-Process -FilePath 'dotnet' -ArgumentList $agentArgs -RedirectStandardOutput $AgentOut -RedirectStandardError $AgentErr -PassThru -WindowStyle Hidden

    $agentDeadline = (Get-Date).AddSeconds(20)
    $connected = $false

    do {
        $Agent.Refresh()
        if ($Agent.HasExited) {
            throw "Session Agent exited early with code $($Agent.ExitCode)."
        }

        if (Test-Path $SessionStatusPath) {
            $sessionStatus = Get-Content -Raw -LiteralPath $SessionStatusPath | ConvertFrom-Json
            if ($sessionStatus.connected -and $sessionStatus.authenticated) {
                $connected = $true
                break
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $agentDeadline)

    if (-not $connected) {
        throw 'Session Agent did not reach authenticated connected state.'
    }

    $providerOutput = & dotnet run --no-build --project $ProviderProject --configuration $Configuration -- --probe-screen 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "Local provider probe failed with exit code $LASTEXITCODE. Output: $providerOutput"
    }

    $joined = ($providerOutput | Out-String)
    if ($joined -notmatch 'ERGE_LOCAL_PROVIDER_SCREEN_INFO_OK') {
        throw "Local provider success marker missing. Output: $joined"
    }

    Write-Output $providerOutput
    Write-Output 'ERGE_PHASE5_E2E_OK'
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
