[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DestinationRoot,
    [string]$DeployRoot = (Join-Path $PSScriptRoot '..\deploy'),
    [string]$PrerequisiteRoot = (Join-Path $PSScriptRoot '..\.runtime\prerequisites'),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$kitModule = Join-Path $PSScriptRoot 'Cerebral.InstallationKit.psm1'
$behaviorPackModule = Join-Path $PSScriptRoot 'Cerebral.CortexBehaviorPacks.psm1'
$payloadReuseModule = Join-Path $PSScriptRoot '..\setup\Cerebral.PayloadReuse.psm1'
Import-Module $kitModule -Force
Import-Module $behaviorPackModule -Force
Import-Module $payloadReuseModule -Force
$DeployRoot = [IO.Path]::GetFullPath($DeployRoot)
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
$destinationParent = [IO.Path]::GetDirectoryName($DestinationRoot)
if ([string]::IsNullOrWhiteSpace($destinationParent) -or $DestinationRoot -eq [IO.Path]::GetPathRoot($DestinationRoot)) {
    throw 'DestinationRoot must be a named kit folder, not a drive root.'
}
[IO.Directory]::CreateDirectory($destinationParent) | Out-Null

$installer = Join-Path $DeployRoot 'Cerebral-Setup.exe'
$workerInstaller = Join-Path $DeployRoot 'Cerebral-Worker-Setup.exe'
$workerRoot = Join-Path $DeployRoot 'Workers'
$modelRoot = Join-Path $DeployRoot 'Models'
$pluginRoot = Join-Path $DeployRoot 'Plugins'
$featureRoot = Join-Path $DeployRoot 'Features'
$coreRoot = Join-Path $DeployRoot 'win-x64'
$cortexRoot = Join-Path $DeployRoot 'Pegasus-Cortex-Setup'
$behaviorPackRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\BehaviorPacks'))
$PrerequisiteRoot = [IO.Path]::GetFullPath($PrerequisiteRoot)
$supportScripts = @(
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\Install-CerebralPluginResources.ps1'))
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\Install-CerebralFeature.ps1'))
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\Cache-CerebralInstallationSource.ps1'))
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\Install-CerebralCore.ps1'))
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\Install-CerebralWorker.ps1'))
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\Register-CerebralOwnerPipe.ps1'))
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\Get-CerebralInstallerState.ps1'))
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup\Cerebral.PayloadReuse.psm1'))
)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Missing Cerebral installer: $installer" }
if (-not (Test-Path -LiteralPath $workerInstaller -PathType Leaf)) { throw "Missing standalone Worker installer: $workerInstaller" }
if (-not (Test-Path -LiteralPath (Join-Path $cortexRoot 'PegasusCortexInstaller.exe') -PathType Leaf)) {
    throw "Missing canonical Cortex prerequisite companion: $cortexRoot"
}
foreach ($runtime in @('win-x64','win-arm64','win-x86')) {
    foreach ($required in @('Cerebral.Worker.exe','Cerebral.Worker.dll','Cerebral.Core.dll')) {
        $path = Join-Path $workerRoot "$runtime\$required"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing $runtime standalone Worker payload: $path" }
    }
}
foreach ($required in @('linux-arm64\Worker\Cerebral.Worker','linux-arm64\install-linux.sh')) {
    $path = Join-Path $workerRoot $required
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing optional Linux ARM64 Worker payload: $path" }
}
foreach ($application in @{
    Worker = 'Applications\Worker\Cerebral.Worker.exe'
    Integrator = 'Applications\Desktop\Cerebral.Integrator.exe'
    Orchestrator = 'Applications\Desktop\Cerebral.Orchestrator.exe'
}.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath (Join-Path $coreRoot $application.Value) -PathType Leaf)) {
        throw "Missing Cerebral core application payload: $($application.Key)"
    }
}
$corePayloadFiles = @(Get-ChildItem -LiteralPath $coreRoot -File -Recurse | Where-Object {
    $relative = [IO.Path]::GetRelativePath($coreRoot, $_.FullName).Replace('\','/')
    $relative -notmatch '^Plugins/(Service|Desktop)/[^/]+/'
})
$models = @(Get-ChildItem -LiteralPath $modelRoot -File -Filter '*.CerebralLLM' -ErrorAction Stop | Sort-Object Name)
$plugins = @(Get-ChildItem -LiteralPath $pluginRoot -File -Filter '*.CerebralPlugin' -ErrorAction Stop | Sort-Object Name)
$features = @(Get-ChildItem -LiteralPath $featureRoot -File -Filter '*.CerebralFeature' -ErrorAction Stop | Sort-Object Name)
if ($models.Count -eq 0) { throw 'The kit requires at least one .CerebralLLM package.' }
if ($plugins.Count -eq 0) { throw 'No independently importable .CerebralPlugin packages were found.' }
if ($features.Count -eq 0) { throw 'No independently selectable .CerebralFeature payloads were found.' }
Add-Type -AssemblyName System.IO.Compression
function Assert-ModelPackage([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $manifestEntry = $archive.GetEntry('manifest.json')
            $weights = $archive.GetEntry('model.gguf')
            if ($null -eq $manifestEntry -or $null -eq $weights -or $weights.Length -le 0) { throw "Model package lacks manifest.json or model.gguf: $Path" }
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try { $package = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($package.contract -cne 'pegasus.cerebral.model' -or $package.version -notin @(1, 2)) { throw "Unsupported model package contract: $Path" }
            if ($package.version -eq 2 -and ($package.model.visionProjectorPath -cne 'projector.gguf' -or $null -eq $archive.GetEntry('projector.gguf') -or $archive.GetEntry('projector.gguf').Length -le 0)) {
                throw "Version 2 model package lacks its fixed projector.gguf component: $Path"
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}
foreach ($model in $models) { Assert-ModelPackage $model.FullName }
function Read-PluginPackage([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $manifestEntry = $archive.GetEntry('cerebral-plugin.json')
            if ($null -eq $manifestEntry -or $manifestEntry.Length -gt 64KB) { throw "Plugin package manifest is missing or oversized: $Path" }
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try { $package = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($package.contract -cne 'pegasus.cerebral.plugin' -or $package.schemaVersion -notin @(1, 2) -or
                $package.kind -cne 'desktop' -or $null -eq $archive.GetEntry('plugin/desktop-plugin.json')) {
                throw "Unsupported desktop plugin package: $Path"
            }
            $resources = if ($null -ne $package.PSObject.Properties['resources']) { @($package.resources) } else { @() }
            if ($package.schemaVersion -eq 2 -and $resources.Count -eq 0) { throw "Schema 2 plugin package has no resources: $Path" }
            foreach ($resource in $resources) {
                $prefix = 'plugin/' + ([string]$resource.source).Replace('\','/').TrimEnd('/') + '/'
                if (-not $archive.Entries.Where({ -not $_.FullName.EndsWith('/') -and $_.FullName.StartsWith($prefix, [StringComparison]::Ordinal) }, 'First')) {
                    throw "Plugin resource $($resource.id) is missing from $Path"
                }
            }
            $uncompressedBytes = [long](($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | Measure-Object Length -Sum).Sum)
            return [pscustomobject]@{
                Path=[IO.Path]::GetFullPath($Path); Id=[string]$package.id; Name=[string]$package.name;
                Version=[string]$package.version; Description=[string]$package.description;
                SchemaVersion=[int]$package.schemaVersion; Resources=$resources.Count; UncompressedBytes=$uncompressedBytes
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}
$pluginMetadata = @($plugins | ForEach-Object { Read-PluginPackage $_.FullName })
function Read-FeaturePackage([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $manifestEntry = $archive.GetEntry('feature.json')
            if ($null -eq $manifestEntry -or $manifestEntry.Length -gt 64KB) { throw "Feature package manifest is missing or oversized: $Path" }
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($manifest.contract -cne 'pegasus.cerebral.installer-feature' -or $manifest.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$manifest.id)) {
                throw "Unsupported installer feature package: $Path"
            }
            $description = ''
            $described = $archive.Entries | Where-Object { $_.FullName -match '/(desktop|service)-plugin\.json$' } | Select-Object -First 1
            if ($null -ne $described -and $described.Length -le 64KB) {
                $reader = [IO.StreamReader]::new($described.Open())
                try { $description = [string](($reader.ReadToEnd() | ConvertFrom-Json).description) } finally { $reader.Dispose() }
            }
            return [pscustomobject]@{
                Path=[IO.Path]::GetFullPath($Path); Id=[string]$manifest.id; Name=[string]$manifest.name;
                Version=[string]$manifest.version; Description=$description;
                UncompressedBytes=[long](($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | Measure-Object Length -Sum).Sum)
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}
$featureMetadata = @($features | ForEach-Object { Read-FeaturePackage $_.FullName })
$duplicateFeatures = @($featureMetadata | Group-Object Id | Where-Object Count -ne 1)
if ($duplicateFeatures.Count -gt 0) { throw "Installer feature IDs must be unique: $($duplicateFeatures.Name -join ', ')" }
$aiDictationPackage = @($pluginMetadata | Where-Object Id -CEQ 'pegasus.ai-dictation')
if ($aiDictationPackage.Count -ne 1 -or $aiDictationPackage[0].SchemaVersion -ne 2 -or $aiDictationPackage[0].Resources -lt 2) {
    throw 'The installation kit requires one self-contained AI Dictation plugin with runtime and model resources.'
}
$behaviorPacks = @(Get-CerebralBehaviorPackInventory -Root $behaviorPackRoot)
foreach ($required in $supportScripts) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Installation support script is missing: $required" }
}
$prerequisiteManifest = Join-Path $PrerequisiteRoot 'manifest.json'
$prerequisiteInstaller = Join-Path $PrerequisiteRoot 'Install-CerebralPrerequisites.ps1'
foreach ($required in @($prerequisiteManifest, $prerequisiteInstaller)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Prerequisite cache is incomplete: $required" }
}
$prerequisiteMetadata = Get-Content -LiteralPath $prerequisiteManifest -Raw | ConvertFrom-Json
function Assert-InstallerPayload([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 8) { throw "Prerequisite installer is truncated: $Path" }
        $magic = [byte[]]::new(8); [void]$stream.Read($magic, 0, $magic.Length)
    } finally { $stream.Dispose() }
    switch ([IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        '.exe' { if ($magic[0] -ne 0x4d -or $magic[1] -ne 0x5a) { throw "Prerequisite .exe is not a Windows PE image: $Path" } }
        '.msi' {
            $ole = [byte[]](0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1)
            for ($index = 0; $index -lt $ole.Length; $index++) { if ($magic[$index] -ne $ole[$index]) { throw "Prerequisite .msi is not an OLE installer image: $Path" } }
        }
        default { throw "Unsupported prerequisite installer type: $Path" }
    }
}
foreach ($item in $prerequisiteMetadata.components) {
    if ($null -eq $item.PSObject.Properties['required'] -or $item.required -isnot [bool] -or
        $null -eq $item.PSObject.Properties['default'] -or $item.default -isnot [bool]) {
        throw "Prerequisite metadata must declare Boolean required and default fields: $($item.id)"
    }
    if ($item.required -and -not $item.default) {
        throw "A required prerequisite must also be selected by default: $($item.id)"
    }
    $prerequisitePath = Join-Path $PrerequisiteRoot ([string]$item.file)
    if (-not (Test-Path -LiteralPath $prerequisitePath -PathType Leaf)) {
        throw "Prerequisite cache is missing $($item.id): $($item.file)"
    }
    Assert-InstallerPayload $prerequisitePath
}

$payload = @(
    Get-Item -LiteralPath $installer
    Get-Item -LiteralPath $workerInstaller
    $corePayloadFiles
    Get-ChildItem -LiteralPath $cortexRoot -File -Recurse
    $behaviorPacks | ForEach-Object { Get-ChildItem -LiteralPath $_.Root -File -Recurse }
    $models
    $plugins
    $features
    $supportScripts | ForEach-Object { Get-Item -LiteralPath $_ }
    Get-ChildItem -LiteralPath $workerRoot -File -Recurse
    Get-ChildItem -LiteralPath $PrerequisiteRoot -File -Recurse
)
$requiredBytes = [long](($payload | Measure-Object Length -Sum).Sum)
$reusablePayloads = @{}
foreach ($name in @('Models', 'Prerequisites')) {
    $source = if ($name -eq 'Models') { $modelRoot } else { $PrerequisiteRoot }
    $existing = Join-Path $DestinationRoot $name
    $reusablePayloads[$name] = (Test-CerebralPayloadInventoryEqual -Source $source -Existing $existing)
}
$reusedBytes = [long]0
if ($reusablePayloads.Models) { $reusedBytes += [long](($models | Measure-Object Length -Sum).Sum) }
if ($reusablePayloads.Prerequisites) { $reusedBytes += [long]((Get-ChildItem -LiteralPath $PrerequisiteRoot -File -Recurse | Measure-Object Length -Sum).Sum) }
$stagingBytes = $requiredBytes - $reusedBytes
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($DestinationRoot))
if (-not $drive.IsReady) { throw "Destination drive is not ready: $($drive.Name)" }
if ($drive.AvailableFreeSpace -lt $stagingBytes + 512MB) {
    throw "The destination needs at least $([Math]::Ceiling(($stagingBytes + 512MB) / 1GB)) GiB free for a safe staged update."
}
if ($ValidateOnly) {
    [pscustomobject]@{ Valid = $true; SourceBytes = $requiredBytes; StagingBytes = $stagingBytes; ReusedBytes = $reusedBytes; Models = $models.Count; Plugins = $plugins.Count; Features = $features.Count; BehaviorPacks = $behaviorPacks.Count; Prerequisites = @($prerequisiteMetadata.components).Count }
    return
}

$staging = Join-Path $destinationParent ('.' + [IO.Path]::GetFileName($DestinationRoot) + '.staging-' + [Guid]::NewGuid().ToString('N'))
function Test-DirectChild([string]$Parent, [string]$Child) {
    $relative = [IO.Path]::GetRelativePath($Parent, $Child)
    return -not [IO.Path]::IsPathRooted($relative) -and $relative -notin '.', '..' -and
        -not $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)
}
if (-not (Test-DirectChild $destinationParent $staging)) {
    throw 'Kit staging paths escaped the selected destination parent.'
}

$copyReceipts = [Collections.Generic.List[object]]::new()
[IO.Directory]::CreateDirectory($staging) | Out-Null
Copy-Item -LiteralPath $installer -Destination (Join-Path $staging 'Cerebral-Setup.exe')
Copy-Item -LiteralPath $workerInstaller -Destination (Join-Path $staging 'Cerebral-Worker-Setup.exe')
[IO.Directory]::CreateDirectory((Join-Path $staging 'BehaviorPacks')) | Out-Null
foreach ($behaviorPack in $behaviorPacks) {
    $copyReceipts.Add((Copy-CerebralInstallationKitDirectory -Source $behaviorPack.Root -Destination (Join-Path $staging ('BehaviorPacks\' + $behaviorPack.Directory)) -Label ('stage/behavior-pack/' + $behaviorPack.PackId)))
}
[IO.Directory]::CreateDirectory((Join-Path $staging 'Core')) | Out-Null
$copyReceipts.Add((Copy-CerebralInstallationKitDirectory -Source $coreRoot -Destination (Join-Path $staging 'Core\win-x64') -Label 'stage/core'))
    # Feature code is independently selectable. The released core contains only
    # the host applications; selected plugin generations are published from Features.
    foreach ($relative in @('Plugins\Service','Plugins\Desktop')) {
        $bundledPlugins = Join-Path $staging (Join-Path 'Core\win-x64' $relative)
        if (Test-Path -LiteralPath $bundledPlugins) {
            Get-ChildItem -LiteralPath $bundledPlugins -Directory | Remove-Item -Recurse -Force
        }
    }
$copyReceipts.Add((Copy-CerebralInstallationKitDirectory -Source $featureRoot -Destination (Join-Path $staging 'Features') -Label 'stage/features'))
if ($reusablePayloads.Models -and (New-CerebralHardLinkPayloadClone -Existing (Join-Path $DestinationRoot 'Models') -Destination (Join-Path $staging 'Models'))) {
    $copyReceipts.Add([pscustomobject]@{ Label='stage/models'; Mode='HardLinkReuse'; Files=$models.Count; Bytes=0; ReusedBytes=[long](($models | Measure-Object Length -Sum).Sum) })
} else {
    $copyReceipts.Add((Copy-CerebralInstallationKitDirectory -Source $modelRoot -Destination (Join-Path $staging 'Models') -Label 'stage/models'))
}
$copyReceipts.Add((Copy-CerebralInstallationKitDirectory -Source $pluginRoot -Destination (Join-Path $staging 'Plugins') -Label 'stage/plugins'))
$copyReceipts.Add((Copy-CerebralInstallationKitDirectory -Source $workerRoot -Destination (Join-Path $staging 'Workers') -Label 'stage/workers'))
$copyReceipts.Add((Copy-CerebralInstallationKitDirectory -Source $cortexRoot -Destination (Join-Path $staging 'Pegasus-Cortex-Setup') -Label 'stage/cortex'))
    [IO.Directory]::CreateDirectory((Join-Path $staging 'Support')) | Out-Null
    foreach ($support in $supportScripts) { Copy-Item -LiteralPath $support -Destination (Join-Path $staging 'Support') }
if ($reusablePayloads.Prerequisites -and (New-CerebralHardLinkPayloadClone -Existing (Join-Path $DestinationRoot 'Prerequisites') -Destination (Join-Path $staging 'Prerequisites'))) {
    $prerequisiteBytes = [long]((Get-ChildItem -LiteralPath $PrerequisiteRoot -File -Recurse | Measure-Object Length -Sum).Sum)
    $copyReceipts.Add([pscustomobject]@{ Label='stage/prerequisites'; Mode='HardLinkReuse'; Files=@(Get-ChildItem -LiteralPath $PrerequisiteRoot -File -Recurse).Count; Bytes=0; ReusedBytes=$prerequisiteBytes })
} else {
    $copyReceipts.Add((Copy-CerebralInstallationKitDirectory -Source $PrerequisiteRoot -Destination (Join-Path $staging 'Prerequisites') -Label 'stage/prerequisites'))
}

$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer).FileVersion
if ([string]::IsNullOrWhiteSpace($version)) { throw 'The Cerebral installer does not contain a file version.' }
function Assert-IniValue([string]$Value, [string]$Name) {
    if ($Value.Contains("`r") -or $Value.Contains("`n") -or $Value.Contains([char]0)) {
        throw "Installation-kit INI value must be single-line text: $Name"
    }
}
$ini = [Collections.Generic.List[string]]::new()
$ini.Add('[kit]')
$ini.Add('contract=pegasus.cerebral.installation-kit')
$ini.Add('schemaVersion=2')
$ini.Add("cerebralVersion=$version")
$ini.Add("createdAt=$([DateTimeOffset]::UtcNow.ToString('O'))")
$ini.Add("payloadBytes=$requiredBytes")
$ini.Add('checksums=omitted-by-design')
$ini.Add('')
$ini.Add('[installer]')
$ini.Add('path=Cerebral-Setup.exe')
$ini.Add("bytes=$((Get-Item -LiteralPath $installer).Length)")
$ini.Add('')
$ini.Add('[workerInstaller]')
$ini.Add('path=Cerebral-Worker-Setup.exe')
$ini.Add("bytes=$((Get-Item -LiteralPath $workerInstaller).Length)")
$ini.Add('desktopArchitecture=x64')
$ini.Add('windowsRuntimes=win-x64,win-arm64,win-x86')
$ini.Add('optionalRuntimes=linux-arm64')
$ini.Add('')
$ini.Add('[core]')
$ini.Add('path=Core/win-x64')
$ini.Add("bytes=$([long](($corePayloadFiles | Measure-Object Length -Sum).Sum))")
$ini.Add('compressed=false')
$ini.Add('')
$ini.Add('[cortex]')
$ini.Add('path=Pegasus-Cortex-Setup/PegasusCortexInstaller.exe')
$ini.Add('bundledCompanion=true')

function Add-ComponentMetadata([string]$Id, [string]$Name, [string]$Version, [long]$Bytes, [string]$Description) {
    foreach ($entry in @($Id, $Name, $Version, $Description)) { Assert-IniValue ([string]$entry) "component.$Id" }
    $ini.Add('')
    $ini.Add("[component.$Id]")
    $ini.Add("name=$Name")
    $ini.Add("version=$Version")
    $ini.Add("bytes=$Bytes")
    $ini.Add("sizeKib=$([Math]::Ceiling($Bytes / 1KB))")
    $ini.Add("description=$Description")
}

$coreBytes = [long](($corePayloadFiles | Measure-Object Length -Sum).Sum)
Add-ComponentMetadata 'cerebral-core' 'Pegasus Cerebral application' $version $coreBytes 'Required Cerebral desktop, local worker, shared contracts and default configuration. Updates replace program files while preserving projects, models, conversations and settings.'
$cortexManifest = Get-Content -LiteralPath (Join-Path $cortexRoot 'package-manifest.json') -Raw | ConvertFrom-Json
$cortexBytes = [long]((Get-ChildItem -LiteralPath $cortexRoot -File -Recurse | Measure-Object Length -Sum).Sum)
Add-ComponentMetadata 'cortex-core' 'Pegasus Cortex continuity service' ([string]$cortexManifest.releaseVersion) $cortexBytes 'Required local continuity and AgentOps service for retained project knowledge and bounded system operations.'
Add-ComponentMetadata 'cortex-trust' 'Windows trust certificate' ([string]$cortexManifest.releaseVersion) 0 'Recommended trust registration for Pegasus-signed binaries. The private Cerebral-to-Cortex pipe also authenticates the signed application identity.'

foreach ($feature in $featureMetadata) {
    $plugin = @($pluginMetadata | Where-Object Id -CEQ $feature.Id | Select-Object -First 1)
    $description = if ($plugin.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($plugin[0].Description)) { $plugin[0].Description } else { $feature.Description }
    $bytes = $feature.UncompressedBytes + $(if ($plugin.Count -gt 0) { $plugin[0].UncompressedBytes } else { 0 })
    Add-ComponentMetadata $feature.Id $feature.Name $feature.Version $bytes $description
}

foreach ($pack in $behaviorPacks) {
    $description = if ($pack.PackId -eq 'pegasus-cerebral-guides-v1') { 'Current Cerebral contracts and operating guidance for bounded agent retrieval.' } else { 'Curated Windows operations knowledge for bounded agent retrieval.' }
    Add-ComponentMetadata $pack.PackId $pack.PackId ([string]$pack.PackRevision) ([long]$pack.Bytes) $description
}

$prerequisiteDescriptions = @{
    'dotnet-sdk'='Required shared .NET desktop runtime and SDK used to run Cerebral and build, test and package .NET projects and extensions without duplicating the framework under each application.'
    'powershell'='PowerShell 7 runtime for registered scripts and developer automation.'
    'git'='Source control, snapshots and project history.'
    'node'='Node.js long-term-support runtime and package tools.'
    'python'='Python runtime for local tools, model helpers and plugin development.'
    'sevenzip'='7-Zip archive inspection and extraction support.'
    'vc-redist'='Required Microsoft Visual C++ x64 runtime for local model inference.'
    'nsis'='NSIS compiler used to build Windows installers.'
    'vs-buildtools'='MSVC, MSBuild, CMake, Ninja, vcpkg and Windows SDK tools for native and desktop builds.'
    'wsl'='Windows Subsystem for Linux for workflows that require a Linux environment; a restart may be required.'
}
foreach ($item in $prerequisiteMetadata.components) {
    $source = Join-Path $PrerequisiteRoot ([string]$item.file)
    Add-ComponentMetadata ([string]$item.id) ([string]$item.name) ([string]$item.version) ([long](Get-Item -LiteralPath $source).Length) ([string]$prerequisiteDescriptions[[string]$item.id])
}
Add-ComponentMetadata 'cortex-codex-registration' 'Register Cortex with Codex' $version 0 'Registers the installed Cortex MCP and PAO tools with Codex on this computer.'
Add-ComponentMetadata 'cortex-codex-guidance' 'Add Cortex guidance to Codex' $version 0 'Adds the managed Cortex guidance block to the user Codex AGENTS.md file.'
Add-ComponentMetadata 'installation-source-cache' 'Offline repair source' $version $requiredBytes 'Keeps a local copy of this installation source for repair or reinstall without the original USB drive or download.'
foreach ($collection in @(
    [pscustomobject]@{ Section='models'; Items=@($models | ForEach-Object { [pscustomobject]@{ Path='Models/' + $_.Name; Bytes=$_.Length; Extra=$null } }) },
    [pscustomobject]@{ Section='plugins'; Items=@($plugins | ForEach-Object { $metadata=@($pluginMetadata | Where-Object Path -CEQ $_.FullName)[0]; [pscustomobject]@{ Path='Plugins/' + $_.Name; Bytes=$_.Length; Extra="resources=$($metadata.Resources)" } }) },
    [pscustomobject]@{ Section='features'; Items=@($features | ForEach-Object { [pscustomobject]@{ Path='Features/' + $_.Name; Bytes=$_.Length; Extra=$null } }) },
    [pscustomobject]@{ Section='behaviorPacks'; Items=@($behaviorPacks | ForEach-Object { [pscustomobject]@{ Path='BehaviorPacks/' + $_.Directory; Bytes=$_.Bytes; Extra="id=$($_.PackId)|revision=$($_.PackRevision)|records=$($_.Records)|namespace=$($_.Namespace)|selectedByDefault=$([bool]$_.SelectedByDefault)" } }) },
    [pscustomobject]@{ Section='support'; Items=@($supportScripts | ForEach-Object { [pscustomobject]@{ Path='Support/' + [IO.Path]::GetFileName($_); Bytes=(Get-Item -LiteralPath $_).Length; Extra=$null } }) },
    [pscustomobject]@{ Section='prerequisites'; Items=@($prerequisiteMetadata.components | ForEach-Object { $source=Join-Path $PrerequisiteRoot ([string]$_.file); [pscustomobject]@{ Path='Prerequisites/' + ([string]$_.file).Replace('\','/'); Bytes=(Get-Item -LiteralPath $source).Length; Extra="id=$($_.id)|version=$($_.version)|required=$([bool]$_.required)|selectedByDefault=$([bool]$_.default)" } }) }
)) {
    $ini.Add('')
    $ini.Add("[$($collection.Section)]")
    $ini.Add("count=$($collection.Items.Count)")
    for ($index = 0; $index -lt $collection.Items.Count; $index++) {
        $item = $collection.Items[$index]
        Assert-IniValue $item.Path "$($collection.Section)[$index].path"
        Assert-IniValue ([string]$item.Extra) "$($collection.Section)[$index].metadata"
        $ini.Add("item$index.path=$($item.Path)")
        $ini.Add("item$index.bytes=$($item.Bytes)")
        if (-not [string]::IsNullOrWhiteSpace($item.Extra)) { $ini.Add("item$index.metadata=$($item.Extra)") }
    }
}
$ini | Set-Content -LiteralPath (Join-Path $staging 'kit.ini') -Encoding utf8NoBOM

    $readme = @"
PEGASUS CEREBRAL INSTALLATION KIT
Version: $version

1. Keep the adjacent BehaviorPacks, Core, Models, Plugins, Prerequisites, Support, Workers, and Pegasus-Cortex-Setup folders together. Run Cerebral-Setup.exe for the Windows x64 desktop and its local x64 Worker. Run Cerebral-Worker-Setup.exe on a Worker-only Windows machine; it selects the native x64, ARM64, or x86 payload automatically. Core application files remain uncompressed; setup stages and atomically publishes them without unpacking one large executable.
2. Setup silently installs or upgrades the required Pegasus Cortex core from Pegasus-Cortex-Setup. The optional Windows trust certificate is selected by default when absent; the secure Cerebral pipe itself authenticates signed service identity without requiring that certificate. Codex registration and guidance are separate optional components.
3. Review prerequisites on the Components page. The machine-wide .NET 10 desktop runtime/SDK and Visual C++ x64 runtime are required and stay selected. Other developer tools are optional, selected by default for the complete workstation profile, and may be deselected. A version-pinned portable build environment belongs in an independently versioned plugin rather than being duplicated by core. WSL may require a restart after its Windows features are enabled. Offline installation-source caching is optional and unchecked by default.
4. Choose one or more model packages on the Model packages page. The starter package includes Qwen text weights and its matching vision projector. Setup automatically fits context and GPU layers to the detected memory at model load.
5. AI Dictation is self-contained: its .CerebralPlugin package carries the CPU NeMo-Speech runtime, NVIDIA Parakeet model, licenses, and provenance metadata. Setup publishes those resources into the configured Tools and Models roots only when that plugin is selected.
6. Choose independent local roots for replaceable program files, writable data, and agent workspaces. Defaults are C:\Program Files\Pegasus\Cerebral, C:\ProgramData\Pegasus\Cerebral, and C:\CerebralProjects. Models, portable plugin tools, shared files, user profiles, and plugin data derive from the writable-data root by default and can be changed in Configuration later.

The BehaviorPacks folder contains only the independently selectable Cerebral guide and Windows operational-knowledge modules admitted by Cortex's behavior-pack v2 contract. The Features folder contains the install-time native plugin generations; setup extracts only selected features. The Plugins folder contains independent .CerebralPlugin packages for double-click import, updates, or sharing, including self-contained resource payloads when declared. Models, prerequisites, and browser/runtime assets remain separate so unselected components are not materialized on the computer.

Keep BehaviorPacks, Models, Plugins, Prerequisites, Support, Workers, and Pegasus-Cortex-Setup beside both setup executables. The Linux ARM64 Worker and its explicit systemd deployment helper are in Workers\linux-arm64; WSL is optional and is not used to build or run the Windows desktop. Selecting the offline-source option saves one replaceable Current copy under the configured Shared root for later repair or reinstall; normal use and uninstall do not require that copy.

The bundled Visual Studio Build Tools payload remains subject to Microsoft's Visual Studio license terms. Upstream 7-Zip and NSIS installers are preserved from their named official distribution sources; the kit records provenance rather than inventing a Cerebral signature for third-party binaries.
"@
Set-Content -LiteralPath (Join-Path $staging 'README.txt') -Value $readme -Encoding utf8NoBOM

$publish = Invoke-CerebralInstallationKitPublish -StagingRoot $staging -DestinationRoot $DestinationRoot

$installed = Get-ChildItem -LiteralPath $DestinationRoot -File -Recurse
[pscustomobject]@{
    Destination = $DestinationRoot
    Version = $version
    Files = $installed.Count
    Bytes = [long](($installed | Measure-Object Length -Sum).Sum)
    Models = $models.Count
    Plugins = $plugins.Count
    BehaviorPacks = $behaviorPacks.Count
    WorkerRuntimes = 4
    PublishMode = $publish.Mode
    RobocopyOperations = $copyReceipts.Count + @($publish.Robocopy).Count
    RobocopyHighestExitCode = @($copyReceipts + @($publish.Robocopy) | Measure-Object ExitCode -Maximum).Maximum
    CleanupPending = @($publish.CleanupPending)
    AvailableBytesAfter = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($DestinationRoot)).AvailableFreeSpace
}
