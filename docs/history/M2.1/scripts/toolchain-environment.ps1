# Process-local build environment. Call Exit-ToolchainEnvironment in a finally block.
function Enter-ToolchainEnvironment([string]$Root) {
    $taskEnvironment = @{
        DOTNET_CLI_HOME = (Join-Path $Root '.tools/dotnet-home')
        DOTNET_ROOT = (Join-Path $Root '.tools/dotnet')
        DOTNET_ROOT_X64 = (Join-Path $Root '.tools/dotnet')
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
        DOTNET_NOLOGO = '1'
        APPDATA = (Join-Path $Root '.tools/build-appdata')
        LOCALAPPDATA = (Join-Path $Root '.tools/build-localappdata')
    }
    $taskPrevious = @{}
    New-Item -ItemType Directory -Path $taskEnvironment.APPDATA, $taskEnvironment.LOCALAPPDATA, $taskEnvironment.DOTNET_CLI_HOME -Force | Out-Null
    foreach ($taskKey in $taskEnvironment.Keys) {
        $taskPrevious[$taskKey] = [Environment]::GetEnvironmentVariable($taskKey, 'Process')
        [Environment]::SetEnvironmentVariable($taskKey, $taskEnvironment[$taskKey], 'Process')
    }
    return $taskPrevious
}

function Exit-ToolchainEnvironment([hashtable]$State) {
    foreach ($taskKey in $State.Keys) {
        if ($null -eq $State[$taskKey]) { Remove-Item -LiteralPath ("Env:\" + $taskKey) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($taskKey, $State[$taskKey], 'Process') }
    }
}
