[CmdletBinding()]
param([ValidateRange(1,600)][int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$setup = Join-Path $projectRoot 'deploy\Cerebral-Setup.exe'
$testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.runtime\installer-smoke'))
if (-not $testRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test target escaped workspace.' }
if (Test-Path -LiteralPath $testRoot) { throw "Test folder already exists: $testRoot. Inspect and remove that exact test folder before another run." }

# Keep the dependency check read-only; no fixture registration or live Cortex writes.
function Get-CortexSnapshot {
    $probe = [Diagnostics.ProcessStartInfo]::new((Join-Path $projectRoot 'deploy\win-x64\Applications\Worker\Cerebral.Worker.exe'))
    $probe.UseShellExecute = $false
    $probe.CreateNoWindow = $true
    $probe.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $probe.RedirectStandardOutput = $true
    $probe.RedirectStandardError = $true
    $probe.ArgumentList.Add('manage')
    $probe.ArgumentList.Add('cortex-prerequisite')
    $process = [Diagnostics.Process]::Start($probe)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
            throw 'Cortex prerequisite check timed out.'
        }
        if ($process.ExitCode -ne 0) { throw "Install or update Cortex before running this test: $($errorOutput.GetAwaiter().GetResult())" }
        $location = $output.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($location.ready -ne $true) { throw 'Cortex prerequisite check did not confirm readiness.' }
    } finally { $process.Dispose() }

    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $machine.OpenSubKey('SOFTWARE\Pegasus\Cortex', $false)
        if (-not $key) { throw 'Cortex registration disappeared during its read-only snapshot.' }
        try {
            $registration = [ordered]@{}
            $registeredNames = $key.GetValueNames()
            foreach ($name in @('InstallLocation', 'DataLocation')) {
                if ($name -eq 'DataLocation' -and $registeredNames -notcontains $name) {
                    # Legacy Cortex may resolve its default without registering DataLocation.
                    $registration[$name] = [ordered]@{ present = $false }
                    continue
                }
                $registration[$name] = [ordered]@{
                    present = $true
                    kind = $key.GetValueKind($name).ToString()
                    value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                }
            }
        } finally { $key.Dispose() }
    } finally { $machine.Dispose() }
    $files = foreach ($path in @($location.executablePath, $location.paoPath, (Join-Path $location.installDirectory 'state\install\active.v1.json'))) {
        $file = Get-Item -LiteralPath $path
        [ordered]@{
            path = $file.FullName
            bytes = $file.Length
            lastWriteUtc = $file.LastWriteTimeUtc.ToString('O')
        }
    }
    [ordered]@{ registration = $registration; installation = $location; files = @($files) } | ConvertTo-Json -Depth 6 -Compress
}
$cortexBefore = Get-CortexSnapshot

