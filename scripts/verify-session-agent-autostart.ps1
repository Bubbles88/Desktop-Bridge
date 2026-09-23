[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$InstallRoot = Join-Path $env:LOCALAPPDATA 'ErGe\SessionAgent'
$Exe = Join-Path $InstallRoot 'ErGe.SessionAgent.exe'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName = 'ErGeSessionAgent'
$Expected = '"' + $Exe + '"'

if (-not (Test-Path $Exe)) {
    throw "Session Agent executable is missing: $Exe"
}

$Actual = (Get-ItemProperty -Path $RunKey -Name $RunName -ErrorAction Stop).$RunName
if ($Actual -ne $Expected) {
    throw "Session Agent Run registration mismatch. Expected '$Expected'; found '$Actual'."
}

Write-Output "ERGE_SESSION_AUTOSTART_VERIFY_OK $Exe"
