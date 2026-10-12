[CmdletBinding()]
param([switch]$SkipBuild, [ValidateRange(30, 600)][int]$TimeoutSeconds = 180, [string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskBuildState = Enter-ToolchainEnvironment $taskRoot
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Offline }
    & (Join-Path $taskRoot '.tools/dotnet/dotnet.exe') build (Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/DesktopHub.LaunchFixture.csproj') -c Release --no-restore --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
}
finally { Exit-ToolchainEnvironment $taskBuildState }
$taskApplication = Join-Path $taskRoot 'src/DesktopHub.Windows/bin/Release/net8.0-windows/DesktopHub.Windows.exe'
$taskApplicationDirectory = Split-Path -Parent $taskApplication
$taskEvidence = Join-Path $taskApplicationDirectory 'm21-smoke-evidence'
$taskStarted = [DateTime]::UtcNow
$taskEvidenceRoot = if ($EvidenceDirectory) { $EvidenceDirectory } else { Join-Path $taskRoot 'docs/m21-evidence/native' }
$taskDestination = Join-Path $taskEvidenceRoot ('run-' + $taskStarted.ToString('yyyyMMddTHHmmssfffZ'))
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
$taskOldFixture = $env:DESKTOP_HUB_TEST_EXE
$taskOldDotnet = $env:DOTNET_ROOT
$taskOldDotnetX64 = $env:DOTNET_ROOT_X64
$taskPassed = $false
$taskPhases = [Collections.Generic.List[object]]::new()
try {
    $env:DOTNET_ROOT = Join-Path $taskRoot '.tools/dotnet'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DESKTOP_HUB_TEST_EXE = Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/bin/Release/net8.0-windows/DesktopHub.LaunchFixture.exe'
    foreach ($taskFlag in @('--scene-smoke', '--scene-smoke-restart')) {
        $taskPhaseStart = [DateTime]::UtcNow
        $taskProcess = Start-Process -FilePath $taskApplication -ArgumentList $taskFlag -WorkingDirectory $taskApplicationDirectory -WindowStyle Hidden -PassThru
        if (-not $taskProcess.WaitForExit($TimeoutSeconds * 1000)) { $taskProcess.Kill(); throw 'Isolated scene acceptance host timed out.' }
        $taskProcess.Refresh()
        $taskPhases.Add([pscustomobject]@{flag=$taskFlag;processId=$taskProcess.Id;exitCode=$taskProcess.ExitCode;startedUtc=$taskPhaseStart.ToString('O')})
        if ($taskProcess.ExitCode -ne 0) { throw "Scene native phase failed: $taskFlag" }
        $taskSummary = Join-Path $taskEvidence $(if ($taskFlag -eq '--scene-smoke') { 'phase1-summary.txt' } else { 'summary.txt' })
        if (-not (Test-Path -LiteralPath $taskSummary) -or (Get-Item -LiteralPath $taskSummary).LastWriteTimeUtc -lt $taskPhaseStart) { throw 'Fresh completion evidence missing.' }
        Get-Content -LiteralPath $taskSummary
    }
    $taskPassed = $true
}
finally {
    $env:DESKTOP_HUB_TEST_EXE = $taskOldFixture; $env:DOTNET_ROOT = $taskOldDotnet; $env:DOTNET_ROOT_X64 = $taskOldDotnetX64
    if (Test-Path -LiteralPath $taskEvidence) { Get-ChildItem -LiteralPath $taskEvidence -File | Where-Object { $_.LastWriteTimeUtc -ge $taskStarted } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskDestination } }
    $taskStartupError = Join-Path $taskApplicationDirectory 'm21-smoke-error.txt'
    if ((Test-Path -LiteralPath $taskStartupError) -and (Get-Item -LiteralPath $taskStartupError).LastWriteTimeUtc -ge $taskStarted) { Copy-Item -LiteralPath $taskStartupError -Destination $taskDestination }
    [pscustomobject]@{passed=$taskPassed;startedUtc=$taskStarted.ToString('O');completedUtc=[DateTime]::UtcNow.ToString('O');phases=$taskPhases.ToArray();evidence=$taskDestination} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDestination 'run.json') -Encoding utf8
}