New-Item -ItemType Directory -Path $testRoot | Out-Null
$install = Join-Path $testRoot 'install'
$state = Join-Path $testRoot 'state.txt'
Set-Content -LiteralPath $state -Value 'application data remains outside replaceable output'
function Invoke-Setup([string]$Executable, [string]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw 'Installer timed out; inspect the task-owned test directory.'
    }
    $process.ExitCode
}
if ((Invoke-Setup $setup "/S /PORTABLE /D=$install") -ne 0) { throw 'Portable installation failed.' }
$dll = Join-Path $install 'Applications\Worker\Cerebral.Core.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Installer did not use the explicit test destination.' }
$data = Join-Path $testRoot 'data'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$url = "http://127.0.0.1:$port"
$worker = Join-Path $install 'Applications\Worker\Cerebral.Worker.exe'
$pathsStart = [Diagnostics.ProcessStartInfo]::new($worker)
$pathsStart.UseShellExecute = $false
$pathsStart.CreateNoWindow = $true
$pathsStart.RedirectStandardOutput = $true
$pathsStart.RedirectStandardError = $true
$pathsStart.ArgumentList.Add('manage')
$pathsStart.ArgumentList.Add('installation-paths')
$pathsProcess = [Diagnostics.Process]::Start($pathsStart)
try {
    $pathsOutput = $pathsProcess.StandardOutput.ReadToEndAsync()
    $pathsError = $pathsProcess.StandardError.ReadToEndAsync()
    if (-not $pathsProcess.WaitForExit(30000) -or $pathsProcess.ExitCode -ne 0) {
        throw "Installed portable path inspection failed: $($pathsError.GetAwaiter().GetResult())"
    }
    $paths = $pathsOutput.GetAwaiter().GetResult() | ConvertFrom-Json
    if ($paths.portable -ne $true -or $paths.base -ne $install -or $paths.install -ne $install -or
        $paths.data -ne (Join-Path $install 'Data') -or $paths.tools -ne (Join-Path $install 'Tools') -or
        $paths.userData -ne (Join-Path $install 'UserData')) {
        throw 'Portable executables did not resolve their roots from installation.ini.'
    }
} finally { $pathsProcess.Dispose() }
foreach ($directory in @('Data', 'Models', 'Tools', 'Shared', 'UserData')) {
    if (-not (Test-Path -LiteralPath (Join-Path $install $directory) -PathType Container)) {
        throw "Portable installation did not create its $directory root."
    }
}
$bundledPlugins = @(Get-ChildItem -LiteralPath (Join-Path $install 'Plugins\Desktop') -Directory)
if ($bundledPlugins.Count -ne 5) { throw 'Silent portable installation did not retain all default-selected bundled plugins.' }
$start = [Diagnostics.ProcessStartInfo]::new($worker)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('--data', $data, '--url', $url)) { $start.ArgumentList.Add($argument) }
$running = [Diagnostics.Process]::Start($start)
$stdout = $running.StandardOutput.ReadToEndAsync()
$stderr = $running.StandardError.ReadToEndAsync()
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($running.HasExited) { throw "Installed worker exited: $($stderr.GetAwaiter().GetResult())" }
        try {
            $health = Invoke-RestMethod -Uri "$url/health" -TimeoutSec 2
            if ($health.product -ne 'Pegasus Cerebral' -or $health.dataDirectory -ne $data) { throw 'Unexpected installed worker identity.' }
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'Installed worker did not become healthy.' }
    $verifyStart = [Diagnostics.ProcessStartInfo]::new($worker)
    $verifyStart.UseShellExecute = $false
    $verifyStart.CreateNoWindow = $true
    $verifyStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $verifyStart.RedirectStandardOutput = $true
    $verifyStart.RedirectStandardError = $true
    foreach ($argument in @('manage', 'verify', '--data', $data, '--url', $url)) { $verifyStart.ArgumentList.Add($argument) }
    $verification = [Diagnostics.Process]::Start($verifyStart)
    try {
        $verifyOutput = $verification.StandardOutput.ReadToEndAsync()
        $verifyError = $verification.StandardError.ReadToEndAsync()
        if (-not $verification.WaitForExit(50000)) {
            $verification.Kill($true)
            $stopped = $verification.WaitForExit(5000)
            throw "Installed worker verification timed out after 50 seconds; verifier stop confirmed: $stopped."
        }
        $verifiedOutput = $verifyOutput.GetAwaiter().GetResult()
        $verifiedError = $verifyError.GetAwaiter().GetResult()
        if ($verification.ExitCode -ne 0) {
            $outputTail = $verifiedOutput.Substring([Math]::Max(0, $verifiedOutput.Length - 2048))
            $errorTail = $verifiedError.Substring([Math]::Max(0, $verifiedError.Length - 2048))
            throw "Installed worker verification failed with exit $($verification.ExitCode). stderr: $errorTail stdout: $outputTail"
        }
        $verified = $verifiedOutput | ConvertFrom-Json
        if ($verified.verified -ne $true -or $verified.product -ne 'Pegasus Cerebral' -or $verified.dataDirectory -ne $data) {
            throw 'Installed worker verification did not return a confirmed matching product/data report.'
        }
    } finally { $verification.Dispose() }
} finally {
    if (-not $running.HasExited) { $running.Kill($true); $running.WaitForExit() }
    $running.Dispose()
}
$expected = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'deploy\win-x64\Applications\Worker\Cerebral.Core.dll'))
[IO.File]::WriteAllText($dll, 'test-owned stale file')
if ((Invoke-Setup $setup "/S /PORTABLE /D=$install") -ne 0) { throw 'Portable update failed.' }
if (-not [Collections.StructuralComparisons]::StructuralEqualityComparer.Equals([IO.File]::ReadAllBytes($dll), $expected)) { throw 'Update did not replace the stale file.' }
$lock = [IO.File]::Open($dll, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
try { $lockedExit = Invoke-Setup $setup "/S /PORTABLE /D=$install" }
finally { $lock.Dispose() }
if ($lockedExit -eq 0) { throw 'Installer incorrectly reported success with a locked payload file.' }
if ((Invoke-Setup $setup "/S /PORTABLE /D=$install") -ne 0) { throw 'Repair after a locked-file failure failed.' }
if (-not (Test-Path -LiteralPath $state)) { throw 'Installer modified application state.' }
# _?= runs NSIS uninstall in-place so the test can observe its actual completion.
$uninstaller = Join-Path $install 'Uninstall.exe'
if ((Invoke-Setup $uninstaller "/S _?=$install") -ne 0) { throw 'Portable uninstall failed.' }
if (Test-Path -LiteralPath (Join-Path $install 'Applications\Worker\Cerebral.Worker.exe')) { throw 'Uninstall left the Worker executable.' }
if (-not (Test-Path -LiteralPath $state)) { throw 'Uninstall removed unrelated data.' }
$cortexAfter = Get-CortexSnapshot
if ($cortexAfter -cne $cortexBefore) { throw 'Cortex registration or checked product files changed during the portable test. Inspect concurrent Cortex updates before attributing the change.' }
# NSIS cannot remove its own in-place executable; remove only this exact test-owned file.
if (Test-Path -LiteralPath $uninstaller) { Remove-Item -LiteralPath $uninstaller -Force }
$resolvedInstall = [IO.Path]::GetFullPath($install)
if (-not $resolvedInstall.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Portable install cleanup escaped its owned directory.'
}
# Portable data, tools, models and resource receipts deliberately survive the
# product uninstaller. This smoke fixture owns its entire isolated target, so
# remove those retained roots only after the uninstaller behavior was checked.
if (Test-Path -LiteralPath $resolvedInstall) { Remove-Item -LiteralPath $resolvedInstall -Recurse -Force }
Remove-Item -LiteralPath $state
# This exact data directory was created by the test-owned worker above.
$resolvedData = [IO.Path]::GetFullPath($data)
if (-not $resolvedData.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test data escaped its owned directory.' }
Remove-Item -LiteralPath $resolvedData -Recurse -Force
Remove-Item -LiteralPath $testRoot
Write-Host 'PASS: NSIS portable install, marker-based dynamic roots, five default plugins, installed-worker HTTP health/version, stale-file update, locked-file failure, retry repair, uninstall, state preservation, and unchanged Cortex registration/product files.'
