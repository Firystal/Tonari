param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestPaths.ps1')
$run = New-TestRun 'test-role' $OutputDirectory
$passed = $false
try {
    $sourceDir = $run.Source
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $testExe = Join-Path $run.Output 'GameTestRoleTests.exe'
    & $compiler /nologo /target:exe /langversion:5 /codepage:65001 "/out:$testExe" (Join-Path $sourceDir 'GameTestRole.cs') (Join-Path $PSScriptRoot 'GameTestRoleTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Machine-state test compilation failed.' }
    & $testExe $run.Fixtures | Tee-Object -FilePath (Join-Path $run.Output 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Machine-state tests failed.' }
    $passed = $true
} finally { Close-TestRun $run $passed }

