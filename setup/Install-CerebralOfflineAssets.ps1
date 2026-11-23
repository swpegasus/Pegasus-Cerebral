[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FeatureRoot,
    [Parameter(Mandatory)][string]$ToolsDirectory,
    [Parameter(Mandatory)][string]$ModelsDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Full([string]$Path) { [IO.Path]::GetFullPath($Path) }
function RequireFile([string]$Path, [long]$Bytes = 0) {
    $value = Full $Path
    if (-not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "Required offline asset is missing: $value" }
    if ($Bytes -gt 0 -and (Get-Item -LiteralPath $value).Length -ne $Bytes) { throw "Offline asset has an unexpected size: $value" }
    return $value
}
function CopyFileAtomic([string]$Source, [string]$Destination) {
    $parent = [IO.Path]::GetDirectoryName($Destination)
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $temporary = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
    try {
        [IO.File]::Copy($Source, $temporary, $false)
        [IO.File]::Move($temporary, $Destination, $true)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

$feature = Full $FeatureRoot
$tools = Full $ToolsDirectory
$models = Full $ModelsDirectory
$metadataPath = RequireFile (Join-Path $feature 'speech-assets.json')
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if ($metadata.schema -cne 'cerebral.speech.assets/1') { throw 'Unsupported AI Dictation asset manifest.' }

$runtimeSource = Full (Join-Path $feature 'Runtime')
$runtimeExecutable = RequireFile (Join-Path $runtimeSource 'bin\nemo-speech.exe')
$license = RequireFile (Join-Path $runtimeSource 'share\licenses\nemo-speech\LICENSE')
$modelSource = RequireFile (Join-Path $feature 'Model\parakeet-tdt-0.6b-v3.q8_0.gguf') ([long]$metadata.model.bytes)
$modelCard = RequireFile (Join-Path $feature 'Model\MODEL-CARD.md')
$attribution = RequireFile (Join-Path $feature 'Model\ATTRIBUTION.txt')

$runtimeDestination = Join-Path $tools 'AI-Dictation\NeMo-Speech'
$runtimeStage = $runtimeDestination + '.staging-' + [Guid]::NewGuid().ToString('N')
$runtimePrevious = $runtimeDestination + '.previous-' + [Guid]::NewGuid().ToString('N')
try {
    [IO.Directory]::CreateDirectory($runtimeStage) | Out-Null
    Copy-Item -Path (Join-Path $runtimeSource '*') -Destination $runtimeStage -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $runtimeStage 'bin\nemo-speech.exe') -PathType Leaf)) { throw 'Staged AI Dictation runtime is incomplete.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($runtimeDestination)) | Out-Null
    if (Test-Path -LiteralPath $runtimeDestination) { [IO.Directory]::Move($runtimeDestination, $runtimePrevious) }
    [IO.Directory]::Move($runtimeStage, $runtimeDestination)
    if (Test-Path -LiteralPath $runtimePrevious) { Remove-Item -LiteralPath $runtimePrevious -Recurse -Force }
} catch {
    if ((Test-Path -LiteralPath $runtimePrevious) -and -not (Test-Path -LiteralPath $runtimeDestination)) {
        [IO.Directory]::Move($runtimePrevious, $runtimeDestination)
    }
    throw
} finally {
    if (Test-Path -LiteralPath $runtimeStage) { Remove-Item -LiteralPath $runtimeStage -Recurse -Force }
}

$modelDestination = Join-Path $models 'AI-Dictation\parakeet-tdt-0.6b-v3.q8_0.gguf'
CopyFileAtomic $modelSource $modelDestination
CopyFileAtomic $modelCard (Join-Path ([IO.Path]::GetDirectoryName($modelDestination)) 'MODEL-CARD.md')
CopyFileAtomic $attribution (Join-Path ([IO.Path]::GetDirectoryName($modelDestination)) 'ATTRIBUTION.txt')
CopyFileAtomic $metadataPath (Join-Path ([IO.Path]::GetDirectoryName($modelDestination)) 'speech-assets.json')

[pscustomobject]@{
    contract = 'pegasus.cerebral.offline-assets/1'
    feature = 'pegasus.ai-dictation'
    runtime = (Join-Path $runtimeDestination 'bin\nemo-speech.exe')
    model = $modelDestination
    runtimeLicense = (Join-Path $runtimeDestination 'share\licenses\nemo-speech\LICENSE')
    ready = $true
} | ConvertTo-Json -Compress
