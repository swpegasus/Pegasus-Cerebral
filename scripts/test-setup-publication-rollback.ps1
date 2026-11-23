[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishedWorkerDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$testRoot = Join-Path $artifactsRoot ('setup-rollback-' + [Guid]::NewGuid().ToString('N'))
$PublishedWorkerDirectory = [IO.Path]::GetFullPath($PublishedWorkerDirectory)
if (-not $PublishedWorkerDirectory.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture validation must use a task-owned published Worker below artifacts.'
}
$script:fixtureInspector = Join-Path $PublishedWorkerDirectory 'Cerebral.Worker.exe'
$script:fixtureEngineAssembly = Join-Path $PublishedWorkerDirectory 'Engines\A\Cerebral.Engine.Default.dll'
if (-not (Test-Path -LiteralPath $script:fixtureInspector -PathType Leaf) -or -not (Test-Path -LiteralPath $script:fixtureEngineAssembly -PathType Leaf)) {
    throw 'Publish the isolated Worker and default A engine before running these fixtures.'
}
$script:fixtureRoot = $testRoot
$script:fixtureMode = 'Success'
$script:fixtureTarget = ''
$script:fixtureMoves = [Collections.Generic.List[object]]::new()
$results = [Collections.Generic.List[object]]::new()

function Assert-Fixture([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-FixturePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($script:fixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "A fixture operation escaped its temporary root: $resolved"
    }
}

function Write-FixtureFile([string]$Path, [string]$Value) {
    Assert-FixturePath $Path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Value)
}

function Get-FixtureSnapshot([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($Root, $_.FullName) + '=' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    } | Sort-Object)
}

function Write-FixtureEngine([string]$Root, [string]$Slot, [string]$Label) {
    $directory = Join-Path $Root $Slot
    Assert-FixturePath $directory
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $assembly = Join-Path $directory 'Cerebral.Engine.Default.dll'
    # A legal PE overlay makes the byte identity distinct without executing or
    # recompiling a plugin. Custom configuration and package bytes must survive.
    $bytes = [IO.File]::ReadAllBytes($script:fixtureEngineAssembly)
    $overlay = [Text.Encoding]::UTF8.GetBytes($Label)
    $combined = [byte[]]::new($bytes.Length + $overlay.Length)
    [Array]::Copy($bytes, $combined, $bytes.Length)
    [Array]::Copy($overlay, 0, $combined, $bytes.Length, $overlay.Length)
    [IO.File]::WriteAllBytes($assembly, $combined)
    Write-FixtureFile (Join-Path $directory 'engine-settings.ini') ("[settings]" + [Environment]::NewLine + "label=$Label" + [Environment]::NewLine)
    $assemblyHash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
    $configurationHash = (Get-FileHash -LiteralPath (Join-Path $directory 'engine-settings.ini') -Algorithm SHA256).Hash
    Write-FixtureFile (Join-Path $directory 'engine.ini') (@(
        '[engine]','id=pegasus.default-engine','version=1.0.0','protocol=pegasus.cerebral.engine/1',
        'assembly=Cerebral.Engine.Default.dll',"assembly-sha256=$assemblyHash",
        'entry-type=Cerebral.Engine.Default.DefaultEnginePlugin',"configuration-sha256=$configurationHash"
    ) -join [Environment]::NewLine)
}

function Write-FixtureSelection([string]$Path, [string]$Engines, [string]$Slot) {
    $package = (Get-FileHash -LiteralPath (Join-Path $Engines "$Slot\engine.ini") -Algorithm SHA256).Hash.ToLowerInvariant()
    $configuration = (Get-FileHash -LiteralPath (Join-Path $Engines "$Slot\engine-settings.ini") -Algorithm SHA256).Hash.ToLowerInvariant()
    $knownPackage = (Get-FileHash -LiteralPath (Join-Path $Engines 'A\engine.ini') -Algorithm SHA256).Hash.ToLowerInvariant()
    $knownConfiguration = (Get-FileHash -LiteralPath (Join-Path $Engines 'A\engine-settings.ini') -Algorithm SHA256).Hash.ToLowerInvariant()
    $selection = @('[selection]','schema=1','epoch=37',"slot=$Slot","package=$package","configuration=$configuration",
        'known-slot=A',"known-package=$knownPackage","known-configuration=$knownConfiguration",'pending=false','paused=true') -join [Environment]::NewLine
    Write-FixtureFile $Path $selection
    return $selection
}

