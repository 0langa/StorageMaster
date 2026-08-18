[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsixPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$logDir = Join-Path $env:LOCALAPPDATA "StorageMaster\logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logPath = Join-Path $logDir "installer-prereqs.log"

function Write-PrereqLog {
    param([Parameter(Mandatory = $true)][string]$Message)
    $line = "[{0:O}] {1}" -f [DateTimeOffset]::Now, $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
    Write-Host $Message
}

if (-not (Test-Path -LiteralPath $MsixPath)) {
    throw "Windows App SDK runtime package was not found: $MsixPath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-RequiredVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)

    try {
        $manifestEntry = $archive.Entries | Where-Object FullName -eq "AppxManifest.xml" | Select-Object -First 1

        if (-not $manifestEntry) {
            throw "AppxManifest.xml was not found in $PackagePath"
        }

        $stream = $manifestEntry.Open()

        try {
            $reader = New-Object System.IO.StreamReader($stream)

            try {
                $manifestXml = [xml]$reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }

        return [version]$manifestXml.Package.Identity.Version
    }
    finally {
        $archive.Dispose()
    }
}

$requiredVersion = Get-RequiredVersion -PackagePath $MsixPath
$installedPackage = Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.1.6" -ErrorAction SilentlyContinue |
    Where-Object Architecture -eq "X64" |
    Sort-Object Version -Descending |
    Select-Object -First 1

if ($installedPackage -and ([version]$installedPackage.Version -ge $requiredVersion)) {
    Write-PrereqLog "Windows App SDK runtime already present: $($installedPackage.Version)"
    exit 0
}

Write-PrereqLog "Installing Windows App SDK runtime $requiredVersion from $MsixPath"
try {
    Add-AppxPackage -Path $MsixPath -ForceUpdateFromAnyVersion -ErrorAction Stop
    $installedAfter = Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.1.6" -ErrorAction SilentlyContinue |
        Where-Object Architecture -eq "X64" |
        Sort-Object Version -Descending |
        Select-Object -First 1

    if (-not $installedAfter -or ([version]$installedAfter.Version -lt $requiredVersion)) {
        throw "Windows App SDK runtime post-install verification failed. Required $requiredVersion; found $($installedAfter.Version)."
    }

    Write-PrereqLog "Windows App SDK runtime install verified: $($installedAfter.Version)"
}
catch {
    Write-PrereqLog "Windows App SDK runtime install failed: $($_.Exception.Message)"
    throw
}
