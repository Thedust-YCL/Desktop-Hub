[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/win-x64-full-usability',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
$taskEvidence = Join-Path $taskRoot ('artifacts/usability-release/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$taskStarted = [DateTime]::UtcNow
$taskPassed = $false
$taskEnvironmentState = $null
$taskSwitches = @{}
New-Item -ItemType Directory -Path $taskEvidence -Force | Out-Null
. (Join-Path $PSScriptRoot 'toolchain-environment.ps1')

function Get-PayloadManifest([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            path = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}

function Assert-Payload([string]$Directory, [object[]]$Manifest) {
    foreach ($taskEntry in $Manifest) {
        # publish.ps1 adds this notice; test projects receive all other product files.
        $taskFile = if ($taskEntry.path -eq 'licenses/WebView2-LICENSE.txt') {
            Join-Path $taskRoot '.tools/nuget-packages/microsoft.web.webview2/1.0.4258.31/LICENSE.txt'
        } else { Join-Path $Directory $taskEntry.path }
        if (-not (Test-Path -LiteralPath $taskFile -PathType Leaf) -or
            (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash -ne $taskEntry.sha256) {
            throw "Release payload mismatch in ${Directory}: $($taskEntry.path)"
        }
    }
}

Start-Transcript -Path (Join-Path $taskEvidence 'acceptance.log') | Out-Null
try {
    # Never inherit a developer's focused/skip-test mode into a release gate.
    # Remove through Env:; PowerShell can coerce a .NET string $null to an empty value.
    foreach ($taskName in @('TASK7_MEMORY_PROBE', 'TASK7_PDF_PROBE', 'TASK7_STRIDE_ONLY', 'TASK7_SKIP_PDF', 'TASK7_SKIP_WEBP', 'TASK7_SKIP_POLICY', 'TASK7_FINALIZE_CHECK')) {
        $taskSwitches[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
        Remove-Item -LiteralPath ("Env:" + $taskName) -ErrorAction SilentlyContinue
    }
    $env:TASK7_FINALIZE_CHECK = '1'
    if (-not $SkipPublish) {
        & (Join-Path $PSScriptRoot 'publish.ps1') -Offline -OutputDirectory $OutputDirectory
    }
    if (-not (Test-Path -LiteralPath (Join-Path $taskOutput 'DesktopHub.Windows.exe') -PathType Leaf)) {
        throw 'Published application is missing.'
    }
    $taskManifest = Get-PayloadManifest $taskOutput
    $taskManifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $taskEvidence 'release-manifest.json') -Encoding utf8

    # Build only the opt-in native harnesses; publish already ran the full test suite.
    $taskEnvironmentState = Enter-ToolchainEnvironment $taskRoot
    $taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
    foreach ($taskProject in @('DesktopHub.LaunchFixture', 'DesktopHub.Usability.Tests', 'DesktopHub.Usability78.Tests')) {
        $taskProjectPath = Join-Path $taskRoot "tests/$taskProject/$taskProject.csproj"
        & $taskDotnet restore $taskProjectPath --source (Join-Path $taskRoot '.tools/nuget-feed') --packages (Join-Path $taskRoot '.tools/nuget-packages') -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw "Native restore failed: $taskProject" }
        & $taskDotnet build $taskProjectPath -c Release --no-restore --disable-build-servers -m:1
        if ($LASTEXITCODE -ne 0) { throw "Native build failed: $taskProject" }
    }
    Exit-ToolchainEnvironment $taskEnvironmentState
    $taskEnvironmentState = $null

    foreach ($taskNative in @(
        @{ project = 'DesktopHub.Usability.Tests'; script = 'test-usability-46-native.ps1'; evidence = 'native-task4-6' },
        @{ project = 'DesktopHub.Usability78.Tests'; script = 'test-usability-78-native.ps1'; evidence = 'native-task7-8' }
    )) {
        $taskBinary = Join-Path $taskRoot "tests/$($taskNative.project)/bin/Release/net8.0-windows10.0.17763.0"
        Assert-Payload $taskBinary $taskManifest
        & (Join-Path $PSScriptRoot $taskNative.script) -SkipBuild
        Assert-Payload $taskBinary $taskManifest
        # Keep the original native evidence intact and provide a small delivery index.
        $taskRun = Get-ChildItem -LiteralPath (Join-Path $taskRoot "artifacts/usability-development/$($taskNative.evidence)") -Directory |
            Where-Object { $_.CreationTimeUtc -ge $taskStarted } | Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
        if (-not $taskRun) { throw "Fresh native evidence missing: $($taskNative.project)" }
        Get-Content -LiteralPath (Join-Path $taskRun.FullName 'run.json') -Raw |
            Set-Content -LiteralPath (Join-Path $taskEvidence ($taskNative.evidence + '.json')) -Encoding utf8
    }
    Assert-Payload $taskOutput $taskManifest
    $taskAfter = Get-PayloadManifest $taskOutput
    if (($taskAfter | ConvertTo-Json -Compress) -cne ($taskManifest | ConvertTo-Json -Compress)) {
        throw 'Release files changed during native acceptance.'
    }
    $taskAfter | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $taskEvidence 'release-manifest-after.json') -Encoding utf8
    $taskPassed = $true
    Write-Output "Published payload native acceptance passed. Payload files: $($taskManifest.Count). Evidence: $taskEvidence"
}
finally {
    if ($null -ne $taskEnvironmentState) { Exit-ToolchainEnvironment $taskEnvironmentState }
    foreach ($taskName in $taskSwitches.Keys) {
        if ($null -eq $taskSwitches[$taskName]) { Remove-Item -LiteralPath ("Env:" + $taskName) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($taskName, $taskSwitches[$taskName], 'Process') }
    }
    [pscustomobject]@{
        passed = $taskPassed
        startedUtc = $taskStarted.ToString('O')
        completedUtc = [DateTime]::UtcNow.ToString('O')
        output = $taskOutput
        evidence = $taskEvidence
        publishAndSuiteRun = (-not $SkipPublish)
        scope = 'Byte-matched native 4-6 / 7-8 acceptance on this machine; full suite/publish reused when SkipPublish is set; hardware matrix remains unverified.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskEvidence 'run.json') -Encoding utf8
    Stop-Transcript | Out-Null
}