function Invoke-FixtureMove([string]$Kind, [string]$Source, [string]$Destination) {
    Assert-FixturePath $Source
    Assert-FixturePath $Destination
    if ($Source -match '[\\/]\.(?:core|feature)-previous-[a-f0-9]{32}[\\/]') {
        $phase = 'RollbackMove'; $activePath = $Destination
    }
    elseif ($Destination -match '[\\/]\.(?:core|feature)-previous-[a-f0-9]{32}[\\/]') {
        $phase = 'OldMove'; $activePath = $Source
    }
    elseif ($Source -match '[\\/]\.(?:core|feature)-stage-[a-f0-9]{32}[\\/]') {
        $phase = 'NewMove'; $activePath = $Destination
    }
    else { throw "Unrecognized fixture move: $Source -> $Destination" }
    $targeted = $activePath.Equals($script:fixtureTarget, [StringComparison]::OrdinalIgnoreCase)
    $script:fixtureMoves.Add([pscustomobject]@{ Phase=$phase; Targeted=$targeted })
    if ($targeted -and (
        ($script:fixtureMode -eq 'OldMove' -and $phase -eq 'OldMove') -or
        ($script:fixtureMode -in @('NewMove','RollbackMove') -and $phase -eq 'NewMove') -or
        ($script:fixtureMode -eq 'RollbackMove' -and $phase -eq 'RollbackMove'))) {
        # Avoid the production IOException retry delay; the rename boundary is
        # deterministic and no production fault-injection option is introduced.
        throw [InvalidOperationException]::new("Fixture injected $phase failure.")
    }
    if ($Kind -eq 'Directory') { [IO.Directory]::Move($Source, $Destination) }
    else { [IO.File]::Move($Source, $Destination) }
}

function Get-InstrumentedInstaller([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$parseErrors)
    Assert-Fixture ($parseErrors.Count -eq 0) "Installer parsing failed: $Path"
    # Run the complete current installer orchestration, replacing only native
    # rename calls with a path-confined fault injector. Successful calls still
    # perform real filesystem renames; no installer or executable is launched.
    $moves = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
        $node.Static -and $node.Expression -is [Management.Automation.Language.TypeExpressionAst] -and
        $node.Expression.TypeName.FullName -in @('IO.Directory','IO.File') -and
        $node.Member.Value -eq 'Move'
    }, $true) | Sort-Object { $_.Extent.StartOffset } -Descending)
    Assert-Fixture ($moves.Count -gt 0) "No native rename boundaries found: $Path"
    $edits = [Collections.Generic.List[object]]::new()
    foreach ($move in $moves) {
        Assert-Fixture ($move.Arguments.Count -eq 2) "Unexpected rename arguments: $Path"
        $kind = if ($move.Expression.TypeName.FullName -eq 'IO.Directory') { 'Directory' } else { 'File' }
        $replacement = "(Invoke-FixtureMove -Kind '$kind' -Source ($($move.Arguments[0].Extent.Text)) -Destination ($($move.Arguments[1].Extent.Text)))"
        $edits.Add([pscustomobject]@{ Start=$move.Extent.StartOffset; End=$move.Extent.EndOffset; Text=$replacement })
    }
    # The payload executables are inert marker files. Redirect only the validator
    # executable argument to the actual isolated Worker; package inspection and
    # all preservation logic still run without mock validation.
    $validations = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Get-ValidatedEnginePackages'
    }, $true))
    foreach ($validation in $validations) {
        $argument = $validation.CommandElements[1].Extent
        $edits.Add([pscustomobject]@{ Start=$argument.StartOffset; End=$argument.EndOffset; Text='$script:fixtureInspector' })
    }
    foreach ($edit in $edits | Sort-Object Start -Descending) {
        $text = $text.Remove($edit.Start, $edit.End - $edit.Start).Insert($edit.Start, $edit.Text)
    }
    [ScriptBlock]::Create($text)
}

