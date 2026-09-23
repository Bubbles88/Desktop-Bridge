[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Root = Join-Path $env:RUNNER_TEMP ('erge-phase6-' + [guid]::NewGuid().ToString('N'))
$PolicyPath = Join-Path $Root 'policy.json'
$StatusPath = Join-Path $Root 'runtime-status.json'
$SessionStatusPath = Join-Path $Root 'session-agent-status.json'
$OwnerPath = Join-Path $Root 'session-owner.json'
$CoreOut = Join-Path $Root 'core.out.log'
$CoreErr = Join-Path $Root 'core.err.log'

$CoreProject = Join-Path $RepoRoot 'src\ErGe.Core.Service\ErGe.Core.Service.csproj'
$OwnerCliProject = Join-Path $RepoRoot 'src\ErGe.OwnerControl.Cli\ErGe.OwnerControl.Cli.csproj'

$Core = $null

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

function Invoke-OwnerCommand {
    param([string]$Argument)

    $output = & dotnet run --no-build --project $OwnerCliProject --configuration $Configuration -- $Argument 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Owner Control command $Argument failed with exit code $LASTEXITCODE. Output: $output"
    }

    $joined = ($output | Out-String)
    if ($joined -notmatch 'ERGE_OWNER_CONTROL_OK') {
        throw "Owner Control success marker missing for $Argument. Output: $joined"
    }

    return $joined
}

try {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

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
        if ($Core.HasExited) {
            throw "Core exited early with code $($Core.ExitCode)."
        }
        if (Test-Path $StatusPath) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    if (-not (Test-Path $StatusPath)) {
        throw 'Core did not create runtime status.'
    }

    $status = Invoke-OwnerCommand '--status'
    if ($status -notmatch '"persistentMode":"AlwaysOff"') {
        throw "Initial owner state was not AlwaysOff. Output: $status"
    }

    $alwaysOn = Invoke-OwnerCommand '--always-on'
    if ($alwaysOn -notmatch '"persistentMode":"AlwaysOn"') {
        throw "Always On command did not take effect. Output: $alwaysOn"
    }

    if (-not (Test-Path $PolicyPath)) {
        throw 'Always On did not persist the policy file.'
    }

    $policy = Get-Content -Raw -LiteralPath $PolicyPath | ConvertFrom-Json
    if ($policy.PersistentMode -ne 'AlwaysOn') {
        throw "Persisted policy is not AlwaysOn: $($policy.PersistentMode)"
    }

    $localOnly = Invoke-OwnerCommand '--local-only'
    if ($localOnly -notmatch '"sessionOverride":"LocalOnly"') {
        throw "Local Only override did not take effect. Output: $localOnly"
    }

    $clear = Invoke-OwnerCommand '--clear-override'
    if ($clear -notmatch '"sessionOverride":"None"') {
        throw "Session override did not clear. Output: $clear"
    }

    $alwaysOff = Invoke-OwnerCommand '--always-off'
    if ($alwaysOff -notmatch '"persistentMode":"AlwaysOff"') {
        throw "Always Off command did not take effect. Output: $alwaysOff"
    }

    $policy = Get-Content -Raw -LiteralPath $PolicyPath | ConvertFrom-Json
    if ($policy.PersistentMode -ne 'AlwaysOff') {
        throw "Persisted policy is not AlwaysOff: $($policy.PersistentMode)"
    }

    Write-Output 'ERGE_PHASE6_OWNER_CONTROL_E2E_OK'
}
catch {
    Write-Host '--- Core stdout ---'
    if (Test-Path $CoreOut) { Get-Content -LiteralPath $CoreOut }
    Write-Host '--- Core stderr ---'
    if (Test-Path $CoreErr) { Get-Content -LiteralPath $CoreErr }
    throw
}
finally {
    Stop-Tree $Core
    Remove-Item Env:ERGE_POLICY_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:ERGE_STATUS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:ERGE_SESSION_STATUS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:ERGE_SESSION_OWNER_PATH -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
}
