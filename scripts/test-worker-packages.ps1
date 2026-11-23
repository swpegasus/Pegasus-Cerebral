[CmdletBinding()]
param(
    [string]$PackageRoot = (Join-Path $PSScriptRoot '..\deploy\Workers'),
    [string]$ExpectedVersion = '',
    [switch]$SkipRunnableChecks
)

$ErrorActionPreference = 'Stop'
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw
    $ExpectedVersion = [string]$props.Project.PropertyGroup.Version
}

function Read-PeMachine([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a PE image: $Path" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature: $Path" }
        return $reader.ReadUInt16()
    }
    finally { $stream.Dispose() }
}

function Read-ElfMachine([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        $magic = $reader.ReadBytes(4)
        if ($magic.Count -ne 4 -or $magic[0] -ne 0x7F -or $magic[1] -ne 0x45 -or $magic[2] -ne 0x4C -or $magic[3] -ne 0x46) {
            throw "Not an ELF image: $Path"
        }
        if ($reader.ReadByte() -ne 2 -or $reader.ReadByte() -ne 1) {
            throw "Linux ARM64 Worker must be a little-endian 64-bit ELF image: $Path"
        }
        $stream.Position = 18
        return $reader.ReadUInt16()
    }
    finally { $stream.Dispose() }
}

function Invoke-WorkerHealth([string]$Worker, [string]$Runtime) {
    $temporaryData = Join-Path ([IO.Path]::GetTempPath()) ('cerebral-worker-package-' + $Runtime + '-' + [Guid]::NewGuid().ToString('N'))
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $start = [Diagnostics.ProcessStartInfo]::new($Worker)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--data',$temporaryData,'--url',"http://127.0.0.1:$port")) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $client = [Net.Http.HttpClient]::new()
    try {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
        $health = $null
        do {
            if ($process.HasExited) {
                $errorText = $process.StandardError.ReadToEnd()
                throw "$Runtime Worker exited before health acceptance: $errorText"
            }
            try {
                $json = $client.GetStringAsync("http://127.0.0.1:$port/health").GetAwaiter().GetResult()
                $health = $json | ConvertFrom-Json
            } catch [Net.Http.HttpRequestException] { Start-Sleep -Milliseconds 150 }
        } while ($null -eq $health -and [DateTimeOffset]::UtcNow -lt $deadline)
        if ($null -eq $health) { throw "$Runtime Worker did not become healthy within 60 seconds." }
        if ($health.product -cne 'Pegasus Cerebral' -or $health.version -cne $ExpectedVersion) {
            throw "$Runtime Worker health identity/version mismatch."
        }
        return [string]$health.architecture
    }
    finally {
        $client.Dispose()
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(10000) | Out-Null }
        $process.Dispose()
        if (Test-Path -LiteralPath $temporaryData) { Remove-Item -LiteralPath $temporaryData -Recurse -Force }
    }
}

$targets = @(
    [pscustomobject]@{ Rid='win-x64'; Machine=0x8664; Runnable=[Environment]::Is64BitOperatingSystem },
    [pscustomobject]@{ Rid='win-arm64'; Machine=0xAA64; Runnable=$env:PROCESSOR_ARCHITECTURE -eq 'ARM64' },
    [pscustomobject]@{ Rid='win-x86'; Machine=0x014C; Runnable=$true }
)
$results = [Collections.Generic.List[object]]::new()
foreach ($target in $targets) {
    $root = Join-Path $PackageRoot $target.Rid
    $worker = Join-Path $root 'Cerebral.Worker.exe'
    foreach ($required in @($worker, (Join-Path $root 'Cerebral.Worker.dll'), (Join-Path $root 'Cerebral.Core.dll'), (Join-Path $root 'Cerebral.Worker.runtimeconfig.json'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing $($target.Rid) Worker file: $required" }
    }
    $machine = Read-PeMachine $worker
    if ($machine -ne $target.Machine) { throw ('Worker PE architecture mismatch for {0}: expected 0x{1:X4}, observed 0x{2:X4}.' -f $target.Rid,$target.Machine,$machine) }
    $version = $null
    $liveArchitecture = $null
    if (-not $SkipRunnableChecks -and $target.Runnable) {
        $start = [Diagnostics.ProcessStartInfo]::new($worker)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add('--version')
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "$($target.Rid) Worker version probe timed out." }
            if ($process.ExitCode -ne 0) { throw "$($target.Rid) Worker version probe failed: $($stderr.GetAwaiter().GetResult())" }
            $versionText = $stdout.GetAwaiter().GetResult().Trim()
            $prefix = 'Pegasus Cerebral Worker '
            if (-not $versionText.StartsWith($prefix, [StringComparison]::Ordinal)) { throw "$($target.Rid) Worker returned an unexpected version line: $versionText" }
            $version = $versionText.Substring($prefix.Length)
            if ($version -ne $ExpectedVersion) { throw "$($target.Rid) Worker reported version $version; expected $ExpectedVersion." }
        }
        finally { $process.Dispose() }
        $liveArchitecture = Invoke-WorkerHealth $worker $target.Rid
    }
    $results.Add([pscustomobject]@{ Runtime=$target.Rid; Machine=('0x{0:X4}' -f $machine); Executed=($null -ne $version); Architecture=$liveArchitecture; Version=$version; Files=@(Get-ChildItem -LiteralPath $root -File -Recurse).Count })
}
$linuxRoot = Join-Path $PackageRoot 'linux-arm64'
$linuxWorkerRoot = Join-Path $linuxRoot 'Worker'
$linuxWorker = Join-Path $linuxWorkerRoot 'Cerebral.Worker'
foreach ($required in @($linuxWorker, (Join-Path $linuxWorkerRoot 'Cerebral.Worker.dll'), (Join-Path $linuxWorkerRoot 'Cerebral.Core.dll'), (Join-Path $linuxWorkerRoot 'Cerebral.Worker.runtimeconfig.json'), (Join-Path $linuxRoot 'install-linux.sh'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing linux-arm64 Worker file: $required" }
}
$linuxHelper = Get-Content -LiteralPath (Join-Path $linuxRoot 'install-linux.sh') -Raw
foreach ($requiredContract in @(
    'mv "$install_dir" "$rollback"',
    'mv "$prepared" "$install_dir"',
    '--auto-port',
    'worker-endpoint.txt',
    'http://127.0.0.1:$selected_port/health',
    'Deployment verification failed; restoring the previous generation.'
)) {
    if (-not $linuxHelper.Contains($requiredContract, [StringComparison]::Ordinal)) {
        throw "Linux deployment helper is missing its atomic update contract: $requiredContract"
    }
}
if ($linuxHelper.Contains('cp -R "$source_dir/." "$install_dir/"', [StringComparison]::Ordinal)) {
    throw 'Linux deployment helper still overlays the active installation in place.'
}
$linuxMachine = Read-ElfMachine $linuxWorker
if ($linuxMachine -ne 0x00B7) {
    throw ('Worker ELF architecture mismatch for linux-arm64: expected 0x00B7, observed 0x{0:X4}.' -f $linuxMachine)
}
$results.Add([pscustomobject]@{ Runtime='linux-arm64'; Machine=('0x{0:X4}' -f $linuxMachine); Executed=$false; Architecture='AArch64'; Version=$null; Files=@(Get-ChildItem -LiteralPath $linuxRoot -File -Recurse).Count })
$results | Format-Table -AutoSize
Write-Host "Standalone Worker packages passed architecture and payload validation for $($targets.Count) Windows runtimes and one optional Linux ARM64 runtime."
