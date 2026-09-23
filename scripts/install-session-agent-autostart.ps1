[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src\ErGe.SessionAgent\ErGe.SessionAgent.csproj'
$InstallRoot = Join-Path $env:LOCALAPPDATA 'ErGe\SessionAgent'
$Stage = Join-Path $env:TEMP ('erge-session-agent-' + [guid]::NewGuid().ToString('N'))
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName = 'ErGeSessionAgent'

try {
    New-Item -ItemType Directory -Force -Path $Stage | Out-Null

    dotnet publish $Project --configuration $Configuration --runtime win-x64 --self-contained false --output $Stage
    if ($LASTEXITCODE -ne 0) {
        throw "Session Agent publish failed with exit code $LASTEXITCODE."
    }

    if (Test-Path $InstallRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    Copy-Item -Path (Join-Path $Stage '*') -Destination $InstallRoot -Recurse -Force

    $Exe = Join-Path $InstallRoot 'ErGe.SessionAgent.exe'
    if (-not (Test-Path $Exe)) {
        throw "Published Session Agent executable not found: $Exe"
    }

    New-Item -Path $RunKey -Force | Out-Null
    $RunCommand = '"' + $Exe + '"'
    Set-ItemProperty -Path $RunKey -Name $RunName -Value $RunCommand -Type String

    Write-Output "ERGE_SESSION_AUTOSTART_INSTALL_OK $Exe"
}
finally {
    Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
}
