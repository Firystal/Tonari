param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$outDir = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\Tonari' }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$output = Join-Path $outDir 'Tonari.exe'
$sources = @('Program.cs','RoomConfig.cs','ZeroTierSession.cs','SetupActions.cs','RuntimeSupport.cs','NetworkDiagnostics.cs','GameLauncher.cs','GameFilePreparation.cs','GameTestRole.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$patchHashes = @{
 'connect_compat.dll'='1312E4D4DE3B35E60FF2410C944AF828CAD24A7A04E33172F6483606754AFAD7'
 'daemon_registry_fix.dll'='D4C4A57652FB6F4FDAE12FE3736A58A63E1DF5F42B0F38BA89877B64C3D4AB10'
}
foreach($name in $patchHashes.Keys) {
 if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "patches\$name") -Algorithm SHA256).Hash -ne $patchHashes[$name]) { throw "Unexpected embedded module: $name" }
}
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$output" "/win32manifest:$PSScriptRoot\launcher.manifest" "/win32icon:$PSScriptRoot\app.ico" "/resource:$PSScriptRoot\app.ico,app.ico" "/resource:$PSScriptRoot\MainWindow.xaml,MainWindow.xaml" "/resource:$PSScriptRoot\patches\connect_compat.dll,connect_compat.dll" "/resource:$PSScriptRoot\patches\daemon_registry_fix.dll,daemon_registry_fix.dll" "/reference:$framework\WPF\PresentationFramework.dll" "/reference:$framework\WPF\PresentationCore.dll" "/reference:$framework\WPF\WindowsBase.dll" /reference:System.Xaml.dll /reference:System.Windows.Forms.dll /reference:System.ServiceProcess.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw '联机助手编译失败。' }
Get-Item -LiteralPath $output | Select-Object FullName,Length
