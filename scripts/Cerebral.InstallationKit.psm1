Set-StrictMode -Version Latest

$script:KitEntries = [ordered]@{
    'Cerebral-Setup.exe'   = 'Leaf'
    'Cerebral-Worker-Setup.exe' = 'Leaf'
    'BehaviorPacks'        = 'Container'
    'Core'                 = 'Container'
    'Features'             = 'Container'
    'Models'               = 'Container'
    'Plugins'              = 'Container'
    'Pegasus-Cortex-Setup' = 'Container'
    'Prerequisites'        = 'Container'
    'Support'              = 'Container'
    'Workers'              = 'Container'
    'README.txt'           = 'Leaf'
    'kit.ini'              = 'Leaf'
}
$script:LegacyKitEntries = [ordered]@{
    'Cerebral-Setup.exe'   = 'Leaf'
    'Features'             = 'Container'
    'Models'               = 'Container'
    'Plugins'              = 'Container'
    'Pegasus-Cortex-Setup' = 'Container'
    'Prerequisites'        = 'Container'
    'README.txt'           = 'Leaf'
    'kit.json'             = 'Leaf'
}

function Test-CerebralDirectChild {
    param(
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Child
    )

    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $childPath = [IO.Path]::GetFullPath($Child)
    $relative = [IO.Path]::GetRelativePath($parentPath, $childPath)
    return -not [IO.Path]::IsPathRooted($relative) -and
        $relative -notin '.', '..' -and
        -not $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -and
        -not $relative.Contains([IO.Path]::DirectorySeparatorChar) -and
        -not $relative.Contains([IO.Path]::AltDirectorySeparatorChar)
}

function Assert-CerebralNoReparsePoints {
    param([Parameter(Mandatory)][string]$Path)

    $root = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Installation-kit path is a reparse point: $Path"
    }
    $reparsePoint = Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw "Installation-kit payload contains a reparse point: $($reparsePoint.FullName)"
    }
}

function Assert-CerebralInstallationKitRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowLegacy,
        [switch]$PassThru
    )

    $root = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Installation-kit root does not exist: $root"
    }
    Assert-CerebralNoReparsePoints -Path $root

    $actual = @(Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop)
    $actualNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $actual) {
        if (-not $actualNames.Add($entry.Name)) {
            throw "Installation-kit root contains duplicate case-insensitive entry names: $root"
        }
    }

    $layouts = [Collections.Generic.List[object]]::new()
    $layouts.Add([pscustomobject]@{ Name = 'Current'; Entries = $script:KitEntries })
    if ($AllowLegacy) { $layouts.Add([pscustomobject]@{ Name = 'Legacy'; Entries = $script:LegacyKitEntries }) }
    $selected = $null
    foreach ($layout in $layouts) {
        $expectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in $layout.Entries.Keys) { [void]$expectedNames.Add($name) }
        if ($actualNames.Count -eq $expectedNames.Count -and
            @($actualNames | Where-Object { -not $expectedNames.Contains($_) }).Count -eq 0) {
            $selected = $layout
            break
        }
    }
    if ($null -eq $selected) {
        $allowed = @($layouts | ForEach-Object { $_.Name + '=[' + ($_.Entries.Keys -join ', ') + ']' }) -join ' or '
        throw "Installation-kit root does not match an admitted layout. Actual=[$(@($actual | ForEach-Object Name) -join ', ')] Expected $allowed. Root=$root"
    }

    foreach ($pair in $selected.Entries.GetEnumerator()) {
        $entryPath = Join-Path $root $pair.Key
        if (-not (Test-Path -LiteralPath $entryPath -PathType $pair.Value)) {
            throw "Installation-kit entry has the wrong type: $entryPath (expected $($pair.Value))."
        }
    }
    if ($PassThru) { return $selected.Name }
}

function ConvertFrom-CerebralRobocopyNumber {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return [long]0 }
    $digits = -join ([regex]::Matches($Text, '\d') | ForEach-Object Value)
    if ([string]::IsNullOrWhiteSpace($digits)) { return [long]0 }
    return [long]::Parse($digits, [Globalization.CultureInfo]::InvariantCulture)
}

