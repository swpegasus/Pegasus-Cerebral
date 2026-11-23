[CmdletBinding()]
param(
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$Installer,
    [switch]$SkipAcceptance,
    [switch]$SkipPublish,
    [switch]$WindowsOnly,
    [switch]$DesktopOnly,
    [switch]$Offline,
    [string]$DotnetExecutable,
    [ValidateSet('Auto','true','false')][string]$IncludeBooks = 'Auto',
    [string]$NsisCompiler,
    [string]$LlamaRuntimeDirectory,
    [string]$LinuxArm64LlamaRuntimeDirectory,
    [string]$StarterModelPath,
    [string]$StarterVisionProjectorPath,
    [switch]$ExcludeStarterModel,
    [string]$StarterModelManifest,
    [string]$CortexInstallerPath,
    [switch]$SignWindows,
    [ValidateSet('Pipe','Release')][string]$SigningScope = 'Pipe',
    [string]$SigningModule,
    [string]$SigningTrustBundleRoot,
    [string]$PublisherThumbprint,
    [string]$SigningProvider = 'Microsoft Software Key Storage Provider'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$deployRoot = Join-Path $projectRoot 'deploy'
# Local in-place updates need only the installed Windows applications. Keep the
# same payload destination without building standalone Workers or an installer.
if ($DesktopOnly -and (-not $Publish -or $Installer -or $SkipPublish)) {
    throw '-DesktopOnly requires -Publish and cannot be combined with -Installer or -SkipPublish.'
}
if ([string]::IsNullOrWhiteSpace($StarterModelManifest)) {
    $StarterModelManifest = Join-Path $projectRoot 'setup\starter-model.json'
}

if (-not $DotnetExecutable) {
    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($dotnetCommand) {
        $DotnetExecutable = $dotnetCommand.Source
    } else {
        $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
        $installedDotnet = Join-Path $programFiles 'dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $installedDotnet -PathType Leaf) { $DotnetExecutable = $installedDotnet }
    }
}
if (-not $DotnetExecutable -or -not [IO.Path]::IsPathFullyQualified($DotnetExecutable) -or
    -not (Test-Path -LiteralPath $DotnetExecutable -PathType Leaf)) {
    throw 'Could not locate dotnet. Supply -DotnetExecutable with an existing absolute dotnet executable path.'
}
$DotnetExecutable = [IO.Path]::GetFullPath($DotnetExecutable)

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
if ($Installer -and -not $includeBooksEnabled) {
    throw 'The current full installer requires Books. Use a source build or -Publish without -Installer for a no-Books payload; no installer or publication was started.'
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $output.GetAwaiter().GetResult()
        $stderr = $errorOutput.GetAwaiter().GetResult()
        if ($stdout) { Write-Host $stdout.TrimEnd() }
        if ($stderr) { Write-Warning $stderr.TrimEnd() }
        if ($process.ExitCode -ne 0) {
            throw "$Executable failed with exit code $($process.ExitCode). Review the failed action. The fixed Windows payload is replaced only after all application publishes and layout checks succeed."
        }
    } finally { $process.Dispose() }
}

function Copy-LlamaRuntime([string]$SourceDirectory, [string]$DestinationDirectory) {
    if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
        $SourceDirectory = Join-Path $projectRoot '.runtime\llama'
    }
    if (-not [IO.Path]::IsPathFullyQualified($SourceDirectory)) {
        $SourceDirectory = Join-Path $projectRoot $SourceDirectory
    }
    $SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "The Windows llama.cpp runtime was not found at $SourceDirectory. Supply -LlamaRuntimeDirectory with the extracted runtime folder."
    }

    # llama.cpp selects the best CPU backend at runtime, while the CUDA backend
    # loads its companion libraries from the same folder. Keep that shared DLL
    # set together and omit unrelated sample, benchmark and conversion programs.
    $required = @(
        'llama-server.exe',
        'llama-server-impl.dll',
        'llama-common.dll',
        'llama.dll',
        'ggml.dll',
        'ggml-base.dll',
        'ggml-cuda.dll',
        'cublas64_13.dll',
        'cublasLt64_13.dll',
        'cudart64_13.dll',
        'llama-fit-params.exe',
        'llama-fit-params-impl.dll',
        'libomp.dll',
        'LICENSE-LLVM-OpenMP'
    )
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $name) -PathType Leaf)) {
            throw "The llama.cpp runtime is incomplete: $name is missing from $SourceDirectory."
        }
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -File -Filter '*.dll') {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $DestinationDirectory $file.Name) -Force
    }
    foreach ($name in @('llama-server.exe','llama-fit-params.exe','LICENSE-LLVM-OpenMP')) {
        Copy-Item -LiteralPath (Join-Path $SourceDirectory $name) -Destination (Join-Path $DestinationDirectory $name) -Force
    }
}

