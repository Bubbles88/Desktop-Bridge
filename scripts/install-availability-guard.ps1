[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Phase 8 availability guard installation must run elevated.'
    }
}

Assert-Administrator

$TaskName = 'ErGeAvailabilityGuard'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$SourceGuard = Join-Path $RepoRoot 'scripts\availability\erge-availability-guard.ps1'
$InstallRoot = Join-Path $env:ProgramData 'ErGe\Availability'
$InstalledGuard = Join-Path $InstallRoot 'erge-availability-guard.ps1'

if (-not (Test-Path $SourceGuard)) { throw "Guard source is missing: $SourceGuard" }
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
Copy-Item -LiteralPath $SourceGuard -Destination $InstalledGuard -Force
& $InstalledGuard -CaptureBaseline

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.User) { throw 'Unable to resolve the owner Windows identity.' }
$owner = $identity.Name
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + $InstalledGuard + '" -Watch')
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $owner
$dailyTrigger = New-ScheduledTaskTrigger -Daily -At '00:05'
$principal = New-ScheduledTaskPrincipal -UserId $owner -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($logonTrigger,$dailyTrigger) -Principal $principal -Settings $settings -Description 'Owner-controlled ErGe Windows availability and drift correction.' -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2
Write-Output "ERGE_PHASE8_INSTALL_OK $InstalledGuard"
