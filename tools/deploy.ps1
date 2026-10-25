[CmdletBinding(SupportsShouldProcess = $true)]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$releaseRoot = Join-Path $projectRoot 'dist\Tonari'
$archive = Join-Path $projectRoot 'dist\Tonari.zip'
$desktop = [Environment]::GetFolderPath('Desktop')
if ([string]::IsNullOrWhiteSpace($desktop)) { throw '无法确定桌面目录。' }
$targetRoot = Join-Path $desktop 'Tonari'
$targetArchive = Join-Path $desktop 'Tonari.zip'
$legacyRoot = Join-Path $desktop 'CHUNITHM-LAN'
$legacyArchive = Join-Path $desktop 'CHUNITHM-四机联机工具包.zip'

function Assert-LocalPath([string]$Path, [string]$Boundary) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    if ($full -ne $root -and -not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "路径超出目标目录：$full" }
    $current = $full
    while ($current.Length -ge $root.Length) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "路径不能包含链接：$current" }
        }
        if ($current -eq $root) { break }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function Resolve-Entry([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw '发布清单路径无效。' }
    $parts = $Relative.Replace('\', '/').Split('/')
    if (@($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 }).Count) { throw "发布清单路径无效：$Relative" }
    return Assert-LocalPath (Join-Path $Root ($parts -join '\')) $Root
}

function Assert-LocalTree([string]$Path, [string]$Boundary) {
    $null = Assert-LocalPath $Path $Boundary
    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force)) {
        $null = Assert-LocalPath $entry.FullName $Boundary
        if ($entry.PSIsContainer) { Assert-LocalTree $entry.FullName $Boundary }
    }
}

function Get-StreamHash([IO.Stream]$Stream) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($Stream)).Replace('-', '') }
    finally { $hash.Dispose() }
}

function Get-FileDigest([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try { return Get-StreamHash $stream }
    finally { $stream.Dispose() }
}

function Assert-Archive($Zip, [string]$Prefix, $Manifest, [string]$ManifestHash, [string]$Boundary) {
    if ($Manifest.Count -eq 0 -or $Zip.Entries.Count -ne $Manifest.Count + 1) { throw '压缩包条目与发布清单不一致。' }
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Manifest) {
        $null = Resolve-Entry $Boundary $item.Path
        if (-not $names.Add([string]$item.Path)) { throw "压缩包清单包含重复路径：$($item.Path)" }
        $entry = $Zip.GetEntry($Prefix + '/' + $item.Path)
        if ($null -eq $entry -or $entry.Length -ne $item.Length) { throw "压缩包缺少文件：$($item.Path)" }
        $stream = $entry.Open()
        try { if ((Get-StreamHash $stream) -ne $item.SHA256) { throw "压缩包校验失败：$($item.Path)" } }
        finally { $stream.Dispose() }
    }
    $entry = $Zip.GetEntry($Prefix + '/SHA256-manifest.json')
    if ($null -eq $entry) { throw '压缩包缺少发布清单。' }
    $stream = $entry.Open()
    try { if ((Get-StreamHash $stream) -ne $ManifestHash) { throw '压缩包发布清单不一致。' } }
    finally { $stream.Dispose() }
}

$releaseRoot = Assert-LocalPath $releaseRoot $projectRoot
$targetRoot = Assert-LocalPath $targetRoot $desktop
$archive = Assert-LocalPath $archive $projectRoot
$targetArchive = Assert-LocalPath $targetArchive $desktop
$legacyRoot = Assert-LocalPath $legacyRoot $desktop
$legacyArchive = Assert-LocalPath $legacyArchive $desktop
$manifestPath = Resolve-Entry $releaseRoot 'SHA256-manifest.json'
$targetManifest = Resolve-Entry $targetRoot 'SHA256-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.Count -eq 0) { throw '发布清单为空，请先打包。' }
$manifestHash = Get-FileDigest $manifestPath
$expected = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($item in $manifest) {
    if (-not $expected.Add([string]$item.Path)) { throw "发布清单包含重复路径：$($item.Path)" }
    $source = Resolve-Entry $releaseRoot $item.Path
    $null = Resolve-Entry $targetRoot $item.Path
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -ne $item.Length -or (Get-FileDigest $source) -ne $item.SHA256) { throw "源文件校验失败：$($item.Path)" }
}
if (-not $expected.Contains('Tonari.exe')) { throw '发布清单缺少主程序。' }

# Renaming the previous directory preserves files that are not owned by its manifest.
$migrateLegacy = Test-Path -LiteralPath $legacyRoot -PathType Container
$previousRoot = $targetRoot
if ($migrateLegacy) {
    if (Test-Path -LiteralPath $targetRoot) { throw '桌面同时存在 Tonari 与 CHUNITHM-LAN，请先整理这两个目录再部署。' }
    Assert-LocalTree $legacyRoot $legacyRoot
    $previousRoot = $legacyRoot
}
$previousManifest = Resolve-Entry $previousRoot 'SHA256-manifest.json'
if ($migrateLegacy -and -not (Test-Path -LiteralPath $previousManifest -PathType Leaf)) { throw '旧目录缺少发布清单，无法确认迁移内容。' }

