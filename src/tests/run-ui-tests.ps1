param([string]$ApplicationPath = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with Windows PowerShell -STA.' }
. (Join-Path $PSScriptRoot 'TestPaths.ps1')
$run = New-TestRun 'ui' $OutputDirectory
$passed = $false
try {
    if (-not $ApplicationPath) {
        $candidates = @(
            (Join-Path $run.Project 'dist\Tonari\Tonari.exe'),
            (Join-Path (Split-Path -Parent $run.Source) 'Tonari.exe')
        )
        $ApplicationPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $ApplicationPath) { throw 'Build the application first or pass -ApplicationPath.' }
    }
    $ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
    $versionMatch = [regex]::Match([IO.File]::ReadAllText((Join-Path $run.Source 'Program.cs')), 'AssemblyVersion\("([^"]+)"\)')
    if (-not $versionMatch.Success) { throw 'AssemblyVersion was not found in Program.cs.' }
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml,System.Web.Extensions
    [Reflection.Assembly]::LoadFrom($ApplicationPath) | Out-Null
    $references = @($ApplicationPath, [Windows.Window].Assembly.Location, [Windows.Media.Visual].Assembly.Location,
        [Windows.Threading.Dispatcher].Assembly.Location, [System.Xaml.XamlReader].Assembly.Location)
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $helperPath = Join-Path $run.Output 'UiFlowTests.dll'
    $compileArguments = @('/nologo','/target:library','/langversion:5','/codepage:65001',('/out:' + $helperPath))
    $compileArguments += $references | ForEach-Object { '/reference:' + $_ }
    $compileArguments += Join-Path $PSScriptRoot 'UiFlowTests.cs'
    & $compiler @compileArguments
    if ($LASTEXITCODE -ne 0) { throw 'UI helper compilation failed.' }
    [Reflection.Assembly]::LoadFrom($helperPath) | Out-Null
    $report = [ChuniUiTests.UiFlowTests]::Run($run.Output, $run.Fixtures, $versionMatch.Groups[1].Value)
    $report | Add-Member -NotePropertyName AssemblyHash -NotePropertyValue (Get-FileHash -LiteralPath $ApplicationPath -Algorithm SHA256).Hash
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run.Output 'ui-flow-check.json') -Encoding UTF8
    if ($report.Failure) { throw $report.Failure }
    $passed = $true
    Write-Output ('ALL PASSED ' + $report.Checks.Count + '; screenshots=' + ($report.Layouts | Where-Object { $_.Image }).Count + '; output=' + $run.Output)
} finally { Close-TestRun $run $passed }

