[CmdletBinding()]
param([switch]$SkipBuild, [ValidateRange(30,600)][int]$TimeoutSeconds=360)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskEnvironment=Enter-ToolchainEnvironment $taskRoot
$taskRun=[Guid]::NewGuid().ToString('D')
$taskBinary=Join-Path $taskRoot 'tests/DesktopHub.Usability78.Tests/bin/Release/net8.0-windows10.0.17763.0'
$taskEvidence=Join-Path $taskRoot "artifacts/usability-development/native-task7-8/$taskRun"
$taskPreviousFixture=$env:DESKTOP_HUB_TEST_EXE
$taskStarted=[DateTime]::UtcNow
$taskPassed=$false
$taskProcess=$null
try {
    if(-not $SkipBuild) {
        foreach($taskProject in @('tests/DesktopHub.Usability78.Tests','tests/DesktopHub.LaunchFixture')) {
            & (Join-Path $taskRoot '.tools/dotnet/dotnet.exe') restore (Join-Path $taskRoot $taskProject) --source (Join-Path $taskRoot '.tools/nuget-feed') --packages (Join-Path $taskRoot '.tools/nuget-packages') -p:NuGetAudit=false
            if($LASTEXITCODE -ne 0){throw "Native restore failed: $taskProject"}
            & (Join-Path $taskRoot '.tools/dotnet/dotnet.exe') build (Join-Path $taskRoot $taskProject) -c Release --no-restore --disable-build-servers -m:1
            if($LASTEXITCODE -ne 0){throw "Native build failed: $taskProject"}
        }
    }
    foreach($taskKey in @('DOTNET_ROOT','DOTNET_ROOT_X64')){[Environment]::SetEnvironmentVariable($taskKey,$null,'Process')}
    $env:DESKTOP_HUB_TEST_EXE=Join-Path $taskRoot 'tests/DesktopHub.LaunchFixture/bin/Release/net8.0-windows/DesktopHub.LaunchFixture.exe'
    $taskProcess=Start-Process -FilePath (Join-Path $taskBinary 'DesktopHub.Usability78.Tests.exe') -ArgumentList $taskRun -WorkingDirectory $taskBinary -WindowStyle Hidden -PassThru
    if(-not $taskProcess.WaitForExit($TimeoutSeconds*1000)){$taskProcess.Kill();throw 'Task7–8 native test timed out.'}
    $taskProcess.Refresh()
    if($taskProcess.ExitCode -ne 0){throw "Task7–8 native test failed, exit $($taskProcess.ExitCode)."}
    $taskSummary=Join-Path $taskBinary "task78-owned-$taskRun/evidence/summary.txt"
    if(-not (Test-Path -LiteralPath $taskSummary) -or (Get-Item -LiteralPath $taskSummary).LastWriteTimeUtc -lt $taskStarted){throw 'Fresh native completion marker is missing.'}
    Get-Content -LiteralPath $taskSummary
    $taskPassed=$true
}
finally {
    $env:DESKTOP_HUB_TEST_EXE=$taskPreviousFixture
    Exit-ToolchainEnvironment $taskEnvironment
    $taskOwnedRoot=[IO.Path]::GetFullPath((Join-Path $taskBinary "task78-owned-$taskRun"))+[IO.Path]::DirectorySeparatorChar
    foreach($taskFixtureProcess in @(Get-Process -Name 'DesktopHub.LaunchFixture' -ErrorAction SilentlyContinue)) {
        try {
            $taskFixturePath=[IO.Path]::GetFullPath($taskFixtureProcess.MainModule.FileName)
            if($taskFixturePath.StartsWith($taskOwnedRoot,[StringComparison]::OrdinalIgnoreCase) -and $taskFixtureProcess.StartTime.ToUniversalTime() -ge $taskStarted){$taskFixtureProcess.Kill();$null=$taskFixtureProcess.WaitForExit(5000)}
        } catch {Write-Warning $_.Exception.Message} finally {$taskFixtureProcess.Dispose()}
    }
    New-Item -ItemType Directory -Path $taskEvidence -Force | Out-Null
    $taskNativeEvidence=Join-Path $taskBinary "task78-owned-$taskRun/evidence"
    if(Test-Path -LiteralPath $taskNativeEvidence){Get-ChildItem -LiteralPath $taskNativeEvidence -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $taskEvidence}}
    [pscustomobject]@{passed=$taskPassed;runId=$taskRun;startedUtc=$taskStarted.ToString('O');completedUtc=[DateTime]::UtcNow.ToString('O');scope='Task7–8 scoped integrated native; actual monitor DPI; hardware matrix remains Task9';evidence=$taskEvidence} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskEvidence 'run.json')
    Write-Output "Task7–8 native evidence: $taskEvidence"
}
