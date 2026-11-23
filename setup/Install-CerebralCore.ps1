[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$publicationCompleted = $false
$cleanupCompleted = $false
trap {
    $failure = $_
    if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
        try {
            $receipt = [ordered]@{
                contract='pegasus.cerebral.core-install/1'; at=[DateTimeOffset]::UtcNow.ToString('O'); ok=$false
                publicationCompleted=$publicationCompleted; cleanupCompleted=$cleanupCompleted
                error=$failure.Exception.Message
            }
            if ($publicationCompleted) {
                $receipt['destination'] = $DestinationRoot
                $receipt['stagingPath'] = $stageRoot
                $receipt['previousPath'] = $previousRoot
            }
            $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
            [IO.Directory]::CreateDirectory($parent) | Out-Null
            $receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
        }
        catch { Write-Warning "Could not record the core installer failure: $($_.Exception.Message)" -WarningAction Continue }
    }
    if ($publicationCompleted) {
        # Cleanup can already have removed part of the previous generation. The
        # caller must preserve this outcome for recovery instead of retrying setup.
        Write-Warning "The new Cerebral core was published. Setup stopped during finalization: $($failure.Exception.Message)" -WarningAction Continue
        exit 2
    }
    throw $failure
}
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
$destinationParent = [IO.Path]::GetDirectoryName($DestinationRoot)
if ([string]::IsNullOrWhiteSpace($destinationParent) -or $DestinationRoot -eq [IO.Path]::GetPathRoot($DestinationRoot)) {
    throw 'DestinationRoot must be a product directory, not a drive root.'
}
if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "The adjacent Cerebral core payload is missing: $SourceRoot"
}
if ($SourceRoot.TrimEnd('\') -eq $DestinationRoot.TrimEnd('\')) {
    throw 'The Cerebral core source and destination must be different directories.'
}

function Get-EngineTreeSnapshot([string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    for ($ancestor = $resolved; $null -ne $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        try { $attributes = [IO.File]::GetAttributes($ancestor) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Engine payload paths cannot traverse reparse points: $ancestor"
        }
    }
    if (-not (Test-Path -LiteralPath $resolved)) { return $null }
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "Engine payload root must be a directory: $resolved" }
    $snapshot = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($resolved)
    [long]$expanded = 0
    while ($pending.Count -ne 0) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Engine payload contains a reparse point: $($entry.FullName)" }
            $relative = $entry.FullName.Substring($resolved.Length + 1)
            if ($snapshot.Count -ge 1024 -or $relative.Split([IO.Path]::DirectorySeparatorChar).Length -gt 16) {
                throw 'Engine payload exceeds its preservation entry/depth boundary.'
            }
            if ($entry.PSIsContainer) {
                $snapshot.Add($relative + [IO.Path]::DirectorySeparatorChar, 'directory')
                $pending.Push($entry.FullName)
            }
            else {
                $expanded += $entry.Length
                if ($expanded -gt 128MB) { throw 'Engine payload exceeds its 128 MiB preservation boundary.' }
                # Keep the NSIS Windows PowerShell host independent of module
                # auto-loading (its inherited PSModulePath may belong to pwsh).
                $input = [IO.File]::OpenRead($entry.FullName)
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try { $snapshot.Add($relative, [BitConverter]::ToString($algorithm.ComputeHash($input)).Replace('-','')) }
                finally { $algorithm.Dispose(); $input.Dispose() }
            }
        }
    }
    return ,$snapshot
}

function Get-ValidatedEnginePackages([string]$WorkerPath, [string]$Root) {
    $start = [Diagnostics.ProcessStartInfo]::new($WorkerPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Windows paths cannot contain quotes; trimming the final separator also
    # avoids the native command-line backslash/closing-quote escape ambiguity.
    $start.Arguments = 'manage inspect-engine-packages --directory "' + $Root.TrimEnd('\','/') + '"'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Static engine package inspection timed out.' }
        $report = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Static engine package inspection failed: $errorText" }
        if ([Text.Encoding]::UTF8.GetByteCount($report) -gt 65536) { throw 'Static engine package inspection returned an oversized report.' }
        return ($report | ConvertFrom-Json)
    }
    finally { $process.Dispose() }
}

function Assert-EngineTreeSnapshot($Expected, $Actual, [string]$Context) {
    if ($null -eq $Actual -or $Expected.Count -ne $Actual.Count) { throw "Engine payload changed during $Context." }
    foreach ($path in $Expected.Keys) {
        if (-not $Actual.ContainsKey($path) -or $Expected[$path] -cne $Actual[$path]) {
            throw ("Engine payload changed during {0}: {1}" -f $Context, $path)
        }
    }
}

$applications = @('Worker', 'Integrator', 'Orchestrator')
foreach ($application in $applications) {
    $group = if ($application -eq 'Worker') { 'Worker' } else { 'Desktop' }
    $executable = Join-Path $SourceRoot "Applications\$group\Cerebral.$application.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The Cerebral core payload is incomplete: $executable"
    }
}
$brandingManifest = Join-Path $SourceRoot 'Resources\Branding\Cerebral\brand.ini'
if (-not (Test-Path -LiteralPath $brandingManifest -PathType Leaf)) {
    throw "The Cerebral core payload is missing its shared branding package: $brandingManifest"
}