function Invoke-CerebralKitRobocopy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Label,
        [switch]$Move
    )

    $sourcePath = [IO.Path]::GetFullPath($Source)
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
        throw "Robocopy source directory does not exist: $sourcePath"
    }
    Assert-CerebralNoReparsePoints -Path $sourcePath
    [IO.Directory]::CreateDirectory($destinationPath) | Out-Null

    $robocopy = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32\robocopy.exe'
    if (-not (Test-Path -LiteralPath $robocopy -PathType Leaf)) {
        throw "Windows Robocopy is unavailable at its system path: $robocopy"
    }
    $arguments = @(
        $sourcePath,
        $destinationPath,
        '/E',
        '/COPY:DAT',
        '/DCOPY:DAT',
        '/J',
        '/R:2',
        '/W:1',
        '/XJ',
        '/XJD',
        '/XJF',
        '/NP',
        '/BYTES'
    )
    if ($Move) { $arguments += '/MOVE' }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $robocopy
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Robocopy did not start for '$Label'." }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $lines = @(($standardOutput.GetAwaiter().GetResult() -split '\r?\n') +
            ($standardError.GetAwaiter().GetResult() -split '\r?\n') | Where-Object { $_.Length -gt 0 })
        $exitCode = $process.ExitCode
    }
    finally { $process.Dispose() }
    $summary = @{}
    foreach ($line in $lines) {
        if ($line -match '^\s*(Dirs|Files|Bytes)\s*:\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)') {
            $summary[$matches[1]] = [pscustomobject]@{
                Total = ConvertFrom-CerebralRobocopyNumber $matches[2]
                Copied = ConvertFrom-CerebralRobocopyNumber $matches[3]
                Skipped = ConvertFrom-CerebralRobocopyNumber $matches[4]
                Mismatch = ConvertFrom-CerebralRobocopyNumber $matches[5]
                Failed = ConvertFrom-CerebralRobocopyNumber $matches[6]
                Extras = ConvertFrom-CerebralRobocopyNumber $matches[7]
            }
        }
    }

    $receipt = [pscustomobject]@{
        Label = $Label
        Mode = if ($Move) { 'Move' } else { 'Copy' }
        ExitCode = $exitCode
        Files = if ($summary.ContainsKey('Files')) { $summary['Files'].Copied } else { [long]0 }
        Bytes = if ($summary.ContainsKey('Bytes')) { $summary['Bytes'].Copied } else { [long]0 }
    }
    if ($exitCode -ge 8) {
        $tail = @($lines | Select-Object -Last 12) -join [Environment]::NewLine
        throw "Robocopy failed for '$Label' with exit code $exitCode.`n$tail"
    }
    return $receipt
}

function Copy-CerebralInstallationKitDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Label
    )

    return Invoke-CerebralKitRobocopy -Source $Source -Destination $Destination -Label $Label
}

function Move-CerebralKitEntry {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[object]]$Receipts
    )

    if (Test-Path -LiteralPath $Source -PathType Container) {
        $Receipts.Add((Invoke-CerebralKitRobocopy -Source $Source -Destination $Destination -Label $Label -Move))
        if (Test-Path -LiteralPath $Source) {
            $remaining = @(Get-ChildItem -LiteralPath $Source -Force -ErrorAction Stop)
            if ($remaining.Count -ne 0) {
                throw "Robocopy left entries in a moved installation-kit directory: $Source"
            }
            [IO.Directory]::Delete($Source)
        }
        return
    }
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Installation-kit entry is missing while moving '$Label': $Source"
    }
    if (Test-Path -LiteralPath $Destination) {
        throw "Installation-kit entry destination already exists while moving '$Label': $Destination"
    }
    [IO.File]::Move($Source, $Destination)
}

function Set-CerebralSwapJournal {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Phase,
        [string]$Detail = ''
    )

    foreach ($value in @($Phase, $Detail)) {
        if ($value.Contains("`r") -or $value.Contains("`n") -or $value.Contains([char]0)) {
            throw 'Swap journal values must be single-line text.'
        }
    }
    @(
        '[swap]'
        'schemaVersion=1'
        "phase=$Phase"
        "updatedAt=$([DateTimeOffset]::UtcNow.ToString('O'))"
        "detail=$Detail"
        "entries=$($script:KitEntries.Keys -join ',')"
    ) | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Test-CerebralRootRenameDenied {
    param([Parameter(Mandatory)][Exception]$Exception)

    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [UnauthorizedAccessException]) { return $true }
        if ($current -is [IO.IOException]) {
            $nativeCode = $current.HResult -band 0xffff
            if ($nativeCode -in @(5, 32, 33)) { return $true }
        }
        $current = $current.InnerException
    }
    return $false
}

