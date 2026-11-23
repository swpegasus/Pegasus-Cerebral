[CmdletBinding()]
param(
    [string]$DestinationRoot = (Join-Path $PSScriptRoot '..\.runtime\prerequisites'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\setup\prerequisites.json'),
    [switch]$SkipVisualStudioLayout
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$destination = [IO.Path]::GetFullPath($DestinationRoot)
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($manifest.contract -cne 'pegasus.cerebral.prerequisites' -or $manifest.schemaVersion -ne 1) { throw 'Unsupported prerequisite manifest.' }
[IO.Directory]::CreateDirectory($destination) | Out-Null
Copy-Item -LiteralPath $manifestFile -Destination (Join-Path $destination 'manifest.json') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\setup\Install-CerebralPrerequisites.ps1') -Destination (Join-Path $destination 'Install-CerebralPrerequisites.ps1') -Force

function AssertInstallerPayload([string]$Path, [string]$ExpectedExtension = '') {
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 8) { throw "Downloaded installer is empty or truncated: $Path" }
        $magic = [byte[]]::new(8)
        [void]$stream.Read($magic, 0, $magic.Length)
    } finally { $stream.Dispose() }
    $extension = if ($ExpectedExtension) { $ExpectedExtension } else { [IO.Path]::GetExtension($Path) }
    switch ($extension.ToLowerInvariant()) {
        '.exe' { if ($magic[0] -ne 0x4d -or $magic[1] -ne 0x5a) { throw "Downloaded .exe is not a Windows PE image: $Path" } }
        '.msi' {
            $ole = [byte[]](0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1)
            for ($index = 0; $index -lt $ole.Length; $index++) {
                if ($magic[$index] -ne $ole[$index]) { throw "Downloaded .msi is not an OLE installer image: $Path" }
            }
        }
        default { throw "Unsupported prerequisite payload extension: $Path" }
    }
}

function Download([string]$Url, [string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        try { AssertInstallerPayload $Path; return }
        catch { Remove-Item -LiteralPath $Path -Force }
    }
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
    try {
        if ($Url.Contains('sourceforge.net/', [StringComparison]::OrdinalIgnoreCase)) {
            $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
            if (-not (Test-Path -LiteralPath $curl -PathType Leaf)) { throw 'Windows curl.exe is required to resolve the SourceForge mirror safely.' }
            & $curl '--fail' '--location' '--silent' '--show-error' '--retry' '3' '--output' $temporary $Url
            if ($LASTEXITCODE -ne 0) { throw "curl.exe could not download $Url (exit $LASTEXITCODE)." }
        } else {
            Invoke-WebRequest -Uri $Url -OutFile $temporary -MaximumRedirection 10
        }
        if ((Get-Item -LiteralPath $temporary).Length -lt 32768) { throw "Downloaded payload is unexpectedly small: $Url" }
        AssertInstallerPayload $temporary ([IO.Path]::GetExtension($Path))
        Move-Item -LiteralPath $temporary -Destination $Path
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

foreach ($item in $manifest.components) {
    if ($item.id -eq 'vs-buildtools') { continue }
    $target = Join-Path $destination ([string]$item.file)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Download ([string]$item.download) $target
    $signature = Get-AuthenticodeSignature -LiteralPath $target
    if ($signature.Status -ne 'Valid') { Write-Warning "$($item.id) is not Authenticode-valid ($($signature.Status)); retain the official source metadata and review before public distribution." }
}

if (-not $SkipVisualStudioLayout) {
    $vs = @($manifest.components | Where-Object id -eq 'vs-buildtools')[0]
    $layout = Join-Path $destination 'vs-buildtools-layout'
    [IO.Directory]::CreateDirectory($layout) | Out-Null
    $bootstrapper = Join-Path $layout 'vs_BuildTools.exe'
    Download ([string]$vs.download) $bootstrapper
    $signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
    if ($signature.Status -ne 'Valid') { throw "Visual Studio Build Tools bootstrapper is not Authenticode-valid: $($signature.Status)" }
    # Windows PowerShell's Start-Process flattens an ArgumentList array without preserving
    # quotes around paths containing spaces. Pass one deliberately quoted command line.
    $layoutArguments = '--layout "{0}" --lang en-US --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --quiet --wait --norestart' -f $layout.Replace('"', '')
    $process = Start-Process -FilePath $bootstrapper -ArgumentList $layoutArguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Visual Studio offline layout failed with exit code $($process.ExitCode)." }
    if (-not (Test-Path -LiteralPath (Join-Path $layout 'channelManifest.json') -PathType Leaf)) { throw 'Visual Studio offline layout did not produce channelManifest.json.' }
}

$files = Get-ChildItem -LiteralPath $destination -File -Recurse
[pscustomobject]@{ root=$destination; files=$files.Count; bytes=[long](($files | Measure-Object Length -Sum).Sum); complete=(-not $SkipVisualStudioLayout) }
