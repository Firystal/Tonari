param(
    [string]$ToolchainDirectory,
    [string]$PythonPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$compilerNames = if ($ToolchainDirectory) { @('i686-w64-mingw32-clang.exe','i686-w64-mingw32-gcc.exe') } else { @('i686-w64-mingw32-gcc','i686-w64-mingw32-clang') }
$compiler = $null
foreach ($name in $compilerNames) {
    if ($ToolchainDirectory) {
        $candidate = Join-Path $ToolchainDirectory $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $compiler = [IO.Path]::GetFullPath($candidate); break }
    } else {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $compiler = $command.Source; break }
    }
}
if (-not $compiler) { throw '未找到 x86 MinGW 编译器。请设置 -ToolchainDirectory，或将 i686-w64-mingw32-gcc 加入 PATH。' }
if ($PythonPath) {
    if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) { throw '-PythonPath 指定的 Python 不存在。' }
    $python = [IO.Path]::GetFullPath($PythonPath)
} else {
    $command = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { throw '未找到 Python。请设置 -PythonPath 或将 Python 加入 PATH。' }
    $python = $command.Source
}
& $python -c 'import pefile'
if ($LASTEXITCODE -ne 0) { throw 'Python 需要 pefile：python -m pip install pefile' }
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectRoot = Split-Path $sourceRoot -Parent
$outDir = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts\native\connect' }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -ToolchainDirectory $ToolchainDirectory -OutputDirectory $outDir
$fixtureFolder = Join-Path $outDir 'native-fixture'
New-Item -ItemType Directory -Path $fixtureFolder -Force | Out-Null
$fixture = Join-Path $fixtureFolder 'chusanApp.exe'
$testModule = Join-Path $outDir 'test_connect_probe.dll'
$productionModule = Join-Path $outDir 'chuni_connect_probe.dll'
$ordinalFixture = Join-Path $outDir 'native-fixture-ordinal\chusanApp.exe'
Push-Location -LiteralPath $outDir
try {
    & $compiler -std=c11 -O2 -Wall -Wextra -Werror -DCC_TEST_NATIVE -shared -static (Join-Path $PSScriptRoot 'connect_probe.c') -lws2_32 '-Wl,--no-insert-timestamp' -o $testModule
    if ($LASTEXITCODE -ne 0) { throw '连接补丁测试模块编译失败。' }
    & $compiler -std=c11 -O2 -Wall -Wextra -Werror -static (Join-Path $PSScriptRoot 'test_native_fixture.c') -lws2_32 '-Wl,--no-insert-timestamp' -o $fixture
    if ($LASTEXITCODE -ne 0) { throw '连接测试程序编译失败。' }
    foreach ($scenario in @('success','refused')) {
        foreach ($mode in @('observe','compat')) {
            & $fixture $testModule $mode $scenario
            if ($LASTEXITCODE -ne 0) { throw "连接测试失败：$scenario / $mode" }
        }
    }
    foreach ($mode in @('observe','compat')) {
        & $fixture $productionModule $mode production
        if ($LASTEXITCODE -ne 0) { throw "生产模块透传测试失败：$mode" }
    }
    & $fixture $productionModule compat production-room
    if ($LASTEXITCODE -ne 0) { throw '房间地址存根测试失败。' }
    & $python (Join-Path $PSScriptRoot 'make-ordinal-fixture.py') $fixture $ordinalFixture
    if ($LASTEXITCODE -ne 0) { throw '序号导入测试程序生成失败。' }
    & $ordinalFixture $testModule compat success
    if ($LASTEXITCODE -ne 0) { throw '序号导入测试模块验证失败。' }
    & $ordinalFixture $productionModule compat production
    if ($LASTEXITCODE -ne 0) { throw '序号导入生产模块验证失败。' }
} finally { Pop-Location }
