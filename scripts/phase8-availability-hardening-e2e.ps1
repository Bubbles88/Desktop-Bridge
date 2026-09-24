[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Phase8Files = @(
    (Join-Path $RepoRoot 'scripts\availability\erge-availability-guard.ps1'),
    (Join-Path $RepoRoot 'scripts\install-availability-guard.ps1'),
    (Join-Path $RepoRoot 'scripts\verify-availability-guard.ps1'),
    (Join-Path $RepoRoot 'scripts\uninstall-availability-guard.ps1')
)

foreach ($file in $Phase8Files) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "Phase 8 PowerShell parser errors in $file : $($parseErrors | Out-String)" }
}

$Guard = Join-Path $RepoRoot 'scripts\availability\erge-availability-guard.ps1'
& $Guard -SelfTest
if ($LASTEXITCODE -ne 0) { throw "Phase 8 guard self-test failed with exit code $LASTEXITCODE." }
Write-Output 'ERGE_PHASE8_E2E_OK'
