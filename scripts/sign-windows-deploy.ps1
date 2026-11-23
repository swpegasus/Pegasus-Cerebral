[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$TrustBundleRoot,
    [Parameter(Mandatory)][string]$SigningModule,
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider',
    [ValidateSet('Pipe', 'Release', 'Explicit')][string]$Scope = 'Pipe',
    [string[]]$AdditionalOwnedFile = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
$trustRoot = [IO.Path]::GetFullPath($TrustBundleRoot).TrimEnd('\', '/')
$module = [IO.Path]::GetFullPath($SigningModule)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "PackageRoot does not exist: $root" }
if (-not (Test-Path -LiteralPath $module -PathType Leaf)) { throw "SigningModule does not exist: $module" }

Import-Module $module -Force
Assert-PegasusNoPrivateKeyMaterial -Root $root
$trust = Read-PegasusTrustBundle -PackageRoot $trustRoot
$publisher = Get-PegasusStoreCertificate `
    -Thumbprint $PublisherThumbprint `
    -StoreLocation CurrentUser `
    -Provider $Provider `
    -RequirePrivateKey
$expectedThumbprint = ConvertTo-PegasusThumbprint $publisher.Thumbprint
if ($expectedThumbprint -cne (ConvertTo-PegasusThumbprint $trust.Publisher.Thumbprint)) {
    throw 'The selected publisher key does not match the public trust bundle.'
}

$files = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File -ErrorAction Stop |
    Where-Object { $_.Extension -in @('.exe', '.dll') } |
    Sort-Object FullName)
if ($files.Count -eq 0) { throw 'The deployment contains no executable files.' }

$additionalOwnedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $AdditionalOwnedFile) {
    $parts = @($relative -split '[\\/]')
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathFullyQualified($relative) -or
        @($parts | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw "AdditionalOwnedFile must be a confined relative file path: '$relative'"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "AdditionalOwnedFile is missing or escaped PackageRoot: '$relative'"
    }
    $null = $additionalOwnedPaths.Add($path)
}

$requiredPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$requiredRelatives = if ($Scope -eq 'Pipe') {
    @(
        'Applications\Worker\Cerebral.Worker.exe'
        'Applications\Worker\Cerebral.Worker.dll'
        'Applications\Worker\Cerebral.Tools.dll'
        'Applications\Worker\Cerebral.Client.dll'
        'Applications\Worker\Cerebral.Core.dll'
    )
} else {
    @()
}
foreach ($relative in $requiredRelatives) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required secure-pipe file is missing: $relative" }
    $null = $requiredPaths.Add($path)
}
foreach ($path in $additionalOwnedPaths) { $null = $requiredPaths.Add($path) }

$signed = 0
$preservedOwned = 0
$preservedVendor = 0
$unsignedThirdParty = 0
foreach ($file in $files) {
    $owned = $requiredPaths.Contains($file.FullName) -or ($Scope -eq 'Release' -and
        ($file.Extension.Equals('.exe', [StringComparison]::OrdinalIgnoreCase) -or
         $file.Name.StartsWith('Cerebral.', [StringComparison]::OrdinalIgnoreCase)))
    $current = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if (-not $owned) {
        if ($current.Status -eq [Management.Automation.SignatureStatus]::Valid) { $preservedVendor++ }
        else { $unsignedThirdParty++ }
        continue
    }
    if ($current.Status -eq [Management.Automation.SignatureStatus]::Valid -and
        $null -ne $current.SignerCertificate -and
        (ConvertTo-PegasusThumbprint $current.SignerCertificate.Thumbprint) -ceq $expectedThumbprint) {
        $preservedOwned++
        continue
    }

    $result = $null
    for ($attempt = 1; $attempt -le 50; $attempt++) {
        try {
            $result = Set-AuthenticodeSignature `
                -LiteralPath $file.FullName `
                -Certificate $publisher `
                -HashAlgorithm SHA256 `
                -ErrorAction Stop
            break
        }
        catch [IO.IOException] {
            if ($attempt -eq 50) { throw }
            [Threading.Thread]::Sleep(100)
        }
    }
    if ($null -eq $result.SignerCertificate -or
        (ConvertTo-PegasusThumbprint $result.SignerCertificate.Thumbprint) -cne $expectedThumbprint -or
        $result.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signing failed for '$($file.FullName)': $($result.Status) $($result.StatusMessage)"
    }
    $signed++
}

$invalidOwned = @()
foreach ($file in $files) {
    $owned = $requiredPaths.Contains($file.FullName) -or ($Scope -eq 'Release' -and
        ($file.Extension.Equals('.exe', [StringComparison]::OrdinalIgnoreCase) -or
         $file.Name.StartsWith('Cerebral.', [StringComparison]::OrdinalIgnoreCase)))
    if (-not $owned) { continue }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        (ConvertTo-PegasusThumbprint $signature.SignerCertificate.Thumbprint) -cne $expectedThumbprint) {
        $invalidOwned += [pscustomobject]@{ path = $file.FullName; status = [string]$signature.Status }
    }
}
if ($invalidOwned.Count -ne 0) {
    throw "The deployment still contains $($invalidOwned.Count) Cerebral-owned executable file(s) without the selected publisher signature."
}

$ownedApplications = if ($Scope -eq 'Release') {
    @(
        (Join-Path $root 'Applications\Worker\Cerebral.Worker.exe')
        (Join-Path $root 'Applications\Desktop\Cerebral.Integrator.exe')
        (Join-Path $root 'Applications\Desktop\Cerebral.Orchestrator.exe')
    )
} elseif ($Scope -eq 'Pipe') {
    @((Join-Path $root 'Applications\Worker\Cerebral.Worker.exe'))
} else {
    @()
}
foreach ($application in @($ownedApplications) + @($additionalOwnedPaths)) {
    if (-not (Test-Path -LiteralPath $application -PathType Leaf)) { throw "Owned application is missing: $application" }
    $signature = Get-AuthenticodeSignature -LiteralPath $application
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        (ConvertTo-PegasusThumbprint $signature.SignerCertificate.Thumbprint) -cne $expectedThumbprint) {
        throw "Owned application does not use the selected Pegasus publisher: $application"
    }
}

[pscustomobject]@{
    PackageRoot = $root
    Scope = $Scope
    PublisherThumbprint = $expectedThumbprint
    ExecutableFiles = $files.Count
    SignedNow = $signed
    PreservedOwnedSignatures = $preservedOwned
    PreservedVendorSignatures = $preservedVendor
    UnsignedThirdPartyLibraries = $unsignedThirdParty
    RequiredApplicationsVerified = @($ownedApplications).Count + $additionalOwnedPaths.Count
    RequiredSecurePipeFiles = $requiredPaths.Count
} | ConvertTo-Json -Compress
