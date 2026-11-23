[CmdletBinding()]
param(
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release',
    [string]$DotnetExecutable,
    [string]$KitDestinationDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$deployment = [IO.Path]::GetFullPath((Join-Path $projectRoot 'deploy\Workers\linux-arm64'))
$runtimeSource = [IO.Path]::GetFullPath((Join-Path $projectRoot '.runtime\llama-linux-arm64'))
$helperSource = [IO.Path]::GetFullPath((Join-Path $projectRoot 'setup\install-linux.sh'))

if (-not $DotnetExecutable) {
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($dotnet) { $DotnetExecutable = $dotnet.Source }
}
if (-not $DotnetExecutable -or -not [IO.Path]::IsPathRooted($DotnetExecutable) -or
    -not (Test-Path -LiteralPath $DotnetExecutable -PathType Leaf)) {
    throw 'Could not locate dotnet. Supply -DotnetExecutable with an existing absolute dotnet executable path.'
}
if (-not (Test-Path -LiteralPath $runtimeSource -PathType Container)) { throw "Missing Linux ARM64 runtime: $runtimeSource" }
if (-not (Test-Path -LiteralPath $helperSource -PathType Leaf)) { throw "Missing Linux installer helper: $helperSource" }

function Remove-Replaceable([string]$Path, [string]$ExpectedParent) {
    $full = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\') + '\'
    if (-not $full.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the expected replacement parent: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

function Replace-Directory([string]$Prepared, [string]$Target) {
    $preparedFull = [IO.Path]::GetFullPath($Prepared)
    $targetFull = [IO.Path]::GetFullPath($Target)
    $parent = [IO.Path]::GetDirectoryName($targetFull)
    if ([IO.Path]::GetDirectoryName($preparedFull) -ne $parent) { throw 'Prepared and target directories must share one parent for replacement.' }
    $previous = $targetFull + '.previous'
    Remove-Replaceable $previous $parent
    # Directory.Move asks the filesystem for one directory rename. PowerShell's
    # Move-Item may fall back to per-file moves and leave a partial tree when a
    # reader holds one file open on removable media.
    if (Test-Path -LiteralPath $targetFull) { [IO.Directory]::Move($targetFull, $previous) }
    try { [IO.Directory]::Move($preparedFull, $targetFull) }
    catch {
        if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $targetFull)) {
            [IO.Directory]::Move($previous, $targetFull)
        }
        throw
    }
    Remove-Replaceable $previous $parent
}

$deploymentParent = [IO.Path]::GetDirectoryName($deployment)
New-Item -ItemType Directory -Path $deploymentParent -Force | Out-Null
$staging = $deployment + '.next'
Remove-Replaceable $staging $deploymentParent
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$publishArtifactsParent = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\intermediate'))
$publishArtifacts = Join-Path $publishArtifactsParent 'linux-arm64-worker'
New-Item -ItemType Directory -Path $publishArtifactsParent -Force | Out-Null
Remove-Replaceable $publishArtifacts $publishArtifactsParent
try {
    & $DotnetExecutable publish (Join-Path $projectRoot 'src\Cerebral.Worker\Cerebral.Worker.csproj') `
        -c $Configuration -r linux-arm64 --self-contained true --artifacts-path $publishArtifacts `
        --disable-build-servers -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true `
        "-p:PathMap=$projectRoot=/_/src" -o (Join-Path $staging 'Worker')
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    Copy-Item -LiteralPath $runtimeSource -Destination (Join-Path $staging 'Worker\runtime') -Recurse -Force
    Copy-Item -LiteralPath $helperSource -Destination (Join-Path $staging 'install-linux.sh') -Force
    foreach ($required in @('Worker\Cerebral.Worker','Worker\runtime\llama-server','Worker\runtime\llama-fit-params','install-linux.sh')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required) -PathType Leaf)) { throw "Published Worker is missing $required." }
    }
    Replace-Directory $staging $deployment

    if ($KitDestinationDirectory) {
        if (-not [IO.Path]::IsPathRooted($KitDestinationDirectory)) { throw 'Kit destination must be an absolute directory.' }
        $kit = [IO.Path]::GetFullPath($KitDestinationDirectory)
        $kitParent = [IO.Path]::GetDirectoryName($kit)
        if (-not (Test-Path -LiteralPath $kitParent -PathType Container)) { throw "Kit parent does not exist: $kitParent" }
        $kitStaging = $kit + '.next'
        Remove-Replaceable $kitStaging $kitParent
        Copy-Item -LiteralPath $deployment -Destination $kitStaging -Recurse
        Replace-Directory $kitStaging $kit
    }
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Replaceable $staging $deploymentParent }
    if (Test-Path -LiteralPath $publishArtifacts) { Remove-Item -LiteralPath $publishArtifacts -Recurse -Force }
}

$files = Get-ChildItem -LiteralPath $deployment -Recurse -File
$relativePaths = [string[]]@($files | ForEach-Object { $_.FullName.Substring($deployment.Length + 1).Replace('\','/') })
[Array]::Sort($relativePaths, [StringComparer]::Ordinal)
$manifestLines = foreach ($relative in $relativePaths) {
    $path = Join-Path $deployment $relative.Replace('/','\')
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
$manifestBytes = [Text.Encoding]::UTF8.GetBytes(($manifestLines -join "`n") + "`n")
$treeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant()
[pscustomobject]@{
    Deployment = $deployment
    Kit = if ($KitDestinationDirectory) { [IO.Path]::GetFullPath($KitDestinationDirectory) } else { $null }
    Files = $files.Count
    Bytes = ($files | Measure-Object -Property Length -Sum).Sum
    TreeSha256 = $treeHash
    Version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $deployment 'Worker\Cerebral.Worker.dll')).Version.ToString(3)
} | Format-List
