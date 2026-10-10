[CmdletBinding()]
param([switch]$SkipBuild, [switch]$CoreFixtureIntegration, [ValidateRange(30, 600)][int]$TimeoutSeconds = 240, [string]$EvidenceDirectory)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskProject = Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/DesktopHub.LaunchFixture.csproj'
$taskFixture = Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/bin/Release/net8.0-windows/DesktopHub.LaunchFixture.exe'
$taskApplication = Join-Path $taskRoot 'src/DesktopHub.Windows/bin/Release/net8.0-windows/DesktopHub.Windows.exe'
$taskApplicationDirectory = Split-Path -Parent $taskApplication

# Offline compilation environment never leaks AppData overrides into the actual Windows host.
$taskEnvironmentState = Enter-ToolchainEnvironment $taskRoot
try {
    & (Join-Path $PSScriptRoot 'bootstrap.ps1') -Offline
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Offline }
    & $taskDotnet restore $taskProject --source (Join-Path $taskRoot '.tools/nuget-feed') --packages (Join-Path $taskRoot '.tools/nuget-packages') -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Controlled fixture restore failed (exit $LASTEXITCODE)." }
    & $taskDotnet build $taskProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Controlled fixture build failed (exit $LASTEXITCODE)." }
    if ($CoreFixtureIntegration) {
        & $taskDotnet run --project (Join-Path $taskRoot 'tests/DesktopHub.Core.Tests/DesktopHub.Core.Tests.csproj') -c Release --no-restore -- --fixture $taskFixture
        if ($LASTEXITCODE -ne 0) { throw "Core controlled launch integration failed (exit $LASTEXITCODE)." }
    }
}
finally { Exit-ToolchainEnvironment $taskEnvironmentState }

if (-not (Test-Path -LiteralPath $taskApplication)) { throw 'The Windows application has not been built.' }
if (-not (Test-Path -LiteralPath $taskFixture)) { throw 'The controlled fixture has not been built.' }
$taskLaunchState = @{}
foreach ($taskKey in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DESKTOP_HUB_TEST_EXE')) {
    $taskLaunchState[$taskKey] = [Environment]::GetEnvironmentVariable($taskKey, 'Process')
}
$taskStarted = [DateTime]::UtcNow
$taskEvidence = Join-Path $taskApplicationDirectory 'm15-smoke-evidence'
$taskDestination = if ($EvidenceDirectory) { $EvidenceDirectory } else { Join-Path $taskRoot 'docs/m15-evidence/native' }
$taskRunDestination = Join-Path $taskDestination ('run-' + $taskStarted.ToString('yyyyMMddTHHmmssfffZ'))
New-Item -ItemType Directory -Path $taskDestination, $taskRunDestination -Force | Out-Null
$taskLatestSummary = Join-Path $taskDestination 'summary.txt'
if (Test-Path -LiteralPath $taskLatestSummary -PathType Leaf) { Remove-Item -LiteralPath $taskLatestSummary -Force }
$taskPhases = [Collections.Generic.List[object]]::new()
$taskSuccess = $false
$taskFailure = $null
try {
    $env:DOTNET_ROOT = Join-Path $taskRoot '.tools/dotnet'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DESKTOP_HUB_TEST_EXE = $taskFixture
    foreach ($taskPhase in @(
        @{ Name = 'management-and-launch'; Flag = '--smoke-test'; Completion = 'phase1-summary.txt' },
        @{ Name = 'actual-host-restart'; Flag = '--smoke-test-restart'; Completion = 'summary.txt' }
    )) {
        $taskPhaseStarted = [DateTime]::UtcNow
        $taskProcess = Start-Process -FilePath $taskApplication -ArgumentList $taskPhase.Flag -WorkingDirectory $taskApplicationDirectory -WindowStyle Hidden -PassThru
        $taskWait = [Diagnostics.Stopwatch]::StartNew()
        $taskTimedOut = $false
        while (-not $taskProcess.WaitForExit(1000)) {
            if ($taskWait.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $taskTimedOut = $true
                # Only the exact opt-in host process created above is terminated on a harness timeout.
                $taskProcess.Kill()
                $null = $taskProcess.WaitForExit(10000)
                break
            }
        }
        $taskPhases.Add([pscustomobject]@{ name = $taskPhase.Name; processId = $taskProcess.Id; flag = $taskPhase.Flag;
            exitCode = $taskProcess.ExitCode; timedOut = $taskTimedOut; elapsedSeconds = $taskWait.Elapsed.TotalSeconds })
        if ($taskTimedOut) { throw "Native phase $($taskPhase.Name) exceeded $TimeoutSeconds seconds. The opt-in host was terminated." }
        if ($taskProcess.ExitCode -ne 0) { throw "Native phase $($taskPhase.Name) failed (exit $($taskProcess.ExitCode))." }
        $taskCompletion = Join-Path $taskEvidence $taskPhase.Completion
        if (-not (Test-Path -LiteralPath $taskCompletion) -or (Get-Item -LiteralPath $taskCompletion).LastWriteTimeUtc -lt $taskPhaseStarted) {
            throw "Native phase $($taskPhase.Name) did not produce fresh completion evidence."
        }
        Write-Host (Get-Content -LiteralPath $taskCompletion -Raw)
    }
    $taskSuccess = $true
}
catch { $taskFailure = $_.Exception.Message }
finally {
    if (Test-Path -LiteralPath $taskEvidence) {
        Get-ChildItem -LiteralPath $taskEvidence -Force | Where-Object { $_.LastWriteTimeUtc -ge $taskStarted } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $taskRunDestination -Recurse -Force
            Copy-Item -LiteralPath $_.FullName -Destination $taskDestination -Recurse -Force
        }
    }
    $taskStartupError = Join-Path $taskApplicationDirectory 'm15-smoke-error.txt'
    if ((Test-Path -LiteralPath $taskStartupError) -and (Get-Item -LiteralPath $taskStartupError).LastWriteTimeUtc -ge $taskStarted) {
        Copy-Item -LiteralPath $taskStartupError -Destination $taskRunDestination -Force
        Copy-Item -LiteralPath $taskStartupError -Destination $taskDestination -Force
    }
    $taskRunRecord = [pscustomobject]@{ startedUtc = $taskStarted.ToString('O'); completedUtc = [DateTime]::UtcNow.ToString('O');
        passed = $taskSuccess; phases = $taskPhases.ToArray(); failure = $taskFailure; archive = $taskRunDestination }
    $taskRunRecord | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRunDestination 'run.json') -Encoding UTF8
    $taskRunRecord | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDestination 'latest-run.json') -Encoding UTF8
    if ($taskSuccess) {
        foreach ($taskFailureName in @('error.txt', 'm15-smoke-error.txt', 'timeout.txt')) {
            $taskFailurePath = Join-Path $taskDestination $taskFailureName
            if (Test-Path -LiteralPath $taskFailurePath -PathType Leaf) { Remove-Item -LiteralPath $taskFailurePath -Force }
        }
    }
    Exit-ToolchainEnvironment $taskLaunchState
}
if (-not $taskSuccess) { throw "$taskFailure Evidence: $taskRunDestination" }
Write-Host "M1.5 native evidence copied to $taskRunDestination"
