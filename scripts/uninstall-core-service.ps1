[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = 'Stop'

$ServiceName = 'ErGeCore'
$InstallRoot = Join-Path $env:ProgramFiles 'ErGe\Core'
$DataRoot = Join-Path $env:ProgramData 'ErGe'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'ErGe Core removal must be run from an elevated PowerShell session.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }

    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to delete ErGeCore service.'
    }
}

if (Test-Path $InstallRoot) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}

if ($RemoveData -and (Test-Path $DataRoot)) {
    Remove-Item -LiteralPath $DataRoot -Recurse -Force
}

Write-Output 'ERGE_CORE_UNINSTALL_OK'
