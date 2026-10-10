[CmdletBinding()]
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskApplicationDirectory = Join-Path $taskRoot 'artifacts/win-x64'
$taskApplication = Join-Path $taskApplicationDirectory 'DesktopHub.Windows.exe'
$taskEvidence = Join-Path $taskApplicationDirectory 'resource-evidence'
$taskArchiveRoot = Join-Path $taskRoot 'docs/resource-usage/M1'
$taskTimeoutSeconds = 600

if (-not $SkipBuild) {
    # publish.ps1 scopes its SDK/build environment and restores it before returning.
    & (Join-Path $PSScriptRoot 'publish.ps1') -Offline
}
if (-not (Test-Path -LiteralPath $taskApplication -PathType Leaf)) {
    throw 'The published Windows application is missing. Run scripts/publish.ps1 -Offline first.'
}

# Diagnostics use resource-data beside the EXE, leaving ordinary user settings intact.
# The app starts as the current normal user. On this computer, the tool sandbox may
# require permission to execute native GUI/COM code; that is not application elevation.
# Clear process-local SDK overrides only for launch so the app uses the installed
# Desktop Runtime, matching a normal published application launched from Explorer.
$taskLaunchState = @{}
foreach ($taskKey in @('DOTNET_ROOT', 'DOTNET_ROOT_X64')) {
    $taskLaunchState[$taskKey] = [Environment]::GetEnvironmentVariable($taskKey, 'Process')
}
try {
    foreach ($taskKey in $taskLaunchState.Keys) {
        [Environment]::SetEnvironmentVariable($taskKey, $null, 'Process')
    }
    $taskStarted = [DateTime]::UtcNow
    $taskProcess = Start-Process -FilePath $taskApplication -ArgumentList '--resource-test' `
        -WorkingDirectory $taskApplicationDirectory -WindowStyle Hidden -PassThru
}
finally { Exit-ToolchainEnvironment $taskLaunchState }

Write-Host "Resource diagnostic PID $($taskProcess.Id) started. Four phases each settle for 15 s and sample for 60 s."
$taskWait = [Diagnostics.Stopwatch]::StartNew()
$taskTimedOut = $false
while (-not $taskProcess.WaitForExit(1000)) {
    if ($taskWait.Elapsed.TotalSeconds -ge $taskTimeoutSeconds) {
        $taskTimedOut = $true
        # Stop only the diagnostic host started above, never another Desktop Hub or
        # an arbitrary browser process. The retained Process handle avoids PID reuse.
        if (-not $taskProcess.HasExited) { $taskProcess.Kill() }
        $null = $taskProcess.WaitForExit(10000)
        break
    }
}

$taskRunDestination = Join-Path $taskArchiveRoot ('run-' + $taskStarted.ToString('yyyyMMddTHHmmssfffZ'))
New-Item -ItemType Directory -Path $taskArchiveRoot -Force | Out-Null
if (Test-Path -LiteralPath $taskRunDestination) {
    throw "Refusing to overwrite an existing resource run: $taskRunDestination"
}
New-Item -ItemType Directory -Path $taskRunDestination | Out-Null

if (Test-Path -LiteralPath $taskEvidence -PathType Container) {
    $taskEvidencePrefix = [IO.Path]::GetFullPath($taskEvidence).TrimEnd('\') + '\'
    # Archive only fresh files and preserve relative paths. Failed runs therefore
    # cannot accidentally reuse an earlier successful summary or sample collection.
    Get-ChildItem -LiteralPath $taskEvidence -File -Recurse -Force |
        Where-Object { $_.LastWriteTimeUtc -ge $taskStarted } |
        ForEach-Object {
            $taskRelativePath = $_.FullName.Substring($taskEvidencePrefix.Length)
            $taskCopyPath = Join-Path $taskRunDestination $taskRelativePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $taskCopyPath) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $taskCopyPath
        }
}
foreach ($taskFailureName in @('resource-error.txt', 'resource-test-error.txt')) {
    $taskFailurePath = Join-Path $taskApplicationDirectory $taskFailureName
    if ((Test-Path -LiteralPath $taskFailurePath -PathType Leaf) -and
        (Get-Item -LiteralPath $taskFailurePath).LastWriteTimeUtc -ge $taskStarted) {
        Copy-Item -LiteralPath $taskFailurePath -Destination (Join-Path $taskRunDestination $taskFailureName)
    }
}

if ($taskTimedOut) {
    $taskTimeoutMessage = "Resource diagnostic exceeded $taskTimeoutSeconds seconds. Only its own host was terminated."
    $taskTimeoutMessage | Set-Content -LiteralPath (Join-Path $taskRunDestination 'timeout.txt') -Encoding UTF8
    throw "$taskTimeoutMessage Evidence: $taskRunDestination"
}
if ($taskProcess.ExitCode -ne 0) {
    throw "Resource diagnostic failed (exit $($taskProcess.ExitCode)). Evidence: $taskRunDestination"
}
foreach ($taskRequiredName in @('summary.json', 'samples.json', 'summary.txt', 'environment.json')) {
    $taskRequiredPath = Join-Path $taskRunDestination $taskRequiredName
    if (-not (Test-Path -LiteralPath $taskRequiredPath -PathType Leaf)) {
        throw "Resource diagnostic did not produce fresh $taskRequiredName. Evidence: $taskRunDestination"
    }
    if ($taskRequiredName.EndsWith('.json')) {
        $null = Get-Content -LiteralPath $taskRequiredPath -Raw | ConvertFrom-Json
    }
}
Write-Host (Get-Content -LiteralPath (Join-Path $taskRunDestination 'summary.txt') -Raw)
Write-Host "M1 resource evidence archived to $taskRunDestination"
# The path is also available to a caller as this script's final pipeline result.
Write-Output $taskRunDestination
