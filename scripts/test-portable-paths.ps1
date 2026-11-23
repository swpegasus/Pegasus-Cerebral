[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $projectRoot 'deploy\win-x64'
$testRoot = Join-Path $projectRoot '.runtime\portable-path-smoke'
if (-not $testRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Portable path test escaped the workspace.'
}
if (Test-Path -LiteralPath $testRoot) { throw "Inspect and remove the existing task-owned test folder first: $testRoot" }
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $testRoot -Recurse
    @"
[Cerebral]
Product=PegasusCerebral
Portable=1
"@ | Set-Content -LiteralPath (Join-Path $testRoot 'installation.ini') -Encoding ascii
    $worker = Join-Path $testRoot 'Applications\Worker\Cerebral.Worker.exe'
    $start = [Diagnostics.ProcessStartInfo]::new($worker)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('manage')
    $start.ArgumentList.Add('installation-paths')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0) {
            throw "Portable path inspection failed: $($errorOutput.GetAwaiter().GetResult())"
        }
        $paths = $output.GetAwaiter().GetResult() | ConvertFrom-Json
    } finally { $process.Dispose() }
    if ($paths.portable -ne $true -or $paths.base -ne $testRoot -or $paths.install -ne $testRoot -or
        $paths.data -ne (Join-Path $testRoot 'Data') -or $paths.tools -ne (Join-Path $testRoot 'Tools') -or
        $paths.userData -ne (Join-Path $testRoot 'UserData')) {
        throw 'The published Worker did not resolve the portable marker to the expected roots.'
    }
    Write-Host 'PASS: published Worker resolves portable base, install, data, tools and user-data paths from installation.ini.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
