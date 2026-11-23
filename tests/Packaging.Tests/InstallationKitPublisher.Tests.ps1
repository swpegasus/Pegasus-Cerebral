$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$module = Join-Path $projectRoot 'scripts\Cerebral.InstallationKit.psm1'
Import-Module $module -Force

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Cerebral-Kit-Publisher-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-TestKit([string]$Path, [string]$Tag, [switch]$Legacy) {
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $directories = if ($Legacy) {
        @('Features','Models','Plugins','Pegasus-Cortex-Setup','Prerequisites')
    } else {
        @('BehaviorPacks','Core','Features','Models','Plugins','Pegasus-Cortex-Setup','Prerequisites','Support')
    }
    foreach ($name in $directories) {
        $directory = Join-Path $Path $name
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        Set-Content -LiteralPath (Join-Path $directory 'payload.txt') -Value "$Tag/$name" -Encoding utf8NoBOM
    }
    Set-Content -LiteralPath (Join-Path $Path 'Cerebral-Setup.exe') -Value "$Tag/setup" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $Path 'README.txt') -Value "$Tag/readme" -Encoding utf8NoBOM
    $marker = if ($Legacy) { 'kit.json' } else { 'kit.ini' }
    Set-Content -LiteralPath (Join-Path $Path $marker) -Value "$Tag/marker" -Encoding utf8NoBOM
}

try {
    $createStage = Join-Path $testRoot '.create.staging'
    $createDestination = Join-Path $testRoot 'create'
    New-TestKit $createStage 'create-new'
    $created = Invoke-CerebralInstallationKitPublish -StagingRoot $createStage -DestinationRoot $createDestination
    Assert-True ($created.Mode -ceq 'AtomicCreate') 'A missing destination must publish with one atomic create rename.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $createDestination 'kit.ini') -Raw).Contains('create-new')) 'Atomic create did not publish the new marker.'

    $replaceStage = Join-Path $testRoot '.replace.staging'
    $replaceDestination = Join-Path $testRoot 'replace'
    New-TestKit $replaceDestination 'replace-old'
    New-TestKit $replaceStage 'replace-new'
    $replaced = Invoke-CerebralInstallationKitPublish -StagingRoot $replaceStage -DestinationRoot $replaceDestination
    Assert-True ($replaced.Mode -ceq 'AtomicReplace') 'An available destination root must use atomic replacement.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $replaceDestination 'kit.ini') -Raw).Contains('replace-new')) 'Atomic replacement retained the old marker.'

    $containedStage = Join-Path $testRoot '.contained.staging'
    $containedDestination = Join-Path $testRoot 'contained'
    New-TestKit $containedDestination 'contained-old'
    New-TestKit $containedStage 'contained-new'
    $contained = Invoke-CerebralInstallationKitPublish -StagingRoot $containedStage -DestinationRoot $containedDestination -SimulatedRenameFailure AccessDenied
    Assert-True ($contained.Mode -ceq 'Contained') 'An access-denied root rename must use the contained swap.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $containedDestination 'kit.ini') -Raw).Contains('contained-new')) 'Contained replacement did not publish the new marker.'
    Assert-True (@(Get-ChildItem -LiteralPath $testRoot -Directory -Force | Where-Object Name -Like '.contained.previous-*').Count -eq 0) 'Successful contained replacement retained a rollback directory.'

    $legacyStage = Join-Path $testRoot '.legacy.staging'
    $legacyDestination = Join-Path $testRoot 'legacy'
    New-TestKit $legacyDestination 'legacy-old' -Legacy
    New-TestKit $legacyStage 'legacy-new'
    $legacy = Invoke-CerebralInstallationKitPublish -StagingRoot $legacyStage -DestinationRoot $legacyDestination -SimulatedRenameFailure AccessDenied
    Assert-True ($legacy.Mode -ceq 'Contained') 'A legacy JSON kit must support the one-way contained upgrade.'
    Assert-True ((Test-Path -LiteralPath (Join-Path $legacyDestination 'kit.ini') -PathType Leaf) -and
        -not (Test-Path -LiteralPath (Join-Path $legacyDestination 'kit.json'))) 'Legacy replacement did not switch its outer marker to kit.ini.'

    $failureStage = Join-Path $testRoot '.failure.staging'
    $failureDestination = Join-Path $testRoot 'failure'
    New-TestKit $failureDestination 'failure-old' -Legacy
    New-TestKit $failureStage 'failure-new'
    $failed = $false
    try {
        Invoke-CerebralInstallationKitPublish -StagingRoot $failureStage -DestinationRoot $failureDestination `
            -SimulatedRenameFailure AccessDenied -FailContainedSwapAt AfterNewPayload | Out-Null
    } catch { $failed = $true }
    Assert-True $failed 'The injected contained-swap failure did not fail.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $failureDestination 'kit.json') -Raw).Contains('failure-old')) 'Contained-swap rollback did not restore the legacy marker.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $failureDestination 'Models\payload.txt') -Raw).Contains('failure-old')) 'Contained-swap rollback did not restore the previous payload.'

    Write-Output 'InstallationKitPublisher.Tests PASS (5 scenarios)'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove test data outside the temp root: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
