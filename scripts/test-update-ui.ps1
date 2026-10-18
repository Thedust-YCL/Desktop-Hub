[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskEnvironmentState = Enter-ToolchainEnvironment $taskRoot
$taskProcess = $null
try {
    $taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
    $taskProject = Join-Path $taskRoot 'tests/DesktopHub.UpdateUi.Tests'
    if (-not $SkipBuild) {
        & $taskDotnet restore $taskProject --source (Join-Path $taskRoot '.tools/nuget-feed') --packages (Join-Path $taskRoot '.tools/nuget-packages') -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw 'UI test restore failed.' }
        & $taskDotnet build $taskProject -c Release --no-restore --disable-build-servers -m:1
        if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
    }
    $taskEvidence = Join-Path $taskRoot ('artifacts/update-ui/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $taskEvidence | Out-Null
    $env:LOCALAPPDATA = Join-Path $taskEvidence 'local-appdata'
    $taskExecutable = Join-Path $taskProject 'bin/Release/net8.0-windows10.0.17763.0/DesktopHub.UpdateUi.Tests.exe'
    $taskProcess = Start-Process -FilePath $taskExecutable -ArgumentList ('"' + $taskEvidence + '"') -PassThru -WindowStyle Hidden
    if (-not $taskProcess.WaitForExit(60000)) { throw 'UI test timed out.' }
    if ($taskProcess.ExitCode -ne 0 -or (Test-Path (Join-Path $taskEvidence 'error.txt'))) {
        if (Test-Path (Join-Path $taskEvidence 'error.txt')) { Get-Content (Join-Path $taskEvidence 'error.txt') }
        throw "UI test failed. Evidence: $taskEvidence"
    }
    Get-Content (Join-Path $taskEvidence 'result.json')
    Write-Host "UI evidence: $taskEvidence"
}
finally {
    if ($taskProcess) { if (-not $taskProcess.HasExited) { $taskProcess.Kill(); [void]$taskProcess.WaitForExit(5000) }; $taskProcess.Dispose() }
    Exit-ToolchainEnvironment $taskEnvironmentState
}
