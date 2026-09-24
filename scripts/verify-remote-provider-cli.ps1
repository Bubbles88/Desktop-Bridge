[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$Exe = Join-Path $env:LOCALAPPDATA 'ErGe\RemoteProvider\ErGe.RemoteProvider.Cli.exe'
if (-not (Test-Path $Exe)) {
    throw "Remote Provider CLI executable is missing: $Exe"
}

$item = Get-Item -LiteralPath $Exe
if ($item.Length -le 0) {
    throw "Remote Provider CLI executable is empty: $Exe"
}

[pscustomobject]@{
    Path = $Exe
    Size = $item.Length
    Modified = $item.LastWriteTime
}

Write-Output 'ERGE_REMOTE_PROVIDER_VERIFY_OK'
