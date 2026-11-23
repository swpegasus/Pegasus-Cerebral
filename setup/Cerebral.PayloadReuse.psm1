Set-StrictMode -Version Latest

function Get-CerebralPayloadInventory {
    param([Parameter(Mandatory)][string]$Root)

    $resolved = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { return @() }
    $prefix = $resolved.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $resolved -File -Recurse -Force -ErrorAction Stop |
        ForEach-Object {
            if (-not $_.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Payload inventory escaped its selected root: $($_.FullName)"
            }
            [pscustomobject]@{
                RelativePath = $_.FullName.Substring($prefix.Length).Replace('/', '\')
                Length = [long]$_.Length
                LastWriteUtcTicks = $_.LastWriteTimeUtc.Ticks
            }
        } | Sort-Object RelativePath)
}

function Test-CerebralPayloadInventoryEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Existing
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container) -or
        -not (Test-Path -LiteralPath $Existing -PathType Container)) { return $false }
    $sourceFiles = @(Get-CerebralPayloadInventory -Root $Source)
    $existingFiles = @(Get-CerebralPayloadInventory -Root $Existing)
    if ($sourceFiles.Count -ne $existingFiles.Count) { return $false }
    for ($index = 0; $index -lt $sourceFiles.Count; $index++) {
        $left = $sourceFiles[$index]; $right = $existingFiles[$index]
        if (-not $left.RelativePath.Equals($right.RelativePath, [StringComparison]::OrdinalIgnoreCase) -or
            $left.Length -ne $right.Length -or $left.LastWriteUtcTicks -ne $right.LastWriteUtcTicks) { return $false }
    }
    return $true
}

function New-CerebralHardLinkPayloadClone {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Existing,
        [Parameter(Mandatory)][string]$Destination
    )

    $source = [IO.Path]::GetFullPath($Existing)
    $target = [IO.Path]::GetFullPath($Destination)
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { return $false }
    if ([IO.Path]::GetPathRoot($source) -ne [IO.Path]::GetPathRoot($target)) { return $false }
    if (Test-Path -LiteralPath $target) { throw "Reusable payload destination already exists: $target" }

    try {
        [IO.Directory]::CreateDirectory($target) | Out-Null
        $sourcePrefix = $source.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        foreach ($directory in Get-ChildItem -LiteralPath $source -Directory -Recurse -Force -ErrorAction Stop) {
            if (-not $directory.FullName.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload directory escaped its source root.' }
            $relative = $directory.FullName.Substring($sourcePrefix.Length)
            [IO.Directory]::CreateDirectory((Join-Path $target $relative)) | Out-Null
        }
        foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse -Force -ErrorAction Stop) {
            if (-not $file.FullName.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload file escaped its source root.' }
            $relative = $file.FullName.Substring($sourcePrefix.Length)
            $destinationFile = Join-Path $target $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationFile)) | Out-Null
            New-Item -ItemType HardLink -Path $destinationFile -Target $file.FullName -ErrorAction Stop | Out-Null
        }
        return $true
    }
    catch {
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue }
        return $false
    }
}

Export-ModuleMember -Function Test-CerebralPayloadInventoryEqual, New-CerebralHardLinkPayloadClone
