[CmdletBinding()]
param([switch]$RemoveBaseline)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Phase 8 availability guard uninstall must run elevated.' }
}

Assert-Administrator
$TaskName = 'ErGeAvailabilityGuard'
$InstallRoot = Join-Path $env:ProgramData 'ErGe\Availability'
$InstalledGuard = Join-Path $InstallRoot 'erge-availability-guard.ps1'
$BaselinePath = Join-Path $InstallRoot 'availability-baseline.json'
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false }
if ((Test-Path $InstalledGuard) -and (Test-Path $BaselinePath)) { & $InstalledGuard -RestoreBaseline }
Remove-Item -LiteralPath (Join-Path $InstallRoot 'availability-state.json') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $InstalledGuard -Force -ErrorAction SilentlyContinue
if ($RemoveBaseline) { Remove-Item -LiteralPath $BaselinePath -Force -ErrorAction SilentlyContinue }
Write-Output 'ERGE_PHASE8_UNINSTALL_OK'
