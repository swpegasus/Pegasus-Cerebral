[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..\deploy\win-x64\Plugins\Desktop'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\deploy\Plugins'),
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..'),
    [string]$ResourceBindingsPath = (Join-Path $PSScriptRoot '..\setup\plugin-package-resources.json')
)

$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$ResourceBindingsPath = [IO.Path]::GetFullPath($ResourceBindingsPath)
$maximumEntries = 2048
$maximumDirectories = 512
$maximumDepth = 16
$maximumExpandedBytes = 4GB

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "Desktop plugin source folder does not exist: $SourceRoot"
}
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

function Assert-OrdinaryPath([IO.FileSystemInfo]$Item) {
    if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Plugin packages cannot contain links or reparse points: $($Item.FullName)"
    }
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

$bindings = @()
if (Test-Path -LiteralPath $ResourceBindingsPath -PathType Leaf) {
    $bindingDocument = Get-Content -LiteralPath $ResourceBindingsPath -Raw | ConvertFrom-Json
    if ($bindingDocument.schemaVersion -ne 1 -or $null -eq $bindingDocument.bindings) {
        throw 'Unsupported plugin resource binding manifest.'
    }
    $bindings = @($bindingDocument.bindings)
}

$built = @()
foreach ($directory in Get-ChildItem -LiteralPath $SourceRoot -Directory | Sort-Object Name) {
    Assert-OrdinaryPath $directory
    $manifestPath = Join-Path $directory.FullName 'desktop-plugin.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Desktop plugin $($directory.Name) is missing desktop-plugin.json."
    }
    $desktop = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($desktop.schemaVersion -notin 1, 2, 3 -or $desktop.contract -cne 'pegasus.cerebral.desktop' -or
        $desktop.apiVersion -ne 1 -or [string]::IsNullOrWhiteSpace($desktop.id) -or
        $desktop.id -cne $directory.Name -or [string]::IsNullOrWhiteSpace($desktop.name) -or
        [string]::IsNullOrWhiteSpace($desktop.version) -or [string]::IsNullOrWhiteSpace($desktop.description)) {
        throw "Desktop plugin identity is invalid or does not match its folder: $($directory.FullName)"
    }

    $files = @(Get-ChildItem -LiteralPath $directory.FullName -File -Recurse | Sort-Object FullName)
    $resourceManifestPath = Join-Path $directory.FullName 'plugin-resources.json'
    $resources = @()
    if (Test-Path -LiteralPath $resourceManifestPath -PathType Leaf) {
        $resourceDocument = Get-Content -LiteralPath $resourceManifestPath -Raw | ConvertFrom-Json
        if ($resourceDocument.schemaVersion -ne 1 -or $null -eq $resourceDocument.resources) {
            throw "Unsupported plugin resource manifest: $resourceManifestPath"
        }
        $resources = @($resourceDocument.resources)
    }

    $archiveSources = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        Assert-OrdinaryPath $file
        $relative = [IO.Path]::GetRelativePath($directory.FullName, $file.FullName).Replace('\', '/')
        $archiveSources.Add([pscustomobject]@{ Entry = "plugin/$relative"; Path = $file.FullName; Resource = $false })
    }

    $resourceIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($resource in $resources) {
        if ([string]::IsNullOrWhiteSpace($resource.id) -or -not $resourceIds.Add([string]$resource.id) -or
            $resource.kind -notin @('tool','model','data','shared')) {
            throw "Plugin $($desktop.id) has an invalid or duplicate resource declaration."
        }
        $resourceSource = [string]$resource.source
        [void](Resolve-ContainedPath $directory.FullName $resourceSource 'Plugin resource source')
        [void](Resolve-ContainedPath $directory.FullName ([string]$resource.destination) 'Plugin resource destination')
        $matchingBindings = @($bindings | Where-Object { $_.pluginId -ceq $desktop.id -and $_.resourceId -ceq $resource.id })
        if ($matchingBindings.Count -ne 1) {
            throw "Plugin resource $($desktop.id)/$($resource.id) requires exactly one build binding."
        }
        $binding = $matchingBindings[0]
        $source = Resolve-ContainedPath $ProjectRoot ([string]$binding.source) 'Plugin resource build source'
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Plugin resource source is missing: $source" }
        Assert-OrdinaryPath (Get-Item -LiteralPath $source)
        foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName) {
            Assert-OrdinaryPath $file
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
            $archiveSources.Add([pscustomobject]@{ Entry = "plugin/$($resourceSource.TrimEnd([char[]]@('/','\')))/$relative"; Path = $file.FullName; Resource = $true })
        }
        $additionalFiles = if ($null -eq $binding.PSObject.Properties['additionalFiles']) { @() } else { @($binding.additionalFiles) }
        foreach ($additional in $additionalFiles) {
            $additionalSource = Resolve-ContainedPath $ProjectRoot ([string]$additional.source) 'Additional plugin resource source'
            if (-not (Test-Path -LiteralPath $additionalSource -PathType Leaf)) { throw "Additional plugin resource file is missing: $additionalSource" }
            Assert-OrdinaryPath (Get-Item -LiteralPath $additionalSource)
            $additionalDestination = [string]$additional.destination
            [void](Resolve-ContainedPath $directory.FullName $additionalDestination 'Additional plugin resource destination')
            $archiveSources.Add([pscustomobject]@{ Entry = "plugin/$($resourceSource.TrimEnd([char[]]@('/','\')))/$($additionalDestination.Replace('\','/'))"; Path = $additionalSource; Resource = $true })
        }
    }

    if ($archiveSources.Count -lt 1 -or $archiveSources.Count -gt ($maximumEntries - 1)) {
        throw "Desktop plugin $($desktop.id) has an unsupported file count."
    }
    $entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $expanded = [long]0
    foreach ($source in $archiveSources) {
        if (-not $entryNames.Add([string]$source.Entry)) { throw "Desktop plugin $($desktop.id) has overlapping package entries." }
        $parts = ([string]$source.Entry).Split('/')
        if ($parts.Count -gt $maximumDepth) { throw "Desktop plugin $($desktop.id) exceeds the package depth limit." }
        for ($index = 1; $index -lt $parts.Count; $index++) { [void]$directories.Add(($parts[0..($index - 1)] -join '/')) }
        if ($directories.Count -gt $maximumDirectories) { throw "Desktop plugin $($desktop.id) exceeds the package directory limit." }
        $expanded += [long](Get-Item -LiteralPath $source.Path).Length
        if ($expanded -gt $maximumExpandedBytes) { throw "Desktop plugin $($desktop.id) exceeds 4 GiB expanded." }
    }

    $safeId = $desktop.id -replace '[^A-Za-z0-9._-]', '-'
    $safeVersion = $desktop.version -replace '[^A-Za-z0-9._-]', '-'
    $destination = Join-Path $OutputRoot "$safeId-$safeVersion.CerebralPlugin"
    $temporary = "$destination.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
            try {
                $outer = [ordered]@{
                    schemaVersion = $(if ($resources.Count -eq 0) { 1 } else { 2 })
                    contract = 'pegasus.cerebral.plugin'
                    kind = 'desktop'
                    id = [string]$desktop.id
                    name = [string]$desktop.name
                    version = [string]$desktop.version
                    description = [string]$desktop.description
                    payload = 'plugin/desktop-plugin.json'
                    settingsMode = 'none'
                }
                if ($resources.Count -ne 0) { $outer.resources = $resources }
                $entry = $archive.CreateEntry('cerebral-plugin.json', [IO.Compression.CompressionLevel]::Optimal)
                $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
                try { $writer.Write(($outer | ConvertTo-Json -Depth 8)) } finally { $writer.Dispose() }
                foreach ($source in $archiveSources) {
                    $compression = if ($source.Resource) { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
                    $entry = $archive.CreateEntry([string]$source.Entry, $compression)
                    $input = [IO.File]::OpenRead([string]$source.Path)
                    $target = $entry.Open()
                    try { $input.CopyTo($target) } finally { $target.Dispose(); $input.Dispose() }
                }
            } finally { $archive.Dispose() }
        } finally { $stream.Dispose() }
        foreach ($oldPackage in Get-ChildItem -LiteralPath $OutputRoot -File -Filter "$safeId-*.CerebralPlugin") {
            if ($oldPackage.FullName -cne $destination) { Remove-Item -LiteralPath $oldPackage.FullName -Force }
        }
        [IO.File]::Move($temporary, $destination, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    $built += Get-Item -LiteralPath $destination
}

if ($built.Count -eq 0) { throw 'No published desktop plugins were found to package.' }
$built | Select-Object FullName, Length
