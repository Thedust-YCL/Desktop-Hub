[CmdletBinding()]
param([string]$VcVars = 'E:/DevTools/VS2022BuildTools/VC/Auxiliary/Build/vcvars64.bat')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVendor = Join-Path $taskRoot 'src/DesktopHub.Windows/ThirdParty/WebP'
$taskLibraries = Join-Path $taskVendor 'lib'
if ((Get-FileHash -LiteralPath (Join-Path $taskLibraries 'libwebp.lib') -Algorithm SHA256).Hash -ne 'F7CCC6F78850640593AD071BB24415B4ADDDE3F70CF611CDA1FE17510B7A65FD' -or
    (Get-FileHash -LiteralPath (Join-Path $taskLibraries 'libwebpdemux.lib') -Algorithm SHA256).Hash -ne '8F041B85E097EC1E0BAB68B8C941F30F4542C4BDF9D4521C5C646DFBEA3AB278') { throw 'Pinned official WebP static library mismatch' }
$taskBuild = Join-Path $taskRoot 'artifacts/usability-development/task-7-decoder-build'
New-Item -ItemType Directory -Force -Path $taskBuild | Out-Null
# All paths are fixed private workspace artifacts. This optional operation is never run by dotnet build.
$taskBatch = Join-Path $taskBuild 'compile.cmd'
$taskLines = @('@echo off', ('call "{0}"' -f $VcVars), ('cd /d "{0}"' -f $taskBuild), ('cl.exe /nologo /O2 /MT /LD /EHsc /std:c++17 /W4 /I"{0}/include" "{0}/shim.c" "{0}/pdf.cpp" "{1}/libwebp.lib" "{1}/libwebpdemux.lib" windowsapp.lib shcore.lib ole32.lib d3d11.lib windows.data.pdf.lib windowscodecs.lib /link /Brepro /OUT:"{0}/desktop_hub_webp.dll" /IMPLIB:"{2}/desktop_hub_webp.lib"' -f $taskVendor,$taskLibraries,$taskBuild), 'exit /b %errorlevel%')
Set-Content -LiteralPath $taskBatch -Value (@('@echo off', 'chcp 65001 >nul') + $taskLines[1..($taskLines.Length-1)]) -Encoding utf8NoBOM
& cmd.exe /d /c $taskBatch
if ($LASTEXITCODE -ne 0) { throw 'Preview decoder shim compilation failed' }
Get-FileHash -LiteralPath (Join-Path $taskVendor 'desktop_hub_webp.dll') -Algorithm SHA256