function Restore-CerebralContainedSwap {
    param(
        [Parameter(Mandatory)][string]$Staging,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Rollback,
        [Parameter(Mandatory)][string]$Journal,
        [Parameter(Mandatory)][ValidateSet('Current','Legacy')][string]$OldLayout,
        [Parameter(Mandatory)][bool]$NewPublishStarted,
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[object]]$Receipts
    )

    Set-CerebralSwapJournal -Path $Journal -Phase 'rolling-back' -Detail 'Restoring the previous admitted generation.'
    if ($NewPublishStarted) {
        foreach ($name in @('kit.ini') + @($script:KitEntries.Keys | Where-Object { $_ -cne 'kit.ini' })) {
            $published = Join-Path $Destination $name
            if (Test-Path -LiteralPath $published) {
                Move-CerebralKitEntry -Source $published -Destination (Join-Path $Staging $name) -Label "rollback-new/$name" -Receipts $Receipts
            }
        }
    }

    $oldEntries = if ($OldLayout -ceq 'Legacy') { $script:LegacyKitEntries } else { $script:KitEntries }
    $oldMarker = if ($OldLayout -ceq 'Legacy') { 'kit.json' } else { 'kit.ini' }
    foreach ($name in @($oldEntries.Keys | Where-Object { $_ -cne $oldMarker })) {
        $saved = Join-Path $Rollback $name
        if (Test-Path -LiteralPath $saved) {
            Move-CerebralKitEntry -Source $saved -Destination (Join-Path $Destination $name) -Label "restore-old/$name" -Receipts $Receipts
        }
    }
    $savedMarker = Join-Path $Rollback $oldMarker
    if (Test-Path -LiteralPath $savedMarker -PathType Leaf) {
        Move-CerebralKitEntry -Source $savedMarker -Destination (Join-Path $Destination $oldMarker) -Label "restore-old/$oldMarker" -Receipts $Receipts
    }
    Assert-CerebralInstallationKitRoot -Path $Destination -AllowLegacy
    Set-CerebralSwapJournal -Path $Journal -Phase 'rolled-back' -Detail 'The previous generation is restored; staged and journal data are retained for diagnosis.'
}

function Invoke-CerebralContainedSwap {
    param(
        [Parameter(Mandatory)][string]$Staging,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Rollback,
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[object]]$Receipts,
        [ValidateSet('None','AfterOldMarker','AfterOldPayload','AfterNewPayload')][string]$FailAt = 'None'
    )

    $oldLayout = Assert-CerebralInstallationKitRoot -Path $Destination -AllowLegacy -PassThru
    $oldEntries = if ($oldLayout -ceq 'Legacy') { $script:LegacyKitEntries } else { $script:KitEntries }
    $oldMarker = if ($oldLayout -ceq 'Legacy') { 'kit.json' } else { 'kit.ini' }
    [IO.Directory]::CreateDirectory($Rollback) | Out-Null
    $journal = Join-Path $Rollback 'swap-journal.ini'
    $newPublishStarted = $false
    $committed = $false
    try {
        Set-CerebralSwapJournal -Path $journal -Phase 'prepared' -Detail 'The existing root rename was denied; beginning a contained swap.'

        Move-CerebralKitEntry -Source (Join-Path $Destination $oldMarker) -Destination (Join-Path $Rollback $oldMarker) -Label "save-old/$oldMarker" -Receipts $Receipts
        Set-CerebralSwapJournal -Path $journal -Phase 'old-marker-removed' -Detail 'The visible kit is non-admissible during replacement.'
        if ($FailAt -ceq 'AfterOldMarker') { throw 'Injected contained-swap failure after old marker removal.' }

        foreach ($name in @($oldEntries.Keys | Where-Object { $_ -cne $oldMarker })) {
            Move-CerebralKitEntry -Source (Join-Path $Destination $name) -Destination (Join-Path $Rollback $name) -Label "save-old/$name" -Receipts $Receipts
        }
        Set-CerebralSwapJournal -Path $journal -Phase 'old-payload-saved' -Detail 'The previous payload is contained in the rollback directory.'
        if ($FailAt -ceq 'AfterOldPayload') { throw 'Injected contained-swap failure after old payload preservation.' }

        $newPublishStarted = $true
        foreach ($name in @($script:KitEntries.Keys | Where-Object { $_ -cne 'kit.ini' })) {
            Move-CerebralKitEntry -Source (Join-Path $Staging $name) -Destination (Join-Path $Destination $name) -Label "publish-new/$name" -Receipts $Receipts
        }
        Set-CerebralSwapJournal -Path $journal -Phase 'new-payload-published' -Detail 'The new payload is present but remains non-admissible until kit.ini is published.'
        if ($FailAt -ceq 'AfterNewPayload') { throw 'Injected contained-swap failure after new payload publication.' }

        Move-CerebralKitEntry -Source (Join-Path $Staging 'kit.ini') -Destination (Join-Path $Destination 'kit.ini') -Label 'publish-new/kit.ini' -Receipts $Receipts
        Assert-CerebralInstallationKitRoot -Path $Destination
        Set-CerebralSwapJournal -Path $journal -Phase 'committed' -Detail 'The new kit generation is admitted.'
        $committed = $true
    }
    catch {
        $failure = $_
        try {
            Restore-CerebralContainedSwap -Staging $Staging -Destination $Destination -Rollback $Rollback -Journal $journal -OldLayout $oldLayout -NewPublishStarted $newPublishStarted -Receipts $Receipts
        }
        catch {
            throw "Contained installation-kit swap failed and rollback also failed. Original error: $($failure.Exception.Message) Rollback error: $($_.Exception.Message) Recovery data remains at '$Rollback' and '$Staging'."
        }
        throw "Contained installation-kit swap failed; the previous kit was restored. $($failure.Exception.Message) Recovery data remains at '$Rollback' and '$Staging'."
    }

    $cleanupPending = [Collections.Generic.List[string]]::new()
    if ($committed) {
        foreach ($path in @($Rollback, $Staging)) {
            if (Test-Path -LiteralPath $path) {
                try { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop }
                catch { $cleanupPending.Add($path) }
            }
        }
    }
    return [pscustomobject]@{ Mode = 'Contained'; CleanupPending = @($cleanupPending) }
}

