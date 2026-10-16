[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [ValidateSet('quick','lightweight')][string]$Mode='quick',
    [switch]$LatencyOnly,
    [ValidateRange(1,30)][int]$ColdSamples=20
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskDirectory=[IO.Path]::GetFullPath($ApplicationDirectory)
$taskExe=Join-Path $taskDirectory 'DesktopHub.Windows.exe'
if(-not (Test-Path -LiteralPath $taskExe -PathType Leaf)){throw 'Diagnostic executable is missing.'}
function Get-DiagnosticPayloadManifest([string]$Directory) {
    $taskFiles=@(Get-ChildItem -LiteralPath $Directory -File | Where-Object {$_.Extension -in @('.exe','.dll','.json')})
    foreach($taskPayload in @('UI','ThirdParty','licenses','runtimes')) {
        $taskPayloadPath=Join-Path $Directory $taskPayload
        if(Test-Path -LiteralPath $taskPayloadPath -PathType Container){$taskFiles+=@(Get-ChildItem -LiteralPath $taskPayloadPath -File -Recurse)}
    }
    @($taskFiles | Sort-Object FullName -Unique | ForEach-Object {
        [pscustomobject]@{path=$_.FullName;relativePath=[IO.Path]::GetRelativePath($Directory,$_.FullName);length=$_.Length;hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
$taskRun=[Guid]::NewGuid().ToString('D')
$taskEvidence=Join-Path $taskRoot "artifacts/usability-comparison/runs/$taskRun"
New-Item -ItemType Directory -Path $taskEvidence -Force | Out-Null
$taskOld=@{}
foreach($taskKey in @('DOTNET_ROOT','DOTNET_ROOT_X64','DESKTOP_HUB_PROBE_MODE','DESKTOP_HUB_PROBE_LATENCY_ONLY')){$taskOld[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey,'Process')}
$taskStarted=[DateTime]::UtcNow
$taskRuns=[Collections.Generic.List[object]]::new()
$taskHashes=@(Get-DiagnosticPayloadManifest $taskDirectory)
[pscustomobject]@{capturedUtc=[DateTime]::UtcNow.ToString('O');applicationDirectory=$taskDirectory;scope='root executable/library/runtime metadata plus recursive UI, ThirdParty, licenses and runtimes; diagnostic data/evidence excluded';files=$taskHashes} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskEvidence 'payload-manifest.json')
try {
    foreach($taskKey in @('DOTNET_ROOT','DOTNET_ROOT_X64')){[Environment]::SetEnvironmentVariable($taskKey,$null,'Process')}
    $env:DESKTOP_HUB_PROBE_MODE=$Mode
    $env:DESKTOP_HUB_PROBE_LATENCY_ONLY=if($LatencyOnly){'1'}else{'0'}
    $taskCount=if($LatencyOnly){$ColdSamples}else{1}
    for($taskIndex=0;$taskIndex -lt $taskCount;$taskIndex++) {
        $taskStart=[DateTime]::UtcNow
        $taskProcess=Start-Process -FilePath $taskExe -ArgumentList '--resource-test' -WorkingDirectory $taskDirectory -WindowStyle Hidden -PassThru
        try {
            $taskTimeout=if($LatencyOnly){60}else{660}
            if(-not $taskProcess.WaitForExit($taskTimeout*1000)){$taskProcess.Kill();throw 'Owned diagnostic exceeded its timeout.'}
            $taskProcess.Refresh()
            if($taskProcess.ExitCode -ne 0){throw "Diagnostic failed: $($taskProcess.ExitCode)"}
        }finally{$taskProcess.Dispose()}
        $taskSource=Join-Path $taskDirectory 'resource-evidence'
        $taskSummary=Join-Path $taskSource 'summary.json'
        if(-not (Test-Path -LiteralPath $taskSummary) -or (Get-Item -LiteralPath $taskSummary).LastWriteTimeUtc -lt $taskStart){throw 'Fresh diagnostic completion evidence missing.'}
        $taskDestination=Join-Path $taskEvidence $taskIndex
        New-Item -ItemType Directory -Path $taskDestination | Out-Null
        Get-ChildItem -LiteralPath $taskSource -File | Where-Object {$_.LastWriteTimeUtc -ge $taskStart} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $taskDestination}
        $taskResult=Get-Content -LiteralPath $taskSummary -Raw | ConvertFrom-Json
        if(-not $LatencyOnly) {
            $taskReturned=@($taskResult.scenarios | Where-Object scenario -eq 'mini-after-use')
            if($taskReturned.Count -ne 1){throw 'Missing returned-Mini measurement.'}
            $taskExpectedRelease=$Mode -eq 'lightweight'
            if($taskReturned[0].dockReleased -ne $taskExpectedRelease -or $taskReturned[0].workspaceReleased -ne $taskExpectedRelease){throw 'Returned-Mini surface retention does not match the selected mode.'}
        }
        $taskRuns.Add([pscustomobject]@{index=$taskIndex;startedUtc=$taskStart.ToString('O');summary=$taskResult})
        Write-Host "Diagnostic sample $($taskIndex+1)/$taskCount complete."
    }
    $taskAfter=@(Get-DiagnosticPayloadManifest $taskDirectory)
    [pscustomobject]@{capturedUtc=[DateTime]::UtcNow.ToString('O');files=$taskAfter} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskEvidence 'payload-manifest-after.json')
    if(@(Compare-Object ($taskHashes | ForEach-Object {"$($_.relativePath)|$($_.length)|$($_.hash)"}) ($taskAfter | ForEach-Object {"$($_.relativePath)|$($_.length)|$($_.hash)"})).Count){throw 'Diagnostic payload changed during measurement.'}
    $taskTimes=@($taskRuns | ForEach-Object {$_.summary.latencies | Where-Object scenario -eq 'process-cold' | ForEach-Object {$_.summary.meanMilliseconds}} | Sort-Object)
    $taskP95=if($taskTimes.Count -ge 20){$taskTimes[[Math]::Ceiling($taskTimes.Count*.95)-1]}else{$null}
    $taskSurfaceCold=@(foreach($taskScenario in @('dock-surface-cold','workspace-surface-cold')) {
        $taskValues=@($taskRuns | ForEach-Object {$_.summary.latencies | Where-Object scenario -eq $taskScenario | ForEach-Object {$_.summary.meanMilliseconds}} | Sort-Object)
        [pscustomobject]@{scenario=$taskScenario;count=$taskValues.Count;rawMilliseconds=$taskValues;p95Milliseconds=if($taskValues.Count -ge 20){$taskValues[[Math]::Ceiling($taskValues.Count*.95)-1]}else{$null}}
    })
    [pscustomobject]@{mode=$Mode;latencyOnly=[bool]$LatencyOnly;startedUtc=$taskStarted.ToString('O');finishedUtc=[DateTime]::UtcNow.ToString('O');sampleCount=$taskTimes.Count;processColdRawMilliseconds=$taskTimes;processColdP95Milliseconds=$taskP95;surfaceCold=$taskSurfaceCold;files=$taskHashes;runs=$taskRuns.ToArray();method='same viewport/config/profile seed; actual 60s hidden then15s settle for return-Mini; no GC/working-set trim; process cold keeps OS/profile disk caches'} | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $taskEvidence 'comparison-run.json')
}finally{
    foreach($taskKey in $taskOld.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskOld[$taskKey],'Process')}
    Write-Output "Usability diagnostic evidence: $taskEvidence"
}
