[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src\ErGe.RemoteProvider.Cli\ErGe.RemoteProvider.Cli.csproj'
$InstallRoot = Join-Path $env:LOCALAPPDATA 'ErGe\RemoteProvider'
$Stage = Join-Path $env:TEMP ('erge-remote-provider-' + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $Stage | Out-Null

    dotnet publish $Project --configuration $Configuration --runtime win-x64 --self-contained false --output $Stage
    if ($LASTEXITCODE -ne 0) {
        throw "Remote Provider CLI publish failed with exit code $LASTEXITCODE."
    }

    if (Test-Path $InstallRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    Copy-Item -Path (Join-Path $Stage '*') -Destination $InstallRoot -Recurse -Force

    $Exe = Join-Path $InstallRoot 'ErGe.RemoteProvider.Cli.exe'
    if (-not (Test-Path $Exe)) {
        throw "Published Remote Provider CLI executable not found: $Exe"
    }

    Write-Output "ERGE_REMOTE_PROVIDER_INSTALL_OK $Exe"
}
finally {
    Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
}
