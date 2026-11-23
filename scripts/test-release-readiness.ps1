[CmdletBinding()]
param(
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$Full,
    [ValidateRange(10,2000)][int]$DesktopPluginIterations = 100,
    [ValidateRange(10,10000)][int]$ServicePluginIterations = 500,
    [string]$DotnetExecutable,
    [ValidateSet('Auto','true','false')][string]$IncludeBooks = 'Auto'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotnetExecutable) {
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $DotnetExecutable = $command.Source }
    else {
        $installed = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $installed -PathType Leaf) { $DotnetExecutable = $installed }
    }
}
if (-not $DotnetExecutable -or -not (Test-Path -LiteralPath $DotnetExecutable -PathType Leaf)) {
    throw 'Could not locate dotnet for release-readiness acceptance.'
}

# Resolve the same canonical feature property used by direct project/solution builds.
# This error-only target does not build an application or publish a payload.
$booksArguments = @('msbuild', (Join-Path $projectRoot 'src/Cerebral.Worker/Cerebral.Worker.csproj'),
    '-nologo', '-verbosity:quiet', '-nodeReuse:false', '-target:ValidateCerebralBooksFeature',
    '-getProperty:CerebralIncludeBooks', "-p:Configuration=$Configuration")
if ($IncludeBooks -ne 'Auto') { $booksArguments += "-p:CerebralIncludeBooks=$($IncludeBooks.ToLowerInvariant())" }
$booksOutput = @(& $DotnetExecutable @booksArguments)
$booksExitCode = $LASTEXITCODE
$booksValues = @($booksOutput | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } | Where-Object { $_ -in @('true','false') })
foreach ($line in $booksOutput) { if (([string]$line).Trim().ToLowerInvariant() -notin @('true','false')) { Write-Host $line } }
if ($booksExitCode -ne 0) { throw 'Could not validate the Books build feature. No build or publication was started.' }
if ($booksValues.Count -ne 1) { throw 'The Books feature query did not return one explicit boolean.' }
$includeBooksValue = $booksValues[0]
$includeBooksEnabled = $includeBooksValue -eq 'true'
$booksProperty = "-p:CerebralIncludeBooks=$includeBooksValue"
Write-Host "Books build feature: $includeBooksValue"

function Invoke-Checked([string]$Label, [string[]]$Arguments, [int]$TimeoutSeconds = 900) {
    Write-Host "[$Label]"
    $start = [Diagnostics.ProcessStartInfo]::new($DotnetExecutable)
    $start.WorkingDirectory = $projectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true); $process.WaitForExit()
            throw "$Label exceeded the bounded $TimeoutSeconds second acceptance limit."
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($output) { Write-Host $output.TrimEnd() }
        if ($errors) { Write-Warning $errors.TrimEnd() }
        if ($process.ExitCode -ne 0) { throw "$Label failed with exit code $($process.ExitCode)." }
    } finally { $process.Dispose() }
}

if (-not $SkipBuild) {
    Invoke-Checked 'Release build' @('build', (Join-Path $projectRoot 'Pegasus.Cerebral.slnx'), '-c', $Configuration, '--disable-build-servers', $booksProperty) 1200
}

$desktopTfm = 'net10.0-windows'
$workerTfm = 'net10.0'
$orchestrator = Join-Path $projectRoot "src\Cerebral.Orchestrator\bin\$Configuration\$desktopTfm\Cerebral.Orchestrator.dll"
$configurationApp = Join-Path $projectRoot "src\Cerebral.Integrator\bin\$Configuration\$desktopTfm\Cerebral.Integrator.dll"
$desktopTests = Join-Path $projectRoot "tests\Cerebral.Desktop.Tests\bin\$Configuration\$desktopTfm\Cerebral.Desktop.Tests.dll"
$workerTests = Join-Path $projectRoot "tests\Cerebral.Worker.Tests\bin\$Configuration\$workerTfm\Cerebral.Worker.Tests.dll"
$reviewManifest = Join-Path $projectRoot "examples\Cerebral.PromptReviewPlugin\bin\$Configuration\$desktopTfm\desktop-plugin.json"
foreach ($required in @($orchestrator, $configurationApp, $desktopTests, $workerTests, $reviewManifest)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Acceptance input is missing: $required" }
}

Invoke-Checked 'Source startup contract' @($orchestrator, '--check-source-local-options') 60
Invoke-Checked 'Configuration recovery surface' @($configurationApp, '--check-ui') 120
Invoke-Checked 'Retained tray lifecycle and splash diagnostics' @($orchestrator, '--check-desktop-lifecycle') 180
Invoke-Checked 'Service plugin failure stress' @($workerTests, '--plugin-stress', $ServicePluginIterations.ToString()) 300
Invoke-Checked 'Desktop plugin failure stress' @($desktopTests, '--plugin-stress', $reviewManifest, $DesktopPluginIterations.ToString()) 300

if ($Full) {
    $programming = Join-Path $projectRoot "examples\Cerebral.WorkspacePlugin\bin\$Configuration\$desktopTfm\desktop-plugin.json"
    $dictation = Join-Path $projectRoot "examples\Cerebral.DictationPlugin\bin\$Configuration\$desktopTfm\desktop-plugin.json"
    $books = if ($includeBooksEnabled) { Join-Path $projectRoot "examples\Cerebral.BookPlugin\bin\$Configuration\$desktopTfm\desktop-plugin.json" } else { '-' }
    $review = Join-Path $projectRoot "examples\Cerebral.ReviewPlugin\Desktop\bin\$Configuration\$desktopTfm\desktop-plugin.json"
    $packageManifests = @($programming, $dictation, $review)
    if ($includeBooksEnabled) { $packageManifests += $books }
    else { Write-Host 'Books acceptance checks: not included in this build.' }
    foreach ($packageManifest in $packageManifests) {
        if (-not (Test-Path -LiteralPath $packageManifest -PathType Leaf)) { throw "Desktop acceptance package is missing: $packageManifest" }
    }
    Invoke-Checked 'Desktop deterministic suite' @($desktopTests, $programming, $dictation, $books, $review) 900
    Invoke-Checked 'Worker deterministic suite' @($workerTests) 1800
}

Write-Host "Pegasus Cerebral release readiness passed ($Configuration; Full=$($Full.IsPresent); BooksIncluded=$includeBooksValue)."
