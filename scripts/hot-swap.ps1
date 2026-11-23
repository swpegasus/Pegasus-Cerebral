[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\deploy\win-x64'),
    [string]$InstallDirectory,
    [string]$DataDirectory,
    [string]$ExpectedVersion,
    [string]$ReceiptPath,
    [switch]$InspectOnly
)

$ErrorActionPreference = 'Stop'
$started = [DateTimeOffset]::UtcNow
$prior = [ordered]@{ ServiceRunning = $false; ConnectorRunning = $false; IntegratorRunning = $false; OrchestratorRunning = $false }

function Write-Receipt([bool]$Success, [string]$State, [string]$Message) {
    $receipt = [ordered]@{
        success = $Success; state = $State; message = $Message; version = $ExpectedVersion
        installDirectory = $InstallDirectory; dataDirectory = $DataDirectory
        startedUtc = $started; finishedUtc = [DateTimeOffset]::UtcNow
    }
    $json = $receipt | ConvertTo-Json -Compress
    if ($ReceiptPath) {
        $target = [IO.Path]::GetFullPath($ReceiptPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::WriteAllText($target, $json, [Text.UTF8Encoding]::new($false))
    }
    Write-Output $json
}

function Invoke-Worker([string]$Executable, [string[]]$Arguments, [bool]$Required = $true) {
    $detached = $Arguments.Count -ge 4 -and $Arguments[0] -eq 'manage' -and
        $Arguments[1] -eq 'connector' -and $Arguments[2] -eq '--action' -and $Arguments[3] -eq 'start'
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = -not $detached; $start.RedirectStandardError = -not $detached
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = if ($start.RedirectStandardOutput) { $process.StandardOutput.ReadToEndAsync() } else { $null }
        $stderr = if ($start.RedirectStandardError) { $process.StandardError.ReadToEndAsync() } else { $null }
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); $process.WaitForExit(); throw 'Cerebral Worker management timed out.' }
        $out = if ($stdout) { $stdout.GetAwaiter().GetResult().Trim() } else { '' }
        $err = if ($stderr) { $stderr.GetAwaiter().GetResult().Trim() } else { '' }
        if ($Required -and $process.ExitCode -ne 0) { throw "Cerebral Worker management failed ($($process.ExitCode)): $err $out" }
        return [ordered]@{ ExitCode=$process.ExitCode; Output=$out; Error=$err }
    } finally { $process.Dispose() }
}

