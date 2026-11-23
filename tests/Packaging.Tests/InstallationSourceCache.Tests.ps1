$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('cerebral-install-cache-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $source = Join-Path $scratch 'kit'
    $destination = Join-Path $scratch 'shared\InstallationSource\Current'
    [IO.Directory]::CreateDirectory((Join-Path $source 'Plugins')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $source 'Models')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $source 'Prerequisites')) | Out-Null
    Set-Content -LiteralPath (Join-Path $source 'Cerebral-Setup.exe') -Encoding utf8NoBOM -Value 'setup-v1'
    Set-Content -LiteralPath (Join-Path $source 'Plugins\sample.CerebralPlugin') -Encoding utf8NoBOM -Value 'plugin-v1'
    Set-Content -LiteralPath (Join-Path $source 'Models\starter.CerebralLLM') -Encoding utf8NoBOM -Value 'model-v1'
    Set-Content -LiteralPath (Join-Path $source 'Prerequisites\runtime.exe') -Encoding utf8NoBOM -Value 'runtime-v1'
    & (Join-Path $projectRoot 'setup\Cache-CerebralInstallationSource.ps1') -SourceRoot $source -DestinationRoot $destination | Out-Null
    if ((Get-Content -LiteralPath (Join-Path $destination 'Plugins\sample.CerebralPlugin') -Raw).Trim() -cne 'plugin-v1') {
        throw 'The initial installation-source cache is incomplete.'
    }
    Set-Content -LiteralPath (Join-Path $source 'Cerebral-Setup.exe') -Encoding utf8NoBOM -Value 'setup-v2'
    Remove-Item -LiteralPath (Join-Path $source 'Plugins\sample.CerebralPlugin')
    Set-Content -LiteralPath (Join-Path $source 'Plugins\replacement.CerebralPlugin') -Encoding utf8NoBOM -Value 'plugin-v2'
    $second = & (Join-Path $projectRoot 'setup\Cache-CerebralInstallationSource.ps1') -SourceRoot $source -DestinationRoot $destination | ConvertFrom-Json
    if ((Get-Content -LiteralPath (Join-Path $destination 'Cerebral-Setup.exe') -Raw).Trim() -cne 'setup-v2' -or
        (Test-Path -LiteralPath (Join-Path $destination 'Plugins\sample.CerebralPlugin')) -or
        -not (Test-Path -LiteralPath (Join-Path $destination 'Plugins\replacement.CerebralPlugin')) -or
        @($second.reusedPayloads).Count -ne 2 -or 'Models' -notin @($second.reusedPayloads) -or 'Prerequisites' -notin @($second.reusedPayloads)) {
        throw 'The installation-source cache was not atomically replaced as one Current generation.'
    }
    Write-Output 'InstallationSourceCache.Tests PASS'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
