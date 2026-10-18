[CmdletBinding()]
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskEnvironmentState = Enter-ToolchainEnvironment $taskRoot
try {
    . (Join-Path $PSScriptRoot 'bootstrap.ps1') -Offline:$Offline
    $taskLock = Get-Content (Join-Path $PSScriptRoot 'release-runtime-lock.json') -Raw | ConvertFrom-Json
    foreach ($taskRuntime in $taskLock.packages) {
        $taskName = "$($taskRuntime.id).$($taskLock.version).nupkg"
        Receive-Artifact "https://api.nuget.org/v3-flatcontainer/$($taskRuntime.id)/$($taskLock.version)/$taskName" (Join-Path $taskRoot ".tools/nuget-feed/$taskName") $taskRuntime.sha512
    }
    $taskCli = Join-Path $taskRoot '.tools/velopack-cli'
    if (-not (Test-Path (Join-Path $taskCli 'vpk.exe'))) {
        & (Join-Path $taskRoot '.tools/dotnet/dotnet.exe') tool install vpk --version 1.2.161 --tool-path $taskCli --source (Join-Path $taskRoot '.tools/nuget-feed')
        if ($LASTEXITCODE -ne 0) { throw 'Failed to install the pinned Velopack CLI.' }
    }
    $taskVersion = & (Join-Path $taskCli 'vpk.exe') -h
    if ($LASTEXITCODE -ne 0 -or "$taskVersion" -notmatch 'Velopack CLI 1\.2\.161,') { throw 'Unexpected Velopack CLI version.' }
}
finally { Exit-ToolchainEnvironment $taskEnvironmentState }