function Resolve-Worker([string]$Root) {
    foreach ($relative in @('Applications\Worker\Cerebral.Worker.exe','Cerebral.Worker.exe','Worker\Cerebral.Worker.exe')) {
        $candidate = Join-Path $Root $relative
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return Join-Path $Root 'Applications\Worker\Cerebral.Worker.exe'
}

function Resolve-InstalledProcess([string]$Name) {
    $prefix = $InstallDirectory.TrimEnd('\') + '\'
    @(Get-CimInstance Win32_Process -Filter "Name='$Name.exe'" | Where-Object {
        $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    })
}

function Stop-InstalledDesktop([string]$Name) {
    foreach ($entry in Resolve-InstalledProcess $Name) {
        $process = Get-Process -Id $entry.ProcessId -ErrorAction Stop
        if ($process.MainWindowHandle -ne 0) {
            $null = $process.CloseMainWindow()
            if ($process.WaitForExit(10000)) { continue }
        }
        Stop-Process -Id $entry.ProcessId -Force -ErrorAction Stop
        $process.WaitForExit(10000) | Out-Null
    }
}

function Wait-InstalledProcessesExit([TimeSpan]$Timeout) {
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = @(
            Resolve-InstalledProcess 'Cerebral.Worker'
            Resolve-InstalledProcess 'Cerebral.Integrator'
            Resolve-InstalledProcess 'Cerebral.Orchestrator'
        )
        if ($remaining.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    } while ($stopwatch.Elapsed -lt $Timeout)

    $owners = $remaining | ForEach-Object { "$($_.Name) (PID $($_.ProcessId))" }
    throw "Cerebral update preparation left a process owning installed program files: $($owners -join ', ')."
}

function Start-PriorDesktop {
    if ($prior.IntegratorRunning) { Start-Process -FilePath (Join-Path $InstallDirectory 'Applications\Desktop\Cerebral.Integrator.exe') }
    if ($prior.OrchestratorRunning) { Start-Process -FilePath (Join-Path $InstallDirectory 'Applications\Desktop\Cerebral.Orchestrator.exe') }
}

try {
    $registered = Get-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Pegasus\Cerebral'
    if (-not $InstallDirectory) { $InstallDirectory = $registered.InstallDir }
    if (-not $DataDirectory) { $DataDirectory = $registered.DataDir }
    if (-not $ExpectedVersion) { $ExpectedVersion = ([Xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
    $SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
    $InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
    $DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
    if ($InstallDirectory -eq [IO.Path]::GetPathRoot($InstallDirectory) -or $DataDirectory -eq [IO.Path]::GetPathRoot($DataDirectory)) {
        throw 'Cerebral program and data paths must be product-specific directories.'
    }
    $marker = Join-Path $InstallDirectory 'installation.ini'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        -not (Select-String -LiteralPath $marker -Pattern '^Product=PegasusCerebral$' -Quiet)) {
        throw "The target is not a recognized Pegasus Cerebral installation: $InstallDirectory"
    }
    foreach ($app in @{
        Worker = 'Applications\Worker\Cerebral.Worker.exe'
        Integrator = 'Applications\Desktop\Cerebral.Integrator.exe'
        Orchestrator = 'Applications\Desktop\Cerebral.Orchestrator.exe'
    }.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $app.Value) -PathType Leaf)) {
            throw "The Cerebral payload is incomplete: $($app.Value)"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory 'Resources\Branding\Cerebral\brand.ini') -PathType Leaf)) {
        throw 'The merged Cerebral payload is missing its branding resources.'
    }
    foreach ($manifest in @('service-plugin.json','desktop-plugin.json')) {
        if (Test-Path -LiteralPath (Join-Path $SourceDirectory $manifest) -PathType Leaf) {
            throw "A plugin manifest was flattened into the program root: $manifest"
        }
    }

    $service = Get-Service -Name PegasusCerebral -ErrorAction Stop
    $prior.ServiceRunning = $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running
    $prior.IntegratorRunning = (Resolve-InstalledProcess 'Cerebral.Integrator').Count -gt 0
    $prior.OrchestratorRunning = (Resolve-InstalledProcess 'Cerebral.Orchestrator').Count -gt 0
    $prior.ConnectorRunning = @((Resolve-InstalledProcess 'Cerebral.Worker') | Where-Object CommandLine -Like '* connector *').Count -gt 0
    if ($InspectOnly) {
        Write-Receipt $true 'ready' "Validated the grouped $ExpectedVersion payload and current installation; no process or file was changed."
        exit 0
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Hot swap requires an elevated PowerShell process.'
    }

    $oldWorker = Resolve-Worker $InstallDirectory
    Invoke-Worker $oldWorker @('manage','connector','--action','stop') $false | Out-Null
    Stop-InstalledDesktop 'Cerebral.Orchestrator'
    Stop-InstalledDesktop 'Cerebral.Integrator'
    Invoke-Worker $oldWorker @('manage','stop') $true | Out-Null
    (Get-Service -Name PegasusCerebral).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(40))
    Wait-InstalledProcessesExit ([TimeSpan]::FromSeconds(30))

    & (Join-Path $PSScriptRoot '..\setup\Install-CerebralCore.ps1') -SourceRoot $SourceDirectory -DestinationRoot $InstallDirectory
    $newWorker = Resolve-Worker $InstallDirectory
    Invoke-Worker $newWorker @('manage','start') $true | Out-Null
    Invoke-Worker $newWorker @('manage','verify','--data',$DataDirectory,'--expected-version',$ExpectedVersion) $true | Out-Null
    if ($prior.ConnectorRunning) { Invoke-Worker $newWorker @('manage','connector','--action','start') $true | Out-Null }

    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class CerebralIni {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WritePrivateProfileString(string section, string key, string value, string path);
}
'@
    if (-not [CerebralIni]::WritePrivateProfileString('Cerebral','Version',$ExpectedVersion,$marker)) { throw 'The installed version marker could not be updated.' }
    Set-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Pegasus\Cerebral' -Name Version -Value $ExpectedVersion
    Set-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral' -Name DisplayVersion -Value $ExpectedVersion
    Start-PriorDesktop
    Write-Receipt $true 'complete' "Pegasus Cerebral $ExpectedVersion was replaced with the grouped program layout, restarted, and verified."
    exit 0
}
catch {
    $failure = $_.Exception.Message
    try {
        $worker = Resolve-Worker $InstallDirectory
        if ($prior.ServiceRunning -and (Test-Path -LiteralPath $worker -PathType Leaf)) { Invoke-Worker $worker @('manage','start') $false | Out-Null }
        if ($prior.ConnectorRunning -and (Test-Path -LiteralPath $worker -PathType Leaf)) { Invoke-Worker $worker @('manage','connector','--action','start') $false | Out-Null }
        Start-PriorDesktop
    } catch { $failure += ' Prior-state recovery also failed: ' + $_.Exception.Message }
    Write-Receipt $false 'failed' $failure
    exit 1
}
