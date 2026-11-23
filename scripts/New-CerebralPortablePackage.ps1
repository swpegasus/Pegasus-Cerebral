[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$payload = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\deploy\win-x64')).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputPath)
$parent = [IO.Path]::GetDirectoryName($output)
if (-not $parent -or $output -eq [IO.Path]::GetPathRoot($output) -or
    -not $output.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a named .zip file in an existing directory.'
}
if (-not [IO.Directory]::Exists($parent) -or [IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'The output directory must exist and the package path must be unused.'
}
if ($output.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The package cannot be written inside its payload.'
}
if (-not [IO.Directory]::Exists($payload)) { throw "Published Windows payload is missing: $payload" }
foreach ($relative in @('Applications\Worker\Cerebral.Worker.exe',
        'Applications\Desktop\Cerebral.Integrator.exe',
        'Applications\Desktop\Cerebral.Orchestrator.exe')) {
    if (-not [IO.File]::Exists((Join-Path $payload $relative))) {
        throw "Published Windows payload is incomplete: $relative"
    }
}
$allowed = @('Applications', 'Plugins', 'Resources')
$topLevel = @(Get-ChildItem -LiteralPath $payload -Force)
if (@($topLevel | Where-Object { -not $_.PSIsContainer -or $_.Name -notin $allowed }).Count -ne 0 -or
    @($allowed | Where-Object { $_ -notin @($topLevel.Name) }).Count -ne 0) {
    throw 'The portable payload must contain only Applications, Plugins, and Resources; mutable data and profiles cannot be packaged.'
}
$all = @(Get-ChildItem -LiteralPath $payload -Recurse -Force)
$files = @($all | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
$directories = @($all | Where-Object { $_.PSIsContainer } | Sort-Object FullName |
    ForEach-Object { $_.FullName.Substring($payload.Length + 1).Replace('\', '/') })
foreach ($entry in @((Get-Item -LiteralPath $payload)) + $all) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The portable package cannot contain a linked path: $($entry.FullName)"
    }
}
$entries = [Collections.Generic.List[object]]::new()
foreach ($file in $files) {
    if (-not $file.FullName.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "A payload path escaped the published root: $($file.FullName)"
    }
    $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
    if ($relative.StartsWith('../', [StringComparison]::Ordinal) -or $relative.Contains('/../')) {
        throw "A payload path escaped the published root: $relative"
    }
    $entries.Add([pscustomobject]@{
        path = $relative
        bytes = [long]$file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}
$marker = "[Cerebral]`r`nProduct=PegasusCerebral`r`nPortable=1`r`nBaseDir=.`r`nDataDir=Data`r`nModelsDir=Models`r`nToolsDir=Tools`r`nProjectsDir=Projects`r`nSharedDir=Shared`r`nUserDataDir=UserData`r`n"
$bootstrap = [Collections.Generic.List[object]]::new()
foreach ($name in @('Start-CerebralPortable.cmd', 'Start-CerebralPortable.ps1')) {
    $path = Join-Path $PSScriptRoot $name
    if (-not [IO.File]::Exists($path)) { throw "The portable launcher is missing: $path" }
    $bootstrap.Add([pscustomobject]@{
        path = $name
        bytes = [long](Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}
$markerBytes = [Text.Encoding]::UTF8.GetBytes($marker)
$markerHash = [Security.Cryptography.SHA256]::Create()
try { $markerDigest = $markerHash.ComputeHash($markerBytes) } finally { $markerHash.Dispose() }
$bootstrap.Add([pscustomobject]@{
    path = 'installation.ini'
    bytes = [long]$markerBytes.Length
    sha256 = [BitConverter]::ToString($markerDigest).Replace('-', '').ToLowerInvariant()
})
$manifest = [pscustomobject]@{
    contract = 'pegasus.cerebral.portable/1'
    product = 'PegasusCerebral'
    platform = 'win-x64'
    mode = 'portable'
    roots = [ordered]@{ program = '.'; data = 'Data'; models = 'Models'; tools = 'Tools'; projects = 'Projects'; shared = 'Shared'; userData = 'UserData' }
    prerequisites = @('Microsoft .NET 10 Desktop Runtime x64', 'Microsoft Visual C++ Runtime x64')
    directories = $directories
    files = $entries.ToArray()
    bootstrap = $bootstrap.ToArray()
}
$temporary = Join-Path $parent ('.cerebral-portable-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($directory in $directories) { $null = $archive.CreateEntry($directory + '/') }
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $input = [IO.File]::OpenRead($file.FullName)
                try {
                    $destination = $entry.Open()
                    try { $input.CopyTo($destination) } finally { $destination.Dispose() }
                } finally { $input.Dispose() }
            }
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
                $pinned = @($entries | Where-Object path -CEQ $relative)
                if ($pinned.Count -ne 1 -or $file.Length -ne $pinned[0].bytes -or
                    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pinned[0].sha256) {
                    throw "The published payload changed while packaging: $relative"
                }
            }
            foreach ($directory in @('Data/', 'Models/', 'Tools/', 'Projects/', 'Shared/', 'UserData/')) {
                $null = $archive.CreateEntry($directory)
            }
            foreach ($launcherName in @('Start-CerebralPortable.cmd','Start-CerebralPortable.ps1')) {
                $source = Join-Path $PSScriptRoot $launcherName
                $pinned = @($bootstrap | Where-Object path -CEQ $launcherName)
                if ($pinned.Count -ne 1 -or (Get-Item -LiteralPath $source).Length -ne $pinned[0].bytes -or
                    (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pinned[0].sha256) {
                    throw "The portable launcher changed while packaging: $launcherName"
                }
                $entry = $archive.CreateEntry($launcherName)
                $input = [IO.File]::OpenRead($source)
                try {
                    $destination = $entry.Open()
                    try { $input.CopyTo($destination) } finally { $destination.Dispose() }
                } finally { $input.Dispose() }
            }
            $entry = $archive.CreateEntry('installation.ini')
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($marker) } finally { $writer.Dispose() }
            $entry = $archive.CreateEntry('portable-manifest.json')
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write(($manifest | ConvertTo-Json -Depth 7 -Compress)) } finally { $writer.Dispose() }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    [IO.File]::Move($temporary, $output)
} finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
[pscustomobject]@{ path = $output; files = $entries.Count; bytes = [long](Get-Item -LiteralPath $output).Length }
