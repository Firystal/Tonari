function Assert-TestPathHasNoLinks([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Test output path contains a link: $cursor" }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Remove-OwnedTestEntry([string]$Path, [string]$Boundary) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    if ($full -ne $root -and -not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped its directory.' }
    $item = Get-Item -LiteralPath $full -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($full, $false) }
        else { Remove-Item -LiteralPath $full -Force }
        return
    }
    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $full -Force)) { Remove-OwnedTestEntry $child.FullName $root }
    }
    Remove-Item -LiteralPath $full -Force
}

function Remove-TestFixtures($Run) {
    Assert-TestPathHasNoLinks $Run.Output
    $fixtures = [IO.Path]::GetFullPath((Join-Path $Run.Output 'fixtures'))
    if ($fixtures -ne $Run.Fixtures) { throw 'Unexpected fixture cleanup path.' }
    if (-not (Test-Path -LiteralPath $fixtures)) { return }
    Assert-TestPathHasNoLinks $fixtures
    $marker = Join-Path $fixtures '.chunilan-test-fixtures'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or [IO.File]::ReadAllText($marker) -ne $Run.Marker) { throw 'Fixture ownership marker missing or invalid.' }
    Remove-OwnedTestEntry $fixtures $fixtures
}

function New-TestRun([string]$Suite, [string]$OutputDirectory) {
    if ($Suite -notmatch '^[a-z-]+$') { throw 'Invalid test suite name.' }
    $source = Split-Path -Parent $PSScriptRoot
    $project = if ((Split-Path -Leaf $source) -eq 'src') { Split-Path -Parent $source } else { $source }
    $output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $project ('artifacts\tests\' + $Suite) }
    Assert-TestPathHasNoLinks $output
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $marker = "CHUNILAN_TEST_OUTPUT_V1`n$Suite"
    $markerPath = Join-Path $output '.chunilan-test-suite'
    if (Test-Path -LiteralPath $markerPath) {
        if ([IO.File]::ReadAllText($markerPath) -ne $marker) { throw 'Test output belongs to another suite.' }
    } elseif (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
        throw 'Use an empty output directory or an existing output directory for this suite.'
    } else { [IO.File]::WriteAllText($markerPath, $marker, [Text.UTF8Encoding]::new($false)) }
    $lock = [IO.File]::Open((Join-Path $output '.run.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $run = [pscustomobject]@{ Output = $output; Fixtures = (Join-Path $output 'fixtures'); Marker = $marker; Source = $source; Project = $project; Lock = $lock }
    try {
        Remove-TestFixtures $run
        New-Item -ItemType Directory -Path $run.Fixtures | Out-Null
        [IO.File]::WriteAllText((Join-Path $run.Fixtures '.chunilan-test-fixtures'), $marker, [Text.UTF8Encoding]::new($false))
        return $run
    } catch { $lock.Dispose(); throw }
}

function Close-TestRun($Run, [bool]$Passed) {
    try { if ($Passed) { Remove-TestFixtures $Run } }
    finally { $Run.Lock.Dispose() }
}
