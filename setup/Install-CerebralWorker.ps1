[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'

function Write-WorkerInstallReceipt([bool]$Ok, [string]$Message, [hashtable]$Details = @{}) {
    if ([string]::IsNullOrWhiteSpace($ReceiptPath)) { return }
    $receipt = [ordered]@{
        contract = 'pegasus.cerebral.worker-install/1'
        at = [DateTimeOffset]::UtcNow.ToString('O')
        ok = $Ok
        message = $Message
    }
    foreach ($entry in $Details.GetEnumerator()) { $receipt[$entry.Key] = $entry.Value }
    $target = [IO.Path]::GetFullPath($ReceiptPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::WriteAllText($target, ($receipt | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
}

try {
    $SourceRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
    $DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
    $destinationParent = [IO.Path]::GetDirectoryName($DestinationRoot)
    if ([string]::IsNullOrWhiteSpace($destinationParent) -or $DestinationRoot -eq [IO.Path]::GetPathRoot($DestinationRoot)) {
        throw 'DestinationRoot must be a product directory, not a drive root.'
    }
    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        throw "The adjacent Worker payload is missing: $SourceRoot"
    }
    if ($SourceRoot -eq $DestinationRoot) { throw 'Worker source and destination must be different directories.' }
    $sourceExecutable = Join-Path $SourceRoot 'Cerebral.Worker.exe'
    foreach ($required in @($sourceExecutable, (Join-Path $SourceRoot 'Cerebral.Worker.dll'), (Join-Path $SourceRoot 'Cerebral.Core.dll'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "The Worker payload is incomplete: $required" }
    }

    [IO.Directory]::CreateDirectory($DestinationRoot) | Out-Null
    $transaction = [Guid]::NewGuid().ToString('N')
    $stage = Join-Path $destinationParent ('.cerebral-worker-stage-' + $transaction)
    $previous = Join-Path $destinationParent ('.cerebral-worker-previous-' + $transaction)
    try {
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        foreach ($entry in Get-ChildItem -LiteralPath $SourceRoot -Force) {
            Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $stage $entry.Name) -Recurse
        }
        if (-not (Test-Path -LiteralPath (Join-Path $stage 'Cerebral.Worker.exe') -PathType Leaf)) {
            throw 'The staged Worker payload is incomplete.'
        }
        if (Test-Path -LiteralPath $DestinationRoot) {
            # Retry only the bounded directory rename after the service reports stopped.
            for ($attempt = 0; $attempt -lt 8; $attempt++) {
                try { [IO.Directory]::Move($DestinationRoot, $previous); break }
                catch {
                    if ($attempt -eq 7 -or ($_.Exception -isnot [IO.IOException] -and $_.Exception -isnot [UnauthorizedAccessException])) { throw }
                    Start-Sleep -Milliseconds (250 * ($attempt + 1))
                }
            }
        }
        try { [IO.Directory]::Move($stage, $DestinationRoot) }
        catch {
            if (Test-Path -LiteralPath $DestinationRoot) { Remove-Item -LiteralPath $DestinationRoot -Recurse -Force }
            if (Test-Path -LiteralPath $previous) { [IO.Directory]::Move($previous, $DestinationRoot) }
            throw
        }
        if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
    }
    finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }

    Write-WorkerInstallReceipt $true 'The standalone Worker payload was published.' @{
        destination = $DestinationRoot
        executable = (Join-Path $DestinationRoot 'Cerebral.Worker.exe')
    }
}
catch {
    Write-WorkerInstallReceipt $false $_.Exception.Message @{ errorType = $_.Exception.GetType().FullName }
    throw
}
