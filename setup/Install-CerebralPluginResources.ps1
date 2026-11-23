[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ToolsDirectory,
    [Parameter(Mandatory)][string]$ModelsDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$SharedDirectory,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
trap {
    if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
        $receipt = [pscustomobject]@{ contract='pegasus.cerebral.plugin-resources.install/1'; at=[DateTimeOffset]::UtcNow.ToString('O'); ok=$false; error=$_.Exception.Message }
        $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
    }
    throw
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
if (-not $PackagePath.EndsWith('.CerebralPlugin', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw 'PackagePath must identify an existing .CerebralPlugin package.'
}

function Resolve-ContainedPath([string]$Root, [string]$Relative, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) {
        throw "$Label must be a contained relative path."
    }
    $segments = $Relative.Split([char[]]@('/', '\'))
    if ($segments.Where({ $_ -in @('', '.', '..') }, 'First')) { throw "$Label must be a contained relative path." }
    $rootPath = [IO.Path]::GetFullPath($Root)
    $candidate = [IO.Path]::GetFullPath((Join-Path $rootPath $Relative))
    $prefix = $rootPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "$Label escaped its configured root." }
    return $candidate
}

Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::OpenRead($PackagePath)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        if ($archive.Entries.Count -lt 2 -or $archive.Entries.Count -gt 2048) { throw 'The plugin package has an unsupported entry count.' }
        $manifestEntries = @($archive.Entries | Where-Object { $_.FullName -ceq 'cerebral-plugin.json' })
        if ($manifestEntries.Count -ne 1 -or $manifestEntries[0].Length -gt 64KB) { throw 'The plugin package manifest is missing or invalid.' }
        $reader = [IO.StreamReader]::new($manifestEntries[0].Open(), [Text.UTF8Encoding]::new($false, $true), $true)
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.schemaVersion -ne 2 -or $manifest.contract -cne 'pegasus.cerebral.plugin' -or
            $manifest.kind -cne 'desktop' -or $null -eq $manifest.resources) {
            throw 'The plugin package does not declare a supported resource contract.'
        }

        $roots = @{
            tool = [IO.Path]::GetFullPath($ToolsDirectory)
            model = [IO.Path]::GetFullPath($ModelsDirectory)
            data = [IO.Path]::GetFullPath($DataDirectory)
            shared = [IO.Path]::GetFullPath($SharedDirectory)
        }
        $transaction = [Guid]::NewGuid().ToString('N')
        $pending = [Collections.Generic.List[object]]::new()
        $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $expanded = [long]0

        try {
            foreach ($resource in @($manifest.resources)) {
                if ([string]::IsNullOrWhiteSpace($resource.id) -or -not $ids.Add([string]$resource.id) -or
                    -not $roots.ContainsKey([string]$resource.kind)) {
                    throw 'Plugin resource IDs must be unique and use a supported resource kind.'
                }
                $source = ([string]$resource.source).Replace('\', '/').TrimEnd('/')
                if ([string]::IsNullOrWhiteSpace($source) -or $source.Split('/').Where({ $_ -in @('', '.', '..') }, 'First')) {
                    throw 'Plugin resource sources must be contained relative paths.'
                }
                $destination = Resolve-ContainedPath $roots[[string]$resource.kind] ([string]$resource.destination) 'Plugin resource destination'
                if (-not $destinations.Add($destination)) { throw 'Plugin resource destinations must be unique.' }
                if (Test-Path -LiteralPath $destination -PathType Leaf) { throw "Plugin resource destination is a file: $destination" }
                $stage = $destination + '.staging-' + $transaction
                $previous = $destination + '.previous-' + $transaction
                if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $previous)) { throw 'A plugin resource staging path already exists.' }

                $prefix = 'plugin/' + $source + '/'
                $matches = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') -and $_.FullName.StartsWith($prefix, [StringComparison]::Ordinal) })
                if ($matches.Count -eq 0) { throw "Plugin resource source is empty or missing: $($resource.id)." }
                [IO.Directory]::CreateDirectory($stage) | Out-Null
                $pending.Add([pscustomobject]@{ Id=[string]$resource.id; Destination=$destination; Stage=$stage; Previous=$previous; OriginalMoved=$false; Published=$false; Files=$matches.Count })
                foreach ($entry in $matches) {
                    $relative = $entry.FullName.Substring($prefix.Length)
                    $target = Resolve-ContainedPath $stage $relative 'Plugin resource entry'
                    $expanded += [long]$entry.Length
                    if ($expanded -gt 4GB) { throw 'The expanded plugin resources exceed 4 GiB.' }
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                    $input = $entry.Open()
                    try {
                        $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try { $input.CopyTo($output) } finally { $output.Dispose() }
                    } finally { $input.Dispose() }
                    if ((Get-Item -LiteralPath $target).Length -ne $entry.Length) { throw "Plugin resource extraction was incomplete: $relative" }
                }
            }

            try {
                foreach ($item in $pending) {
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.Destination)) | Out-Null
                    if (Test-Path -LiteralPath $item.Destination -PathType Container) {
                        [IO.Directory]::Move($item.Destination, $item.Previous)
                        $item.OriginalMoved = $true
                    }
                    [IO.Directory]::Move($item.Stage, $item.Destination)
                    $item.Published = $true
                }
            } catch {
                for ($index = $pending.Count - 1; $index -ge 0; $index--) {
                    $item = $pending[$index]
                    if ($item.Published -and (Test-Path -LiteralPath $item.Destination -PathType Container)) {
                        Remove-Item -LiteralPath $item.Destination -Recurse -Force
                    }
                    if ($item.OriginalMoved) { [IO.Directory]::Move($item.Previous, $item.Destination) }
                }
                throw
            }
        } finally {
            foreach ($item in $pending) {
                if (Test-Path -LiteralPath $item.Stage -PathType Container) { Remove-Item -LiteralPath $item.Stage -Recurse -Force }
            }
        }

        # All resources are published. A cleanup fault must not undo the commit
        # after any of the original directories have already been removed.
        foreach ($item in $pending) {
            if ($item.OriginalMoved) { Remove-Item -LiteralPath $item.Previous -Recurse -Force }
        }

        $result = [pscustomobject]@{
            contract = 'pegasus.cerebral.plugin-resources.install/1'
            at = [DateTimeOffset]::UtcNow.ToString('O')
            ok = $true
            pluginId = [string]$manifest.id
            resources = @($pending | ForEach-Object { [ordered]@{ id=$_.Id; destination=$_.Destination; files=$_.Files } })
            expandedBytes = $expanded
        }
        if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
            $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
            [IO.Directory]::CreateDirectory($parent) | Out-Null
            $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
        }
        $result | ConvertTo-Json -Depth 5 -Compress
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