# Core replacement carries the exact existing engine component forward. A
# deliberate engine update must use its own lifecycle, independently of core files.
$sourceEngineRoot = Join-Path $SourceRoot 'Applications\Worker\Engines'
$existingEngineRoot = Join-Path $DestinationRoot 'Applications\Worker\Engines'
$sourceEngineSnapshot = Get-EngineTreeSnapshot $sourceEngineRoot
$existingEngineSnapshot = Get-EngineTreeSnapshot $existingEngineRoot
if ($null -eq $existingEngineSnapshot -and $null -eq $sourceEngineSnapshot) {
    throw 'The fresh core payload is missing its default engine package.'
}
$validationWorker = Join-Path $SourceRoot 'Applications\Worker\Cerebral.Worker.exe'
$sourceEnginePackages = if ($null -ne $sourceEngineSnapshot) { Get-ValidatedEnginePackages $validationWorker $sourceEngineRoot } else { $null }
if ($null -ne $existingEngineSnapshot) {
    Get-ValidatedEnginePackages $validationWorker $existingEngineRoot | Out-Null
}
elseif (-not ($sourceEnginePackages.slots | Where-Object { $_.slot -ceq 'A' -and $_.available })) {
    throw 'The fresh core payload requires a validated default A engine package.'
}

[IO.Directory]::CreateDirectory($DestinationRoot) | Out-Null
$transaction = [Guid]::NewGuid().ToString('N')
$stageRoot = Join-Path $DestinationRoot ('.core-stage-' + $transaction)
$previousRoot = Join-Path $DestinationRoot ('.core-previous-' + $transaction)
$published = [Collections.Generic.List[object]]::new()
$retainRecovery = $false

function Move-DirectoryBounded([string]$Source, [string]$Destination) {
    # A service can report Stopped just before Windows releases its last image
    # handle. Retry only this atomic directory rename for a short bounded window.
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        try {
            [IO.Directory]::Move($Source, $Destination)
            return
        } catch {
            if ($_.Exception -isnot [IO.IOException] -and $_.Exception -isnot [UnauthorizedAccessException]) { throw }
            if ($attempt -eq 7) { throw }
            Start-Sleep -Milliseconds (250 * ($attempt + 1))
        }
    }
}

