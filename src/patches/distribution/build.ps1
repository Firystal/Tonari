param(
    [string]$ToolchainDirectory,
    [string]$OutputDirectory,
    [string]$MainImagePath,
    [string]$HookImagePath
)
$ErrorActionPreference = 'Stop'
if ([bool]$MainImagePath -ne [bool]$HookImagePath) { throw '-MainImagePath 与 -HookImagePath 必须同时指定。' }
$compilerNames = if ($ToolchainDirectory) { @('x86_64-w64-mingw32-clang.exe','x86_64-w64-mingw32-gcc.exe') } else { @('x86_64-w64-mingw32-gcc','x86_64-w64-mingw32-clang') }
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
if (-not $compiler) { throw '未找到 x64 MinGW 编译器。请设置 -ToolchainDirectory，或将 x86_64-w64-mingw32-gcc 加入 PATH。' }
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectRoot = Split-Path $sourceRoot -Parent
$outDir = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts\native\distribution' }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$module = Join-Path $outDir 'daemon_registry_fix.dll'
$testExe = Join-Path $outDir 'test_registry_fix.exe'
$identityArguments = @()
if ($MainImagePath) { $identityArguments = @([IO.Path]::GetFullPath($MainImagePath), [IO.Path]::GetFullPath($HookImagePath)) }
Push-Location -LiteralPath $outDir
try {
    & $compiler -std=c11 -O2 -Wall -Wextra -Werror -shared -static (Join-Path $PSScriptRoot 'daemon_registry_fix.c') -lws2_32 '-Wl,--no-insert-timestamp' -o $module
    if ($LASTEXITCODE -ne 0) { throw '配信补丁编译失败。' }
    & $compiler -std=c11 -O2 -Wall -Wextra -Werror -static -municode (Join-Path $PSScriptRoot 'test_registry_fix.c') -lws2_32 '-Wl,--no-insert-timestamp' -o $testExe
    if ($LASTEXITCODE -ne 0) { throw '配信补丁测试编译失败。' }
    & $testExe @identityArguments | Tee-Object -FilePath (Join-Path $outDir 'test-results.txt')
    if ($LASTEXITCODE -ne 0) { throw '配信补丁测试失败。' }
    Get-FileHash -Algorithm SHA256 -LiteralPath $module
} finally { Pop-Location }
