[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Guard = Join-Path $RepoRoot 'scripts\availability\erge-availability-guard.ps1'
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($Guard,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw "Phase 8 guard has PowerShell parser errors: $($parseErrors | Out-String)" }
& $Guard -SelfTest
if ($LASTEXITCODE -ne 0) { throw "Phase 8 guard self-test failed with exit code $LASTEXITCODE." }
Write-Output 'ERGE_PHASE8_E2E_OK'
