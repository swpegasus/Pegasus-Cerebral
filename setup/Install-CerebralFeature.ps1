[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [Parameter(Mandatory)][string]$ExpectedId,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
trap {
    if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
        $receipt = [pscustomobject]@{ contract='pegasus.cerebral.feature-install/1'; at=[DateTimeOffset]::UtcNow.ToString('O'); ok=$false; id=$ExpectedId; error=$_.Exception.Message }
        $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
    }
    throw $_
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "Feature package is missing: $PackagePath" }
if ($DestinationRoot -eq [IO.Path]::GetPathRoot($DestinationRoot)) { throw 'DestinationRoot must not be a drive root.' }
if ($ExpectedId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or $ExpectedId.Length -gt 128) { throw 'ExpectedId is invalid.' }

function Get-FeatureDirectory([string]$Path) {
    $parent = [IO.Path]::GetDirectoryName($Path)
    if ($null -ne $parent) { $null = Get-FeatureDirectory $parent }
    try { $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop }
    catch [System.Management.Automation.ItemNotFoundException] { return $null }
    if ($item -isnot [IO.DirectoryInfo] -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Feature destination must be a real directory. Resolve the existing item before retrying: $Path"
    }
    return $item
}

Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::OpenRead($PackagePath)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        if ($archive.Entries.Count -gt 2048) { throw 'Feature package contains too many entries.' }
        $manifestEntry = $archive.GetEntry('feature.json')
        if ($null -eq $manifestEntry -or $manifestEntry.Length -gt 64KB) { throw 'Feature package manifest is missing or oversized.' }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.contract -cne 'pegasus.cerebral.installer-feature' -or $manifest.schemaVersion -ne 1 -or $manifest.id -cne $ExpectedId) {
            throw 'Feature package identity does not match the selected component.'
        }

        $prefixes = @(
            "Plugins/Service/$ExpectedId/",
            "Plugins/Desktop/$ExpectedId/"
        )
        $files = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') -and $_.FullName -cne 'feature.json' })
        if ($files.Count -eq 0) { throw 'Feature package contains no plugin payload.' }
        [long]$expanded = 0
        foreach ($entry in $files) {
            $name = $entry.FullName.Replace('\','/')
            if ($name.StartsWith('/') -or $name.Contains('../') -or $name.Contains('/./') -or $name.Contains(':') -or
                -not $prefixes.Where({ $name.StartsWith($_, [StringComparison]::Ordinal) }, 'First')) {
                throw "Feature package entry is outside its declared plugin roots: $name"
            }
            $expanded += $entry.Length
            if ($expanded -gt 1GB) { throw 'Feature package expands beyond the 1 GiB limit.' }
        }

        # Directory.Move can rename a file too. Check every affected role before
        # either can be displaced, and refuse destinations reached through links.
        $publishPrefixes = @($prefixes | Where-Object {
            $prefix = $_
            $files.Where({ $_.FullName.Replace('\','/').StartsWith($prefix, [StringComparison]::Ordinal) }, 'First').Count -gt 0
        })
        foreach ($prefix in $publishPrefixes) {
            $relativeDirectory = $prefix.TrimEnd('/').Replace('/', [IO.Path]::DirectorySeparatorChar)
            $null = Get-FeatureDirectory (Join-Path $DestinationRoot $relativeDirectory)
        }

        [IO.Directory]::CreateDirectory($DestinationRoot) | Out-Null
        $transaction = [Guid]::NewGuid().ToString('N')
        $stageRoot = Join-Path $DestinationRoot ('.feature-stage-' + $transaction)
        $previousRoot = Join-Path $DestinationRoot ('.feature-previous-' + $transaction)
        $retainRecovery = $false
        [IO.Directory]::CreateDirectory($stageRoot) | Out-Null
        [IO.Directory]::CreateDirectory($previousRoot) | Out-Null
        try {
            foreach ($entry in $files) {
                $relative = $entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
                $destination = [IO.Path]::GetFullPath((Join-Path $stageRoot $relative))
                if (-not $destination.StartsWith($stageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Feature package entry escaped the staging directory.'
                }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                $input = $entry.Open()
                $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }

            $published = [Collections.Generic.List[object]]::new()
            try {
                foreach ($prefix in $publishPrefixes) {
                    $relativeDirectory = $prefix.TrimEnd('/').Replace('/', [IO.Path]::DirectorySeparatorChar)
                    $source = Join-Path $stageRoot $relativeDirectory
                    if (-not (Test-Path -LiteralPath $source -PathType Container)) { continue }
                    $destination = Join-Path $DestinationRoot $relativeDirectory
                    $previous = Join-Path $previousRoot $relativeDirectory
                    $existing = Get-FeatureDirectory $destination
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($previous)) | Out-Null
                    # Record the entry before displacement so a failed publication
                    # can restore it without deleting an entry whose old move failed.
                    $item = [pscustomobject]@{ Destination=$destination; Previous=$previous; PreviousMoved=$false; NewPayload=$false }
                    $published.Add($item)
                    if ($null -ne $existing) {
                        [IO.Directory]::Move($destination, $previous)
                        $item.PreviousMoved = $true
                    }
                    [IO.Directory]::Move($source, $destination)
                    $item.NewPayload = $true
                }
            }
            catch {
                $publishError = $_
                $rollbackErrors = [Collections.Generic.List[Exception]]::new()
                for ($index = $published.Count - 1; $index -ge 0; $index--) {
                    $item = $published[$index]
                    try {
                        if ($item.NewPayload -and (Test-Path -LiteralPath $item.Destination)) { Remove-Item -LiteralPath $item.Destination -Recurse -Force }
                        if ($item.PreviousMoved) {
                            if (-not (Test-Path -LiteralPath $item.Previous -PathType Container)) {
                                throw [IO.IOException]::new("The previous feature entry is missing: $($item.Previous)")
                            }
                            [IO.Directory]::Move($item.Previous, $item.Destination)
                        }
                    }
                    catch { $rollbackErrors.Add($_.Exception) }
                }
                if ($rollbackErrors.Count -ne 0) {
                    $retainRecovery = $true
                    throw [AggregateException]::new("Cerebral feature publication failed and rollback was incomplete. Recovery artifacts were retained at '$stageRoot' and '$previousRoot'.", @($publishError.Exception) + @($rollbackErrors))
                }
                throw
            }
        }
        finally {
            if (-not $retainRecovery) {
                if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
                if (Test-Path -LiteralPath $previousRoot) { Remove-Item -LiteralPath $previousRoot -Recurse -Force }
            }
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }

$result = [pscustomobject]@{ contract='pegasus.cerebral.feature-install/1'; at=[DateTimeOffset]::UtcNow.ToString('O'); ok=$true; id=$ExpectedId; destination=$DestinationRoot }
if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
}
$result | ConvertTo-Json -Compress
