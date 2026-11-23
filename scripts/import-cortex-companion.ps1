[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [string]$DestinationDirectory = (Join-Path $PSScriptRoot '..\deploy\Pegasus-Cortex-Setup')
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = [IO.Path]::GetFullPath($SourceDirectory)
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$workspacePrefix = $workspace.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Cortex companion destination must remain inside the Cerebral workspace.'
}
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw 'The Cortex companion source directory does not exist.' }

$contractPath = Join-Path $source 'installer-contract.v7.json'
$manifestPath = Join-Path $source 'package-manifest.json'
foreach ($required in @($contractPath, $manifestPath, (Join-Path $source 'package-manifest.p7s'),
    (Join-Path $source 'PegasusCortexInstaller.exe'), (Join-Path $source 'product\package-manifest.json'),
    (Join-Path $source 'product\package-manifest.p7s'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "The Cortex companion is incomplete: $required" }
}
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($contract.contract -cne 'pegasus-cortex-nsis-installer' -or $contract.version -ne 7 -or
    $contract.payloadLayout -cne 'adjacent-uncompressed-product-directory' -or
    $manifest.schema -cne 'pegasus.cortex.local-operational-package/1.2' -or
    $manifest.role -cne 'product-installer' -or $manifest.channel -cne 'Prod Alpha' -or
    $manifest.releaseVersion -cne $contract.releaseVersion) {
    throw 'The Cortex companion contract is incompatible with Cerebral setup.'
}

function Assert-ManifestFiles([string]$Root, [object]$PackageManifest) {
    $rootPath = [IO.Path]::GetFullPath($Root)
    $rootPrefix = $rootPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in @($PackageManifest.files)) {
        $relative = [string]$record.path
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or
            $relative.Split([char[]]@('/', '\')).Where({ $_ -in @('', '.', '..') }, 'First') -or -not $names.Add($relative)) {
            throw 'The Cortex package manifest contains an unsafe or duplicate path.'
        }
        $file = [IO.Path]::GetFullPath((Join-Path $rootPath $relative))
        if (-not $file.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "The Cortex package manifest entry is missing: $relative"
        }
        $info = Get-Item -LiteralPath $file
        if ($info.Length -ne [long]$record.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne ([string]$record.sha256).ToUpperInvariant()) {
            throw "The Cortex package manifest entry does not match its admitted bytes: $relative"
        }
    }
}

Assert-ManifestFiles $source $manifest
$parent = [IO.Path]::GetDirectoryName($destination)
$transaction = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $parent ('.Pegasus-Cortex-Setup.staging-' + $transaction)
$previous = Join-Path $parent ('.Pegasus-Cortex-Setup.previous-' + $transaction)
foreach ($path in @($stage, $previous)) {
    if (-not ([IO.Path]::GetFullPath($path)).StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A Cortex import transaction path escaped the Cerebral workspace.'
    }
}

try {
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $stage -Recurse -Force
    }
    $stagedManifest = Get-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Raw | ConvertFrom-Json
    Assert-ManifestFiles $stage $stagedManifest
    if (Test-Path -LiteralPath $destination -PathType Container) { [IO.Directory]::Move($destination, $previous) }
    [IO.Directory]::Move($stage, $destination)
    if (Test-Path -LiteralPath $previous -PathType Container) { Remove-Item -LiteralPath $previous -Recurse -Force }
} catch {
    if ((Test-Path -LiteralPath $previous -PathType Container) -and -not (Test-Path -LiteralPath $destination)) {
        [IO.Directory]::Move($previous, $destination)
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stage -PathType Container) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $previous -PathType Container) { Remove-Item -LiteralPath $previous -Recurse -Force }
}

$files = @(Get-ChildItem -LiteralPath $destination -File -Recurse)
[pscustomobject]@{
    contract = 'pegasus.cerebral.cortex-companion-import/1'
    destination = $destination
    releaseVersion = [string]$contract.releaseVersion
    files = $files.Count
    bytes = [long](($files | Measure-Object Length -Sum).Sum)
}
