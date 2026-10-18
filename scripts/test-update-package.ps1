[CmdletBinding()]
param([Parameter(Mandatory)][string]$BasePortable, [Parameter(Mandatory)][string]$UpdatePackage, [Parameter(Mandatory)][string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskBase = [IO.Path]::GetFullPath((Join-Path $taskRoot $BasePortable))
$taskPackage = [IO.Path]::GetFullPath((Join-Path $taskRoot $UpdatePackage))
$taskEvidence = Join-Path $taskRoot ('artifacts/update-upgrade/' + [Guid]::NewGuid().ToString('N'))
$taskApp = Join-Path $taskEvidence 'app'
$taskPreviousLocal = $env:LOCALAPPDATA
$taskPreviousTemp = $env:TEMP
$taskPreviousTmp = $env:TMP
$taskProcess = $null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $taskEvidence | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($taskBase, $taskApp)
    # Keep both user data and updater scratch paths isolated from daily use.
    $env:LOCALAPPDATA = Join-Path $taskEvidence 'user-data'
    $env:TEMP = Join-Path $taskEvidence 'temp'; $env:TMP = $env:TEMP
    $taskData = Join-Path $env:LOCALAPPDATA 'DesktopHub'
    New-Item -ItemType Directory -Path $taskData, $env:TEMP -Force | Out-Null
    '{"onboardingCompleted":true,"theme":"dark"}' | Set-Content (Join-Path $taskData 'settings.json')
    '{"testResource":"keep-me"}' | Set-Content (Join-Path $taskData 'resources.json')
    $taskBefore = @(Get-ChildItem $taskData -File | Get-FileHash -Algorithm SHA256 | Select-Object Hash)
    $taskLog = Join-Path $taskEvidence 'updater.log'
    $taskArguments = @('apply', '--package', ('"'+$taskPackage+'"'), '--rootDir', ('"'+$taskApp+'"'), '--norestart', '--silent', '--log', ('"'+$taskLog+'"'))
    $taskProcess = Start-Process (Join-Path $taskApp 'Update.exe') -ArgumentList $taskArguments -PassThru -WindowStyle Hidden
    if (-not $taskProcess.WaitForExit(60000)) { throw 'Isolated updater timed out.' }
    if ($taskProcess.ExitCode -ne 0) { throw "Isolated update failed. See $taskLog" }
    [xml]$taskManifest = Get-Content (Join-Path $taskApp 'current/sq.version') -Raw
    if ($taskManifest.package.metadata.version -ne $ExpectedVersion) { throw 'Updated manifest version mismatch.' }
    $taskBinaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskApp 'current/DesktopHub.Windows.exe')).FileVersion
    if ($taskBinaryVersion -ne "$ExpectedVersion.0") { throw "Updated binary version mismatch: $taskBinaryVersion" }
    $taskAfter = @(Get-ChildItem $taskData -File | Get-FileHash -Algorithm SHA256 | Select-Object Hash)
    if (Compare-Object $taskBefore $taskAfter -Property Hash) { throw 'Isolated user data changed during upgrade.' }
    @{ passed=3; failed=0; version=$ExpectedVersion; dataPreserved=$true; scope='local portable update engine, no restart or GitHub network'; evidence=$taskEvidence } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $taskEvidence 'result.json')
}
finally {
    if ($taskProcess) { if (-not $taskProcess.HasExited) { $taskProcess.Kill(); [void]$taskProcess.WaitForExit(5000) }; $taskProcess.Dispose() }
    $env:LOCALAPPDATA=$taskPreviousLocal; $env:TEMP=$taskPreviousTemp; $env:TMP=$taskPreviousTmp
}
