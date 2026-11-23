$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $projectRoot 'scripts\Cerebral.CortexBehaviorPacks.psm1') -Force
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Cerebral-Behavior-Packs-' + [Guid]::NewGuid().ToString('N'))

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Fails([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw $Message }
}

function New-TestPack([string]$Directory, [string]$PackId, [string]$Prefix, [int]$Count) {
    $packRoot = Join-Path $testRoot $Directory
    [IO.Directory]::CreateDirectory((Join-Path $packRoot 'records')) | Out-Null
    $records = for ($index = 0; $index -lt $Count; $index++) {
        $recordId = if ($index -eq 0) { $Prefix } else { "$Prefix.Item$index" }
        $contentPath = "records/item$index.record.md"
        Set-Content -LiteralPath (Join-Path $packRoot $contentPath) -Value "# $recordId`n`nPortable test guidance." -Encoding utf8NoBOM
        [ordered]@{
            recordId = $recordId
            kind = 'tenet'
            tier = 'explicit'
            title = $recordId
            tags = @('generalized')
            source = 'agent'
            contentPath = $contentPath
            sourceRelativePath = "fixture-$index"
        }
    }
    [ordered]@{
        schema = 'pegasus.cortex.behavior-pack/2.0'
        packId = $PackId
        packRevision = 1
        description = 'Portable test pack.'
        includesTasks = $false
        includesMachineHistory = $false
        records = @($records)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packRoot 'behavior-pack.json') -Encoding utf8NoBOM
}

try {
    $sourceInventory = @(Get-CerebralBehaviorPackInventory -Root (Join-Path $projectRoot 'setup\BehaviorPacks'))
    Assert-True ($sourceInventory.Count -eq 2) 'The installer-owned source behavior-pack library is incomplete.'
    Assert-True (($sourceInventory | Where-Object PackId -CEQ 'pegasus-cerebral-guides-v1').PackRevision -eq 7) 'The Cerebral guide library revision is stale.'
    Assert-True (($sourceInventory | Where-Object PackId -CEQ 'windows-operational-knowledge-v1').PackRevision -ge 1) 'The Windows guide library revision is invalid.'

    New-TestPack 'cerebral-guides' 'pegasus-cerebral-guides-v1' 'Knowledge.Cerebral.Guide' 8
    New-TestPack 'windows-operational-knowledge' 'windows-operational-knowledge-v1' 'Knowledge.Windows' 8

    $inventory = @(Get-CerebralBehaviorPackInventory -Root $testRoot)
    Assert-True ($inventory.Count -eq 2) 'The two independent behavior packs were not admitted.'
    Assert-True (($inventory | Measure-Object Records -Sum).Sum -eq 16) 'The behavior-pack inventory did not retain all declared records.'
    Assert-True (@($inventory | Where-Object { $_.Namespace -cne 'knowledge.shared' -or -not $_.SelectedByDefault }).Count -eq 0) 'Behavior-pack defaults or namespace changed.'
    Assert-True (@($inventory | Where-Object PackRevision -ne 1).Count -eq 0) 'Behavior-pack revisions were not retained.'

    $cerebralManifestPath = Join-Path $testRoot 'cerebral-guides\behavior-pack.json'
    $cerebralManifest = Get-Content -LiteralPath $cerebralManifestPath -Raw | ConvertFrom-Json
    $cerebralManifest.includesTasks = $true
    $cerebralManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $cerebralManifestPath -Encoding utf8NoBOM
    Assert-Fails { Get-CerebralBehaviorPackInventory -Root $testRoot } 'A behavior pack containing tasks was admitted.'
    $cerebralManifest.includesTasks = $false
    $cerebralManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $cerebralManifestPath -Encoding utf8NoBOM

    $cerebralManifest.packRevision = 0
    $cerebralManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $cerebralManifestPath -Encoding utf8NoBOM
    Assert-Fails { Get-CerebralBehaviorPackInventory -Root $testRoot } 'A legacy behavior pack without a managed positive revision was admitted.'
    $cerebralManifest.packRevision = 1
    $cerebralManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $cerebralManifestPath -Encoding utf8NoBOM

    $extra = Join-Path $testRoot 'windows-operational-knowledge\records\undeclared.record.md'
    Set-Content -LiteralPath $extra -Value 'undeclared' -Encoding utf8NoBOM
    Assert-Fails { Get-CerebralBehaviorPackInventory -Root $testRoot } 'An undeclared behavior-pack file was admitted.'
    Remove-Item -LiteralPath $extra -Force

    $windowsManifestPath = Join-Path $testRoot 'windows-operational-knowledge\behavior-pack.json'
    $windowsManifest = Get-Content -LiteralPath $windowsManifestPath -Raw | ConvertFrom-Json
    $windowsManifest.records[0].recordId = 'Knowledge.User.Private'
    $windowsManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $windowsManifestPath -Encoding utf8NoBOM
    Assert-Fails { Get-CerebralBehaviorPackInventory -Root $testRoot } 'An out-of-module behavior record was admitted.'

    $windowsManifest.records[0].recordId = 'Knowledge.Windows'
    $windowsManifest.records[0].tags = @('windows')
    $windowsManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $windowsManifestPath -Encoding utf8NoBOM
    Assert-Fails { Get-CerebralBehaviorPackInventory -Root $testRoot } 'A non-generalized behavior record was admitted.'

    Write-Output 'CortexBehaviorPackPackaging.Tests PASS (12 checks)'
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
