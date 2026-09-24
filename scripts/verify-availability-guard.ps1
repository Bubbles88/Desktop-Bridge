[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$TaskName = 'ErGeAvailabilityGuard'
$InstallRoot = Join-Path $env:ProgramData 'ErGe\Availability'
$InstalledGuard = Join-Path $InstallRoot 'erge-availability-guard.ps1'
$BaselinePath = Join-Path $InstallRoot 'availability-baseline.json'
$StatePath = Join-Path $InstallRoot 'availability-state.json'

foreach ($path in @($InstalledGuard,$BaselinePath,$StatePath)) { if (-not (Test-Path $path)) { throw "Phase 8 required file is missing: $path" } }
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
$info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction Stop
if ($task.Principal.RunLevel -ne 'Highest') { throw 'Availability guard is not configured with highest run level.' }
$state = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json
if ($state.Outcome -ne 'Applied') { throw "Availability guard state is not Applied. Outcome: $($state.Outcome). Error: $($state.Error)" }
$baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json
if ($baseline.SchemaVersion -ne 2) { throw "Unsupported availability baseline schema: $($baseline.SchemaVersion)" }
[pscustomobject]@{ TaskName=$TaskName; TaskState=$task.State; LastRunTime=$info.LastRunTime; LastTaskResult=$info.LastTaskResult; DesiredMode=$state.DesiredMode; Outcome=$state.Outcome; ActivePowerScheme=$state.ActivePowerScheme; BaselineCapturedAtUtc=$baseline.CapturedAtUtc; PixelShiftBaseline=$baseline.AsusOledCare.EnablePixelShift.Value; PixelRefreshBaseline=$baseline.AsusOledCare.EnablePixelRefresh.Value; MachineScreenSaverTimeBaseline=$baseline.AsusMachineOledCare.ScreenSaverTime.Value; MachinePixelShiftBaseline=$baseline.AsusMachineOledCare.EnablePixelShift.Value; MachinePixelRefreshBaseline=$baseline.AsusMachineOledCare.EnablePixelRefresh.Value }
Write-Output 'ERGE_PHASE8_VERIFY_OK'