# Retire only unchanged files listed in the preceding deployment manifest.
$obsolete = @()
$previousNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $previousManifest -PathType Leaf) {
    $previous = Get-Content -LiteralPath $previousManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($item in $previous) {
        if (-not $previousNames.Add([string]$item.Path)) { throw "旧发布清单包含重复路径：$($item.Path)" }
        $old = Resolve-Entry $previousRoot $item.Path
        $null = Resolve-Entry $targetRoot $item.Path
        if (-not $expected.Contains([string]$item.Path) -and (Test-Path -LiteralPath $old -PathType Leaf)) {
            if ((Get-Item -LiteralPath $old).Length -ne $item.Length -or (Get-FileDigest $old) -ne $item.SHA256) { throw "待移除的旧文件已被修改：$old" }
            $obsolete += $item
        }
    }
}
if ($migrateLegacy -and -not $previousNames.Contains('CHUNITHM-LAN.exe')) { throw '旧目录不是已知的联机助手发布目录。' }
foreach ($item in $manifest) {
    $destination = Resolve-Entry $previousRoot $item.Path
    if (Test-Path -LiteralPath $destination -PathType Container) { throw "新版本文件与现有目录重名：$destination" }
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and -not $previousNames.Contains([string]$item.Path) -and (Get-FileDigest $destination) -ne $item.SHA256) {
        throw "新版本文件与本地文件重名，已保留本地文件：$destination"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    Assert-Archive $zip 'Tonari' $manifest $manifestHash $targetRoot
} finally { $zip.Dispose() }
$archiveHash = Get-FileDigest $archive
$legacyArchiveHash = $null
if (Test-Path -LiteralPath $legacyArchive -PathType Leaf) {
    $zip = [IO.Compression.ZipFile]::OpenRead($legacyArchive)
    try {
        $entry = $zip.GetEntry('CHUNITHM-LAN/SHA256-manifest.json')
        if ($null -eq $entry) { throw '旧压缩包缺少发布清单，无法确认清理内容。' }
        $stream = $entry.Open()
        try { $legacyManifestHash = Get-StreamHash $stream } finally { $stream.Dispose() }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $legacyManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if (-not ($legacyManifest | Where-Object { $_.Path -eq 'CHUNITHM-LAN.exe' })) { throw '旧压缩包不是已知的联机助手发布包。' }
        Assert-Archive $zip 'CHUNITHM-LAN' $legacyManifest $legacyManifestHash $legacyRoot
    } finally { $zip.Dispose() }
    $legacyArchiveHash = Get-FileDigest $legacyArchive
}
if (Get-Process -Name CHUNITHM-LAN,Tonari -ErrorAction SilentlyContinue) { throw '请先关闭 Tonari 或旧版联机助手。' }

if ($PSCmdlet.ShouldProcess($desktop, '更新 Tonari 目录和 ZIP，并迁移旧版发布文件')) {
    if ($migrateLegacy) {
        Assert-LocalTree $legacyRoot $desktop
        $null = Assert-LocalPath $targetRoot $desktop
        if (Test-Path -LiteralPath $targetRoot) { throw 'Tonari 目标目录已出现，迁移已停止。' }
        Move-Item -LiteralPath $legacyRoot -Destination $targetRoot
    }
    $null = New-Item -ItemType Directory -Path $targetRoot -Force
    foreach ($item in $manifest) {
        $source = Resolve-Entry $releaseRoot $item.Path
        $destination = Resolve-Entry $targetRoot $item.Path
        $parent = [IO.Path]::GetDirectoryName($destination)
        $null = New-Item -ItemType Directory -Path $parent -Force
        $null = Assert-LocalPath $destination $targetRoot
        Copy-Item -LiteralPath $source -Destination $destination -Force
        if ((Get-FileDigest $destination) -ne $item.SHA256) { throw "部署文件校验失败：$($item.Path)" }
    }
    foreach ($item in $obsolete) {
        $old = Resolve-Entry $targetRoot $item.Path
        if (Test-Path -LiteralPath $old -PathType Leaf) {
            if ((Get-FileDigest $old) -ne $item.SHA256) { throw "清理前旧文件发生变化：$old" }
            Remove-Item -LiteralPath $old -Force
        }
        $parent = [IO.Path]::GetDirectoryName($old)
        while ($parent -ne $targetRoot -and (Test-Path -LiteralPath $parent -PathType Container)) {
            $null = Assert-LocalPath $parent $targetRoot
            if (@(Get-ChildItem -LiteralPath $parent -Force).Count -ne 0) { break }
            Remove-Item -LiteralPath $parent -Force
            $parent = [IO.Path]::GetDirectoryName($parent)
        }
    }
    Copy-Item -LiteralPath $manifestPath -Destination $targetManifest -Force
    if ((Get-FileDigest $targetManifest) -ne $manifestHash) { throw '部署清单校验失败。' }
    Copy-Item -LiteralPath $archive -Destination $targetArchive -Force
    if ((Get-FileDigest $targetArchive) -ne $archiveHash) { throw '部署压缩包校验失败。' }
    if ($legacyArchiveHash) {
        $null = Assert-LocalPath $legacyArchive $desktop
        if ((Get-FileDigest $legacyArchive) -ne $legacyArchiveHash) { throw '旧压缩包在部署期间发生变化，已保留。' }
        Remove-Item -LiteralPath $legacyArchive -Force
    }
    Write-Output ('桌面部署完成，已校验 ' + $manifest.Count + ' 个文件。')
}