function Copy-LinuxArm64LlamaRuntime([string]$SourceDirectory, [string]$DestinationDirectory) {
    if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
        $SourceDirectory = Join-Path $projectRoot '.runtime\llama-linux-arm64'
    }
    if (-not [IO.Path]::IsPathFullyQualified($SourceDirectory)) {
        $SourceDirectory = Join-Path $projectRoot $SourceDirectory
    }
    $SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "The Linux ARM64 llama.cpp runtime was not found at $SourceDirectory. Supply -LinuxArm64LlamaRuntimeDirectory with the verified runtime folder built for the target host."
    }

    foreach ($name in @('llama-server','llama-fit-params','LICENSE')) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $name) -PathType Leaf)) {
            throw "The Linux ARM64 llama.cpp runtime is incomplete: $name is missing from $SourceDirectory."
        }
    }
    $libraries = @(Get-ChildItem -LiteralPath $SourceDirectory -File | Where-Object Name -Match '\.so($|\.)')
    foreach ($prefix in @('libggml','libllama','libmtmd')) {
        if (-not $libraries.Where({ $_.Name.StartsWith($prefix, [StringComparison]::Ordinal) }, 'First')) {
            throw "The Linux ARM64 llama.cpp runtime is incomplete: no $prefix shared library was found in $SourceDirectory."
        }
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    foreach ($name in @('llama-server','llama-fit-params','LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $SourceDirectory $name) -Destination (Join-Path $DestinationDirectory $name) -Force
    }
    foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -File | Where-Object Name -Match '\.so($|\.)') {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $DestinationDirectory $file.Name) -Force
    }
}

function Convert-CortexReleaseVersion([string]$Value) {
    if ($Value -notmatch '^(?<major>[0-9]+)\.(?<year>[0-9]{4})\.(?<month>[0-9]{2})\.(?<day>[0-9]{2})\.(?<hour>[0-9]{2})\.(?<minute>[0-9]{2})\.(?<revision>[A-D])$') {
        throw "The Cortex release version is invalid: $Value"
    }
    return @(
        [int]$Matches.major, [int]$Matches.year, [int]$Matches.month,
        [int]$Matches.day, [int]$Matches.hour, [int]$Matches.minute,
        [int][char]$Matches.revision
    )
}

function Compare-CortexReleaseVersion([string]$Left, [string]$Right) {
    $leftParts = Convert-CortexReleaseVersion $Left
    $rightParts = Convert-CortexReleaseVersion $Right
    for ($index = 0; $index -lt $leftParts.Count; $index++) {
        if ($leftParts[$index] -lt $rightParts[$index]) { return -1 }
        if ($leftParts[$index] -gt $rightParts[$index]) { return 1 }
    }
    return 0
}

function Assert-IsolatedPluginLayout(
    [string]$ApplicationDirectory,
    [ValidateSet('Service','Desktop')][string]$Kind,
    [string]$ManifestName,
    [string[]]$ExpectedIds
) {
    $application = [IO.Path]::GetFullPath($ApplicationDirectory)
    foreach ($flattened in @('service-plugin.json','desktop-plugin.json')) {
        if (Test-Path -LiteralPath (Join-Path $application $flattened) -PathType Leaf) {
            throw "Plugin manifest $flattened was flattened into $application. Every plugin must publish below its isolated ID directory."
        }
    }
    $pluginRoot = Join-Path $application "Plugins\$Kind"
    if (-not (Test-Path -LiteralPath $pluginRoot -PathType Container)) {
        throw "The $Kind plugin root is missing from $application."
    }
    $seen = @{}
    foreach ($directory in Get-ChildItem -LiteralPath $pluginRoot -Directory) {
        $manifestPath = Join-Path $directory.FullName $ManifestName
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "Plugin $($directory.Name) is missing $ManifestName in its isolated directory."
        }
        try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
        catch { throw "Plugin manifest is invalid JSON: $manifestPath" }
        if ([string]::IsNullOrWhiteSpace($manifest.id) -or $manifest.id -cne $directory.Name) {
            throw "Plugin manifest ID must exactly match its directory: $manifestPath"
        }
        if ($seen.ContainsKey($manifest.id)) { throw "Duplicate plugin ID in publish output: $($manifest.id)" }
        $seen[$manifest.id] = $true
    }
    foreach ($id in $ExpectedIds) {
        if (-not $seen.ContainsKey($id)) { throw "Expected bundled plugin is missing from publish output: $id" }
    }
    if (-not $includeBooksEnabled) {
        if ($seen.ContainsKey('pegasus.books')) { throw 'Books entered a no-Books payload.' }
        foreach ($bookAssembly in @('Cerebral.Books.dll','Cerebral.BookPlugin.dll')) {
            if (@(Get-ChildItem -LiteralPath $application -Recurse -File -Filter $bookAssembly).Count -ne 0) {
                throw "Books assembly entered a no-Books payload: $bookAssembly"
            }
        }
    }
}

