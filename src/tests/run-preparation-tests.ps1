param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestPaths.ps1')
$run = New-TestRun 'preparation' $OutputDirectory
$passed = $false
try {
    $sourceDir = $run.Source
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $testExe = Join-Path $run.Output 'GameFilePreparationTests.exe'
    & $compiler /nologo /target:exe /langversion:5 /codepage:65001 /main:GameFilePreparationTests /r:System.Web.Extensions.dll "/out:$testExe" (Join-Path $sourceDir 'RoomConfig.cs') (Join-Path $sourceDir 'SetupActions.cs') (Join-Path $sourceDir 'GameLauncher.cs') (Join-Path $sourceDir 'GameTestRole.cs') (Join-Path $sourceDir 'GameFilePreparation.cs') (Join-Path $PSScriptRoot 'GameTestRoleTests.cs') (Join-Path $PSScriptRoot 'GameFilePreparationTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'File preparation test compilation failed.' }
    & $testExe $run.Fixtures | Tee-Object -FilePath (Join-Path $run.Output 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw 'File preparation tests failed.' }
    $passed = $true
} finally { Close-TestRun $run $passed }

