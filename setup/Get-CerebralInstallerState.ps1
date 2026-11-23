[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KitPath,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

function Read-Ini([string]$Path) {
    $result = [ordered]@{}
    $section = ''
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $result }
    foreach ($raw in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        $line = $raw.Trim()
        if ($line.Length -eq 0 -or $line[0] -in ';', '#') { continue }
        if ($line.StartsWith('[') -and $line.EndsWith(']')) {
            $section = $line.Substring(1, $line.Length - 2).Trim()
            if (-not $result.Contains($section)) { $result[$section] = [ordered]@{} }
            continue
        }
        $separator = $line.IndexOf('=')
        if ($section.Length -eq 0 -or $separator -le 0) { continue }
        $result[$section][$line.Substring(0, $separator).Trim()] = $line.Substring($separator + 1).Trim()
    }
    return $result
}

function Read-JsonVersion([string[]]$Paths) {
    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        try {
            $value = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace([string]$value.version)) { return [string]$value.version }
        } catch { }
    }
    return $null
}

function Find-ExecutableVersion([string[]]$Names, [string[]]$FixedPaths = @()) {
    foreach ($path in $FixedPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
            if ([string]::IsNullOrWhiteSpace($version)) { $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion }
            if (-not [string]::IsNullOrWhiteSpace($version)) { return ($version -split '[ +]')[0] }
        }
    }
    foreach ($name in $Names) {
        try {
            $command = Get-Command $name -CommandType Application -ErrorAction Stop | Select-Object -First 1
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($command.Source).ProductVersion
            if ([string]::IsNullOrWhiteSpace($version)) { $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($command.Source).FileVersion }
            if (-not [string]::IsNullOrWhiteSpace($version)) { return ($version -split '[ +]')[0] }
        } catch { }
    }
    return $null
}

function Read-RegistryValue([string]$Path, [string]$Name) {
    $hive = if ($Path.StartsWith('HKLM:\', [StringComparison]::OrdinalIgnoreCase)) {
        [Microsoft.Win32.RegistryHive]::LocalMachine
    } elseif ($Path.StartsWith('HKCU:\', [StringComparison]::OrdinalIgnoreCase)) {
        [Microsoft.Win32.RegistryHive]::CurrentUser
    } else { return $null }
    $subKey = $Path.Substring(6).Replace('/', '\')
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = $null
        $key = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            $key = $base.OpenSubKey($subKey, $false)
            if ($key) {
                $value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if (-not [string]::IsNullOrWhiteSpace([string]$value)) { return [string]$value }
            }
        } catch { }
        finally {
            if ($key) { $key.Dispose() }
            if ($base) { $base.Dispose() }
        }
    }
    return $null
}

function Find-UninstallVersion([string[]]$Patterns) {
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $base = $null
            $uninstall = $null
            try {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
                $uninstall = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', $false)
                if (-not $uninstall) { continue }
                foreach ($subKeyName in $uninstall.GetSubKeyNames()) {
                    $item = $null
                    try {
                        $item = $uninstall.OpenSubKey($subKeyName, $false)
                        $displayName = [string]$item.GetValue('DisplayName')
                        $displayVersion = [string]$item.GetValue('DisplayVersion')
                        foreach ($pattern in $Patterns) {
                            if ($displayName -like $pattern -and -not [string]::IsNullOrWhiteSpace($displayVersion)) {
                                return $displayVersion
                            }
                        }
                    } catch { }
                    finally { if ($item) { $item.Dispose() } }
                }
            } catch { }
            finally {
                if ($uninstall) { $uninstall.Dispose() }
                if ($base) { $base.Dispose() }
            }
        }
    }
    return $null
}

function Compare-ProductVersion([string]$Installed, [string]$Packaged) {
    if ($Packaged -match '(?i)(^|\.)x($|\.)') { return 0 }
    $left = @([regex]::Matches($Installed, '\d+') | ForEach-Object { [int64]$_.Value })
    $right = @([regex]::Matches($Packaged, '\d+') | ForEach-Object { [int64]$_.Value })
    if ($left.Count -eq 0 -or $right.Count -eq 0) { return [string]::Compare($Installed, $Packaged, $true) }
    $length = [Math]::Max($left.Count, $right.Count)
    for ($index = 0; $index -lt $length; $index++) {
        $a = if ($index -lt $left.Count) { $left[$index] } else { 0 }
        $b = if ($index -lt $right.Count) { $right[$index] } else { 0 }
        if ($a -lt $b) { return -1 }
        if ($a -gt $b) { return 1 }
    }
    return 0
}

$kit = Read-Ini $KitPath
$installedMarker = Read-Ini (Join-Path $InstallDirectory 'installation.ini')

function Marker-Version([string]$Id) {
    if ($installedMarker.Contains('ComponentVersions') -and $installedMarker['ComponentVersions'].Contains($Id)) {
        return [string]$installedMarker['ComponentVersions'][$Id]
    }
    return $null
}

function Marker-Present([string]$Id) {
    if (-not $installedMarker.Contains('Components')) { return $false }
    foreach ($prefix in @('Plugin.', 'Prerequisite.', 'BehaviorPack.')) {
        $key = $prefix + $Id
        if ($installedMarker['Components'].Contains($key) -and $installedMarker['Components'][$key] -eq '1') { return $true }
    }
    return $false
}