function Merge-PublishedApplication([string]$SourceDirectory, [string]$DestinationDirectory, [string]$ProgramRoot) {
    $source = [IO.Path]::GetFullPath($SourceDirectory)
    $destination = [IO.Path]::GetFullPath($DestinationDirectory)
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "Published application directory is missing: $source"
    }
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $targetRoot = if ($relative.StartsWith('Plugins\', [StringComparison]::OrdinalIgnoreCase)) { $ProgramRoot } else { $destination }
        $target = [IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
        if (-not $target.StartsWith([IO.Path]::GetFullPath($ProgramRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Published file escaped the merged application directory: $relative"
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $existing = Get-Item -LiteralPath $target
            if ($existing.Length -ne $file.Length -or
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne
                (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
                throw "Published applications contain different files at the shared path '$relative'. Give the app-specific file a unique name or keep it inside its plugin ID directory."
            }
            continue
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Get-PaoNsisOutcome([string]$Message, [int]$ExitCode) {
    try { $receipt = $Message | ConvertFrom-Json } catch { return 'unconfirmed' }
    if ($receipt -isnot [pscustomobject] -or $receipt.op -ne 'nsis_build' -or
        $receipt.ok -isnot [bool] -or $receipt.partial -isnot [bool] -or $receipt.partial) { return 'unconfirmed' }
    if ($ExitCode -eq 0 -and $receipt.ok -and $receipt.state -eq 'done' -and $null -eq $receipt.err -and
        ($receipt.exitCode -is [int] -or $receipt.exitCode -is [long]) -and $receipt.exitCode -eq 0) { return 'complete' }
    if ($ExitCode -ne 0 -and -not $receipt.ok -and $receipt.err -eq 'svc_down' -and
        $receipt.state -eq 'failed') { return 'unsubmitted' }
    return 'unconfirmed'
}

function Invoke-WindowsSigning([string]$PackageRoot, [string]$Scope, [string[]]$AdditionalOwnedFile = @()) {
    if (-not $SignWindows) { return }
    foreach ($required in @($SigningModule, $SigningTrustBundleRoot, $PublisherThumbprint)) {
        if ([string]::IsNullOrWhiteSpace($required)) {
            throw 'Signing requires -SigningModule, -SigningTrustBundleRoot and -PublisherThumbprint.'
        }
    }
    $parameters = @{
        PackageRoot = $PackageRoot
        TrustBundleRoot = $SigningTrustBundleRoot
        SigningModule = $SigningModule
        PublisherThumbprint = $PublisherThumbprint
        Provider = $SigningProvider
        Scope = $Scope
    }
    if ($AdditionalOwnedFile.Count -ne 0) { $parameters.AdditionalOwnedFile = $AdditionalOwnedFile }
    & (Join-Path $PSScriptRoot 'sign-windows-deploy.ps1') @parameters
}

# Routine development builds locally. Packaging is an explicit installation action.
if (-not $Publish -and -not $Installer) {
    Invoke-Checked $DotnetExecutable @('build', (Join-Path $projectRoot 'Pegasus.Cerebral.slnx'), '-c', $Configuration, $booksProperty)
    return
}

if ($Installer -and -not $SkipAcceptance) {
    & (Join-Path $PSScriptRoot 'test-release-readiness.ps1') -Configuration $Configuration -Full -DotnetExecutable $DotnetExecutable -IncludeBooks $includeBooksValue
}
New-Item -ItemType Directory -Path $deployRoot -Force | Out-Null

if (-not $SkipPublish) {
    $restoreArguments = @($booksProperty)
    if ($Offline) {
        # Existing packages still resolve from the user's NuGet cache. An empty,
        # local-only feed makes a missing package fail instead of downloading it.
        $offlineFeed = Join-Path $projectRoot '.tmp\nuget-offline'
        [IO.Directory]::CreateDirectory($offlineFeed) | Out-Null
        $restoreArguments += @("-p:RestoreSources=$offlineFeed", '-p:NuGetAudit=false')
    }
    # Keep compiler intermediates outside source bin/obj directories. A running
    # source application may legitimately hold those files while a deployable
    # payload is prepared. The temporary tree is never part of deployment.
    $publishArtifacts = Join-Path ([IO.Path]::GetTempPath()) ('Pegasus-Cerebral-publish-' + [Guid]::NewGuid().ToString('N'))
    # Prepared payload and rollback renames share the fixed output's volume.
    $publicationStage = Join-Path $deployRoot ('.win-x64-stage-' + [Guid]::NewGuid().ToString('N'))
    $publicationRecoveryRequired = $false
    try {
        # Prepare and validate the entire Windows payload before replacing the one
        # fixed output. A compiler failure must leave the previous payload usable.
        $windowsStage = Join-Path $publicationStage 'payload'
        foreach ($app in @('Worker','Integrator','Orchestrator')) {
            $applicationStage = Join-Path $publishArtifacts "applications\$app"
            # The Windows installer requires the pinned .NET SDK/runtime prerequisite.
            # Keep one machine runtime instead of duplicating the framework in every
            # Cerebral application directory; only product-private assemblies remain.
            Invoke-Checked $DotnetExecutable (@('publish', (Join-Path $projectRoot "src\Cerebral.$app\Cerebral.$app.csproj"), '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false', '--artifacts-path', $publishArtifacts, '-o', $applicationStage) + $restoreArguments)
            if ($app -eq 'Worker') {
                # The Browser plugin is a build-order-only project reference, but
                # Playwright's transitive MSBuild target also places its driver in
                # the referring publish root. Keep that runtime solely inside the
                # isolated pegasus.browser plugin copied by Directory.Build.targets.
                foreach ($pluginOwned in @('.playwright','playwright.ps1')) {
                    $leak = Join-Path $applicationStage $pluginOwned
                    if (Test-Path -LiteralPath $leak) { Remove-Item -LiteralPath $leak -Recurse -Force }
                }
            }
            $applicationDestination = if ($app -eq 'Worker') { Join-Path $windowsStage 'Applications\Worker' } else { Join-Path $windowsStage 'Applications\Desktop' }
            Merge-PublishedApplication $applicationStage $applicationDestination $windowsStage
        }
        # Keep the reusable brand package beside the installed applications as one
        # shared resource set. The desktop assembly embeds the active splash for
        # reliable startup, while plugins, installers and future theme tooling can
        # consume the same named derivatives without copying assets per process.
        $brandingSource = Join-Path $projectRoot 'assets\branding\cerebral'
        $brandingDestination = Join-Path $windowsStage 'Resources\Branding\Cerebral'
        if (-not (Test-Path -LiteralPath $brandingSource -PathType Container)) {
            throw "The Cerebral branding resource package is missing: $brandingSource"
        }
        if (Test-Path -LiteralPath $brandingDestination) { Remove-Item -LiteralPath $brandingDestination -Recurse -Force }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($brandingDestination)) -Force | Out-Null
        Copy-Item -LiteralPath $brandingSource -Destination $brandingDestination -Recurse
        $expectedWindowsServicePlugins = @('pegasus.browser','pegasus.programming')
        $expectedWindowsDesktopPlugins = @('pegasus.ai-dictation','pegasus.dictation','pegasus.programming','pegasus.prompt-review','pegasus.resource-pause')
        if ($includeBooksEnabled) {
            $expectedWindowsServicePlugins += 'pegasus.books'
            $expectedWindowsDesktopPlugins += 'pegasus.books'
        }
        Assert-IsolatedPluginLayout $windowsStage 'Service' 'service-plugin.json' $expectedWindowsServicePlugins
        Assert-IsolatedPluginLayout $windowsStage 'Desktop' 'desktop-plugin.json' $expectedWindowsDesktopPlugins
        foreach ($pluginOwned in @('.playwright','playwright.ps1')) {
            if (Test-Path -LiteralPath (Join-Path $windowsStage "Applications\Worker\$pluginOwned")) {
                throw "Browser plugin payload escaped its isolated directory: $pluginOwned"
            }
        }
        Copy-LlamaRuntime $LlamaRuntimeDirectory (Join-Path $windowsStage 'Applications\Worker\runtime')
        $windowsOutput = Join-Path $deployRoot 'win-x64'
        $previousWindows = Join-Path $publicationStage 'previous'
        $publicationRecoveryRequired = $true
        if (Test-Path -LiteralPath $windowsOutput) {
            Move-Item -LiteralPath $windowsOutput -Destination $previousWindows
        }
        try {
            Move-Item -LiteralPath $windowsStage -Destination $windowsOutput
            $publicationRecoveryRequired = $false
        } catch {
            $publicationError = $_
            if ((Test-Path -LiteralPath $previousWindows) -and -not (Test-Path -LiteralPath $windowsOutput)) {
                Move-Item -LiteralPath $previousWindows -Destination $windowsOutput
                $publicationRecoveryRequired = $false
            }
            throw $publicationError
        }
        # Invalidate the old installer only after a new fixed Windows payload exists.
        $staleInstaller = Join-Path $deployRoot 'Cerebral-Setup.exe'
        if (Test-Path -LiteralPath $staleInstaller) { Remove-Item -LiteralPath $staleInstaller -Force }
        if (-not $DesktopOnly) {
            foreach ($relative in @('Workers','linux-arm64')) {
                $replaceable = [IO.Path]::GetFullPath((Join-Path $deployRoot $relative))
                if (-not $replaceable.StartsWith([IO.Path]::GetFullPath($deployRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'A generated Worker output escaped the deployment directory.'
                }
                if (Test-Path -LiteralPath $replaceable) { Remove-Item -LiteralPath $replaceable -Recurse -Force }
            }

            # The desktop is deliberately Windows x64-only. Standalone Workers are
            # self-contained native payloads so remote Windows machines do not need
            # the full Cerebral desktop or a preinstalled .NET runtime.
            $standaloneWorkers = Join-Path $deployRoot 'Workers'
            New-Item -ItemType Directory -Path $standaloneWorkers -Force | Out-Null
            foreach ($rid in @('win-x64','win-arm64','win-x86')) {
                $workerStage = Join-Path $publishArtifacts "workers\$rid"
                Invoke-Checked $DotnetExecutable (@('publish', (Join-Path $projectRoot 'src\Cerebral.Worker\Cerebral.Worker.csproj'), '-c', $Configuration, '-r', $rid, '--self-contained', 'true', '--artifacts-path', $publishArtifacts, '-o', $workerStage) + $restoreArguments)
                foreach ($pluginOwned in @('.playwright','playwright.ps1')) {
                    $leak = Join-Path $workerStage $pluginOwned
                    if (Test-Path -LiteralPath $leak) { Remove-Item -LiteralPath $leak -Recurse -Force }
                }
                $expectedServicePlugins = if ($rid -eq 'win-x64') { @('pegasus.browser','pegasus.programming') } else { @('pegasus.programming') }
                if ($includeBooksEnabled) { $expectedServicePlugins += 'pegasus.books' }
                Assert-IsolatedPluginLayout $workerStage 'Service' 'service-plugin.json' $expectedServicePlugins
                if ($rid -ne 'win-x64' -and (Test-Path -LiteralPath (Join-Path $workerStage 'Plugins\Service\pegasus.browser'))) {
                    throw "The x64-only Browser runtime was published into the $rid Worker package."
                }
                if ($rid -eq 'win-x64') { Copy-LlamaRuntime $LlamaRuntimeDirectory (Join-Path $workerStage 'runtime') }
                Move-Item -LiteralPath $workerStage -Destination (Join-Path $standaloneWorkers $rid)
            }
            if (-not $WindowsOnly) {
                $linuxArm64Root = Join-Path $standaloneWorkers 'linux-arm64'
                Invoke-Checked $DotnetExecutable (@('publish', (Join-Path $projectRoot 'src\Cerebral.Worker\Cerebral.Worker.csproj'), '-c', $Configuration, '-r', 'linux-arm64', '--self-contained', 'true', '--artifacts-path', $publishArtifacts, '-o', (Join-Path $linuxArm64Root 'Worker')) + $restoreArguments)
                $expectedLinuxServicePlugins = @('pegasus.programming')
                if ($includeBooksEnabled) { $expectedLinuxServicePlugins += 'pegasus.books' }
                Assert-IsolatedPluginLayout (Join-Path $linuxArm64Root 'Worker') 'Service' 'service-plugin.json' $expectedLinuxServicePlugins
                Copy-LinuxArm64LlamaRuntime $LinuxArm64LlamaRuntimeDirectory (Join-Path $linuxArm64Root 'Worker\runtime')
                Copy-Item -LiteralPath (Join-Path $projectRoot 'setup\install-linux.sh') -Destination (Join-Path $linuxArm64Root 'install-linux.sh') -Force
            }
        }
    } finally {
        if ($publicationRecoveryRequired) {
            Write-Warning "Windows payload publication was not reconciled. Preserve recovery files at $publicationStage and $publishArtifacts."
        } else {
            foreach ($cleanup in @(
                @{ Path=$publicationStage; Parent=$deployRoot; Prefix='.win-x64-stage-' },
                @{ Path=$publishArtifacts; Parent=[IO.Path]::GetTempPath(); Prefix='Pegasus-Cerebral-publish-' }
            )) {
                $absolute = [IO.Path]::GetFullPath($cleanup.Path)
                $parent = [IO.Path]::GetFullPath($cleanup.Parent).TrimEnd('\','/')
                if (-not [IO.Path]::GetDirectoryName($absolute).Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [IO.Path]::GetFileName($absolute).StartsWith($cleanup.Prefix, [StringComparison]::Ordinal)) {
                    throw 'Generated publish cleanup escaped its declared parent.'
                }
                if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
            }
        }
    }
}
Invoke-WindowsSigning (Join-Path $deployRoot 'win-x64') $SigningScope
foreach ($rid in $(if ($DesktopOnly) { @() } else { @('win-x64','win-arm64','win-x86') })) {
    $standaloneWorkerRoot = Join-Path $deployRoot "Workers\$rid"
    if ($SigningScope -eq 'Release') {
        Invoke-WindowsSigning $standaloneWorkerRoot 'Release'
    } else {
        Invoke-WindowsSigning $standaloneWorkerRoot 'Explicit' @(
            'Cerebral.Worker.exe'
            'Cerebral.Worker.dll'
            'Cerebral.Tools.dll'
            'Cerebral.Client.dll'
            'Cerebral.Core.dll'
        )
    }
}
if (-not $Installer) { return }
foreach ($app in @('Worker','Integrator','Orchestrator')) {
    $group = if ($app -eq 'Worker') { 'Worker' } else { 'Desktop' }
    if (-not (Test-Path -LiteralPath (Join-Path $deployRoot "win-x64\Applications\$group\Cerebral.$app.exe"))) { throw "Missing $app publish output. Run build.ps1 without -SkipPublish." }
}

$cortexCompanion = Join-Path $deployRoot 'Pegasus-Cortex-Setup'
if ($CortexInstallerPath) {
    if (-not [IO.Path]::IsPathFullyQualified($CortexInstallerPath) -or -not (Test-Path -LiteralPath $CortexInstallerPath -PathType Leaf) -or
        [IO.Path]::GetFileName($CortexInstallerPath) -cne 'PegasusCortexInstaller.exe') {
        throw 'Supply -CortexInstallerPath with the absolute PegasusCortexInstaller.exe from its canonical release directory.'
    }
    $cortexRelease = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($CortexInstallerPath))
    $cortexReleaseFiles = @(
        'installer-contract.v7.json',
        'package-manifest.json',
        'package-manifest.p7s',
        'PegasusCortexInstaller.exe',
        'product',
        'trust\pegasus-prod-alpha-publisher.cer',
        'trust\pegasus-prod-alpha-root.cer',
        'trust\trust-bundle.json'
    )
    foreach ($relative in $cortexReleaseFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $cortexRelease $relative))) {
            throw "The Cortex release directory is incomplete; missing $relative. Copy the canonical release directory without repacking it."
        }
    }
    $minimumCortexRelease = '1.2026.09.27.23.39.A'
    try {
        $cortexContract = Get-Content -LiteralPath (Join-Path $cortexRelease 'installer-contract.v7.json') -Raw | ConvertFrom-Json
        $cortexManifest = Get-Content -LiteralPath (Join-Path $cortexRelease 'package-manifest.json') -Raw | ConvertFrom-Json
    } catch { throw "The Cortex installer contract or package manifest is invalid JSON: $($_.Exception.Message)" }
    if ($cortexContract.contract -cne 'pegasus-cortex-nsis-installer' -or $cortexContract.version -ne 7 -or
        $cortexContract.payloadLayout -cne 'adjacent-uncompressed-product-directory' -or
        $cortexManifest.schema -cne 'pegasus.cortex.local-operational-package/1.2' -or
        $cortexManifest.role -cne 'product-installer' -or $cortexManifest.channel -cne 'Prod Alpha' -or
        $cortexManifest.releaseVersion -cne $cortexContract.releaseVersion -or
        (Compare-CortexReleaseVersion ([string]$cortexContract.releaseVersion) $minimumCortexRelease) -lt 0) {
        throw "The Cortex companion is not a compatible Prod Alpha installer release $minimumCortexRelease or later."
    }
    $installerRecord = @($cortexManifest.files).Where({ $_.path -ceq 'PegasusCortexInstaller.exe' }, 'First')
    if (-not $installerRecord -or [string]::IsNullOrWhiteSpace($installerRecord.sha256)) {
        throw 'The Cortex package manifest does not identify its installer payload.'
    }
    $installerHash = (Get-FileHash -LiteralPath $CortexInstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($installerHash -cne ([string]$installerRecord.sha256).ToLowerInvariant()) {
        throw 'The Cortex installer does not match the package manifest. Use the untouched canonical release directory.'
    }
    if (Test-Path -LiteralPath $cortexCompanion) { Remove-Item -LiteralPath $cortexCompanion -Recurse -Force }
    Copy-Item -LiteralPath $cortexRelease -Destination $cortexCompanion -Recurse
    $legacyCortexCompanion = Join-Path $deployRoot 'Pegasus-Cortex-Setup.exe'
    if (Test-Path -LiteralPath $legacyCortexCompanion) { Remove-Item -LiteralPath $legacyCortexCompanion -Force }
    Write-Host "Cortex prerequisite companion directory: $cortexCompanion"
} elseif (Test-Path -LiteralPath (Join-Path $cortexCompanion 'PegasusCortexInstaller.exe') -PathType Leaf) {
    Write-Host "Keeping current Cortex prerequisite companion directory: $cortexCompanion"
}

# Model packages remain external to NSIS because the installer has a 2 GB payload
# limit. Setup imports every current package from this independently replaceable folder.
$releaseModels = Join-Path $deployRoot 'Models'
$starterPackage = Join-Path $releaseModels 'Pegasus-Cerebral-Starter.CerebralLLM'
$legacyStarterPackage = Join-Path $deployRoot 'Pegasus-Cerebral-Starter.cerebral-model.zip'
$oldStarterPackage = Join-Path $deployRoot 'Pegasus-Cerebral-Starter.CerebralLLM'
if ($ExcludeStarterModel) {
    foreach ($package in @($starterPackage, $oldStarterPackage, $legacyStarterPackage)) {
        if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
    }
    if ((Test-Path -LiteralPath $releaseModels -PathType Container) -and
        -not (Get-ChildItem -LiteralPath $releaseModels -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $releaseModels -Force
    }
    Write-Host 'No starter model package will accompany this installer.'
} elseif ($StarterModelPath) {
    if (-not [IO.Path]::IsPathFullyQualified($StarterModelPath) -or -not (Test-Path -LiteralPath $StarterModelPath -PathType Leaf)) {
        throw 'Supply -StarterModelPath with an existing absolute GGUF path.'
    }
    if (-not [IO.Path]::IsPathFullyQualified($StarterModelManifest)) { $StarterModelManifest = Join-Path $projectRoot $StarterModelManifest }
    $StarterModelManifest = [IO.Path]::GetFullPath($StarterModelManifest)
    New-Item -ItemType Directory -Path $releaseModels -Force | Out-Null
    foreach ($package in @($starterPackage, $oldStarterPackage, $legacyStarterPackage)) {
        if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
    }
    $packageArguments = @{ ModelPath = $StarterModelPath; ManifestPath = $StarterModelManifest; OutputPath = $starterPackage }
    if ($StarterVisionProjectorPath) { $packageArguments.VisionProjectorPath = $StarterVisionProjectorPath }
    & (Join-Path $PSScriptRoot 'package-model.ps1') @packageArguments
    if (-not (Test-Path -LiteralPath $starterPackage -PathType Leaf)) { throw 'Starter model packaging did not produce the expected output.' }
} elseif (Test-Path -LiteralPath $starterPackage -PathType Leaf) {
    Write-Host "Keeping current starter model package in the release Models folder: $starterPackage"
} else {
    Write-Warning 'No starter model package was built. Supply -StarterModelPath to create Models\Pegasus-Cerebral-Starter.CerebralLLM.'
}

# Export the current signed desktop generations as independently installable packages.
# Plugin-owned runtimes and models are bound from relative build inputs and travel inside
# their .CerebralPlugin package, while remaining outside the NSIS executable itself.
& (Join-Path $PSScriptRoot 'package-desktop-plugins.ps1') `
    -SourceRoot (Join-Path $deployRoot 'win-x64\Plugins\Desktop') `
    -OutputRoot (Join-Path $deployRoot 'Plugins') `
    -ProjectRoot $projectRoot `
    -ResourceBindingsPath (Join-Path $projectRoot 'setup\plugin-package-resources.json')
& (Join-Path $PSScriptRoot 'package-installer-features.ps1') `
    -SourceRoot (Join-Path $deployRoot 'win-x64') `
    -OutputRoot (Join-Path $deployRoot 'Features')

# The small installer bootstrap references these adjacent helpers at install time.
# Keep deploy runnable for local acceptance; the installation-kit builder reproduces
# this Support layout atomically beside the released installer.
$supportRoot = Join-Path $deployRoot 'Support'
New-Item -ItemType Directory -Path $supportRoot -Force | Out-Null
foreach ($helper in @('Install-CerebralCore.ps1','Install-CerebralWorker.ps1','Install-CerebralFeature.ps1','Install-CerebralPluginResources.ps1','Register-CerebralOwnerPipe.ps1','Cache-CerebralInstallationSource.ps1','Get-CerebralInstallerState.ps1')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "setup\$helper") -Destination (Join-Path $supportRoot $helper) -Force
}

# Prefer the compiler's ordinary CLI boundary. Packaging presentation and orchestration
# belong to this script; NSIS only needs to compile one declared source file. This also
# prevents a PAO-owned parent build from recursively submitting another PAO mutation.
if (-not $NsisCompiler) {
    $nsisCandidates = [Collections.Generic.List[string]]::new()
    $nsisCommand = Get-Command makensis.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($nsisCommand) { $nsisCandidates.Add($nsisCommand.Source) }
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $programFiles64 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    foreach ($candidate in @(
        (Join-Path $programFilesX86 'NSIS\makensis.exe'),
        (Join-Path $programFiles64 'NSIS\makensis.exe'),
        (Join-Path $projectRoot '.runtime\tools\nsis\makensis.exe')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) { $nsisCandidates.Add($candidate) }
    }
    $resolvedNsisCompiler = $nsisCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    $NsisCompiler = if ($resolvedNsisCompiler) { [string]$resolvedNsisCompiler } else { $null }
}

# When NSIS has not been installed as an ordinary tool, locate the independently
# installed Cortex and use its fixed typed compiler operation as a compatibility path.
$publishedWorker = Join-Path $deployRoot 'win-x64\Applications\Worker\Cerebral.Worker.exe'
$pao = $null
if (-not $NsisCompiler) {
try {
    $probe = [Diagnostics.ProcessStartInfo]::new($publishedWorker)
    $probe.UseShellExecute = $false
    $probe.CreateNoWindow = $true
    $probe.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $probe.RedirectStandardOutput = $true
    $probe.RedirectStandardError = $true
    $probe.ArgumentList.Add('manage')
    $probe.ArgumentList.Add('cortex-locate')
    $process = [Diagnostics.Process]::Start($probe)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
            throw 'Installed Cortex discovery timed out.'
        }
        if ($process.ExitCode -ne 0) { throw "Installed Cortex discovery failed: $($errorOutput.GetAwaiter().GetResult())" }
        $location = $output.GetAwaiter().GetResult() | ConvertFrom-Json
        $pao = $location.paoPath
        if (-not $pao -or -not [IO.Path]::IsPathFullyQualified($pao) -or -not (Test-Path -LiteralPath $pao -PathType Leaf)) {
            throw 'Installed Cortex discovery did not return an existing absolute PAO path.'
        }
    } finally { $process.Dispose() }
} catch {
    $pao = $null
    if (-not $NsisCompiler) { throw }
    Write-Warning "Installed Cortex discovery is unavailable; using the explicitly supplied NSIS compiler. $($_.Exception.Message)"
}
}
if ($NsisCompiler) {
    if (-not [IO.Path]::IsPathFullyQualified($NsisCompiler) -or -not (Test-Path -LiteralPath $NsisCompiler -PathType Leaf)) {
        throw 'Supply -NsisCompiler with an existing absolute makensis.exe path.'
    }
    $NsisCompiler = [IO.Path]::GetFullPath($NsisCompiler)
}

function Invoke-NsisCompilation([string]$SourcePath, [string]$OutputPath) {
    $source = [IO.Path]::GetFullPath($SourcePath)
    $output = [IO.Path]::GetFullPath($OutputPath)
    if (-not $output.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output escaped the workspace.' }
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    $compiled = $false
    if ($pao) {
        $start = [Diagnostics.ProcessStartInfo]::new($pao)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('--json','nsis','build',$source,$output,"cwd=$projectRoot",'tmo=600')) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(620000)) {
                $process.Kill($true); $process.WaitForExit(5000) | Out-Null
                throw 'Pegasus NSIS execution timed out.'
            }
            $exit = $process.ExitCode
            $message = @($stdout.GetAwaiter().GetResult(), $stderr.GetAwaiter().GetResult()).Where({ -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
        } finally { $process.Dispose() }
        $outcome = Get-PaoNsisOutcome $message $exit
        if ($outcome -eq 'complete' -and (Test-Path -LiteralPath $output -PathType Leaf)) { $compiled = $true }
        else {
            if ($outcome -ne 'unsubmitted') { throw "Pegasus NSIS build failed or its execution outcome is unconfirmed: $message" }
            if (-not $NsisCompiler) { throw "Pegasus Cortex NSIS is unavailable. Retry after Cortex is ready or supply -NsisCompiler with an installed makensis.exe path. $message" }
            if (Test-Path -LiteralPath $output) { throw "Pegasus NSIS left output after a failed request; inspect it before retrying. $message" }
            Write-Warning 'Pegasus Cortex NSIS is unavailable. Using the explicitly supplied compiler; no Cortex configuration changed.'
        }
    }
    if (-not $compiled) {
        if (-not $NsisCompiler) { throw 'Pegasus Cortex NSIS is unavailable. Supply -NsisCompiler with an installed makensis.exe path.' }
        Invoke-Checked $NsisCompiler @('/V2', "/DPAO_OUT=$output", $source)
    }
    if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "NSIS did not produce the expected installer: $output" }
}

$installerPath = Join-Path $deployRoot 'Cerebral-Setup.exe'
$workerInstallerPath = Join-Path $deployRoot 'Cerebral-Worker-Setup.exe'
Invoke-NsisCompilation (Join-Path $projectRoot 'setup\Cerebral.nsi') $installerPath
Invoke-NsisCompilation (Join-Path $projectRoot 'setup\Worker.nsi') $workerInstallerPath
Invoke-WindowsSigning $deployRoot 'Explicit' @('Cerebral-Setup.exe','Cerebral-Worker-Setup.exe')
Write-Host "Current installer: $installerPath"
Write-Host "Current standalone Worker installer: $workerInstallerPath"
