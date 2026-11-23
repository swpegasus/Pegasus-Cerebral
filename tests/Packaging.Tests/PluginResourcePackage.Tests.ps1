$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('cerebral-plugin-package-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $sourceRoot = Join-Path $scratch 'source'
    $pluginRoot = Join-Path $sourceRoot 'test.plugin'
    $outputRoot = Join-Path $scratch 'output'
    $resourceRoot = Join-Path $scratch 'resource'
    [IO.Directory]::CreateDirectory($pluginRoot) | Out-Null
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    [IO.Directory]::CreateDirectory($resourceRoot) | Out-Null
    Set-Content -LiteralPath (Join-Path $pluginRoot 'desktop-plugin.json') -Encoding utf8NoBOM -Value '{"schemaVersion":3,"contract":"pegasus.cerebral.desktop","apiVersion":1,"id":"test.plugin","name":"Test","version":"1.0.0","description":"Test","assembly":"Test.dll","entryType":"Test.Plugin","dependencies":[],"defaultEnabled":true,"exclusiveGroup":"fixture.input","exclusivePriority":100}'
    Set-Content -LiteralPath (Join-Path $pluginRoot 'Test.dll') -Encoding utf8NoBOM -Value 'test'
    Set-Content -LiteralPath (Join-Path $pluginRoot 'plugin-resources.json') -Encoding utf8NoBOM -Value '{"schemaVersion":1,"resources":[{"id":"runtime","kind":"tool","source":"Resources/Runtime","destination":"Test/Runtime"}]}'
    Set-Content -LiteralPath (Join-Path $resourceRoot 'tool.exe') -Encoding utf8NoBOM -Value 'resource'
    $bindingsPath = Join-Path $scratch 'bindings.json'
    [ordered]@{
        schemaVersion = 1
        bindings = @([ordered]@{ pluginId='test.plugin'; resourceId='runtime'; source='resource' })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $bindingsPath -Encoding utf8NoBOM

    & (Join-Path $projectRoot 'scripts\package-desktop-plugins.ps1') `
        -SourceRoot $sourceRoot -OutputRoot $outputRoot -ProjectRoot $scratch -ResourceBindingsPath $bindingsPath | Out-Null
    $package = Get-ChildItem -LiteralPath $outputRoot -File -Filter '*.CerebralPlugin' | Select-Object -First 1
    if ($null -eq $package) { throw 'Package script did not create a package.' }
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entry = $archive.GetEntry('cerebral-plugin.json')
        if ($null -eq $entry) { throw 'Package manifest is missing.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.schemaVersion -ne 2 -or $manifest.resources[0].id -cne 'runtime' -or
            $null -eq $archive.GetEntry('plugin/Resources/Runtime/tool.exe')) {
            throw 'Self-contained resource package validation failed.'
        }
    } finally { $archive.Dispose() }
    $tools = Join-Path $scratch 'installed\Tools'
    $models = Join-Path $scratch 'installed\Models'
    $data = Join-Path $scratch 'installed\Data'
    $shared = Join-Path $scratch 'installed\Shared'
    & (Join-Path $projectRoot 'setup\Install-CerebralPluginResources.ps1') `
        -PackagePath $package.FullName -ToolsDirectory $tools -ModelsDirectory $models `
        -DataDirectory $data -SharedDirectory $shared | Out-Null
    $installedResource = Join-Path $tools 'Test\Runtime\tool.exe'
    if ((Get-Content -LiteralPath $installedResource -Raw).Trim() -cne 'resource') {
        throw 'The plugin resource installer did not publish the declared tool resource.'
    }
    Write-Output 'PluginResourcePackage.Tests PASS'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
