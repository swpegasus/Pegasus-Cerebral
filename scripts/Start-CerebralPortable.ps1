[CmdletBinding()]
param([switch]$VerifyOnly)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$marker = Join-Path $root 'installation.ini'
$manifestPath = Join-Path $root 'portable-manifest.json'
if (-not [IO.File]::Exists($marker) -or -not [IO.File]::Exists($manifestPath)) {
    throw 'The portable package is incomplete. Extract the entire archive into one folder.'
}
$markerText = [IO.File]::ReadAllText($marker)
if ($markerText -notmatch '(?im)^Product=PegasusCerebral\r?$' -or
    $markerText -notmatch '(?im)^Portable=1\r?$') {
    throw 'This folder is not a Pegasus Cerebral portable package.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.contract -cne 'pegasus.cerebral.portable/1' -or $manifest.product -cne 'PegasusCerebral' -or
    $manifest.platform -cne 'win-x64' -or $manifest.mode -cne 'portable') {
    throw 'The portable package manifest is unsupported.'
}
$roots = [ordered]@{ data = 'Data'; models = 'Models'; tools = 'Tools'; projects = 'Projects'; shared = 'Shared'; userData = 'UserData' }
foreach ($name in $roots.Keys) {
    if ($manifest.roots.$name -cne $roots[$name]) { throw "The portable $name root is invalid." }
}
if ($manifest.roots.program -cne '.') { throw 'The portable program root is invalid.' }
$programDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in @($manifest.directories)) {
    $relative = [string]$relative
    $parts = $relative -split '/'
    if ($relative.Length -gt 32768 -or $parts.Count -lt 1 -or $parts[0] -cnotin @('Applications','Plugins','Resources') -or
        @($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.Contains('\') }).Count -gt 0 -or
        -not $programDirectories.Add($relative)) { throw 'The portable package has an invalid or repeated directory entry.' }
    $path = [IO.Path]::GetFullPath((Join-Path $root ($relative.Replace('/', '\'))))
    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Directory]::Exists($path) -or
        ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "A portable program directory is missing or linked: $relative"
    }
}
foreach ($top in @('Applications','Plugins','Resources')) {
    if (-not $programDirectories.Contains($top)) { throw "The portable $top directory is undeclared." }
}
$bootstrapNames = @('Start-CerebralPortable.cmd', 'Start-CerebralPortable.ps1', 'installation.ini')
if (@($manifest.bootstrap).Count -ne $bootstrapNames.Count) { throw 'The portable bootstrap declaration is incomplete.' }
foreach ($entry in @($manifest.bootstrap)) {
    $name = [string]$entry.path
    if ($name -cnotin $bootstrapNames -or $entry.bytes -lt 0 -or
        [string]$entry.sha256 -cnotmatch '^[a-f0-9]{64}$') {
        throw 'The portable bootstrap declaration is invalid.'
    }
    $path = Join-Path $root $name
    if (-not [IO.File]::Exists($path)) { throw "A portable bootstrap file is missing: $name" }
    $item = Get-Item -LiteralPath $path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -ne [long]$entry.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$entry.sha256) {
        throw "A portable bootstrap file changed: $name"
    }
}
if (@($manifest.bootstrap | Select-Object -ExpandProperty path -Unique).Count -ne $bootstrapNames.Count) {
    throw 'The portable bootstrap declaration repeats a file.'
}
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($manifest.files)) {
    $relative = [string]$entry.path
    $parts = $relative -split '/'
    if ($relative.Length -gt 32768 -or $parts.Count -lt 2 -or $parts[0] -cnotin @('Applications','Plugins','Resources') -or
        @($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.Contains('\') }).Count -gt 0 -or
        $entry.bytes -lt 0 -or [string]$entry.sha256 -cnotmatch '^[a-f0-9]{64}$' -or -not $expected.Add($relative)) {
        throw 'The portable package has an invalid or repeated file entry.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root ($relative.Replace('/', '\'))))
    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($path)) {
        throw "A portable program file is missing: $relative"
    }
    for ($parent = [IO.Path]::GetDirectoryName($path); $parent -and $parent.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase);
        $parent = [IO.Path]::GetDirectoryName($parent)) {
        if (([IO.File]::GetAttributes($parent) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The portable program path is linked: $relative"
        }
    }
    $item = Get-Item -LiteralPath $path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -ne [long]$entry.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$entry.sha256) {
        throw "A portable program file changed: $relative"
    }
}
foreach ($section in @('Applications','Plugins','Resources')) {
    foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $root $section) -Directory -Recurse -Force) {
        $relative = $directory.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not $programDirectories.Contains($relative)) { throw "The portable program folder contains an undeclared directory: $relative" }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $section) -File -Recurse -Force) {
        if (-not $file.FullName.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "A portable program path escaped the package: $($file.FullName)"
        }
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not $expected.Contains($relative)) { throw "The portable program folder contains an undeclared file: $relative" }
    }
}
if ($VerifyOnly) {
    [pscustomobject]@{ root = $root; contract = $manifest.contract; programFiles = $expected.Count; verified = $true }
    return
}
foreach ($name in $roots.Values) {
    $path = Join-Path $root $name
    if ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path) -and
        ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The portable $name root is not an ordinary directory."
    }
    [IO.Directory]::CreateDirectory($path) | Out-Null
}
$configuration = Join-Path $root 'Applications\Desktop\Cerebral.Integrator.exe'
Start-Process -FilePath $configuration -ArgumentList '--portable-package' -WorkingDirectory ([IO.Path]::GetDirectoryName($configuration))
