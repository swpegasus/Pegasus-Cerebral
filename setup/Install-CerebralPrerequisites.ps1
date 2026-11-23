[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [string[]]$Components = @(),
    [string[]]$ForceComponents = @(),
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($PackageRoot)
$manifestPath = Join-Path $root 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Prerequisite manifest is missing: $manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.contract -cne 'pegasus.cerebral.prerequisites' -or $manifest.schemaVersion -ne 1 -or $manifest.architecture -cne 'win-x64') {
    throw 'Unsupported prerequisite package contract or architecture.'
}
$selected = @($Components | ForEach-Object { $_.Split(',') } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
$forced = @($ForceComponents | ForEach-Object { $_.Split(',') } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
$known = @($manifest.components.id)
foreach ($id in $selected) { if ($id -notin $known) { throw "Unknown prerequisite component: $id" } }
foreach ($id in $forced) {
    if ($id -notin $known) { throw "Unknown forced prerequisite component: $id" }
    if ($id -notin $selected) { throw "A forced prerequisite must also be selected: $id" }
}

$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
$results = [Collections.Generic.List[object]]::new()
$reboot = $false
function Exists([string]$Path) { Test-Path -LiteralPath $Path -PathType Leaf }
function Encode-Argument([string]$Value) {
    # NSIS and several other native installers require switches such as /S to
    # arrive as bare tokens. Quote only values that actually need Windows
    # command-line quoting (not every token indiscriminately).
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}
function Run([string]$File, [string[]]$Arguments, [int[]]$Success = @(0, 1641, 3010)) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.Arguments = ($Arguments | ForEach-Object { Encode-Argument $_ }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    try { $process.WaitForExit(); $code = $process.ExitCode } finally { $process.Dispose() }
    if ($code -notin $Success) { throw "$([IO.Path]::GetFileName($File)) failed with exit code $code." }
    if ($code -in @(1641, 3010)) { $script:reboot = $true }
}
function Ready([string]$Id) {
    switch ($Id) {
        'dotnet-sdk' { $exe=Join-Path $programFiles 'dotnet\dotnet.exe'; if(-not (Exists $exe)){return $false}; return (& $exe --list-sdks 2>$null) -match '^10\.' }
        'powershell' { return Exists (Join-Path $programFiles 'PowerShell\7\pwsh.exe') }
        'git' { return Exists (Join-Path $programFiles 'Git\cmd\git.exe') }
        'node' { return Exists (Join-Path $programFiles 'nodejs\node.exe') }
        'python' { return Exists (Join-Path $programFiles 'Python314\python.exe') }
        'sevenzip' { return Exists (Join-Path $programFiles '7-Zip\7z.exe') }
        'vc-redist' { return (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue).Installed -eq 1 }
        'nsis' { return Exists (Join-Path $programFilesX86 'NSIS\makensis.exe') }
        'vs-buildtools' { return Exists (Join-Path $programFiles 'Microsoft Visual Studio\2026\BuildTools\MSBuild\Current\Bin\MSBuild.exe') }
        'wsl' { return (Exists (Join-Path $programFiles 'WSL\wsl.exe')) -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss') }
        default { return $false }
    }
}
function Install([string]$Id, [string]$File) {
    switch ($Id) {
        'dotnet-sdk' { Run $File @('/install','/quiet','/norestart') }
        'powershell' { Run 'msiexec.exe' @('/i',$File,'/qn','/norestart','ADD_PATH=1','REGISTER_MANIFEST=1','ENABLE_PSREMOTING=0') }
        'git' { Run $File @('/VERYSILENT','/NORESTART','/NOCANCEL','/SP-','/CLOSEAPPLICATIONS') }
        'node' { Run 'msiexec.exe' @('/i',$File,'/qn','/norestart') }
        'python' { Run $File @('/quiet','InstallAllUsers=1','PrependPath=1','Include_test=0','Include_launcher=1','InstallLauncherAllUsers=1') }
        'sevenzip' { Run $File @('/S') }
        'vc-redist' { Run $File @('/install','/quiet','/norestart') }
        'nsis' { Run $File @('/S') }
        'vs-buildtools' {
            $layout = [IO.Path]::GetDirectoryName($File)
            $channel = Join-Path $layout 'channelManifest.json'
            if (-not (Exists $channel)) { throw 'Visual Studio offline layout is incomplete: channelManifest.json is missing.' }
            Run $File @('--noWeb','--channelUri',$channel,'--wait','--quiet','--norestart','--installPath',(Join-Path $programFiles 'Microsoft Visual Studio\2026\BuildTools'),'--add','Microsoft.VisualStudio.Workload.VCTools','--includeRecommended')
        }
        'wsl' {
            foreach ($feature in @('VirtualMachinePlatform','Microsoft-Windows-Subsystem-Linux')) {
                Run 'dism.exe' @('/online','/enable-feature',('/featurename:'+$feature),'/all','/norestart')
            }
            Run 'msiexec.exe' @('/i',$File,'/qn','/norestart')
        }
        default { throw "No installer is defined for $Id" }
    }
}

$receiptDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReceiptPath))
[IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
try {
    foreach ($id in $selected) {
        $item = @($manifest.components | Where-Object id -eq $id)[0]
        $relative = ([string]$item.file).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $file = [IO.Path]::GetFullPath((Join-Path $root $relative))
        $rootPrefix = $root.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
        if (-not $file.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Prerequisite path escaped its package root: $id" }
        if (-not (Exists $file)) { throw "Selected prerequisite is missing from the offline kit: $id ($relative)" }
        $before = Ready $id
        # A normal requested component is installed only when absent. The setup
        # state probe explicitly adds older components to ForceComponents so an
        # offline update remains distinct from an idempotent repair.
        if (-not $before -or $id -in $forced) { Install $id $file }
        $after = Ready $id
        if (-not $after -and $id -ne 'wsl') { throw "Prerequisite did not pass its post-install probe: $id" }
        $results.Add([pscustomobject]@{ id=$id; alreadyPresent=$before; ready=$after; version=[string]$item.version })
    }

    $receipt = [pscustomobject]@{ contract='pegasus.cerebral.prerequisite-install/1'; at=[DateTimeOffset]::UtcNow.ToString('O'); ok=$true; rebootRequired=$reboot; components=$results }
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
    $receipt | ConvertTo-Json -Compress -Depth 6
} catch {
    $failure = [pscustomobject]@{
        contract='pegasus.cerebral.prerequisite-install/1'
        at=[DateTimeOffset]::UtcNow.ToString('O')
        ok=$false
        rebootRequired=$reboot
        error=$_.Exception.Message
        components=$results
    }
    $failure | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
    throw
}
