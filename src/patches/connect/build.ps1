param(
    [string]$ToolchainDirectory,
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
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectRoot = Split-Path $sourceRoot -Parent
$outDir = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts\native\connect' }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$module = Join-Path $outDir 'chuni_connect_probe.dll'
$coreTest = Join-Path $outDir 'test_core.exe'
$signatureTest = Join-Path $outDir 'test_signature.exe'
Push-Location -LiteralPath $outDir
try {
    & $compiler -std=c11 -O2 -Wall -Wextra -Werror -shared -static (Join-Path $PSScriptRoot 'connect_probe.c') -lws2_32 '-Wl,--no-insert-timestamp' -o $module
    if ($LASTEXITCODE -ne 0) { throw '连接补丁编译失败。' }
    & $compiler -std=c11 -O2 -Wall -Wextra -Werror -static (Join-Path $PSScriptRoot 'test_core.c') '-Wl,--no-insert-timestamp' -o $coreTest
    if ($LASTEXITCODE -ne 0) { throw '连接逻辑测试编译失败。' }
    & $coreTest
    if ($LASTEXITCODE -ne 0) { throw '连接逻辑测试失败。' }
    & $compiler -std=c11 -O2 -Wall -Wextra -Werror -static (Join-Path $PSScriptRoot 'test_signature.c') '-Wl,--no-insert-timestamp' -o $signatureTest
    if ($LASTEXITCODE -ne 0) { throw '连接签名测试编译失败。' }
    & $signatureTest
    if ($LASTEXITCODE -ne 0) { throw '连接签名测试失败。' }
    Get-FileHash -Algorithm SHA256 -LiteralPath $module
} finally { Pop-Location }
