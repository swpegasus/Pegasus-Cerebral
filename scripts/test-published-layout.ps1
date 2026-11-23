[CmdletBinding()]
param(
    [string]$PublishRoot = (Join-Path $PSScriptRoot '..\deploy\win-x64'),
    [string]$IconOutputDirectory = (Join-Path $PSScriptRoot '..\.tmp\published-icons')
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PublishRoot)
$expectedVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$required = [ordered]@{
    Worker = 'Applications\Worker\Cerebral.Worker.exe'
    Configuration = 'Applications\Desktop\Cerebral.Integrator.exe'
    Cerebral = 'Applications\Desktop\Cerebral.Orchestrator.exe'
}

function Get-PeMachine([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Published executable is not a PE image: $Path" }
            $stream.Position = 0x3C
            $peOffset = $reader.ReadUInt32()
            if ($peOffset -gt ($stream.Length - 6)) { throw "Published executable has an invalid PE header offset: $Path" }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) { throw "Published executable has no PE signature: $Path" }
            return $reader.ReadUInt16()
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

$topLevel = @(Get-ChildItem -LiteralPath $root -Force | Select-Object -ExpandProperty Name | Sort-Object)
if (($topLevel -join '|') -cne 'Applications|Plugins|Resources') {
    throw "Published program root must contain only Applications, Plugins, and Resources; found: $($topLevel -join ', ')"
}
foreach ($legacy in @('Worker','Integrator','Orchestrator','Cerebral.Worker.exe','Cerebral.Integrator.exe','Cerebral.Orchestrator.exe')) {
    if (Test-Path -LiteralPath (Join-Path $root $legacy)) { throw "Legacy published path remains: $legacy" }
}
foreach ($applicationRoot in @('Applications\Worker','Applications\Desktop')) {
    if (Test-Path -LiteralPath (Join-Path $root "$applicationRoot\Plugins")) {
        throw "Plug-ins were duplicated inside $applicationRoot instead of the shared Plugins root."
    }
}
foreach ($path in @('Plugins\Service','Plugins\Desktop','Resources\Branding\Cerebral')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $path) -PathType Container)) { throw "Shared published path is missing: $path" }
}

Add-Type -AssemblyName System.Drawing
$iconRoot = [IO.Path]::GetFullPath($IconOutputDirectory)
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $iconRoot.StartsWith($workspaceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Icon verification output must remain inside the workspace.'
}
if (Test-Path -LiteralPath $iconRoot) { Remove-Item -LiteralPath $iconRoot -Recurse -Force }
[IO.Directory]::CreateDirectory($iconRoot) | Out-Null

$icons = [ordered]@{}
foreach ($application in $required.GetEnumerator()) {
    $executable = Join-Path $root $application.Value
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published application is missing: $($application.Value)" }
    $machine = Get-PeMachine $executable
    if ($machine -ne 0x8664) {
        throw ("The complete Cerebral product must remain Windows x64; {0} reports PE machine 0x{1:X4}." -f $application.Value, $machine)
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    if ($version.FileVersion -ne "$expectedVersion.0" -or $version.ProductName -ne 'Pegasus Cerebral') {
        throw "Published application identity is stale: $($application.Value)"
    }
    $icon = [Drawing.Icon]::ExtractAssociatedIcon($executable)
    if ($null -eq $icon) { throw "Published application has no embedded icon: $($application.Value)" }
    try {
        $bitmap = $icon.ToBitmap()
        try {
            # Role icons intentionally share the authored Cerebral appmark. Verify
            # each executable against its declared source instead of requiring
            # different artwork that the current brand contract explicitly removed.
            $projectName = @{ Worker='Worker'; Configuration='Integrator'; Cerebral='Orchestrator' }[$application.Key]
            $projectFile = Join-Path $workspaceRoot ("src\Cerebral.$projectName\Cerebral.$projectName.csproj")
            $projectXml = [xml](Get-Content -LiteralPath $projectFile -Raw)
            $iconDeclarations = @($projectXml.SelectNodes('/Project/PropertyGroup/ApplicationIcon'))
            if ($iconDeclarations.Count -ne 1) { throw "The role project must declare one application icon: $projectName" }
            $declaredIcon = $iconDeclarations[0].InnerText.Trim()
            $sourceIconPath = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($projectFile)) $declaredIcon))
            if (-not $sourceIconPath.StartsWith($workspaceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $sourceIconPath -PathType Leaf)) {
                throw "The declared application icon is unavailable or outside the workspace: $projectName"
            }
            $sourceIcon = [Drawing.Icon]::new($sourceIconPath, $bitmap.Width, $bitmap.Height)
            try {
                $sourceBitmap = $sourceIcon.ToBitmap()
                try {
                    if ($sourceBitmap.Width -ne $bitmap.Width -or $sourceBitmap.Height -ne $bitmap.Height) {
                        throw "Published icon dimensions differ from the authored role icon: $projectName"
                    }
                    for ($row = 0; $row -lt $bitmap.Height; $row++) {
                        for ($column = 0; $column -lt $bitmap.Width; $column++) {
                            if ($bitmap.GetPixel($column,$row).ToArgb() -ne $sourceBitmap.GetPixel($column,$row).ToArgb()) {
                                throw "Published icon differs from the authored role icon: $projectName"
                            }
                        }
                    }
                } finally { $sourceBitmap.Dispose() }
            } finally { $sourceIcon.Dispose() }
            $output = Join-Path $iconRoot ($application.Key + '.png')
            $bitmap.Save($output, [Drawing.Imaging.ImageFormat]::Png)
            $icons[$application.Key] = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        } finally { $bitmap.Dispose() }
    } finally { $icon.Dispose() }
}

$embeddedCoreClr = @(Get-ChildItem -LiteralPath (Join-Path $root 'Applications') -Filter 'coreclr.dll' -File -Recurse)
if ($embeddedCoreClr.Count -ne 0) { throw 'The framework-dependent Windows applications unexpectedly duplicate coreclr.dll.' }
foreach ($runtime in @{
    Worker = 'Applications\Worker\Cerebral.Worker.runtimeconfig.json'
    Configuration = 'Applications\Desktop\Cerebral.Integrator.runtimeconfig.json'
    Cerebral = 'Applications\Desktop\Cerebral.Orchestrator.runtimeconfig.json'
}.GetEnumerator()) {
    $runtimePath = Join-Path $root $runtime.Value
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) { throw "Missing framework-dependent runtime contract: $($runtime.Value)" }
    $contract = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
    $frameworks = @($contract.runtimeOptions.frameworks | Select-Object -ExpandProperty name)
    if ($frameworks -notcontains 'Microsoft.NETCore.App') { throw "$($runtime.Key) does not declare the required Microsoft.NETCore.App runtime." }
}
$areas = foreach ($relative in @('Applications\Desktop','Applications\Worker','Plugins','Resources')) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $root $relative) -File -Recurse)
    [pscustomobject]@{ Area=$relative; Files=$files.Count; Bytes=[long](($files | Measure-Object Length -Sum).Sum) }
}
[pscustomobject]@{ Result='PASS'; Version=$expectedVersion; Icons=$iconRoot; Areas=@($areas) } | ConvertTo-Json -Depth 4
