[CmdletBinding()]
param([switch]$SkipBuild, [ValidateRange(30,600)][int]$TimeoutSeconds = 240)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Offline }
$taskFixture = Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/bin/Release/net8.0-windows/DesktopHub.LaunchFixture.exe'
if (-not $SkipBuild -or -not (Test-Path -LiteralPath $taskFixture)) {
    . (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
    $taskBuildEnvironmentState = Enter-ToolchainEnvironment $taskRoot
    try {
        $taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
        $taskFixtureProject = Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/DesktopHub.LaunchFixture.csproj'
        & $taskDotnet restore $taskFixtureProject --source (Join-Path $taskRoot '.tools/nuget-feed') --packages (Join-Path $taskRoot '.tools/nuget-packages') -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw 'Controlled foreground fixture restore failed.' }
        & $taskDotnet build $taskFixtureProject -c Release --no-restore --disable-build-servers
        if ($LASTEXITCODE -ne 0) { throw 'Controlled foreground fixture build failed.' }
    }
    finally { Exit-ToolchainEnvironment $taskBuildEnvironmentState }
}
if (-not (Test-Path -LiteralPath $taskFixture)) { throw 'Controlled foreground fixture is missing.' }
$taskApplication = Join-Path $taskRoot 'src/DesktopHub.Windows/bin/Release/net8.0-windows/DesktopHub.Windows.exe'
$taskAppDirectory = Split-Path -Parent $taskApplication
$taskSource = Join-Path $taskAppDirectory 'm23-smoke-evidence'
$taskStarted = [DateTime]::UtcNow
$taskDestination = Join-Path $taskRoot ('docs/m23-evidence/native/run-' + $taskStarted.ToString('yyyyMMddTHHmmssfffZ'))
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
$taskPreviousDotnet = $env:DOTNET_ROOT
$taskPreviousDotnetX64 = $env:DOTNET_ROOT_X64
$taskPreviousFixture = $env:DESKTOP_HUB_TEST_EXE
$taskPassed = $false
$taskPhases = [Collections.Generic.List[object]]::new()
try {
    $env:DOTNET_ROOT = Join-Path $taskRoot '.tools/dotnet'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DESKTOP_HUB_TEST_EXE = $taskFixture
    foreach ($taskFlag in @('--system-smoke','--system-smoke-restart')) {
        $taskPhaseStarted = [DateTime]::UtcNow
        $taskProcess = Start-Process -FilePath $taskApplication -ArgumentList $taskFlag -WorkingDirectory $taskAppDirectory -WindowStyle Hidden -PassThru
        if (-not $taskProcess.WaitForExit($TimeoutSeconds * 1000)) { $taskProcess.Kill(); throw 'Isolated M2.3 test host timed out.' }
        $taskProcess.Refresh()
        $taskPhases.Add([pscustomobject]@{flag=$taskFlag;pid=$taskProcess.Id;exitCode=$taskProcess.ExitCode;startedUtc=$taskPhaseStarted.ToString('O')})
        if ($taskProcess.ExitCode -ne 0) { throw "M2.3 native test failed: $taskFlag" }
        $taskMarker = Join-Path $taskSource $(if ($taskFlag -eq '--system-smoke') {'phase1-summary.txt'} else {'summary.txt'})
        if (-not (Test-Path -LiteralPath $taskMarker) -or (Get-Item -LiteralPath $taskMarker).LastWriteTimeUtc -lt $taskPhaseStarted) { throw 'Fresh native completion marker missing.' }
        Get-Content -LiteralPath $taskMarker
    }
    $taskPassed = $true
}
finally {
    $env:DOTNET_ROOT = $taskPreviousDotnet
    $env:DOTNET_ROOT_X64 = $taskPreviousDotnetX64
    $env:DESKTOP_HUB_TEST_EXE = $taskPreviousFixture
    if (Test-Path -LiteralPath $taskSource) {
        Get-ChildItem -LiteralPath $taskSource -File | Where-Object { $_.LastWriteTimeUtc -ge $taskStarted } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskDestination }
    }
    $taskError = Join-Path $taskAppDirectory 'm23-smoke-error.txt'
    if ((Test-Path -LiteralPath $taskError) -and (Get-Item -LiteralPath $taskError).LastWriteTimeUtc -ge $taskStarted) { Copy-Item -LiteralPath $taskError -Destination $taskDestination }
    [pscustomobject]@{passed=$taskPassed;startedUtc=$taskStarted.ToString('O');completedUtc=[DateTime]::UtcNow.ToString('O');phases=$taskPhases;evidence=$taskDestination} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDestination 'run.json') -Encoding utf8
}
