# Regenerates the raster-only Cerebral branding package. This does not build or package an application.
[CmdletBinding()]
param(
    [string] $Python = $env:CEREBRAL_PYTHON
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$generator = Join-Path $PSScriptRoot 'build-branding-assets.py'

if (-not $Python) {
    $candidate = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($candidate) { $Python = $candidate.Source }
}
if (-not $Python -or -not (Test-Path -LiteralPath $Python -PathType Leaf)) {
    throw 'Python with Pillow is required to regenerate branding assets. Set CEREBRAL_PYTHON to its python.exe.'
}

& $Python $generator --root $repoRoot
$generatorSucceeded = $?
$generatorExitCode = $LASTEXITCODE
if (-not $generatorSucceeded -or ($null -ne $generatorExitCode -and $generatorExitCode -ne 0)) {
    throw "Branding asset generation failed with exit code $generatorExitCode."
}
