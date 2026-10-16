[CmdletBinding()]
param([switch]$SkipTests, [switch]$Offline)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')
$taskEnvironmentState = Enter-ToolchainEnvironment $taskRoot
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskFeed = Join-Path $taskRoot '.tools/nuget-feed'
$taskPackages = Join-Path $taskRoot '.tools/nuget-packages'
$taskProject = Join-Path $taskRoot 'src/DesktopHub.Windows/DesktopHub.Windows.csproj'

function Invoke-Dotnet([string[]]$Arguments) {
    & $taskDotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet command failed (exit $LASTEXITCODE): $($Arguments -join ' ')" }
}

Push-Location $taskRoot
try {
    & (Join-Path $PSScriptRoot 'bootstrap.ps1') -Offline:$Offline
    Invoke-Dotnet -Arguments @('restore', $taskProject, '--source', $taskFeed, '--packages', $taskPackages, '-p:NuGetAudit=false')
    Invoke-Dotnet -Arguments @('build', $taskProject, '-c', 'Release', '--no-restore')
    if (-not $SkipTests) {
        $taskTests = Join-Path $taskRoot 'tests/DesktopHub.Core.Tests/DesktopHub.Core.Tests.csproj'
        if (Test-Path -LiteralPath $taskTests) {
            Invoke-Dotnet -Arguments @('restore', $taskTests, '--source', $taskFeed, '--packages', $taskPackages, '-p:NuGetAudit=false')
            Invoke-Dotnet -Arguments @('run', '--project', $taskTests, '-c', 'Release', '--no-restore')
        }
        else { Write-Warning 'Core test harness has not been created yet.' }
        $taskSceneTests = Join-Path $taskRoot 'tests/DesktopHub.Scene.Tests/DesktopHub.Scene.Tests.csproj'
        Invoke-Dotnet -Arguments @('restore', $taskSceneTests, '--source', $taskFeed, '--packages', $taskPackages, '-p:NuGetAudit=false')
        Invoke-Dotnet -Arguments @('run', '--project', $taskSceneTests, '-c', 'Release', '--no-restore')
        $taskResourceTests = Join-Path $taskRoot 'tests/DesktopHub.Resource.Tests/DesktopHub.Resource.Tests.csproj'
        Invoke-Dotnet -Arguments @('restore', $taskResourceTests, '--source', $taskFeed, '--packages', $taskPackages, '-p:NuGetAudit=false')
        Invoke-Dotnet -Arguments @('run', '--project', $taskResourceTests, '-c', 'Release', '--no-restore')
        $taskActivityProjectTests = Join-Path $taskRoot 'tests/DesktopHub.ActivityProject.Tests/DesktopHub.ActivityProject.Tests.csproj'
        Invoke-Dotnet -Arguments @('restore', $taskActivityProjectTests, '--source', $taskFeed, '--packages', $taskPackages, '-p:NuGetAudit=false')
        Invoke-Dotnet -Arguments @('run', '--project', $taskActivityProjectTests, '-c', 'Release', '--no-restore')
        foreach ($taskSystemProject in @('tests/DesktopHub.System.Tests/DesktopHub.System.Tests.csproj', 'tests/DesktopHub.Windows.Lifecycle.Tests/DesktopHub.Windows.Lifecycle.Tests.csproj', 'tests/DesktopHub.Windows.Preview.Tests/DesktopHub.Windows.Preview.Tests.csproj', 'tests/DesktopHub.Windows.Layout.Tests/DesktopHub.Windows.Layout.Tests.csproj', 'tests/DesktopHub.Diagnostics.Tests/DesktopHub.Diagnostics.Tests.csproj')) {
            $taskSystemTests = Join-Path $taskRoot $taskSystemProject
            Invoke-Dotnet -Arguments @('restore', $taskSystemTests, '--source', $taskFeed, '--packages', $taskPackages, '-p:NuGetAudit=false')
            Invoke-Dotnet -Arguments @('run', '--project', $taskSystemTests, '-c', 'Release', '--no-restore')
        }
        $taskNodeTests = @((Get-ChildItem -LiteralPath (Join-Path $taskRoot 'desktop/tests') -Filter '*.test.mjs' -File).FullName) + @(Join-Path $taskRoot 'prototype/tests/state.test.mjs')
        if ($taskNodeTests.Count -gt 0) {
            $taskNode = Get-Command node.exe -ErrorAction SilentlyContinue
            if (-not $taskNode) { throw 'Node.js is required to run frontend tests. Use -SkipTests only when intentionally skipping tests.' }
            & $taskNode.Source --test @taskNodeTests
            if ($LASTEXITCODE -ne 0) { throw "Frontend tests failed (exit $LASTEXITCODE)." }
        }
    }
}
finally { Pop-Location; Exit-ToolchainEnvironment $taskEnvironmentState }