function Invoke-CerebralInstallationKitPublish {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$DestinationRoot,
        [Parameter(DontShow)][ValidateSet('None','AccessDenied','Other')][string]$SimulatedRenameFailure = 'None',
        [Parameter(DontShow)][ValidateSet('None','AfterOldMarker','AfterOldPayload','AfterNewPayload')][string]$FailContainedSwapAt = 'None'
    )

    $staging = [IO.Path]::GetFullPath($StagingRoot)
    $destination = [IO.Path]::GetFullPath($DestinationRoot)
    $parent = [IO.Path]::GetDirectoryName($destination)
    if ([string]::IsNullOrWhiteSpace($parent) -or $destination -eq [IO.Path]::GetPathRoot($destination)) {
        throw 'DestinationRoot must be a named kit folder, not a drive root.'
    }
    if (-not (Test-CerebralDirectChild -Parent $parent -Child $staging)) {
        throw 'Installation-kit staging must be a direct sibling of the destination root.'
    }
    Assert-CerebralInstallationKitRoot -Path $staging

    $rollback = Join-Path $parent ('.' + [IO.Path]::GetFileName($destination) + '.previous-' + [Guid]::NewGuid().ToString('N'))
    if (-not (Test-CerebralDirectChild -Parent $parent -Child $rollback)) {
        throw 'Installation-kit rollback path escaped the selected destination parent.'
    }
    $receipts = [Collections.Generic.List[object]]::new()

    if (-not (Test-Path -LiteralPath $destination)) {
        [IO.Directory]::Move($staging, $destination)
        return [pscustomobject]@{ Mode = 'AtomicCreate'; Robocopy = @(); CleanupPending = @() }
    }

    try {
        if ($SimulatedRenameFailure -ceq 'AccessDenied') { throw [UnauthorizedAccessException]::new('Injected root rename access denial.') }
        if ($SimulatedRenameFailure -ceq 'Other') { throw [IO.IOException]::new('Injected non-access root rename failure.', 0x80131620) }
        [IO.Directory]::Move($destination, $rollback)
    }
    catch {
        if (-not (Test-CerebralRootRenameDenied -Exception $_.Exception) -or
            -not (Test-Path -LiteralPath $destination -PathType Container) -or
            (Test-Path -LiteralPath $rollback)) {
            throw
        }
        $contained = Invoke-CerebralContainedSwap -Staging $staging -Destination $destination -Rollback $rollback -Receipts $receipts -FailAt $FailContainedSwapAt
        return [pscustomobject]@{ Mode = $contained.Mode; Robocopy = @($receipts); CleanupPending = @($contained.CleanupPending) }
    }

    try {
        [IO.Directory]::Move($staging, $destination)
        Assert-CerebralInstallationKitRoot -Path $destination
    }
    catch {
        if ((Test-Path -LiteralPath $rollback) -and -not (Test-Path -LiteralPath $destination)) {
            [IO.Directory]::Move($rollback, $destination)
        }
        throw
    }

    $cleanupPending = @()
    if (Test-Path -LiteralPath $rollback) {
        try { Remove-Item -LiteralPath $rollback -Recurse -Force -ErrorAction Stop }
        catch { $cleanupPending = @($rollback) }
    }
    return [pscustomobject]@{ Mode = 'AtomicReplace'; Robocopy = @(); CleanupPending = $cleanupPending }
}

Export-ModuleMember -Function Copy-CerebralInstallationKitDirectory, Invoke-CerebralInstallationKitPublish
