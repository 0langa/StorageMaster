[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsixPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

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
    Write-Host "Windows App SDK runtime already present:" $installedPackage.Version
    exit 0
}

Write-Host "Installing Windows App SDK runtime from" $MsixPath
Add-AppxPackage -Path $MsixPath -ForceUpdateFromAnyVersion -ErrorAction Stop
