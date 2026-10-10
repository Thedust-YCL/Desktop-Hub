[CmdletBinding()]
param([switch]$Offline)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot '.tools'
$taskSdkVersion = '8.0.425'
$taskPackageVersion = '1.0.4258.31'
$taskSdkDirectory = Join-Path $taskTools 'dotnet'
$taskDotnet = Join-Path $taskSdkDirectory 'dotnet.exe'
$taskArchive = Join-Path $taskTools "dotnet-sdk-$taskSdkVersion-win-x64.zip"
$taskFeed = Join-Path $taskTools 'nuget-feed'
$taskPackage = Join-Path $taskFeed "microsoft.web.webview2.$taskPackageVersion.nupkg"
$taskWindowsSdkVersion = '10.0.17763.56'
$taskWindowsSdkPackage = Join-Path $taskFeed "microsoft.windows.sdk.net.ref.$taskWindowsSdkVersion.nupkg"
$taskSdkHash = 'F0B6F15BF6F1A0507205C0CB102AB99E1DEE875C4682C8ED94665BE1D580186A06B21455E83B3A01A0FF7F4CD887B67420F2E2FE09ED985534A4CEA488AE1AF9'
$taskPackageHash = '1E5195C2FC8FFC85A25C053C2B9ED59A0E7D9216370D630EC4C9C7E001EEB05472D3FBC3023288B5750854D5E25D76AAE022DF499C8D522735C0E2259CF2EC3D'
$taskWindowsSdkHash = 'BC5CD10B7B27B0DEF94F8EF98DFC54107DB3DD14C6C78D923F803A8016F58C8E86D0850485D8E294B7A27BB34B8A53A6B7AE342DF096B74E8466DFB6989221B1'

function Test-ArtifactHash([string]$Path, [string]$Expected) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash -eq $Expected)
}

function Receive-Artifact([string]$Uri, [string]$Destination, [string]$Expected) {
    if (Test-ArtifactHash $Destination $Expected) { return }
    if ($Offline) { throw "Offline cache is absent or damaged: $Destination. Run scripts/bootstrap.ps1 with internet access once." }
    $taskPartial = "$Destination.download"
    try {
        Invoke-WebRequest -Uri $Uri -OutFile $taskPartial -UseBasicParsing
        if (-not (Test-ArtifactHash $taskPartial $Expected)) { throw "SHA512 verification failed: $Destination" }
        Move-Item -LiteralPath $taskPartial -Destination $Destination -Force
    }
    finally {
        if (Test-Path -LiteralPath $taskPartial) { Remove-Item -LiteralPath $taskPartial -Force }
    }
}

New-Item -ItemType Directory -Path $taskTools, $taskFeed -Force | Out-Null
# Required by Windows PowerShell 5.1 on some machines; does not modify system settings.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

if (-not (Test-Path -LiteralPath (Join-Path $taskSdkDirectory "sdk/$taskSdkVersion/MSBuild.dll"))) {
    if (-not (Test-ArtifactHash $taskArchive $taskSdkHash)) {
        if ($Offline) { throw 'The pinned .NET SDK is not cached. Run scripts/bootstrap.ps1 with internet access once.' }
        $taskMetadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json'
        $taskSdk = @($taskMetadata.releases | ForEach-Object { @($_.sdk) + @($_.sdks) } | Where-Object { $_.version -eq $taskSdkVersion }) | Select-Object -First 1
        $taskDownload = @($taskSdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') }) | Select-Object -First 1
        if (-not $taskDownload -or $taskDownload.hash -ne $taskSdkHash -or $taskDownload.url -ne "https://builds.dotnet.microsoft.com/dotnet/Sdk/$taskSdkVersion/dotnet-sdk-$taskSdkVersion-win-x64.zip") {
            throw 'Official SDK metadata does not match the pinned version, URL, and SHA512.'
        }
        Receive-Artifact $taskDownload.url $taskArchive $taskSdkHash
    }
    Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskSdkDirectory -Force
}

Receive-Artifact "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$taskPackageVersion/microsoft.web.webview2.$taskPackageVersion.nupkg" $taskPackage $taskPackageHash
Receive-Artifact "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.net.ref/$taskWindowsSdkVersion/microsoft.windows.sdk.net.ref.$taskWindowsSdkVersion.nupkg" $taskWindowsSdkPackage $taskWindowsSdkHash

$env:DOTNET_CLI_HOME = Join-Path $taskTools 'dotnet-home'
$env:DOTNET_ROOT = $taskSdkDirectory
$env:DOTNET_ROOT_X64 = $taskSdkDirectory
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_NOLOGO = '1'
$taskActualSdk = & $taskDotnet --version
if ($LASTEXITCODE -ne 0 -or $taskActualSdk -ne $taskSdkVersion) { throw 'The workspace-local SDK failed its version check.' }
Write-Host "Ready: .NET SDK $taskActualSdk; WebView2 SDK $taskPackageVersion; Windows SDK .NET Ref $taskWindowsSdkVersion; workspace-local cache."
