Set-StrictMode -Version Latest

$script:CerebralBehaviorPackSpecifications = @(
    [pscustomobject]@{
        Directory = 'cerebral-guides'
        PackId = 'pegasus-cerebral-guides-v1'
        RecordPrefix = 'Knowledge.Cerebral.Guide'
        RecordCount = 8
    }
    [pscustomobject]@{
        Directory = 'windows-operational-knowledge'
        PackId = 'windows-operational-knowledge-v1'
        RecordPrefix = 'Knowledge.Windows'
        RecordCount = 8
    }
)

function Assert-CerebralBehaviorPackNoReparsePoints {
    param([Parameter(Mandatory)][string]$Path)

    $root = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Cortex behavior-pack path is a reparse point: $Path"
    }
    $linked = Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($null -ne $linked) {
        throw "Cortex behavior-pack payload contains a reparse point: $($linked.FullName)"
    }
}

function Test-CerebralBehaviorPackRelativePath {
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or
        $Path.Contains(':') -or $Path.Contains('\') -or $Path.StartsWith('/') -or
        $Path.EndsWith('/')) {
        return $false
    }
    $segments = $Path.Split('/')
    return -not $segments.Where({ [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') }, 'First')
}

function Get-CerebralBehaviorPackInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root)

    $sourceRoot = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Cortex behavior-pack root does not exist: $sourceRoot"
    }
    Assert-CerebralBehaviorPackNoReparsePoints -Path $sourceRoot

    $inventory = [Collections.Generic.List[object]]::new()
    foreach ($specification in $script:CerebralBehaviorPackSpecifications) {
        $packRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot $specification.Directory))
        $rootPrefix = $packRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not (Test-Path -LiteralPath $packRoot -PathType Container)) {
            throw "Required Cortex behavior pack is missing: $($specification.Directory)"
        }
        Assert-CerebralBehaviorPackNoReparsePoints -Path $packRoot

        $manifestPath = Join-Path $packRoot 'behavior-pack.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
            (Get-Item -LiteralPath $manifestPath).Length -gt 256KB) {
            throw "Cortex behavior-pack manifest is missing or oversized: $manifestPath"
        }
        try { $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { throw "Cortex behavior-pack manifest is invalid JSON: $manifestPath. $($_.Exception.Message)" }

        $revisionProperty = $manifest.PSObject.Properties['packRevision']
        if ($manifest.schema -cne 'pegasus.cortex.behavior-pack/2.0' -or
            $manifest.packId -cne $specification.PackId -or
            $null -eq $revisionProperty -or
            ($revisionProperty.Value -isnot [int] -and $revisionProperty.Value -isnot [long]) -or
            [long]$revisionProperty.Value -lt 1 -or [long]$revisionProperty.Value -gt 1000000 -or
            $null -eq $manifest.PSObject.Properties['includesTasks'] -or
            $manifest.includesTasks -isnot [bool] -or $manifest.includesTasks -or
            $null -eq $manifest.PSObject.Properties['includesMachineHistory'] -or
            $manifest.includesMachineHistory -isnot [bool] -or $manifest.includesMachineHistory) {
            throw "Cortex behavior pack has an unsupported identity or revision, or includes prohibited state: $manifestPath"
        }

        $records = @($manifest.records)
        if ($records.Count -ne $specification.RecordCount) {
            throw "Cortex behavior pack '$($specification.PackId)' must contain exactly $($specification.RecordCount) records; found $($records.Count)."
        }
        $recordIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $contentPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $expectedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        [void]$expectedFiles.Add('behavior-pack.json')

        foreach ($record in $records) {
            $recordId = [string]$record.recordId
            $prefix = [string]$specification.RecordPrefix
            if ([string]::IsNullOrWhiteSpace($recordId) -or
                ($recordId -cne $prefix -and -not $recordId.StartsWith($prefix + '.', [StringComparison]::Ordinal)) -or
                -not $recordIds.Add($recordId)) {
                throw "Cortex behavior pack contains an unsafe, out-of-module, or duplicate record ID: $recordId"
            }
            if ([string]$record.kind -cne 'tenet' -or [string]$record.tier -cne 'explicit' -or
                [string]::IsNullOrWhiteSpace([string]$record.title) -or
                [string]$record.source -cnotin @('user', 'agent') -or
                'generalized' -cnotin @($record.tags)) {
                throw "Cortex behavior pack contains a non-guide or unsupported provenance record: $recordId"
            }
            $contentPath = [string]$record.contentPath
            if (-not (Test-CerebralBehaviorPackRelativePath -Path $contentPath) -or
                -not $contentPath.StartsWith('records/', [StringComparison]::Ordinal) -or
                [IO.Path]::GetExtension($contentPath) -cne '.md' -or
                -not $contentPaths.Add($contentPath)) {
                throw "Cortex behavior pack contains an unsafe or duplicate content path: $contentPath"
            }
            $contentFile = [IO.Path]::GetFullPath((Join-Path $packRoot $contentPath.Replace('/', [IO.Path]::DirectorySeparatorChar)))
            if (-not $contentFile.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $contentFile -PathType Leaf) -or
                (Get-Item -LiteralPath $contentFile).Length -le 0 -or
                (Get-Item -LiteralPath $contentFile).Length -gt 256KB) {
                throw "Cortex behavior-pack content is missing, empty, oversized, or escaped its module: $contentPath"
            }
            [void]$expectedFiles.Add($contentPath)
        }

        $files = @(Get-ChildItem -LiteralPath $packRoot -File -Recurse -Force -ErrorAction Stop)
        $actualFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($packRoot, $file.FullName).Replace('\', '/')
            if (-not $actualFiles.Add($relative) -or -not $expectedFiles.Contains($relative)) {
                throw "Cortex behavior pack contains an undeclared or duplicate file: $relative"
            }
        }
        if ($actualFiles.Count -ne $expectedFiles.Count) {
            throw "Cortex behavior pack does not contain exactly its declared files: $($specification.PackId)"
        }

        $inventory.Add([pscustomobject]@{
            Directory = [string]$specification.Directory
            PackId = [string]$specification.PackId
            PackRevision = [long]$manifest.packRevision
            Namespace = 'knowledge.shared'
            Records = $records.Count
            Files = $files.Count
            Bytes = [long](($files | Measure-Object Length -Sum).Sum)
            SelectedByDefault = $true
            Root = $packRoot
        })
    }
    return @($inventory)
}

Export-ModuleMember -Function Get-CerebralBehaviorPackInventory
