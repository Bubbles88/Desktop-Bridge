[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'

$ServiceName = 'ErGeCore'
$DisplayName = 'ErGe Core'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src\ErGe.Core.Service\ErGe.Core.Service.csproj'
$InstallRoot = Join-Path $env:ProgramFiles 'ErGe\Core'
$DataRoot = Join-Path $env:ProgramData 'ErGe'
$StatusPath = Join-Path $DataRoot 'runtime-status.json'
$SessionOwnerPath = Join-Path $DataRoot 'session-owner.json'
$Stage = Join-Path $env:TEMP ('erge-core-publish-' + [guid]::NewGuid().ToString('N'))

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'ErGe Core installation must be run from an elevated PowerShell session.'
    }
}

function Get-LocalServiceAccountName {
    return (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-19')).Translate([System.Security.Principal.NTAccount]).Value
}

Assert-Administrator

try {
    New-Item -ItemType Directory -Force -Path $Stage, $DataRoot | Out-Null

    dotnet publish $Project --configuration $Configuration --runtime win-x64 --self-contained false --output $Stage
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }

    if (Test-Path $InstallRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    Copy-Item -Path (Join-Path $Stage '*') -Destination $InstallRoot -Recurse -Force

    $ownerIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $ownerIdentity.User) {
        throw 'Unable to determine the local owner Windows SID.'
    }

    [ordered]@{
        schemaVersion = 1
        userSid = $ownerIdentity.User.Value
        userName = $ownerIdentity.Name
    } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath $SessionOwnerPath

    icacls.exe $DataRoot /inheritance:e /grant '*S-1-5-19:(OI)(CI)M' /grant '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to configure ProgramData ACL.'
    }

    $Exe = Join-Path $InstallRoot 'ErGe.Core.Service.exe'
    $BinaryPath = '"' + $Exe + '"'

    if (-not $existing) {
        $newServiceParams = @{
            Name = $ServiceName
            BinaryPathName = $BinaryPath
            DisplayName = $DisplayName
            Description = 'Persistent local authority and health runtime for ErGe Desktop.'
            StartupType = 'Automatic'
        }
        New-Service @newServiceParams | Out-Null
    }
    else {
        $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        $change = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{
            PathName = $BinaryPath
            StartMode = 'Automatic'
        }
        if ($change.ReturnValue -ne 0) {
            throw "Failed to update ErGeCore service configuration. Win32_Service.Change returned $($change.ReturnValue)."
        }
    }

    $account = Get-LocalServiceAccountName
    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $accountChange = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ StartName = $account }
    if ($accountChange.ReturnValue -ne 0) {
        throw "Failed to configure Local Service identity. Win32_Service.Change returned $($accountChange.ReturnValue)."
    }

    cmd.exe /d /c "sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to configure service recovery actions.'
    }

    cmd.exe /d /c "sc.exe failureflag $ServiceName 1" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to enable recovery on non-crash failures.'
    }

    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))

    $deadline = (Get-Date).AddSeconds(20)
    do {
        if (Test-Path $StatusPath) {
            $status = Get-Content -Raw -LiteralPath $StatusPath | ConvertFrom-Json
            $heartbeat = [DateTimeOffset]$status.HeartbeatAtUtc
            if (([DateTimeOffset]::UtcNow - $heartbeat).TotalSeconds -lt 10) {
                break
            }
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    if (-not (Test-Path $StatusPath)) {
        throw 'ErGeCore is running but no runtime heartbeat was created.'
    }

    $status = Get-Content -Raw -LiteralPath $StatusPath | ConvertFrom-Json
    $heartbeat = [DateTimeOffset]$status.HeartbeatAtUtc
    if (([DateTimeOffset]::UtcNow - $heartbeat).TotalSeconds -ge 10) {
        throw 'ErGeCore runtime heartbeat is stale.'
    }

    Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" | Select-Object Name, State, StartMode, StartName, ProcessId, PathName
    Write-Output "ERGE_CORE_INSTALL_OK $StatusPath"
}
finally {
    Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
}
