[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$normalizedExpected = $ExpectedVersion.Trim()
if ($normalizedExpected.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
    $normalizedExpected = $normalizedExpected.Substring(1)
}

$semVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($normalizedExpected -cnotmatch $semVerPattern) {
    throw "Expected release version '$ExpectedVersion' is not strict SemVer."
}

[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props")
$sourceVersion = [string]$props.Project.PropertyGroup.StorageMasterVersion
$assemblyVersion = [string]$props.Project.PropertyGroup.StorageMasterAssemblyVersion

$cargoPath = Join-Path $repoRoot "turbo-scanner\Cargo.toml"
$cargoVersion = $null
$insidePackage = $false
foreach ($line in Get-Content -LiteralPath $cargoPath) {
    if ($line -match '^\s*\[([^]]+)\]\s*$') {
        $insidePackage = $Matches[1] -ceq 'package'
        continue
    }

    if ($insidePackage -and $line -match '^\s*version\s*=\s*"([^"]+)"\s*$') {
        $cargoVersion = $Matches[1]
        break
    }
}
if ([string]::IsNullOrWhiteSpace($cargoVersion)) {
    throw "Could not read [package].version from $cargoPath."
}

$installerPath = Join-Path $repoRoot "installer\StorageMaster.iss"
$installerVersion = $null
foreach ($line in Get-Content -LiteralPath $installerPath) {
    if ($line -match '^\s*#define\s+AppVersion\s+"([^"]+)"\s*$') {
        $installerVersion = $Matches[1]
        break
    }
}
if ([string]::IsNullOrWhiteSpace($installerVersion)) {
    throw "Could not read fallback AppVersion from $installerPath."
}

$versions = [ordered]@{
    "Directory.Build.props StorageMasterVersion" = $sourceVersion
    "turbo-scanner/Cargo.toml package.version" = $cargoVersion
    "installer/StorageMaster.iss fallback AppVersion" = $installerVersion
}
foreach ($entry in $versions.GetEnumerator()) {
    if (-not [string]::Equals($entry.Value, $normalizedExpected, [StringComparison]::Ordinal)) {
        throw "$($entry.Key) '$($entry.Value)' does not match release version '$normalizedExpected'."
    }
}

$numericCore = ($normalizedExpected -split '[-+]')[0]
$expectedAssemblyVersion = "$numericCore.0"
if (-not [string]::Equals($assemblyVersion, $expectedAssemblyVersion, [StringComparison]::Ordinal)) {
    throw "StorageMasterAssemblyVersion '$assemblyVersion' does not match '$expectedAssemblyVersion'."
}

Write-Host "Release versions agree: $normalizedExpected"
