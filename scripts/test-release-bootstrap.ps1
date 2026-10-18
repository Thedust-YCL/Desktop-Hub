[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskFixture = Join-Path $taskRoot ('artifacts/release-bootstrap-tests/' + [Guid]::NewGuid().ToString('N'))
$taskScripts = Join-Path $taskFixture 'scripts'
$taskTools = Join-Path $taskFixture '.tools'
New-Item -ItemType Directory -Path $taskScripts, $taskTools -Force | Out-Null
foreach ($taskName in @('bootstrap.ps1', 'bootstrap-release.ps1', 'toolchain-environment.ps1', 'release-runtime-lock.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination $taskScripts
}
# Share only verified input downloads and SDK. Tool installation and tool home start empty.
foreach ($taskName in @('dotnet', 'nuget-feed')) {
    New-Item -ItemType Junction -Path (Join-Path $taskTools $taskName) -Target (Join-Path $taskRoot ".tools/$taskName") | Out-Null
}
& (Join-Path $taskScripts 'bootstrap-release.ps1') -Offline
if (-not (Test-Path (Join-Path $taskTools 'velopack-cli/vpk.exe'))) { throw 'Fresh tool installation missing.' }
# Also verify the cached path used by subsequent builds.
& (Join-Path $taskScripts 'bootstrap-release.ps1') -Offline
Write-Host "PASS fresh and cached release bootstrap. Evidence: $taskFixture"