function Invoke-PublicationCase([string]$Kind, [string]$Mode, [string]$Target = '', [bool]$Fresh = $false) {
    $name = "$Kind-$Mode-" + $(if ($Target) { $Target } else { 'directory' }) + $(if ($Fresh) { '-fresh' } else { '' })
    $caseRoot = Join-Path $testRoot $name
    $source = Join-Path $caseRoot 'source'
    $destination = Join-Path $caseRoot 'destination'
    $receipt = Join-Path $caseRoot 'receipt.json'
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $newFiles = [ordered]@{}
    $oldFiles = [ordered]@{}
    if ($Kind -eq 'core') {
        foreach ($relative in @('Applications\Worker\Cerebral.Worker.exe','Applications\Desktop\Cerebral.Integrator.exe',
            'Applications\Desktop\Cerebral.Orchestrator.exe','Resources\Branding\Cerebral\brand.ini','z-file.txt')) {
            $newFiles[$relative] = 'new-' + $relative
            $oldFiles[$relative] = 'old-' + $relative
        }
        $oldFiles['Applications\Worker\retired.txt'] = 'retired'
        $oldFiles['Worker\legacy.txt'] = 'legacy'
        $targetRelative = switch ($Target) { 'file' { 'z-file.txt' } 'legacy' { 'Worker' } default { 'Resources' } }
        $targetMarker = switch ($Target) { 'file' { 'z-file.txt' } 'legacy' { 'Worker\legacy.txt' } default { 'Resources\Branding\Cerebral\brand.ini' } }
        $earlierMarker = 'Applications\Worker\Cerebral.Worker.exe'
    }
    else {
        foreach ($relative in @('Plugins\Service\fixture.plugin\marker.txt','Plugins\Desktop\fixture.plugin\marker.txt')) {
            $newFiles[$relative] = 'new-' + $relative
            $oldFiles[$relative] = 'old-' + $relative
        }
        $oldFiles['Plugins\Service\fixture.plugin\retired.txt'] = 'retired'
        $targetRelative = 'Plugins\Desktop\fixture.plugin'
        $targetMarker = $targetRelative + '\marker.txt'
        $earlierMarker = 'Plugins\Service\fixture.plugin\marker.txt'
        if ($Mode -eq 'FileCollision') {
            $oldFiles.Remove($targetMarker)
            $oldFiles[$targetRelative] = 'unrelated file at the Desktop plugin path'
        }
    }
    foreach ($relative in $newFiles.Keys) { Write-FixtureFile (Join-Path $source $relative) $newFiles[$relative] }
    if (-not $Fresh) {
        foreach ($relative in $oldFiles.Keys) { Write-FixtureFile (Join-Path $destination $relative) $oldFiles[$relative] }
    }
    Write-FixtureFile (Join-Path $destination 'Data\unrelated.txt') 'unrelated-user-data'
    Write-FixtureFile (Join-Path $destination 'installation.ini') 'installer-owned'
    if ($Kind -eq 'core') {
        $sourceEngines = Join-Path $source 'Applications\Worker\Engines'
        $destinationEngines = Join-Path $destination 'Applications\Worker\Engines'
        Write-FixtureEngine $sourceEngines 'A' 'bundled-source-A'
        if (-not $Fresh) {
            Write-FixtureEngine $destinationEngines 'A' 'owner-custom-A'
            Write-FixtureEngine $destinationEngines 'B' 'owner-custom-B'
        }
        $expectedEngineFiles = if ($Fresh) { Get-FixtureSnapshot $sourceEngines } else { Get-FixtureSnapshot $destinationEngines }
        $selectionEngines = if ($Fresh) { $sourceEngines } else { $destinationEngines }
        $selectionSlot = if ($Fresh) { 'A' } else { 'B' }
        $expectedSelection = Write-FixtureSelection (Join-Path $destination 'Data\Engine\selection.ini') $selectionEngines $selectionSlot
    }
    $before = Get-FixtureSnapshot $destination

    $script:fixtureMode = $Mode
    $script:fixtureTarget = Join-Path $destination $targetRelative
    $script:fixtureMoves.Clear()
    $parameters = @{ DestinationRoot=$destination; ReceiptPath=$receipt }
    if ($Kind -eq 'core') {
        $parameters.SourceRoot = $source
        $installer = Get-InstrumentedInstaller (Join-Path $projectRoot 'setup\Install-CerebralCore.ps1')
    }
    else {
        Write-FixtureFile (Join-Path $source 'feature.json') '{"contract":"pegasus.cerebral.installer-feature","schemaVersion":1,"id":"fixture.plugin"}'
        $package = Join-Path $caseRoot 'fixture.CerebralFeature'
        [IO.Compression.ZipFile]::CreateFromDirectory($source, $package)
        $parameters.PackagePath = $package; $parameters.ExpectedId = 'fixture.plugin'
        $installer = Get-InstrumentedInstaller (Join-Path $projectRoot 'setup\Install-CerebralFeature.ps1')
    }
    $caught = $null
    try { & $installer @parameters | Out-Null }
    catch { $caught = $_.Exception }
    Assert-Fixture (Test-Path -LiteralPath $receipt) "$name did not write a receipt."
    $state = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    $stageRoots = @(Get-ChildItem -LiteralPath $destination -Directory -Filter ".$Kind-stage-*")
    $previousRoots = @(Get-ChildItem -LiteralPath $destination -Directory -Filter ".$Kind-previous-*")
    Assert-Fixture ([IO.File]::ReadAllText((Join-Path $destination 'Data\unrelated.txt')) -ceq 'unrelated-user-data') "$name changed unrelated user data."
    Assert-Fixture ([IO.File]::ReadAllText((Join-Path $destination 'installation.ini')) -ceq 'installer-owned') "$name changed installer-owned state."

    if ($Mode -eq 'Success') {
        Assert-Fixture ($null -eq $caught -and $state.ok) "$name failed: $caught"
        foreach ($relative in $newFiles.Keys) {
            Assert-Fixture ([IO.File]::ReadAllText((Join-Path $destination $relative)) -ceq $newFiles[$relative]) "$name did not publish $relative."
        }
        foreach ($relative in $oldFiles.Keys) {
            if (-not $newFiles.Contains($relative)) {
                Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $destination $relative))) "$name retained retired $relative."
            }
        }
        Assert-Fixture ($stageRoots.Count -eq 0 -and $previousRoots.Count -eq 0) "$name retained completed transaction directories."
        if ($Kind -eq 'core') {
            $actualEngineFiles = Get-FixtureSnapshot $destinationEngines
            Assert-Fixture (($expectedEngineFiles -join '|') -ceq ($actualEngineFiles -join '|')) "$name replaced managed engine byte identities."
            Assert-Fixture ([IO.File]::ReadAllText((Join-Path $destination 'Data\Engine\selection.ini')) -ceq $expectedSelection) "$name changed engine selection pins."
        }
    }
    elseif ($Mode -eq 'FileCollision') {
        Assert-Fixture ($null -ne $caught -and -not $state.ok -and $state.error.StartsWith('Feature destination must be a real directory.')) "$name did not report the collision."
        Assert-Fixture ($script:fixtureMoves.Count -eq 0) "$name moved a package before rejecting the collision."
        $after = Get-FixtureSnapshot $destination
        Assert-Fixture (($before -join "`n") -ceq ($after -join "`n")) "$name changed the blocking file or an existing package."
        Assert-Fixture ($stageRoots.Count -eq 0 -and $previousRoots.Count -eq 0) "$name created transaction directories."
    }
    else {
        Assert-Fixture ($null -ne $caught -and -not $state.ok) "$name incorrectly reported success."
        $targetMoves = @($script:fixtureMoves | Where-Object Targeted | ForEach-Object Phase)
        Assert-Fixture ($targetMoves -contains $(if ($Mode -eq 'OldMove') { 'OldMove' } else { 'NewMove' })) "$name never reached its injected fault."
        if ($Mode -eq 'RollbackMove') {
            Assert-Fixture ($caught -is [AggregateException] -and $caught.InnerExceptions.Count -eq 2) "$name did not retain both publication and rollback failures: $caught"
            Assert-Fixture ($caught.Message.Contains('Fixture injected NewMove failure.') -and $caught.Message.Contains('Fixture injected RollbackMove failure.')) "$name lost a failure cause."
            Assert-Fixture ($stageRoots.Count -eq 1 -and $previousRoots.Count -eq 1) "$name deleted incomplete recovery artifacts."
            Assert-Fixture ($state.error.Contains($stageRoots[0].FullName) -and $state.error.Contains($previousRoots[0].FullName)) "$name receipt omitted recovery paths."
            $stagedMarker = if ($Kind -eq 'core') { Join-Path 'Payload' $targetMarker } else { $targetMarker }
            Assert-Fixture ([IO.File]::ReadAllText((Join-Path $previousRoots[0].FullName $targetMarker)) -ceq $oldFiles[$targetMarker]) "$name lost previous contents."
            Assert-Fixture ([IO.File]::ReadAllText((Join-Path $stageRoots[0].FullName $stagedMarker)) -ceq $newFiles[$targetMarker]) "$name lost staged contents."
            Assert-Fixture ([IO.File]::ReadAllText((Join-Path $destination $earlierMarker)) -ceq $oldFiles[$earlierMarker]) "$name stopped before restoring an earlier published entry."
            if ($Kind -eq 'core') {
                $restoredEngineFiles = Get-FixtureSnapshot $destinationEngines
                Assert-Fixture (($expectedEngineFiles -join '|') -ceq ($restoredEngineFiles -join '|')) "$name failed to restore managed engine bytes."
            }
        }
        else {
            $after = Get-FixtureSnapshot $destination
            Assert-Fixture (($before -join "`n") -ceq ($after -join "`n")) "$name failed to restore the exact prior contents."
            Assert-Fixture ($stageRoots.Count -eq 0 -and $previousRoots.Count -eq 0) "$name retained an entirely restored transaction."
            Assert-Fixture ($state.error.Contains("Fixture injected $Mode failure.")) "$name lost the publication failure."
        }
    }
    $results.Add([pscustomobject]@{ case=$name; ok=$true })
}

