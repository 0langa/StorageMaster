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

Get-ChildItem -LiteralPath $publishDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $buildOutputDir "*") -Destination $publishDir -Recurse -Force
Copy-OptionalFfmpegBundle -SourceDirectory $ffmpegBundleSource -PublishDirectory $publishDir
Copy-OptionalTurboScanner -SourcePath $turboScannerSource -PublishDirectory $publishDir

$prereqsDir = Join-Path $PSScriptRoot "prereqs"
$exeDest    = Join-Path $prereqsDir "WindowsAppRuntimeInstall.exe"

if (Test-Path $exeDest) {
    Write-Host "Runtime installer already staged: $exeDest"
} else {
    $stageDir = Join-Path $PSScriptRoot "prereqs-stage"
    New-Item -ItemType Directory -Force -Path $prereqsDir, $stageDir | Out-Null

    Write-Host "Downloading WinAppSDK 1.8 runtime redist bundle via winget..."
    winget download "Microsoft.WindowsAppRuntime.1.8" `
        --download-directory $stageDir `
        --accept-source-agreements `
        --accept-package-agreements

    $zip = Get-ChildItem $stageDir -Filter "*.zip" -Recurse | Select-Object -First 1
    if (-not $zip) {
        throw "winget did not produce a redist zip in $stageDir."
    }

    $extractDir = Join-Path $stageDir "extracted"
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $extractDir -Force

    $exe = Get-ChildItem $extractDir -Filter "WindowsAppRuntimeInstall*.exe" -Recurse |
           Select-Object -First 1
    if (-not $exe) {
        throw "WindowsAppRuntimeInstall.exe not found inside $($zip.Name)."
    }

    Copy-Item $exe.FullName $exeDest -Force
    Remove-Item $stageDir -Recurse -Force
    Write-Host "Runtime installer staged: $exeDest"
}

Invoke-Step -FilePath $iscc -Arguments @($installerScript)

Write-Host ""
Write-Host "Publish output :" $publishDir
Write-Host "Installer output:" $installerOutputDir
