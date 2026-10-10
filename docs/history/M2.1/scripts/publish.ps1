[CmdletBinding()]
param([switch]$SkipTests, [switch]$Offline)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskEnvironmentState = Enter-ToolchainEnvironment $taskRoot
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskProject = Join-Path $taskRoot 'src/DesktopHub.Windows/DesktopHub.Windows.csproj'
$taskOutput = Join-Path $taskRoot 'artifacts/win-x64'
try {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipTests:$SkipTests -Offline:$Offline
    # PlatformTarget is x64 and the pinned SDK is win-x64. An explicit RID restore
    # needlessly downloads runtime packs in this SDK even for FDD; the normal
    # restore creates the x64 apphost and uses the installed runtime instead.
    & $taskDotnet restore $taskProject -p:SelfContained=false --source (Join-Path $taskRoot '.tools/nuget-feed') --packages (Join-Path $taskRoot '.tools/nuget-packages') -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Publish restore failed (exit $LASTEXITCODE)." }
    & $taskDotnet publish $taskProject -c Release --self-contained false --no-restore -o $taskOutput
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
    $taskNotices = Join-Path $taskOutput 'licenses'
    New-Item -ItemType Directory -Path $taskNotices -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot '.tools/nuget-packages/microsoft.web.webview2/1.0.4258.31/LICENSE.txt') -Destination (Join-Path $taskNotices 'WebView2-LICENSE.txt') -Force
    Write-Host "Published to $taskOutput. Requires .NET Desktop Runtime 8 x64 and WebView2 Runtime."
}
finally { Exit-ToolchainEnvironment $taskEnvironmentState }
