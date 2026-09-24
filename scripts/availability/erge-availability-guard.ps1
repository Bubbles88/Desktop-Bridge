[CmdletBinding()]
param(
    [switch]$CaptureBaseline,
    [switch]$RestoreBaseline,
    [switch]$Watch,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$DataRoot = Join-Path $env:ProgramData 'ErGe\Availability'
$BaselinePath = Join-Path $DataRoot 'availability-baseline.json'
$StatePath = Join-Path $DataRoot 'availability-state.json'
$RuntimeStatusPath = Join-Path $env:ProgramData 'ErGe\runtime-status.json'
$PolicyPath = Join-Path $env:ProgramData 'ErGe\policy.json'
$AsusUserPath = 'HKCU:\Software\ASUS\OLEDCare'
$AsusMachinePath = 'HKLM:\SOFTWARE\ASUS\OLEDCare'

$PowerSettings = @(
    [pscustomobject]@{ Name='SleepAfter'; Subgroup='238c9fa8-0aad-41ed-83f4-97be242c8f20'; Setting='29f6c1db-86da-48c5-9fdb-f2b67b1f44da' },
    [pscustomobject]@{ Name='HibernateAfter'; Subgroup='238c9fa8-0aad-41ed-83f4-97be242c8f20'; Setting='9d7815a6-7ee4-497e-8888-515a05f02364' },
    [pscustomobject]@{ Name='DisplayAfter'; Subgroup='7516b95f-f776-4464-8c53-06167f40cc99'; Setting='3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e' },
    [pscustomobject]@{ Name='LidCloseAction'; Subgroup='4f971e89-eebd-4455-a8de-9e59040e7347'; Setting='5ca83367-6e45-459f-a27b-476b1d01c936' }
)

function Save-JsonAtomic {
    param([string]$Path, $Value)
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temp = "$Path.tmp-$([guid]::NewGuid().ToString('N'))"
    try {
        $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp -Encoding UTF8
        Move-Item -LiteralPath $temp -Destination $Path -Force
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Get-ActiveSchemeGuid {
    $text = powercfg.exe /getactivescheme | Out-String
    if ($LASTEXITCODE -ne 0 -or $text -notmatch '([0-9a-fA-F-]{36})') { throw 'Unable to resolve active Windows power scheme.' }
    return $Matches[1].ToLowerInvariant()
}

function Get-AcIndex {
    param([string]$Scheme,[string]$Subgroup,[string]$Setting)
    $path = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\$Scheme\$Subgroup\$Setting"
    if (-not (Test-Path $path)) { return $null }
    $value = Get-ItemPropertyValue -Path $path -Name 'ACSettingIndex' -ErrorAction SilentlyContinue
    if ($null -eq $value) { return $null }
    return [int64]$value
}

function Get-RegistryValueSnapshot {
    param([string]$Path,[string]$Name)
    if (-not (Test-Path $Path)) { return [pscustomobject]@{ Exists=$false; Value=$null } }
    $item = Get-ItemProperty -Path $Path -ErrorAction Stop
    $property = $item.PSObject.Properties[$Name]
    if ($null -eq $property) { return [pscustomobject]@{ Exists=$false; Value=$null } }
    return [pscustomobject]@{ Exists=$true; Value=$property.Value }
}

function Capture-Scheme {
    param([string]$Scheme)
    $settings = foreach ($definition in $PowerSettings) {
        [pscustomobject]@{
            Name = $definition.Name
            Subgroup = $definition.Subgroup
            Setting = $definition.Setting
            AcIndex = Get-AcIndex -Scheme $Scheme -Subgroup $definition.Subgroup -Setting $definition.Setting
        }
    }
    return [pscustomobject]@{ SchemeGuid=$Scheme; Settings=@($settings) }
}

function New-Baseline {
    $scheme = Get-ActiveSchemeGuid
    $desktop = 'HKCU:\Control Panel\Desktop'
    $asus = $AsusUserPath
    return [pscustomobject]@{
        SchemaVersion = 2
        CapturedAtUtc = [DateTimeOffset]::UtcNow
        PowerSchemes = @(Capture-Scheme -Scheme $scheme)
        WindowsScreenSaver = [pscustomobject]@{
            ScreenSaveActive = Get-RegistryValueSnapshot -Path $desktop -Name 'ScreenSaveActive'
            ScreenSaveTimeOut = Get-RegistryValueSnapshot -Path $desktop -Name 'ScreenSaveTimeOut'
            ScrnSaveExe = Get-RegistryValueSnapshot -Path $desktop -Name 'SCRNSAVE.EXE'
        }
        AsusOledCare = [pscustomobject]@{
            KeyExists = (Test-Path $asus)
            ScreenSaverTime = Get-RegistryValueSnapshot -Path $asus -Name 'ScreenSaverTime'
            ScreenSaverImage = Get-RegistryValueSnapshot -Path $asus -Name 'ScreenSaverImage'
            EnablePixelShift = Get-RegistryValueSnapshot -Path $asus -Name 'EnablePixelShift'
            EnablePixelRefresh = Get-RegistryValueSnapshot -Path $asus -Name 'EnablePixelRefresh'
        }
        AsusMachineOledCare = [pscustomobject]@{
            KeyExists = (Test-Path $AsusMachinePath)
            ScreenSaverTime = Get-RegistryValueSnapshot -Path $AsusMachinePath -Name 'ScreenSaverTime'
            ScreenSaverImage = Get-RegistryValueSnapshot -Path $AsusMachinePath -Name 'ScreenSaverImage'
            EnablePixelShift = Get-RegistryValueSnapshot -Path $AsusMachinePath -Name 'EnablePixelShift'
            EnablePixelRefresh = Get-RegistryValueSnapshot -Path $AsusMachinePath -Name 'EnablePixelRefresh'
        }
    }
}

function Load-Baseline {
    if (-not (Test-Path $BaselinePath)) { throw "Availability baseline is missing: $BaselinePath" }
    $baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json
    if ([int]$baseline.SchemaVersion -eq 1) {
        $machineSnapshot = [pscustomobject]@{
            KeyExists = (Test-Path $AsusMachinePath)
            ScreenSaverTime = Get-RegistryValueSnapshot -Path $AsusMachinePath -Name 'ScreenSaverTime'
            ScreenSaverImage = Get-RegistryValueSnapshot -Path $AsusMachinePath -Name 'ScreenSaverImage'
            EnablePixelShift = Get-RegistryValueSnapshot -Path $AsusMachinePath -Name 'EnablePixelShift'
            EnablePixelRefresh = Get-RegistryValueSnapshot -Path $AsusMachinePath -Name 'EnablePixelRefresh'
        }
        $baseline | Add-Member -NotePropertyName AsusMachineOledCare -NotePropertyValue $machineSnapshot -Force
        $baseline.SchemaVersion = 2
        Save-JsonAtomic -Path $BaselinePath -Value $baseline
    }
    if ([int]$baseline.SchemaVersion -ne 2) { throw "Unsupported availability baseline schema: $($baseline.SchemaVersion)" }
    return $baseline
}

function Ensure-ActiveSchemeBaseline {
    param($Baseline)
    $scheme = Get-ActiveSchemeGuid
    $existing = @($Baseline.PowerSchemes | Where-Object { $_.SchemeGuid -eq $scheme })
    if ($existing.Count -gt 0) { return $Baseline }
    $Baseline.PowerSchemes = @($Baseline.PowerSchemes) + @(Capture-Scheme -Scheme $scheme)
    Save-JsonAtomic -Path $BaselinePath -Value $Baseline
    return $Baseline
}

function Set-RegistrySnapshot {
    param([string]$Path,[string]$Name,$Snapshot)
    if ($Snapshot.Exists) {
        New-Item -Path $Path -Force | Out-Null
        Set-ItemProperty -Path $Path -Name $Name -Value $Snapshot.Value
    } elseif (Test-Path $Path) {
        Remove-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue
    }
}

function Set-PowerIndex {
    param([string]$Scheme,[string]$Subgroup,[string]$Setting,[int64]$Value)
    powercfg.exe /setacvalueindex $Scheme $Subgroup $Setting $Value | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "powercfg failed for $Scheme / $Subgroup / $Setting." }
}

function Apply-AlwaysOn {
    param($Baseline)
    $Baseline = Ensure-ActiveSchemeBaseline -Baseline $Baseline
    $scheme = Get-ActiveSchemeGuid
    foreach ($definition in $PowerSettings) {
        Set-PowerIndex -Scheme $scheme -Subgroup $definition.Subgroup -Setting $definition.Setting -Value 0
    }
    powercfg.exe /setactive $scheme | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to reactivate power scheme $scheme." }
    $desktop = 'HKCU:\Control Panel\Desktop'
    New-Item -Path $desktop -Force | Out-Null
    Set-ItemProperty -Path $desktop -Name 'ScreenSaveActive' -Value '0' -Type String
    Set-ItemProperty -Path $desktop -Name 'ScreenSaveTimeOut' -Value '0' -Type String
    $asus = $AsusUserPath
    if (Test-Path $asus) {
        Set-ItemProperty -Path $asus -Name 'ScreenSaverTime' -Value 0 -Type DWord
        Set-ItemProperty -Path $asus -Name 'ScreenSaverImage' -Value '' -Type String
    }
    if ($Baseline.AsusMachineOledCare.KeyExists -and (Test-Path $AsusMachinePath)) {
        Set-ItemProperty -Path $AsusMachinePath -Name 'ScreenSaverTime' -Value 0 -Type DWord
        Set-ItemProperty -Path $AsusMachinePath -Name 'ScreenSaverImage' -Value '' -Type String
    }
    return $Baseline
}

function Restore-Baseline {
    param($Baseline)
    foreach ($schemeBaseline in @($Baseline.PowerSchemes)) {
        $schemePath = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\$($schemeBaseline.SchemeGuid)"
        if (-not (Test-Path $schemePath)) { continue }
        foreach ($settingBaseline in @($schemeBaseline.Settings)) {
            if ($null -ne $settingBaseline.AcIndex) {
                Set-PowerIndex -Scheme $schemeBaseline.SchemeGuid -Subgroup $settingBaseline.Subgroup -Setting $settingBaseline.Setting -Value ([int64]$settingBaseline.AcIndex)
            }
        }
    }
    $active = Get-ActiveSchemeGuid
    powercfg.exe /setactive $active | Out-Null
    $desktop = 'HKCU:\Control Panel\Desktop'
    Set-RegistrySnapshot -Path $desktop -Name 'ScreenSaveActive' -Snapshot $Baseline.WindowsScreenSaver.ScreenSaveActive
    Set-RegistrySnapshot -Path $desktop -Name 'ScreenSaveTimeOut' -Snapshot $Baseline.WindowsScreenSaver.ScreenSaveTimeOut
    Set-RegistrySnapshot -Path $desktop -Name 'SCRNSAVE.EXE' -Snapshot $Baseline.WindowsScreenSaver.ScrnSaveExe
    $asus = $AsusUserPath
    if ($Baseline.AsusOledCare.KeyExists) {
        Set-RegistrySnapshot -Path $asus -Name 'ScreenSaverTime' -Snapshot $Baseline.AsusOledCare.ScreenSaverTime
        Set-RegistrySnapshot -Path $asus -Name 'ScreenSaverImage' -Snapshot $Baseline.AsusOledCare.ScreenSaverImage
    }
    if ($Baseline.AsusMachineOledCare.KeyExists) {
        Set-RegistrySnapshot -Path $AsusMachinePath -Name 'ScreenSaverTime' -Snapshot $Baseline.AsusMachineOledCare.ScreenSaverTime
        Set-RegistrySnapshot -Path $AsusMachinePath -Name 'ScreenSaverImage' -Snapshot $Baseline.AsusMachineOledCare.ScreenSaverImage
    }
}

function Resolve-DesiredMode {
    param($RuntimeStatus,[bool]$RuntimeFresh,$PersistentPolicy)
    if ($RuntimeFresh -and $null -ne $RuntimeStatus) {
        if ([bool]$RuntimeStatus.RemoteAiAllowed) { return 'EnforceAlwaysOn' }
        return 'RestoreBaseline'
    }
    if ($null -ne $PersistentPolicy -and $PersistentPolicy.PersistentMode -eq 'AlwaysOn') { return 'EnforceAlwaysOn' }
    return 'RestoreBaseline'
}

function Get-DesiredState {
    $runtime = $null; $runtimeFresh = $false; $runtimeAge = $null
    if (Test-Path $RuntimeStatusPath) {
        try {
            $runtime = Get-Content -Raw -LiteralPath $RuntimeStatusPath | ConvertFrom-Json
            $heartbeat = [DateTimeOffset]$runtime.HeartbeatAtUtc
            $runtimeAge = ([DateTimeOffset]::UtcNow - $heartbeat).TotalSeconds
            $runtimeFresh = $runtimeAge -ge 0 -and $runtimeAge -lt 15
        } catch { $runtime = $null; $runtimeFresh = $false }
    }
    $policy = $null
    if (Test-Path $PolicyPath) {
        try { $policy = Get-Content -Raw -LiteralPath $PolicyPath | ConvertFrom-Json } catch { $policy = $null }
    }
    $persistentMode = 'AlwaysOff'
    if ($null -ne $policy) { $persistentMode = $policy.PersistentMode }
    $effectiveAccess = $null
    if ($runtimeFresh) { $effectiveAccess = $runtime.EffectiveAccess }
    $mode = Resolve-DesiredMode -RuntimeStatus $runtime -RuntimeFresh $runtimeFresh -PersistentPolicy $policy
    return [pscustomobject]@{
        Mode = $mode
        RuntimeFresh = $runtimeFresh
        RuntimeAgeSeconds = $runtimeAge
        PersistentMode = $persistentMode
        EffectiveAccess = $effectiveAccess
    }
}

function Write-State {
    param([string]$Mode,$Desired,[string]$Outcome,[string]$ErrorMessage)
    $activePowerScheme = $null
    try { $activePowerScheme = Get-ActiveSchemeGuid } catch { }
    $state = [pscustomobject]@{
        SchemaVersion = 1
        UpdatedAtUtc = [DateTimeOffset]::UtcNow
        DesiredMode = $Mode
        Outcome = $Outcome
        RuntimeFresh = $Desired.RuntimeFresh
        RuntimeAgeSeconds = $Desired.RuntimeAgeSeconds
        PersistentMode = $Desired.PersistentMode
        EffectiveAccess = $Desired.EffectiveAccess
        ActivePowerScheme = $activePowerScheme
        Error = $ErrorMessage
    }
    Save-JsonAtomic -Path $StatePath -Value $state
}

function Invoke-GuardOnce {
    $baseline = Load-Baseline
    $desired = Get-DesiredState
    try {
        if ($desired.Mode -eq 'EnforceAlwaysOn') { $baseline = Apply-AlwaysOn -Baseline $baseline } else { Restore-Baseline -Baseline $baseline }
        Write-State -Mode $desired.Mode -Desired $desired -Outcome 'Applied'
    } catch {
        Write-State -Mode $desired.Mode -Desired $desired -Outcome 'Failed' -ErrorMessage $_.Exception.Message
        throw
    }
}

function Invoke-SelfTest {
    $runtimeOn = [pscustomobject]@{ RemoteAiAllowed=$true }
    $runtimeOff = [pscustomobject]@{ RemoteAiAllowed=$false }
    $policyOn = [pscustomobject]@{ PersistentMode='AlwaysOn' }
    $policyOff = [pscustomobject]@{ PersistentMode='AlwaysOff' }
    if ((Resolve-DesiredMode $runtimeOn $true $policyOff) -ne 'EnforceAlwaysOn') { throw 'Fresh remote allowed mapping failed.' }
    if ((Resolve-DesiredMode $runtimeOff $true $policyOn) -ne 'RestoreBaseline') { throw 'Fresh runtime deny must override persistent AlwaysOn.' }
    if ((Resolve-DesiredMode $null $false $policyOn) -ne 'EnforceAlwaysOn') { throw 'Stale runtime fallback to AlwaysOn failed.' }
    if ((Resolve-DesiredMode $null $false $policyOff) -ne 'RestoreBaseline') { throw 'Stale runtime fallback to AlwaysOff failed.' }
    if ((Resolve-DesiredMode $null $false $null) -ne 'RestoreBaseline') { throw 'Missing policy must fail closed.' }
    Write-Output 'ERGE_PHASE8_GUARD_SELFTEST_OK'
}

New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null

if ($SelfTest) { Invoke-SelfTest; exit 0 }
if ($CaptureBaseline) {
    if (-not (Test-Path $BaselinePath)) {
        Save-JsonAtomic -Path $BaselinePath -Value (New-Baseline)
    } else {
        [void](Load-Baseline)
    }
    Write-Output "ERGE_PHASE8_BASELINE_OK $BaselinePath"
    exit 0
}
if ($RestoreBaseline) { Restore-Baseline -Baseline (Load-Baseline); Write-Output 'ERGE_PHASE8_RESTORE_OK'; exit 0 }
if ($Watch) {
    while ($true) {
        try { Invoke-GuardOnce } catch { }
        Start-Sleep -Seconds 60
    }
}
Invoke-GuardOnce
Write-Output 'ERGE_PHASE8_GUARD_OK'
