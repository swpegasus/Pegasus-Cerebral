[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModelPath,
    [string]$VisionProjectorPath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'

$model = [IO.Path]::GetFullPath($ModelPath)
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
$destination = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $model -PathType Leaf)) { throw "Model weights were not found: $model" }
if (-not $model.EndsWith('.gguf', [StringComparison]::OrdinalIgnoreCase)) { throw 'Model packages require a .gguf payload.' }
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw "Model package manifest was not found: $manifestFile" }
if (-not ($destination.EndsWith('.CerebralLLM', [StringComparison]::OrdinalIgnoreCase) -or
    $destination.EndsWith('.cerebral-model.zip', [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Model package output must end in .CerebralLLM (or the legacy .cerebral-model.zip extension).'
}
if (Test-Path -LiteralPath $destination) { throw "Model package output already exists: $destination" }

$magic = [byte[]]::new(4)
$input = [IO.File]::OpenRead($model)
try { $input.ReadExactly($magic) } finally { $input.Dispose() }
if ([Text.Encoding]::ASCII.GetString($magic) -ne 'GGUF') { throw 'The selected model does not have a GGUF header.' }

$projector = $null
if (-not [string]::IsNullOrWhiteSpace($VisionProjectorPath)) {
    $projector = [IO.Path]::GetFullPath($VisionProjectorPath)
    if (-not (Test-Path -LiteralPath $projector -PathType Leaf)) { throw "Vision projector was not found: $projector" }
    if (-not $projector.EndsWith('.gguf', [StringComparison]::OrdinalIgnoreCase)) { throw 'Vision components require a .gguf payload.' }
    $projectorMagic = [byte[]]::new(4)
    $projectorInput = [IO.File]::OpenRead($projector)
    try { $projectorInput.ReadExactly($projectorMagic) } finally { $projectorInput.Dispose() }
    if ([Text.Encoding]::ASCII.GetString($projectorMagic) -ne 'GGUF') { throw 'The selected vision component does not have a GGUF header.' }
}

$manifestText = [IO.File]::ReadAllText($manifestFile, [Text.UTF8Encoding]::new($false))
if ([Text.Encoding]::UTF8.GetByteCount($manifestText) -gt 65536) { throw 'Model package manifest exceeds 64 KiB.' }
$manifest = $manifestText | ConvertFrom-Json -Depth 64
if ($manifest.contract -ne 'pegasus.cerebral.model' -or $manifest.version -notin @(1, 2) -or -not $manifest.package -or -not $manifest.model) {
    throw 'Manifest must use pegasus.cerebral.model version 1 or 2 and contain package and model objects.'
}
if ([string]::IsNullOrWhiteSpace($manifest.model.license) -or [string]::IsNullOrWhiteSpace($manifest.model.upstreamModelUrl) -or
    [string]::IsNullOrWhiteSpace($manifest.model.licenseUrl) -or [string]::IsNullOrWhiteSpace($manifest.model.publisher)) {
    throw 'Manifest must record publisher, license, official upstream model URL, and license URL.'
}
$actualSize = (Get-Item -LiteralPath $model).Length
if ($manifest.model.sizeBytes -and [int64]$manifest.model.sizeBytes -ne $actualSize) {
    throw "Manifest sizeBytes ($($manifest.model.sizeBytes)) does not match the GGUF ($actualSize)."
}
$manifest.model.sizeBytes = $actualSize
$manifest.version = if ($projector) { 2 } else { 1 }
if ($projector) {
    if ($manifest.model.PSObject.Properties.Name -contains 'visionProjectorPath') {
        $manifest.model.visionProjectorPath = 'projector.gguf'
    } else {
        $manifest.model | Add-Member -NotePropertyName visionProjectorPath -NotePropertyValue 'projector.gguf'
    }
} elseif ($manifest.model.PSObject.Properties.Name -contains 'visionProjectorPath') {
    $manifest.model.visionProjectorPath = $null
}
$normalized = $manifest | ConvertTo-Json -Depth 64

$parent = [IO.Path]::GetDirectoryName($destination)
[IO.Directory]::CreateDirectory($parent) | Out-Null
$temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
try {
    $output = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 1048576, [IO.FileOptions]::SequentialScan)
    try {
        $zip = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $metadataEntry = $zip.CreateEntry('manifest.json', [IO.Compression.CompressionLevel]::Fastest)
            $metadata = $metadataEntry.Open()
            try {
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($normalized)
                $metadata.Write($bytes, 0, $bytes.Length)
            } finally { $metadata.Dispose() }
            $weightsEntry = $zip.CreateEntry('model.gguf', [IO.Compression.CompressionLevel]::NoCompression)
            $weights = $weightsEntry.Open()
            try {
                $source = [IO.FileStream]::new($model, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 1048576, [IO.FileOptions]::SequentialScan)
                try { $source.CopyTo($weights, 1048576) } finally { $source.Dispose() }
            } finally { $weights.Dispose() }
            if ($projector) {
                $projectorEntry = $zip.CreateEntry('projector.gguf', [IO.Compression.CompressionLevel]::NoCompression)
                $projectorOutput = $projectorEntry.Open()
                try {
                    $source = [IO.FileStream]::new($projector, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 1048576, [IO.FileOptions]::SequentialScan)
                    try { $source.CopyTo($projectorOutput, 1048576) } finally { $source.Dispose() }
                } finally { $projectorOutput.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $output.Dispose() }
    [IO.File]::Move($temporary, $destination, $false)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
Write-Host "Current model package: $destination ($((Get-Item -LiteralPath $destination).Length) bytes)"
