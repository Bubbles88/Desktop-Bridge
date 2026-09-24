[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$InstallRoot = Join-Path $env:LOCALAPPDATA 'ErGe\RemoteProvider'
if (Test-Path $InstallRoot) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}

Write-Output 'ERGE_REMOTE_PROVIDER_UNINSTALL_OK'
