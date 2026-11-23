$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('cerebral-winps-installer-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $content = Join-Path $scratch 'content'
    $resource = Join-Path $content 'plugin\Resources\Runtime'
    [IO.Directory]::CreateDirectory($resource) | Out-Null
    Set-Content -LiteralPath (Join-Path $content 'cerebral-plugin.json') -Encoding UTF8 -Value '{"schemaVersion":2,"contract":"pegasus.cerebral.plugin","kind":"desktop","id":"test.plugin","name":"Test","version":"1.0.0","description":"Test","payload":"plugin/desktop-plugin.json","settingsMode":"none","resources":[{"id":"runtime","kind":"tool","source":"Resources/Runtime","destination":"Test/Runtime"}]}'
    Set-Content -LiteralPath (Join-Path $resource 'tool.exe') -Encoding UTF8 -Value 'resource'
    Set-Content -LiteralPath (Join-Path $content 'plugin\desktop-plugin.json') -Encoding UTF8 -Value '{}'
    $package = Join-Path $scratch 'test.CerebralPlugin'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageStream = [IO.File]::Open($package, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $packageArchive = [IO.Compression.ZipArchive]::new($packageStream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $content -File -Recurse) {
                $relative = $file.FullName.Substring($content.Length + 1).Replace('\','/')
                $entry = $packageArchive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $input = [IO.File]::OpenRead($file.FullName)
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }
        } finally { $packageArchive.Dispose() }
    } finally { $packageStream.Dispose() }
    & (Join-Path $projectRoot 'setup\Install-CerebralPluginResources.ps1') -PackagePath $package `
        -ToolsDirectory (Join-Path $scratch 'Tools') -ModelsDirectory (Join-Path $scratch 'Models') `
        -DataDirectory (Join-Path $scratch 'Data') -SharedDirectory (Join-Path $scratch 'Shared') | Out-Null
    if ((Get-Content -LiteralPath (Join-Path $scratch 'Tools\Test\Runtime\tool.exe') -Raw).Trim() -cne 'resource') {
        throw 'Windows PowerShell resource installation failed.'
    }

    $kit = Join-Path $scratch 'kit'
    [IO.Directory]::CreateDirectory($kit) | Out-Null
    Set-Content -LiteralPath (Join-Path $kit 'Cerebral-Setup.exe') -Encoding UTF8 -Value 'setup'
    $cache = Join-Path $scratch 'Shared\InstallationSource\Current'
    & (Join-Path $projectRoot 'setup\Cache-CerebralInstallationSource.ps1') -SourceRoot $kit -DestinationRoot $cache | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $cache 'Cerebral-Setup.exe') -PathType Leaf)) {
        throw 'Windows PowerShell installation-source caching failed.'
    }

    $coreSource = Join-Path $scratch 'core-source'
    $coreDestination = Join-Path $scratch 'Program'
    [IO.Directory]::CreateDirectory($coreDestination) | Out-Null
    Set-Content -LiteralPath (Join-Path $coreDestination 'installation.ini') -Encoding UTF8 -Value 'preserve=true'
    foreach ($application in @{
        Worker = 'Applications\Worker\Cerebral.Worker.exe'
        Integrator = 'Applications\Desktop\Cerebral.Integrator.exe'
        Orchestrator = 'Applications\Desktop\Cerebral.Orchestrator.exe'
    }.GetEnumerator()) {
        $applicationPath = Join-Path $coreSource $application.Value
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($applicationPath)) | Out-Null
        Set-Content -LiteralPath $applicationPath -Encoding UTF8 -Value "new-$($application.Key)"
        [IO.Directory]::CreateDirectory((Join-Path $coreDestination $application.Key)) | Out-Null
        Set-Content -LiteralPath (Join-Path $coreDestination "$($application.Key)\old.txt") -Encoding UTF8 -Value "old-$($application.Key)"
    }
    [IO.Directory]::CreateDirectory((Join-Path $coreSource 'Resources\Branding\Cerebral')) | Out-Null
    Set-Content -LiteralPath (Join-Path $coreSource 'Resources\Branding\Cerebral\brand.ini') -Encoding UTF8 -Value 'contract=pegasus.cerebral.branding'
    [IO.Directory]::CreateDirectory((Join-Path $coreDestination 'Resources\Branding\Cerebral')) | Out-Null
    Set-Content -LiteralPath (Join-Path $coreDestination 'Resources\Branding\Cerebral\old.txt') -Encoding UTF8 -Value 'old-resource'
    & (Join-Path $projectRoot 'setup\Install-CerebralCore.ps1') -SourceRoot $coreSource -DestinationRoot $coreDestination | Out-Null
    foreach ($application in @{
        Worker = 'Applications\Worker\Cerebral.Worker.exe'
        Integrator = 'Applications\Desktop\Cerebral.Integrator.exe'
        Orchestrator = 'Applications\Desktop\Cerebral.Orchestrator.exe'
    }.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath (Join-Path $coreDestination $application.Value) -PathType Leaf) -or
            (Test-Path -LiteralPath (Join-Path $coreDestination $application.Key))) {
            throw "Windows PowerShell core publication failed for $($application.Key)."
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $coreDestination 'Resources\Branding\Cerebral\brand.ini') -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $coreDestination 'Resources\Branding\Cerebral\old.txt'))) {
        throw 'Windows PowerShell core publication failed for shared resources.'
    }
    if ((Get-Content -LiteralPath (Join-Path $coreDestination 'installation.ini') -Raw).Trim() -cne 'preserve=true') {
        throw 'Core publication replaced installation metadata outside the application generations.'
    }

    $featureContent = Join-Path $scratch 'feature-content'
    $featurePlugin = Join-Path $featureContent 'Plugins\Desktop\test.feature'
    [IO.Directory]::CreateDirectory($featurePlugin) | Out-Null
    Set-Content -LiteralPath (Join-Path $featureContent 'feature.json') -Encoding UTF8 -Value '{"contract":"pegasus.cerebral.installer-feature","schemaVersion":1,"id":"test.feature","name":"Test feature","version":"1.0.0","surfaces":["Plugins/Desktop"]}'
    Set-Content -LiteralPath (Join-Path $featurePlugin 'desktop-plugin.json') -Encoding UTF8 -Value '{"id":"test.feature"}'
    $featurePackage = Join-Path $scratch 'test.feature.CerebralFeature'
    [IO.Compression.ZipFile]::CreateFromDirectory($featureContent, $featurePackage, [IO.Compression.CompressionLevel]::Optimal, $false)
    & (Join-Path $projectRoot 'setup\Install-CerebralFeature.ps1') -PackagePath $featurePackage `
        -DestinationRoot $coreDestination -ExpectedId 'test.feature' | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $coreDestination 'Plugins\Desktop\test.feature\desktop-plugin.json') -PathType Leaf)) {
        throw 'Windows PowerShell selective feature publication failed.'
    }
    Write-Output 'WindowsPowerShellInstallerHelpers.Tests PASS'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
