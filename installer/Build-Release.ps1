[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$RequireFfmpegBundle
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$uiProject = Join-Path $repoRoot "src\StorageMaster.UI\StorageMaster.UI.csproj"
$buildOutputDir = Join-Path $repoRoot "src\StorageMaster.UI\bin\$Platform\$Configuration\net8.0-windows10.0.19041.0\$RuntimeIdentifier"
$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"
$installerScript = Join-Path $repoRoot "installer\StorageMaster.iss"
$installerOutputDir = Join-Path $repoRoot "artifacts\installer"
$ffmpegBundleSource = Join-Path $repoRoot "installer\ffmpeg"
$turboScannerSource = Join-Path $repoRoot "turbo-scanner\target\release\turbo-scanner.exe"
$windowsAppSdkPackageRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windowsappsdk"
$windowsAppRuntimeInstaller = Join-Path $repoRoot "installer\Install-WindowsAppRuntime.ps1"

function Resolve-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe was not found. Install Visual Studio 2022 with the MSBuild workload."
    }

    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

    if (-not $msbuild) {
        throw "MSBuild.exe was not found. Install Visual Studio 2022 with the Windows application development workload."
    }

    return $msbuild
}

function Resolve-ISCC {
    $candidate = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"

    if (-not (Test-Path $candidate)) {
        throw "ISCC.exe was not found. Install Inno Setup 6 to build the installer."
    }

    return $candidate
}

function Get-WindowsAppSdkVersion {
    [xml]$projectXml = Get-Content -Path $uiProject
    $packageReference = $projectXml.SelectNodes("//PackageReference[@Include='Microsoft.WindowsAppSDK']") |
        Select-Object -First 1

    if (-not $packageReference) {
        throw "Microsoft.WindowsAppSDK package reference was not found in $uiProject"
    }

    return $packageReference.GetAttribute("Version")
}

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host ">" $FilePath ($Arguments -join " ")
    & $FilePath @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE."
    }
}

function Copy-OptionalFfmpegBundle {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory
    )

    $resolvedSource = $SourceDirectory
    if (-not (Test-Path $resolvedSource)) {
        $ffmpegCommand = Get-Command "ffmpeg.exe" -ErrorAction SilentlyContinue
        $ffprobeCommand = Get-Command "ffprobe.exe" -ErrorAction SilentlyContinue
        if ($ffmpegCommand -and $ffprobeCommand) {
            $ffmpegDirectory = Split-Path -Parent $ffmpegCommand.Source
            if ([string]::Equals($ffmpegDirectory, (Split-Path -Parent $ffprobeCommand.Source), [StringComparison]::OrdinalIgnoreCase)) {
                $resolvedSource = $ffmpegDirectory
            }
        }
    }

    if (-not (Test-Path $resolvedSource)) {
        $message = "FFmpeg bundle not found at $SourceDirectory and ffmpeg.exe/ffprobe.exe were not found together on PATH."
        if ($RequireFfmpegBundle) {
            throw $message
        }

        Write-Host $message
        return
    }

    $ffmpegExe = Join-Path $resolvedSource "ffmpeg.exe"
    $ffprobeExe = Join-Path $resolvedSource "ffprobe.exe"
    if (-not (Test-Path $ffmpegExe) -or -not (Test-Path $ffprobeExe)) {
        $message = "FFmpeg source must contain both ffmpeg.exe and ffprobe.exe: $resolvedSource"
        if ($RequireFfmpegBundle) {
            throw $message
        }

        Write-Host $message
        return
    }

    $targetDirectory = Join-Path $PublishDirectory "tools\ffmpeg"
    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    Copy-Item -LiteralPath $ffmpegExe -Destination $targetDirectory -Force
    Copy-Item -LiteralPath $ffprobeExe -Destination $targetDirectory -Force
    Write-Host "Bundled FFmpeg copied to $targetDirectory"
}

function Copy-OptionalTurboScanner {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory
    )

    if (-not (Test-Path $SourcePath)) {
        Write-Host "Turbo scanner binary not found at $SourcePath. Run cargo build --release in turbo-scanner to include it."
        return
    }

    Copy-Item -LiteralPath $SourcePath -Destination (Join-Path $PublishDirectory "turbo-scanner.exe") -Force
    Write-Host "Turbo scanner copied to $PublishDirectory"
}

New-Item -ItemType Directory -Force -Path $publishDir, $installerOutputDir | Out-Null

$msbuild = Resolve-MSBuild
$iscc = Resolve-ISCC
$windowsAppSdkVersion = Get-WindowsAppSdkVersion
$windowsAppRuntimePackage = Join-Path $windowsAppSdkPackageRoot "$windowsAppSdkVersion\tools\MSIX\win10-x64\Microsoft.WindowsAppRuntime.1.6.msix"

Invoke-Step -FilePath $msbuild -Arguments @(
    $uiProject,
    "/t:Clean,Build",
    "/restore",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/p:RuntimeIdentifier=$RuntimeIdentifier",
    "/p:UseXamlCompilerExecutable=false",
    "/m:1",
    "/nr:false"
)

Invoke-Step -FilePath $msbuild -Arguments @(
    $uiProject,
    "/t:Build",
    "/restore",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/p:RuntimeIdentifier=$RuntimeIdentifier",
    "/p:UseXamlCompilerExecutable=false",
    "/m:1",
    "/nr:false"
)

if (-not (Test-Path $buildOutputDir)) {
    throw "Build output directory was not produced: $buildOutputDir"
}

if (-not (Test-Path $windowsAppRuntimePackage)) {
    throw "Windows App SDK runtime package was not found: $windowsAppRuntimePackage"
}

if (-not (Test-Path $windowsAppRuntimeInstaller)) {
    throw "Windows App SDK runtime installer script was not found: $windowsAppRuntimeInstaller"
}

Get-ChildItem -LiteralPath $publishDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $buildOutputDir "*") -Destination $publishDir -Recurse -Force
Copy-OptionalFfmpegBundle -SourceDirectory $ffmpegBundleSource -PublishDirectory $publishDir
Copy-OptionalTurboScanner -SourcePath $turboScannerSource -PublishDirectory $publishDir

$prereqDir = Join-Path $publishDir "prereqs"
New-Item -ItemType Directory -Force -Path $prereqDir | Out-Null
Copy-Item -LiteralPath $windowsAppRuntimePackage -Destination (Join-Path $prereqDir "Microsoft.WindowsAppRuntime.1.6.msix") -Force
Copy-Item -LiteralPath $windowsAppRuntimeInstaller -Destination (Join-Path $prereqDir "Install-WindowsAppRuntime.ps1") -Force
Write-Host "Windows App SDK 1.6 runtime prereqs staged in $prereqDir"

Invoke-Step -FilePath $iscc -Arguments @($installerScript)

Write-Host ""
Write-Host "Publish output :" $publishDir
Write-Host "Installer output:" $installerOutputDir
