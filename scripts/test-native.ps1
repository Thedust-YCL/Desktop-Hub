[CmdletBinding()]
param([switch]$SkipBuild, [switch]$CoreFixtureIntegration, [ValidateRange(30, 600)][int]$TimeoutSeconds = 240)

# Current acceptance is M1.5. Keep this M1-era command as an alias, while all new
# runs are archived under docs/m15-evidence and never overwrite M1 evidence.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-m15-native.ps1') -SkipBuild:$SkipBuild -CoreFixtureIntegration:$CoreFixtureIntegration -TimeoutSeconds $TimeoutSeconds