function Installed-Component([string]$Id) {
    $marked = Marker-Version $Id
    switch ($Id) {
        'cerebral-core' {
            $value = Read-RegistryValue 'HKLM:\Software\Pegasus\Cerebral' 'Version'
            if (-not $value -and $installedMarker.Contains('Cerebral')) { $value = [string]$installedMarker['Cerebral']['Version'] }
            return [pscustomobject]@{ Present = -not [string]::IsNullOrWhiteSpace($value); Version = $value }
        }
        'cortex-core' {
            $value = Read-RegistryValue 'HKLM:\Software\Pegasus\Cortex' 'ReleaseVersion'
            return [pscustomobject]@{ Present = -not [string]::IsNullOrWhiteSpace($value); Version = $value }
        }
        'cortex-trust' {
            $bundlePath = Join-Path (Split-Path -Parent $KitPath) 'Pegasus-Cortex-Setup\trust\trust-bundle.json'
            $release = Read-RegistryValue 'HKLM:\Software\Pegasus\Cortex' 'ReleaseVersion'
            try {
                $bundle = Get-Content -LiteralPath $bundlePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                $rootPresent = Test-Path -LiteralPath ("Cert:\LocalMachine\Root\" + [string]$bundle.rootThumbprint)
                $publisherPresent = Test-Path -LiteralPath ("Cert:\LocalMachine\TrustedPublisher\" + [string]$bundle.publisherThumbprint)
                $present = $rootPresent -and $publisherPresent
                return [pscustomobject]@{ Present = $present; Version = if ($present) { $release } else { $null } }
            } catch {
                return [pscustomobject]@{ Present = $false; Version = $null }
            }
        }
        'vc-redist' {
            $value = Read-RegistryValue 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' 'Version'
            return [pscustomobject]@{ Present = -not [string]::IsNullOrWhiteSpace($value); Version = $value }
        }
        'dotnet-sdk' {
            $sdkRoot = Join-Path $env:ProgramFiles 'dotnet\sdk'
            $value = if (Test-Path -LiteralPath $sdkRoot) { Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue | Sort-Object { [version](($_.Name -split '-')[0]) } -Descending | Select-Object -First 1 -ExpandProperty Name } else { $null }
            return [pscustomobject]@{ Present = -not [string]::IsNullOrWhiteSpace($value); Version = $value }
        }
        'powershell' { $value = Find-ExecutableVersion @('pwsh.exe') @((Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe')); return [pscustomobject]@{ Present = [bool]$value; Version = $value } }
        'git' { $value = Find-ExecutableVersion @('git.exe') @((Join-Path $env:ProgramFiles 'Git\cmd\git.exe')); return [pscustomobject]@{ Present = [bool]$value; Version = $value } }
        'node' { $value = Find-ExecutableVersion @('node.exe') @((Join-Path $env:ProgramFiles 'nodejs\node.exe')); return [pscustomobject]@{ Present = [bool]$value; Version = $value } }
        'python' { $value = Find-ExecutableVersion @('python.exe','python3.exe') @(); return [pscustomobject]@{ Present = [bool]$value; Version = $value } }
        'sevenzip' { $value = Find-ExecutableVersion @('7z.exe') @((Join-Path $env:ProgramFiles '7-Zip\7z.exe')); return [pscustomobject]@{ Present = [bool]$value; Version = $value } }
        'nsis' { $value = Find-UninstallVersion @('Nullsoft Install System*','NSIS*'); return [pscustomobject]@{ Present = [bool]$value; Version = $value } }
        'vs-buildtools' { $value = Find-UninstallVersion @('Visual Studio Build Tools*','Microsoft Visual Studio*Build Tools*'); return [pscustomobject]@{ Present = [bool]$value; Version = $value } }
        'wsl' { $value = Find-ExecutableVersion @('wsl.exe') @((Join-Path $env:SystemRoot 'System32\wsl.exe')); return [pscustomobject]@{ Present = [bool]$value; Version = $value } }
    }

    if ($Id.StartsWith('pegasus.', [StringComparison]::Ordinal)) {
        $value = Read-JsonVersion @(
            (Join-Path $InstallDirectory "Plugins\Desktop\$Id\desktop-plugin.json"),
            (Join-Path $InstallDirectory "Plugins\Service\$Id\service-plugin.json"))
        if (-not $value) { $value = $marked }
        return [pscustomobject]@{ Present = [bool]$value -or (Marker-Present $Id); Version = $value }
    }

    if ($marked) { return [pscustomobject]@{ Present = $true; Version = $marked } }
    return [pscustomobject]@{ Present = (Marker-Present $Id); Version = $null }
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('[componentStates]')
foreach ($sectionName in @($kit.Keys | Where-Object { $_ -like 'component.*' } | Sort-Object)) {
    $id = $sectionName.Substring('component.'.Length)
    $packaged = [string]$kit[$sectionName]['version']
    $installed = Installed-Component $id
    $state = 'not-installed'
    if ($installed.Present) {
        if ([string]::IsNullOrWhiteSpace([string]$installed.Version) -or [string]::IsNullOrWhiteSpace($packaged)) { $state = 'installed-unknown' }
        else {
            $comparison = Compare-ProductVersion ([string]$installed.Version) $packaged
            $state = if ($comparison -lt 0) { 'update' } elseif ($comparison -gt 0) { 'newer' } else { 'current' }
        }
    }
    $lines.Add("$id.state=$state")
    $lines.Add("$id.installedVersion=$([string]$installed.Version)")
    $lines.Add("$id.packagedVersion=$packaged")
}

$parent = Split-Path -Parent $OutputPath
if ($parent) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
# This helper is deliberately launched with the inbox Windows PowerShell host so
# installer state discovery does not depend on optional PowerShell 7.  Every key
# and value written here is constrained to product identifiers and numeric
# versions, so ASCII keeps the file compatible with Windows PowerShell 5.1 and
# NSIS' INI reader without losing information.
$lines | Set-Content -LiteralPath $OutputPath -Encoding ascii
