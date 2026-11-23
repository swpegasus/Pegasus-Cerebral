[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkerPath,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$UserDataRoot,
    [Parameter(Mandatory)][string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'

function Write-Receipt([bool]$Ok, [string]$Message, [hashtable]$Details = @{}) {
    $receipt = [ordered]@{
        contract = 'pegasus.cerebral.owner-pipe-install/1'
        at = [DateTimeOffset]::UtcNow.ToString('O')
        ok = $Ok
        message = $Message
    }
    foreach ($entry in $Details.GetEnumerator()) { $receipt[$entry.Key] = $entry.Value }
    if ($ReceiptPath) {
        $target = [IO.Path]::GetFullPath($ReceiptPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::WriteAllText($target, ($receipt | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    }
    $receipt | ConvertTo-Json -Compress -Depth 5
}

function Write-CreateOnlyMarker([string]$Path, [hashtable]$Value) {
    $json = $Value | ConvertTo-Json -Compress -Depth 6
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    if ($bytes.Length -le 0 -or $bytes.Length -gt 8192) { throw 'Owner-pipe marker exceeds its bound.' }
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function Get-ReturnIdentity {
    $case = [string]$env:PEGASUS_CEREBRAL_RETURN_CASE_ID
    $rid = [string]$env:PEGASUS_CEREBRAL_RETURN_SETUP_RID
    if (($case -eq '') -ne ($rid -eq '')) { throw 'Owner-pipe return identity is incomplete.' }
    if ($case -eq '') { $case = [Guid]::NewGuid().ToString('N'); $rid = [Guid]::NewGuid().ToString('N') }
    if ($case -cnotmatch '^[a-f0-9]{32}$' -or $rid -cnotmatch '^[a-f0-9]{32}$') {
        throw 'Owner-pipe return identity is invalid.'
    }
    return @{ caseId = $case; setupRid = $rid }
}

function Assert-PlainAncestors([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Owner-pipe path contains a reparse point.'
            }
        }
    }
}

try {
    # The installer deliberately uses inbox Windows PowerShell 5.1, where the
    # PowerShell 7 convenience variable `$IsWindows` does not exist.
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Installed owner-pipe setup requires Windows.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Installed owner-pipe setup requires the elevated Cerebral installer.'
    }
    $ownerSid = $identity.User.Value
    $WorkerPath = [IO.Path]::GetFullPath($WorkerPath)
    $DataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
    $UserDataRoot = [IO.Path]::GetFullPath($UserDataRoot).TrimEnd('\')
    foreach ($path in @($WorkerPath, $DataDirectory, $UserDataRoot, $ReceiptPath)) {
        Assert-PlainAncestors $path
    }
    if (-not (Test-Path -LiteralPath $WorkerPath -PathType Leaf) -or
        [IO.Path]::GetFileName($WorkerPath) -cne 'Cerebral.Worker.exe') {
        throw 'WorkerPath must identify the installed Cerebral.Worker.exe.'
    }
    foreach ($directory in @($DataDirectory, $UserDataRoot)) {
        if ($directory -eq [IO.Path]::GetPathRoot($directory)) { throw 'Cerebral data paths must be product-specific directories.' }
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $returnIdentity = Get-ReturnIdentity
    $receiptDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
    if (-not $receiptDirectory.Equals($DataDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Owner-pipe receipt must stay in the registered Cerebral data root.'
    }
    $markerPrefix = Join-Path $receiptDirectory ('installation-owner-pipe.' +
        $returnIdentity.caseId + '.' + $returnIdentity.setupRid)
    $intentPath = $markerPrefix + '.intent.json'
    $startedPath = $markerPrefix + '.started.json'
    $latePath = $markerPrefix + '.late.json'
    $terminalPath = $markerPrefix + '.terminal.json'
    $self = [Diagnostics.Process]::GetCurrentProcess()
    try {
        $helperPid = [int]$self.Id
        $helperStartTicks = [string]$self.StartTime.ToUniversalTime().Ticks
    } finally { $self.Dispose() }
    Write-CreateOnlyMarker $intentPath @{
        schema = 'pegasus.cerebral.owner-pipe-child-intent/2'
        caseId = $returnIdentity.caseId; setupRid = $returnIdentity.setupRid
        helperPid = $helperPid; helperStartUtcTicks = $helperStartTicks
        state = 'CHILD_START_IMMINENT'; createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }

    $start = [Diagnostics.ProcessStartInfo]::new($WorkerPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # ProcessStartInfo.ArgumentList is a modern .NET API and is null under the
    # .NET Framework used by inbox Windows PowerShell 5.1. These admitted values
    # contain no quotes and the normalized data path has no trailing separator,
    # so an explicitly quoted argument string is unambiguous and shell-free.
    $processArguments = @('manage','register-owner-pipe','--owner-sid',$ownerSid,'--confirmed-owner-sid',$ownerSid,'--data',$DataDirectory)
    foreach ($argument in $processArguments) {
        if ([string]::IsNullOrEmpty([string]$argument) -or ([string]$argument).Contains('"')) {
            throw 'Owner-pipe registration received an invalid process argument.'
        }
    }
    $start.Arguments = ($processArguments | ForEach-Object { '"' + [string]$_ + '"' }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Installed owner-pipe registration child did not start.' }
    # Every post-Start exception retains the original child handle until exit.
    # An unrecorded start remains an intent-only case for external recovery.
    try {
    # Start both redirected-pipe drains before any PID/start-time capture. If
    # identity capture fails, waiting on the original handle cannot deadlock
    # behind a full stdout or stderr pipe.
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try { $childPid = [int]$process.Id }
    catch {
        # Retain the child handle even if immediate identity capture fails.
        while ($true) {
            $exited = try { $process.WaitForExit(2000) } catch { Start-Sleep -Seconds 2; $false }
            if ($exited) { break }
        }
        throw
    }
    $childStartTicks = try { [string]$process.StartTime.ToUniversalTime().Ticks } catch { $null }
    $markerFailureType = if ($null -eq $childStartTicks) { 'CHILD_START_TICKS_UNAVAILABLE' } else { $null }
    try {
        Write-CreateOnlyMarker $startedPath @{
            schema = 'pegasus.cerebral.owner-pipe-child-started/2'
            caseId = $returnIdentity.caseId; setupRid = $returnIdentity.setupRid
            processId = $childPid; startUtcTicks = $childStartTicks
            state = 'CHILD_STARTED'; createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
    } catch { $markerFailureType = $_.Exception.GetType().FullName }
    $childTerminalRecorded = $false
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $late = $false
        # The parent keeps the original child handle and does not release the
        # installer while registration may still be applying ACL, SCM or binding writes.
        while ($true) {
            $exited = try { $process.WaitForExit(2000) } catch { Start-Sleep -Seconds 2; $false }
            if ($exited) { break }
            if (-not $late -and $clock.ElapsedMilliseconds -ge 120000) {
                $late = $true
                try {
                    Write-CreateOnlyMarker $latePath @{
                        schema = 'pegasus.cerebral.owner-pipe-child-late/2'
                        caseId = $returnIdentity.caseId; setupRid = $returnIdentity.setupRid
                        processId = $childPid; startUtcTicks = $childStartTicks
                        state = 'CHILD_STILL_OBSERVED'; recordedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                    }
                } catch { if ($null -eq $markerFailureType) { $markerFailureType = $_.Exception.GetType().FullName } }
            }
        }
        $clock.Stop()
        $childExit = [int]$process.ExitCode
        try {
            Write-CreateOnlyMarker $terminalPath @{
                schema = 'pegasus.cerebral.owner-pipe-child-terminal/2'
                caseId = $returnIdentity.caseId; setupRid = $returnIdentity.setupRid
                processId = $childPid; startUtcTicks = $childStartTicks
                exitCode = $childExit; late = $late; markerFailureType = $markerFailureType
                state = 'CHILD_TERMINAL'; recordedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            }
            $childTerminalRecorded = $true
        } catch { if ($null -eq $markerFailureType) { $markerFailureType = $_.Exception.GetType().FullName } }
        if ($null -ne $markerFailureType) {
            throw 'Owner-pipe child finished, but a durable marker could not be recorded.'
        }
        if ($childExit -ne 0) {
            throw ('Installed owner-pipe registration failed: ' + $stderr.GetAwaiter().GetResult().Trim())
        }
        # The management command delegates ACL and service configuration to Windows
        # utilities, which may write informational lines before its final receipt.
        # Treat only the final JSON object as the typed registration result.
        $stdoutText = $stdout.GetAwaiter().GetResult()
        $jsonStart = $stdoutText.LastIndexOf("`n{", [StringComparison]::Ordinal)
        $registrationJson = if ($jsonStart -ge 0) {
            $stdoutText.Substring($jsonStart + 1)
        }
        elseif ($stdoutText.TrimStart().StartsWith('{', [StringComparison]::Ordinal)) {
            $stdoutText.TrimStart()
        }
        else { $null }
        if ([string]::IsNullOrWhiteSpace($registrationJson)) {
            throw 'Installed owner-pipe registration did not return a structured result.'
        }
        $registration = $registrationJson | ConvertFrom-Json
        if (-not $registration.registered -or $registration.ownerSid -cne $ownerSid -or $registration.started) {
            throw 'Installed owner-pipe registration returned an unexpected result.'
        }
    }
    finally {
        if (-not $childTerminalRecorded) {
            # Even a stdout, JSON or marker exception cannot release the original
            # child handle while the business operation may still be applying changes.
            while ($true) {
                $exited = try { $process.WaitForExit(2000) } catch { Start-Sleep -Seconds 2; $false }
                if ($exited) { break }
            }
            if ($null -eq $childStartTicks) {
                $childStartTicks = try { [string]$process.StartTime.ToUniversalTime().Ticks } catch { $null }
            }
            try {
                Write-CreateOnlyMarker $terminalPath @{
                    schema = 'pegasus.cerebral.owner-pipe-child-terminal/2'
                    caseId = $returnIdentity.caseId; setupRid = $returnIdentity.setupRid
                    processId = $childPid; startUtcTicks = $childStartTicks
                    exitCode = [int]$process.ExitCode; late = $late
                    markerFailureType = $markerFailureType
                    state = 'CHILD_TERMINAL_AFTER_CAPTURE_FAILURE'
                    recordedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                }
            } catch { }
        }
    }
    }
    finally {
        if ($null -ne $process) {
            while ($true) {
                $exited = try { $process.WaitForExit(2000) } catch { Start-Sleep -Seconds 2; $false }
                if ($exited) { break }
            }
            $process.Dispose()
            $process=$null
        }
    }

    $rawFolder = [Environment]::UserDomainName + '.' + [Environment]::UserName
    $invalid = [IO.Path]::GetInvalidFileNameChars()
    $safeFolder = -join $rawFolder.ToCharArray().ForEach({ if ($invalid -contains $_ -or [char]::IsControl($_)) { '_' } else { $_ } })
    $safeFolder = $safeFolder.Trim()
    if ([string]::IsNullOrWhiteSpace($safeFolder)) { $safeFolder = 'current-user' }
    $profileRoot = Join-Path $UserDataRoot $safeFolder
    [IO.Directory]::CreateDirectory($profileRoot) | Out-Null
    $connectionPath = Join-Path $profileRoot 'connection.json'
    $seeded = $false
    if (-not (Test-Path -LiteralPath $connectionPath -PathType Leaf)) {
        $connection = [ordered]@{
            schemaVersion = 1
            transport = 'installed-pipe'
            url = ''
            accessKey = ''
            localEndpoint = $null
            localDataDirectory = $null
            autoStartLocalServices = $true
        }
        $temporary = $connectionPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllText($temporary, ($connection | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
            [IO.File]::Move($temporary, $connectionPath)
            $seeded = $true
        }
        finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    }
    Write-Receipt $true 'The protected owner pipe is registered and the per-user connection declaration is ready.' @{
        ownerSid = $ownerSid
        connectionPath = $connectionPath
        connectionSeeded = $seeded
        returnCaseId = $returnIdentity.caseId
        returnSetupRid = $returnIdentity.setupRid
        childPid = $childPid
        childStartUtcTicks = $childStartTicks
        childTerminalPath = $terminalPath
        childLate = $late
    }
}
catch {
    Write-Receipt $false $_.Exception.Message @{
        errorType = $_.Exception.GetType().FullName
        line = $_.InvocationInfo.ScriptLineNumber
        diagnostic = $_.ScriptStackTrace
    }
    exit 1
}
