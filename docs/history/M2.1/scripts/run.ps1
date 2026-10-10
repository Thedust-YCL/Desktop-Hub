[CmdletBinding()]
param([switch]$Offline)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskApplication = Join-Path $taskRoot 'src/DesktopHub.Windows/bin/Release/net8.0-windows/DesktopHub.Windows.exe'
if (-not (Test-Path -LiteralPath $taskApplication)) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Offline:$Offline
}
$taskSdkDirectory = Join-Path $taskRoot '.tools/dotnet'
if (Test-Path -LiteralPath (Join-Path $taskSdkDirectory 'dotnet.exe')) {
    $env:DOTNET_ROOT = $taskSdkDirectory
    $env:DOTNET_ROOT_X64 = $taskSdkDirectory
}
Start-Process -FilePath $taskApplication -WorkingDirectory (Split-Path -Parent $taskApplication) -WindowStyle Hidden
Write-Host 'Desktop Hub started. Close the app through its native exit action.'
