[CmdletBinding()]
param([switch]$SkipBuild, [ValidateRange(30, 600)][int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskEnvironmentState = Enter-ToolchainEnvironment $taskRoot
$taskRun = [Guid]::NewGuid().ToString('D')
$taskProject = Join-Path $taskRoot 'tests/DesktopHub.Usability.Tests/DesktopHub.Usability.Tests.csproj'
$taskBinaryDirectory = Join-Path $taskRoot 'tests/DesktopHub.Usability.Tests/bin/Release/net8.0-windows10.0.17763.0'
$taskFixture = Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/bin/Release/net8.0-windows/DesktopHub.LaunchFixture.exe'
$taskPreviousFixture = $env:DESKTOP_HUB_TEST_EXE
$taskPhases = [Collections.Generic.List[object]]::new()
$taskPassed = $false
$taskStarted = [DateTime]::UtcNow
try {
    if (-not $SkipBuild) {
        foreach ($taskBuildProject in @($taskProject, (Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/DesktopHub.LaunchFixture.csproj'))) {
            & (Join-Path $taskRoot '.tools/dotnet/dotnet.exe') restore $taskBuildProject --source (Join-Path $taskRoot '.tools/nuget-feed') --packages (Join-Path $taskRoot '.tools/nuget-packages') -p:NuGetAudit=false
            if ($LASTEXITCODE -ne 0) { throw "Native test restore failed: $taskBuildProject" }
            & (Join-Path $taskRoot '.tools/dotnet/dotnet.exe') build $taskBuildProject -c Release --no-restore --disable-build-servers -m:1
            if ($LASTEXITCODE -ne 0) { throw "Native test build failed: $taskBuildProject" }
        }
    }
    $env:DESKTOP_HUB_TEST_EXE = $taskFixture
    foreach ($taskPhase in @('first', 'restart')) {
        $taskArguments = @($taskRun)
        if ($taskPhase -eq 'restart') { $taskArguments += '--restart' }
        $taskPhaseStarted = [DateTime]::UtcNow
        $taskProcess = Start-Process -FilePath (Join-Path $taskBinaryDirectory 'DesktopHub.Usability.Tests.exe') -ArgumentList $taskArguments -WorkingDirectory $taskBinaryDirectory -WindowStyle Hidden -PassThru
        if (-not $taskProcess.WaitForExit($TimeoutSeconds * 1000)) { $taskProcess.Kill(); throw "Scoped native phase timed out: $taskPhase" }
        $taskProcess.Refresh()
        $taskPhases.Add([pscustomobject]@{phase=$taskPhase;processId=$taskProcess.Id;exitCode=$taskProcess.ExitCode;startedUtc=$taskPhaseStarted.ToString('O')})
        if ($taskProcess.ExitCode -ne 0) { throw "Scoped native phase failed: $taskPhase" }
        $taskSummaryName = if ($taskPhase -eq 'restart') { 'restart-summary.txt' } else { 'phase1-summary.txt' }
        $taskSummary = Join-Path $taskBinaryDirectory ("task46-owned-$taskRun/evidence/$taskSummaryName")
        if (-not (Test-Path -LiteralPath $taskSummary) -or (Get-Item -LiteralPath $taskSummary).LastWriteTimeUtc -lt $taskPhaseStarted) { throw 'Fresh native completion evidence missing.' }
        Get-Content -LiteralPath $taskSummary
    }
    $taskPassed = $true
}
finally {
    # The child normally closes its fixtures. Cover timeout/crash paths without touching other instances.
    $taskOwnedRoot = [IO.Path]::GetFullPath((Join-Path $taskBinaryDirectory "task46-owned-$taskRun")) + [IO.Path]::DirectorySeparatorChar
    foreach ($taskFixtureProcess in @(Get-Process -Name 'DesktopHub.LaunchFixture' -ErrorAction SilentlyContinue)) {
        try {
            $taskFixturePath = [IO.Path]::GetFullPath($taskFixtureProcess.MainModule.FileName)
            if ($taskFixturePath.StartsWith($taskOwnedRoot, [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($taskFixturePath) -eq 'DesktopHub.LaunchFixture.exe' -and
                $taskFixtureProcess.StartTime.ToUniversalTime() -ge $taskStarted) {
                $taskFixtureProcess.Kill()
                $null = $taskFixtureProcess.WaitForExit(5000)
            }
        }
        catch { Write-Warning "Could not inspect or close an owned fixture: $($_.Exception.Message)" }
        finally { $taskFixtureProcess.Dispose() }
    }
    $env:DESKTOP_HUB_TEST_EXE = $taskPreviousFixture
    Exit-ToolchainEnvironment $taskEnvironmentState
    $taskEvidenceRoot = Join-Path $taskRoot 'artifacts/usability-development/native-task4-6'
    $taskDestination = Join-Path $taskEvidenceRoot $taskRun
    New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
    $taskEvidence = Join-Path $taskBinaryDirectory "task46-owned-$taskRun/evidence"
    if (Test-Path -LiteralPath $taskEvidence) { Get-ChildItem -LiteralPath $taskEvidence -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskDestination } }
    [pscustomobject]@{passed=$taskPassed;runId=$taskRun;startedUtc=$taskStarted.ToString('O');completedUtc=[DateTime]::UtcNow.ToString('O');phases=$taskPhases.ToArray();evidence=$taskDestination;scope='Task4-6 scoped native; current monitor DPI only; not Task9 full acceptance'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDestination 'run.json') -Encoding utf8
    Write-Output "Scoped native evidence: $taskDestination"
}
