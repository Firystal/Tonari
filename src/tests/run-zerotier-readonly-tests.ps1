param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestPaths.ps1')
$run = New-TestRun 'zerotier-readonly' $OutputDirectory
$passed = $false
try {
    $sourceDir = $run.Source
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $testExe = Join-Path $run.Output 'ZeroTierReadOnlyTests.exe'
    & $compiler /nologo /target:exe /langversion:5 /codepage:65001 "/out:$testExe" /r:System.ServiceProcess.dll /r:System.Web.Extensions.dll (Join-Path $sourceDir 'RoomConfig.cs') (Join-Path $sourceDir 'ZeroTierSession.cs') (Join-Path $sourceDir 'RuntimeSupport.cs') (Join-Path $PSScriptRoot 'ZeroTierReadOnlyTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Read-only test compilation failed.' }
    & $testExe | Tee-Object -FilePath (Join-Path $run.Output 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Read-only tests failed.' }
    $passed = $true
} finally { Close-TestRun $run $passed }

