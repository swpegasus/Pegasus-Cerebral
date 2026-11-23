[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Cerebral.PayloadReuse.psm1') -Force
trap {
    if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
        $receipt = [pscustomobject]@{ contract='pegasus.cerebral.installation-source-cache/1'; at=[DateTimeOffset]::UtcNow.ToString('O'); ok=$false; error=$_.Exception.Message }
        $receiptParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
        [IO.Directory]::CreateDirectory($receiptParent) | Out-Null
        $receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
    }
    throw
}
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot 'Cerebral-Setup.exe') -PathType Leaf)) {
    throw 'The selected installation source does not contain Cerebral-Setup.exe.'
}
if ($DestinationRoot -eq [IO.Path]::GetPathRoot($DestinationRoot)) { throw 'The installation cache cannot be a drive root.' }
if ($SourceRoot.Equals($DestinationRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $result = [pscustomobject]@{ contract='pegasus.cerebral.installation-source-cache/1'; at=[DateTimeOffset]::UtcNow.ToString('O'); ok=$true; state='current'; path=$DestinationRoot }
    if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) { $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8 }
    $result | ConvertTo-Json -Compress
    return
}
$sourcePrefix = $SourceRoot + [IO.Path]::DirectorySeparatorChar
$destinationPrefix = $DestinationRoot + [IO.Path]::DirectorySeparatorChar
if ($DestinationRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installation cache cannot be created inside its own source tree.'
}
if ($SourceRoot.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installation source cannot be inside the cache destination being replaced.'
}

$parent = [IO.Path]::GetDirectoryName($DestinationRoot)
if ([string]::IsNullOrWhiteSpace($parent)) { throw 'The installation cache requires a named parent folder.' }
[IO.Directory]::CreateDirectory($parent) | Out-Null
$transaction = [Guid]::NewGuid().ToString('N').Substring(0, 4)
# Keep private sibling names short. The final cache tree is within the Windows
# path limit, so staging and rollback leaves must be no longer than "Current".
# This also keeps recursive rollback cleanup below the legacy PowerShell 5.1
# MAX_PATH boundary used by the elevated installer helper.
$stage = Join-Path $parent ('.s-' + $transaction)
$previous = Join-Path $parent ('.p-' + $transaction)
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $previous)) {
    throw 'A cache transaction path already exists; retry the operation.'
}

try {
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $reusedPayloads = [Collections.Generic.List[string]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $SourceRoot -Force) {
        $target = Join-Path $stage $item.Name
        $existing = Join-Path $DestinationRoot $item.Name
        $reuse = $item.PSIsContainer -and $item.Name -in @('Models', 'Prerequisites') -and
            (Test-CerebralPayloadInventoryEqual -Source $item.FullName -Existing $existing) -and
            (New-CerebralHardLinkPayloadClone -Existing $existing -Destination $target)
        if ($reuse) { $reusedPayloads.Add($item.Name) }
        if (-not $reuse) { Copy-Item -LiteralPath $item.FullName -Destination $stage -Recurse -Force }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'Cerebral-Setup.exe') -PathType Leaf)) { throw 'The staged installation cache is incomplete.' }
    if (Test-Path -LiteralPath $DestinationRoot -PathType Container) { [IO.Directory]::Move($DestinationRoot, $previous) }
    [IO.Directory]::Move($stage, $DestinationRoot)
    if (Test-Path -LiteralPath $previous -PathType Container) { Remove-Item -LiteralPath $previous -Recurse -Force }
} catch {
    if ((Test-Path -LiteralPath $previous -PathType Container) -and -not (Test-Path -LiteralPath $DestinationRoot)) {
        [IO.Directory]::Move($previous, $DestinationRoot)
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stage -PathType Container) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $previous -PathType Container) { Remove-Item -LiteralPath $previous -Recurse -Force }
}

$files = @(Get-ChildItem -LiteralPath $DestinationRoot -File -Recurse -Force)
$result = [pscustomobject]@{
    contract = 'pegasus.cerebral.installation-source-cache/1'
    at = [DateTimeOffset]::UtcNow.ToString('O')
    ok = $true
    state = 'updated'
    path = $DestinationRoot
    files = $files.Count
    bytes = [long](($files | Measure-Object Length -Sum).Sum)
    reusedPayloads = @($reusedPayloads)
}
if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $receiptParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
    [IO.Directory]::CreateDirectory($receiptParent) | Out-Null
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
}
$result | ConvertTo-Json -Compress