try {
    $stagePayload = Join-Path $stageRoot 'Payload'
    [IO.Directory]::CreateDirectory($stagePayload) | Out-Null
    [IO.Directory]::CreateDirectory($previousRoot) | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $SourceRoot -Force) {
        Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $stagePayload $entry.Name) -Recurse
    }
    foreach ($application in $applications) {
        $group = if ($application -eq 'Worker') { 'Worker' } else { 'Desktop' }
        if (-not (Test-Path -LiteralPath (Join-Path $stagePayload "Applications\$group\Cerebral.$application.exe") -PathType Leaf)) {
            throw "The staged Cerebral $application payload is incomplete."
        }
    }

    $stagedEngineRoot = Join-Path $stagePayload 'Applications\Worker\Engines'
    if ($null -ne $existingEngineSnapshot) {
        # Validate and hash before removing the bundled seed from task-owned staging.
        if (Test-Path -LiteralPath $stagedEngineRoot) {
            Get-EngineTreeSnapshot $stagedEngineRoot | Out-Null
            Remove-Item -LiteralPath $stagedEngineRoot -Recurse -Force
        }
        Copy-Item -LiteralPath $existingEngineRoot -Destination (Join-Path $stagePayload 'Applications\Worker') -Recurse
        Assert-EngineTreeSnapshot $existingEngineSnapshot (Get-EngineTreeSnapshot $stagedEngineRoot) 'staging'
        Assert-EngineTreeSnapshot $existingEngineSnapshot (Get-EngineTreeSnapshot $existingEngineRoot) 'pre-publication validation'
    }
    else {
        Assert-EngineTreeSnapshot $sourceEngineSnapshot (Get-EngineTreeSnapshot $stagedEngineRoot) 'fresh staging'
    }

    Get-ValidatedEnginePackages (Join-Path $stagePayload 'Applications\Worker\Cerebral.Worker.exe') $stagedEngineRoot | Out-Null

    # Move legacy isolated application directories out of the active program root.
    # They are restored on failure and permanently removed only after the grouped
    # generation has been published successfully.
    foreach ($legacy in @('Worker','Integrator','Orchestrator')) {
        $destination = Join-Path $DestinationRoot $legacy
        if (-not (Test-Path -LiteralPath $destination)) { continue }
        $previous = Join-Path $previousRoot $legacy
        $item = [pscustomobject]@{ Name=$legacy; Destination=$destination; Previous=$previous; PreviousMoved=$false; NewPayload=$false }
        $published.Add($item)
        Move-DirectoryBounded $destination $previous
        $item.PreviousMoved = $true
    }

    foreach ($entry in Get-ChildItem -LiteralPath $stagePayload -Force | Sort-Object Name) {
        if ($entry.Name -in @('installation.ini','Uninstall.exe')) {
            throw "The replaceable core payload contains installer-owned state: $($entry.Name)"
        }
        $destination = Join-Path $DestinationRoot $entry.Name
        $previous = Join-Path $previousRoot $entry.Name
        # Journal before either rename. An old-move failure must leave the active
        # entry alone; a new-move failure must still restore the displaced entry.
        $item = [pscustomobject]@{ Name=$entry.Name; Destination=$destination; Previous=$previous; PreviousMoved=$false; NewPayload=$false }
        $published.Add($item)
        if (Test-Path -LiteralPath $destination) {
            if (Test-Path -LiteralPath $destination -PathType Container) { Move-DirectoryBounded $destination $previous }
            else { [IO.File]::Move($destination, $previous) }
            $item.PreviousMoved = $true
        }
        if ($entry.PSIsContainer) { Move-DirectoryBounded $entry.FullName $destination }
        else { [IO.File]::Move($entry.FullName, $destination) }
        $item.NewPayload = $true
    }
    $expectedEngineSnapshot = if ($null -ne $existingEngineSnapshot) { $existingEngineSnapshot } else { $sourceEngineSnapshot }
    Assert-EngineTreeSnapshot $expectedEngineSnapshot (Get-EngineTreeSnapshot $existingEngineRoot) 'publication'
    $publicationCompleted = $true
}
catch {
    $publishError = $_
    $rollbackErrors = [Collections.Generic.List[Exception]]::new()
    for ($index = $published.Count - 1; $index -ge 0; $index--) {
        $item = $published[$index]
        try {
            if ($item.NewPayload -and (Test-Path -LiteralPath $item.Destination)) { Remove-Item -LiteralPath $item.Destination -Recurse -Force }
            if ($item.PreviousMoved) {
                if (Test-Path -LiteralPath $item.Previous -PathType Container) { Move-DirectoryBounded $item.Previous $item.Destination }
                elseif (Test-Path -LiteralPath $item.Previous -PathType Leaf) { [IO.File]::Move($item.Previous, $item.Destination) }
                else { throw [IO.IOException]::new("The previous core entry is missing: $($item.Previous)") }
            }
        }
        catch { $rollbackErrors.Add($_.Exception) }
    }
    if ($rollbackErrors.Count -ne 0) {
        $retainRecovery = $true
        throw [AggregateException]::new("Cerebral core publication failed and rollback was incomplete. Recovery artifacts were retained at '$stageRoot' and '$previousRoot'.", @($publishError.Exception) + @($rollbackErrors))
    }
    throw
}
finally {
    if (-not $retainRecovery) {
        if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
        if (Test-Path -LiteralPath $previousRoot) { Remove-Item -LiteralPath $previousRoot -Recurse -Force }
    }
}
$cleanupCompleted = $true

$result = [pscustomobject]@{
    contract = 'pegasus.cerebral.core-install/1'
    at = [DateTimeOffset]::UtcNow.ToString('O')
    ok = $true
    publicationCompleted = $publicationCompleted
    cleanupCompleted = $cleanupCompleted
    applications = $applications.Count
    layout = 'grouped-program-root'
    sharedResources = 1
    destination = $DestinationRoot
}
if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
}
$result | ConvertTo-Json -Compress