# Change only the fixture DLL's metadata AssemblyRef version. No plugin code is
# loaded or run; the real inspector must reject the now incompatible reference.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
public static class FixtureEngineMetadata
{
    public static void BreakEngineInterface(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using (var image = new MemoryStream(bytes, false))
        using (var pe = new PEReader(image))
        {
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeReferences)
            {
                var reference = metadata.GetTypeReference(handle);
                if (metadata.GetString(reference.Name) != "IEnginePlugin") continue;
                var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.String) +
                    MetadataTokens.GetHeapOffset(reference.Name);
                var replacement = System.Text.Encoding.UTF8.GetBytes("IEngineWrongX");
                Array.Copy(replacement, 0, bytes, offset, replacement.Length);
                File.WriteAllBytes(path, bytes);
                return;
            }
        }
        throw new InvalidOperationException("Fixture engine interface reference was missing.");
    }

    public static void BreakContractVersion(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using (var image = new MemoryStream(bytes, false))
        using (var pe = new PEReader(image))
        {
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                if (metadata.GetString(reference.Name) != "Cerebral.Engine.Contracts") continue;
                var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.AssemblyRef) +
                    (MetadataTokens.GetRowNumber(handle) - 1) * metadata.GetTableRowSize(TableIndex.AssemblyRef);
                bytes[offset] = 254; bytes[offset + 1] = 127;
                File.WriteAllBytes(path, bytes);
                return;
            }
        }
        throw new InvalidOperationException("Fixture engine contract reference was missing.");
    }
}
'@

