[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [switch]$Offline, [switch]$SkipTests,
    [string]$BaselineDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskEnvironmentState = Enter-ToolchainEnvironment $taskRoot
function Invoke-ReleaseCommand([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Release command failed: $Executable (exit $LASTEXITCODE)" }
}
try {
    & (Join-Path $PSScriptRoot 'bootstrap-release.ps1') -Offline:$Offline
    & (Join-Path $PSScriptRoot 'build.ps1') -Offline:$Offline -SkipTests:$SkipTests
    $taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
    $taskCli = Join-Path $taskRoot '.tools/velopack-cli/vpk.exe'
    $taskProject = Join-Path $taskRoot 'src/DesktopHub.Windows/DesktopHub.Windows.csproj'
    $taskFeed = Join-Path $taskRoot '.tools/nuget-feed'
    $taskPackages = Join-Path $taskRoot '.tools/nuget-packages'
    # Use fresh directories: never ship stale files from a previous version.
    $taskWork = Join-Path $taskRoot ('artifacts/release-build/' + [Guid]::NewGuid().ToString('N'))
    $taskPublish = Join-Path $taskWork 'publish'
    $taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory)) } else { Join-Path $taskRoot "artifacts/releases/$Version" }
    if (Test-Path -LiteralPath $taskOutput) { throw "Output already exists; select a fresh output directory: $taskOutput" }
    New-Item -ItemType Directory -Path $taskPublish, $taskOutput -Force | Out-Null
    if ($BaselineDirectory) {
        $taskBaseline = [IO.Path]::GetFullPath((Join-Path $taskRoot $BaselineDirectory))
        if (-not (Test-Path (Join-Path $taskBaseline 'releases.win.json'))) { throw 'Baseline feed missing.' }
        Copy-Item (Join-Path $taskBaseline '*') -Destination $taskOutput -Recurse
    }
    $taskRuntime = (Get-Content (Join-Path $PSScriptRoot 'release-runtime-lock.json') -Raw | ConvertFrom-Json).version
    Invoke-ReleaseCommand $taskDotnet @('restore', $taskProject, '-r', 'win-x64', '-p:SelfContained=true', "-p:Version=$Version", '--source', $taskFeed, '--packages', $taskPackages, '-p:NuGetAudit=false')
    Invoke-ReleaseCommand $taskDotnet @('publish', $taskProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', "-p:Version=$Version", '-o', $taskPublish)
    foreach ($taskRequired in @('coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'UI/desktop/index.html')) {
        if (-not (Test-Path (Join-Path $taskPublish $taskRequired))) { throw "Incomplete self-contained payload: $taskRequired" }
    }
    $taskNotices = Join-Path $taskPublish 'licenses'
    New-Item -ItemType Directory -Path $taskNotices -Force | Out-Null
    Copy-Item (Join-Path $taskPackages 'microsoft.web.webview2/1.0.4258.31/LICENSE.txt') (Join-Path $taskNotices 'WebView2-LICENSE.txt')
    Copy-Item (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') $taskNotices
    Copy-Item (Join-Path $taskRoot 'LICENSE') (Join-Path $taskPublish 'LICENSE')
    Copy-Item (Join-Path $PSScriptRoot 'Velopack-LICENSE.txt') $taskNotices
    foreach ($taskId in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        $taskLicenseName = if ($taskId -eq 'microsoft.windowsdesktop.app.runtime.win-x64') { 'LICENSE' } else { 'LICENSE.TXT' }
        Copy-Item (Join-Path $taskPackages "$taskId/$taskRuntime/$taskLicenseName") (Join-Path $taskNotices "$taskId-LICENSE.txt")
        Get-ChildItem (Join-Path $taskPackages "$taskId/$taskRuntime") -Filter '*NOTICES*' | Copy-Item -Destination { Join-Path $taskNotices ($taskId + '-' + $_.Name) }
    }
    Invoke-ReleaseCommand $taskCli @('pack', '--packId', 'DesktopHub', '--packTitle', 'Desktop Hub', '--packVersion', $Version, '--packDir', $taskPublish, '--mainExe', 'DesktopHub.Windows.exe', '--runtime', 'win-x64', '--channel', 'win', '--framework', 'webview2', '--icon', (Join-Path $taskRoot 'src/DesktopHub.Windows/Assets/desktop-hub.ico'), '--outputDir', $taskOutput)
    Get-ChildItem $taskOutput -File | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $([IO.Path]::GetFileName($_.Path))" } | Set-Content (Join-Path $taskOutput 'SHA256SUMS.txt') -Encoding utf8
    Write-Host "Release $Version ready: $taskOutput. .NET included; WebView2 bootstrapped; unsigned unless separately signed."
}
finally { Exit-ToolchainEnvironment $taskEnvironmentState }
