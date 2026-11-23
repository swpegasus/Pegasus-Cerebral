[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedWorkerDirectory,
    [Parameter(Mandatory)][string]$BuildWorkerDirectory,
    [ValidateSet('Fresh','PrepareManaged','VerifyManaged','WindowsPowerShell')][string]$Phase = 'Fresh'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$PublishedWorkerDirectory = [IO.Path]::GetFullPath($PublishedWorkerDirectory).TrimEnd('\','/')
$BuildWorkerDirectory = [IO.Path]::GetFullPath($BuildWorkerDirectory).TrimEnd('\','/')
foreach ($path in @($PublishedWorkerDirectory, $BuildWorkerDirectory)) {
    if (-not $path.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Use only task-owned build/publish directories below artifacts: $path"
    }
}
$worker = Join-Path $PublishedWorkerDirectory 'Cerebral.Worker.exe'
$statePath = Join-Path ([IO.Path]::GetDirectoryName($PublishedWorkerDirectory)) 'engine-preservation-fixture.json'

function Assert-Fixture([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Read-EngineSnapshot([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -Force | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Root, $_.FullName)
        if ($_.PSIsContainer) { $relative + '\=directory' }
        else { $relative + '=' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    } | Sort-Object)
}

function Invoke-Inspection([string[]]$Arguments, [bool]$ExpectedSuccess = $true) {
    $start = [Diagnostics.ProcessStartInfo]::new($worker)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Fixture engine inspection timed out.' }
        $output = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
        if ($ExpectedSuccess) {
            Assert-Fixture ($process.ExitCode -eq 0) "Static inspection failed: $errorText"
            return ($output | ConvertFrom-Json)
        }
        Assert-Fixture ($process.ExitCode -ne 0) 'Static inspection admitted invalid command arguments.'
    }
    finally { $process.Dispose() }
}

function Inspect-Root([string]$Root) {
    Invoke-Inspection @('manage','inspect-engine-packages','--directory',$Root)
}

if ($Phase -eq 'WindowsPowerShell') {
    $nativePowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Assert-Fixture (Test-Path -LiteralPath $nativePowerShell -PathType Leaf) 'Windows PowerShell is unavailable; this fixture cannot pass.'
    $scratch = Join-Path ([IO.Path]::GetDirectoryName($PublishedWorkerDirectory)) ('core-ps5-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    $child = @'
param([string]$Helper,[string]$Worker,[string]$Root)
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($Helper,[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw 'Core helper is not valid Windows PowerShell syntax.' }
$definitions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in @('Get-EngineTreeSnapshot','Get-ValidatedEnginePackages','Assert-EngineTreeSnapshot')},$true))
if ($definitions.Count -ne 3) { throw 'Core engine helpers were not found.' }
. ([ScriptBlock]::Create(($definitions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine))
$before=Get-EngineTreeSnapshot $Root
$report=Get-ValidatedEnginePackages $Worker $Root
Assert-EngineTreeSnapshot $before (Get-EngineTreeSnapshot $Root) 'Windows PowerShell read-only preflight'
if (-not ($report.slots | Where-Object { $_.available })) { throw 'No admitted engine package was reported.' }
if ($null -ne (Get-EngineTreeSnapshot (Join-Path $Root 'missing-fixture-path'))) { throw 'Missing path inspection was not read-only.' }
Write-Host 'PASS: native Windows PowerShell parsed the core helper and ran its exact snapshot/inspection functions without mutation.'
'@
    [IO.File]::WriteAllText($scratch, $child)
    try {
        $start = [Diagnostics.ProcessStartInfo]::new($nativePowerShell)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$scratch,
            '-Helper',(Join-Path $projectRoot 'setup\Install-CerebralCore.ps1'),'-Worker',$worker,
            '-Root',(Join-Path $PublishedWorkerDirectory 'Engines'))) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Native PowerShell fixture timed out.' }
            $output = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
            Assert-Fixture ($process.ExitCode -eq 0) "Native PowerShell compatibility failed: $errorText"
            Write-Host $output.Trim()
        }
        finally { $process.Dispose() }
    }
    finally { if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Force } }
}
elseif ($Phase -eq 'Fresh') {
    $expectedConfig = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'src\Cerebral.Engine.Default\engine-settings.ini') -Algorithm SHA256).Hash
    foreach ($output in @($PublishedWorkerDirectory, $BuildWorkerDirectory)) {
        $root = Join-Path $output 'Engines'
        $before = Read-EngineSnapshot $root
        $status = Inspect-Root $root
        $slot = @($status.slots | Where-Object slot -eq 'A')[0]
        Assert-Fixture ($slot.available -and $slot.package.id -eq 'pegasus.default-engine' -and $slot.package.version -eq '1.0.0') 'Fresh output lacks an admitted default A.'
        Assert-Fixture ($slot.package.configurationSha256.ToUpperInvariant() -ceq $expectedConfig) 'Fresh output default configuration differs from the packaged source.'
        Assert-Fixture (($before -join '|') -ceq ((Read-EngineSnapshot $root) -join '|')) 'Static inspection mutated package or selection state.'
    }
    $root = Join-Path $PublishedWorkerDirectory 'Engines'
    foreach ($arguments in @(
        ,@('manage','inspect-engine-packages','--directory',$root,'--directory',$root),
        ,@('manage','inspect-engine-packages','--directory',$root,'--extra'),
        ,@('manage','inspect-engine-packages','--directory','Engines'),
        ,@('manage','inspect-engine-packages','--directory',[IO.Path]::GetPathRoot($root))
    )) { Invoke-Inspection $arguments $false }
    Write-Host 'PASS: real non-selfcontained build/publish default A packages, exact hash/config admission, read-only inspection, strict argument rejection.'
}
elseif ($Phase -eq 'PrepareManaged') {
    $expected = [ordered]@{}
    foreach ($output in @($PublishedWorkerDirectory, $BuildWorkerDirectory)) {
        $root = Join-Path $output 'Engines'
        $slotA = Join-Path $root 'A'
        $slotB = Join-Path $root 'B'
        Assert-Fixture (-not (Test-Path -LiteralPath $slotB)) 'PrepareManaged requires an absent fixture B; do not overwrite a prior fixture.'
        Copy-Item -LiteralPath $slotA -Destination $slotB -Recurse
        foreach ($slot in @('A','B')) {
            $directory = Join-Path $root $slot
            $assembly = Join-Path $directory 'Cerebral.Engine.Default.dll'
            $settings = Join-Path $directory 'engine-settings.ini'
            [IO.File]::AppendAllText($assembly, "fixture-owned-$slot")
            [IO.File]::WriteAllText($settings, "[settings]" + [Environment]::NewLine + "label=fixture-owned-$slot" + [Environment]::NewLine)
            $assemblyHash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
            $configurationHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
            $manifest = Join-Path $directory 'engine.ini'
            $content = [IO.File]::ReadAllText($manifest) -replace 'assembly-sha256=[a-fA-F0-9]+', "assembly-sha256=$assemblyHash"
            $content = $content -replace 'configuration-sha256=[a-fA-F0-9]+', "configuration-sha256=$configurationHash"
            [IO.File]::WriteAllText($manifest, $content)
        }
        [IO.Directory]::CreateDirectory((Join-Path $root 'B\fixture-empty')) | Out-Null
        Inspect-Root $root | Out-Null
        $expected[$output] = @(Read-EngineSnapshot $root)
    }
    [IO.File]::WriteAllText($statePath, ($expected | ConvertTo-Json -Depth 4))
    Write-Host 'Prepared exact custom A/B byte identities in the isolated build and publish outputs; repeat the typed build/publish, then run VerifyManaged.'
}
else {
    $expected = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($output in @($PublishedWorkerDirectory, $BuildWorkerDirectory)) {
        $root = Join-Path $output 'Engines'
        Assert-Fixture (($expected[$output] -join '|') -ceq ((Read-EngineSnapshot $root) -join '|')) 'Repeated core build/publish changed a managed engine slot.'
        Inspect-Root $root | Out-Null
    }
    Write-Host 'PASS: repeated real build/publish preserved exact managed A/B code/config/manifest hashes and empty directories.'
}
