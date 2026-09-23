[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$InstallRoot = Join-Path $env:LOCALAPPDATA 'ErGe\SessionAgent'
$Exe = Join-Path $InstallRoot 'ErGe.SessionAgent.exe'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName = 'ErGeSessionAgent'

if (Test-Path $RunKey) {
    Remove-ItemProperty -Path $RunKey -Name $RunName -ErrorAction SilentlyContinue
}

if (Test-Path $Exe) {
    Get-CimInstance Win32_Process |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath -eq $Exe } |
        ForEach-Object {
            taskkill.exe /PID $_.ProcessId /T /F 2>$null | Out-Null
        }
}

Remove-Item -LiteralPath $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'ERGE_SESSION_AUTOSTART_UNINSTALL_OK'
