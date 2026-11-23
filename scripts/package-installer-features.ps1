[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..\deploy\win-x64'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\deploy\Features')
)

$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$surfaces = @(
    [pscustomobject]@{ Relative='Plugins\Service'; Manifest='service-plugin.json' },
    [pscustomobject]@{ Relative='Plugins\Desktop'; Manifest='desktop-plugin.json' }
)
$plugins = @{}
foreach ($surface in $surfaces) {
    $root = Join-Path $SourceRoot $surface.Relative
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
    foreach ($directory in Get-ChildItem -LiteralPath $root -Directory) {
        $manifestPath = Join-Path $directory.FullName $surface.Manifest
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Plugin payload lacks $($surface.Manifest): $($directory.FullName)" }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.id -cne $directory.Name) { throw "Plugin ID does not match its payload directory: $manifestPath" }
        if (-not $plugins.ContainsKey($manifest.id)) {
            $plugins[$manifest.id] = [pscustomobject]@{ Id=[string]$manifest.id; Name=[string]$manifest.name; Version=[string]$manifest.version; Sources=[Collections.Generic.List[object]]::new() }
        }
        $plugins[$manifest.id].Sources.Add([pscustomobject]@{ Surface=$surface; Directory=$directory.FullName })
    }
}
if ($plugins.Count -eq 0) { throw "No plugin payloads were found below $SourceRoot" }

$parent = [IO.Path]::GetDirectoryName($OutputRoot)
[IO.Directory]::CreateDirectory($parent) | Out-Null
$stage = Join-Path $parent ('.Features.staging-' + [Guid]::NewGuid().ToString('N'))
$previous = Join-Path $parent ('.Features.previous-' + [Guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    foreach ($plugin in $plugins.Values | Sort-Object Id) {
        $content = Join-Path $stage ('.content-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($content) | Out-Null
        try {
            foreach ($source in $plugin.Sources) {
                $destination = Join-Path $content (Join-Path $source.Surface.Relative $plugin.Id)
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                Copy-Item -LiteralPath $source.Directory -Destination $destination -Recurse
            }
            [ordered]@{
                contract='pegasus.cerebral.installer-feature'
                schemaVersion=1
                id=$plugin.Id
                name=$plugin.Name
                version=$plugin.Version
                surfaces=@($plugin.Sources | ForEach-Object { $_.Surface.Relative.Replace('\','/') })
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $content 'feature.json') -Encoding utf8NoBOM
            $package = Join-Path $stage ($plugin.Id + '.CerebralFeature')
            [IO.Compression.ZipFile]::CreateFromDirectory($content, $package, [IO.Compression.CompressionLevel]::Optimal, $false)
        }
        finally { if (Test-Path -LiteralPath $content) { Remove-Item -LiteralPath $content -Recurse -Force } }
    }
    if (Test-Path -LiteralPath $OutputRoot) { [IO.Directory]::Move($OutputRoot, $previous) }
    [IO.Directory]::Move($stage, $OutputRoot)
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
}
catch {
    if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $OutputRoot)) { [IO.Directory]::Move($previous, $OutputRoot) }
    throw
}
finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force } }

Get-ChildItem -LiteralPath $OutputRoot -File -Filter '*.CerebralFeature' | Sort-Object Name |
    Select-Object Name, Length
