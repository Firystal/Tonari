param([string]$ActualBat = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestPaths.ps1')
$run = New-TestRun 'game-launcher' $OutputDirectory
$passed = $false
try {
    $sourceDir = $run.Source
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $patches = Join-Path $sourceDir 'patches'
    $expectedHashes = @{
        'connect_compat.dll' = '1312E4D4DE3B35E60FF2410C944AF828CAD24A7A04E33172F6483606754AFAD7'
        'daemon_registry_fix.dll' = 'D4C4A57652FB6F4FDAE12FE3736A58A63E1DF5F42B0F38BA89877B64C3D4AB10'
    }
    foreach ($name in $expectedHashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $patches $name) -Algorithm SHA256).Hash -ne $expectedHashes[$name]) { throw "Unexpected bundled module: $name" }
    }
    $testExe = Join-Path $run.Output 'GameLauncherTests.exe'
    & $compiler /nologo /target:exe /langversion:5 /codepage:65001 /r:System.Web.Extensions.dll "/out:$testExe" "/resource:$patches\connect_compat.dll,connect_compat.dll" "/resource:$patches\daemon_registry_fix.dll,daemon_registry_fix.dll" (Join-Path $sourceDir 'GameLauncher.cs') (Join-Path $PSScriptRoot 'GameLauncherTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Game launcher test compilation failed.' }
    # ActualBat adds read-only validation of an installed game; it is optional.
    $testArgs = @($run.Fixtures)
    if ($ActualBat) { $testArgs += (Resolve-Path -LiteralPath $ActualBat).Path }
    & $testExe @testArgs | Tee-Object -FilePath (Join-Path $run.Output 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Game launcher tests failed.' }
    $passed = $true
} finally { Close-TestRun $run $passed }