function Invoke-EngineRejectionCase([string]$Fault) {
    $name = 'core-reject-' + $Fault
    $caseRoot = Join-Path $testRoot $name
    $source = Join-Path $caseRoot 'source'
    $destination = Join-Path $caseRoot 'destination'
    $receipt = Join-Path $caseRoot 'receipt.json'
    foreach ($relative in @('Applications\Worker\Cerebral.Worker.exe','Applications\Desktop\Cerebral.Integrator.exe',
        'Applications\Desktop\Cerebral.Orchestrator.exe','Resources\Branding\Cerebral\brand.ini')) {
        Write-FixtureFile (Join-Path $source $relative) 'new-core'
        Write-FixtureFile (Join-Path $destination $relative) 'old-core'
    }
    $sourceEngines = Join-Path $source 'Applications\Worker\Engines'
    $engines = Join-Path $destination 'Applications\Worker\Engines'
    Write-FixtureEngine $sourceEngines 'A' 'bundled-source-A'
    if ($Fault -ne 'MissingFreshEngine') {
        Write-FixtureEngine $engines 'A' 'owner-custom-A'
        Write-FixtureEngine $engines 'B' 'owner-custom-B'
    }
    $assembly = Join-Path $engines 'B\Cerebral.Engine.Default.dll'
    $manifest = Join-Path $engines 'B\engine.ini'
    switch ($Fault) {
        'MissingFreshEngine' { Remove-Item -LiteralPath $sourceEngines -Recurse -Force }
        'MissingManifest' { Remove-Item -LiteralPath $manifest }
        'HashMismatch' { [IO.File]::AppendAllText($assembly, 'tamper') }
        'BadEntryType' {
            [IO.File]::WriteAllText($manifest, [IO.File]::ReadAllText($manifest).Replace('entry-type=Cerebral.Engine.Default.DefaultEnginePlugin', 'entry-type=Missing.Engine.Type'))
        }
        'BadProtocol' {
            [IO.File]::WriteAllText($manifest, [IO.File]::ReadAllText($manifest).Replace('protocol=pegasus.cerebral.engine/1', 'protocol=pegasus.cerebral.engine/2'))
        }
        'BadInterface' {
            [FixtureEngineMetadata]::BreakEngineInterface($assembly)
            $hash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
            [IO.File]::WriteAllText($manifest, ([IO.File]::ReadAllText($manifest) -replace 'assembly-sha256=[a-fA-F0-9]+', "assembly-sha256=$hash"))
        }
        'BadContract' {
            [FixtureEngineMetadata]::BreakContractVersion($assembly)
            $hash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
            [IO.File]::WriteAllText($manifest, ([IO.File]::ReadAllText($manifest) -replace 'assembly-sha256=[a-fA-F0-9]+', "assembly-sha256=$hash"))
        }
        'BrokenAssembly' {
            [IO.File]::WriteAllText($assembly, 'not-a-managed-assembly')
            $hash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
            [IO.File]::WriteAllText($manifest, ([IO.File]::ReadAllText($manifest) -replace 'assembly-sha256=[a-fA-F0-9]+', "assembly-sha256=$hash"))
        }
        'ExistingReparse' {
            $link = Join-Path $engines 'B'
            $target = Join-Path $caseRoot 'linked-B'
            Assert-FixturePath $link; Assert-FixturePath $target
            [IO.Directory]::Move($link, $target)
            New-Item -ItemType Junction -Path $link -Target $target | Out-Null
        }
        'SourceReparse' {
            $link = Join-Path $sourceEngines 'B'
            New-Item -ItemType Junction -Path $link -Target (Join-Path $sourceEngines 'A') | Out-Null
        }
        'EngineRootReparse' {
            $target = Join-Path $caseRoot 'linked-engines'
            [IO.Directory]::Move($engines, $target)
            New-Item -ItemType Junction -Path $engines -Target $target | Out-Null
        }
        'BrokenEngineRootReparse' {
            $target = Join-Path $caseRoot 'linked-engines'
            [IO.Directory]::Move($engines, $target)
            New-Item -ItemType Junction -Path $engines -Target $target | Out-Null
            Remove-Item -LiteralPath $target -Recurse -Force
        }
        default { throw "Unrecognized engine rejection fixture: $Fault" }
    }
    Write-FixtureFile (Join-Path $destination 'Data\Engine\selection.ini') 'exact-selection-pins'
    Write-FixtureFile (Join-Path $destination 'Data\unrelated.txt') 'preserve-user-data'
    $before = Get-FixtureSnapshot $destination
    $sourceBefore = Get-FixtureSnapshot $source
    $script:fixtureMode = 'Success'; $script:fixtureTarget = ''; $script:fixtureMoves.Clear()
    $installer = Get-InstrumentedInstaller (Join-Path $projectRoot 'setup\Install-CerebralCore.ps1')
    $caught = $null
    try { & $installer -SourceRoot $source -DestinationRoot $destination -ReceiptPath $receipt | Out-Null }
    catch { $caught = $_.Exception }
    Assert-Fixture ($null -ne $caught) "$name admitted unsafe engine evidence."
    $state = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    Assert-Fixture (-not $state.ok -and $script:fixtureMoves.Count -eq 0) "$name moved old application contents before rejecting."
    Assert-Fixture (($before -join '|') -ceq ((Get-FixtureSnapshot $destination) -join '|')) "$name changed current files or pins."
    Assert-Fixture (($sourceBefore -join '|') -ceq ((Get-FixtureSnapshot $source) -join '|')) "$name changed input package files."
    Assert-Fixture (@(Get-ChildItem -LiteralPath $destination -Directory -Filter '.core-*-*').Count -eq 0) "$name retained a needless preflight transaction."
    if ($Fault -eq 'BadContract') {
        Assert-Fixture ($state.error.Contains('different shared engine contract')) "$name failed for the wrong reason."
    }
    if ($Fault -eq 'BadInterface') {
        Assert-Fixture ($state.error.Contains('shared IEnginePlugin interface')) "$name failed for the wrong reason."
    }
    if ($Fault -eq 'ExistingReparse') {
        Assert-Fixture (((Get-Item -LiteralPath $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) "$name changed the existing junction."
    }
    $results.Add([pscustomobject]@{ case=$name; ok=$true })
}

[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    foreach ($kind in @('core','feature')) {
        Invoke-PublicationCase $kind 'Success'
        Invoke-PublicationCase $kind 'Success' -Fresh $true
        Invoke-PublicationCase $kind 'OldMove'
        Invoke-PublicationCase $kind 'NewMove'
        Invoke-PublicationCase $kind 'NewMove' -Fresh $true
        Invoke-PublicationCase $kind 'RollbackMove'
    }
    foreach ($mode in @('OldMove','NewMove','RollbackMove')) { Invoke-PublicationCase 'core' $mode 'file' }
    Invoke-PublicationCase 'core' 'OldMove' 'legacy'
    Invoke-PublicationCase 'feature' 'FileCollision'
    foreach ($fault in @('MissingFreshEngine','MissingManifest','HashMismatch','BadProtocol','BadEntryType','BadInterface','BadContract','BrokenAssembly','ExistingReparse','SourceReparse','EngineRootReparse','BrokenEngineRootReparse')) {
        Invoke-EngineRejectionCase $fault
    }
    Write-Host "PASS: $($results.Count) deterministic setup publication/engine-preservation fixtures (core directory/file/legacy and feature; fresh/update; old/new/rollback failures; retained recovery paths; receipts; unrelated state)."
}
finally {
    # Resolve and confine the recursive deletion to this task-owned fixture root.
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedRoot) -notmatch '^setup-rollback-[a-f0-9]{32}$') {
        throw "Refusing to remove an unexpected fixture root: $resolvedRoot"
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
