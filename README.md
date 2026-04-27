# StorageMaster -- [![Release](https://github.com/0langa/StorageMaster/actions/workflows/release.yml/badge.svg)](https://github.com/0langa/StorageMaster/actions/workflows/release.yml)

A Windows disk analyzer and storage cleaner built with C# / .NET 8 / WinUI 3.

---

## Solution structure

```
StorageMaster/
├── src/
│   ├── StorageMaster.Core/               # Domain models, interfaces, scanner, cleanup rules
│   ├── StorageMaster.Platform.Windows/   # Windows-specific file deletion (Shell32, Recycle Bin)
│   ├── StorageMaster.Storage/            # SQLite persistence via Microsoft.Data.Sqlite
│   └── StorageMaster.UI/                 # WinUI 3 application (Windows App SDK)
└── tests/
    └── StorageMaster.Tests/              # xUnit unit + integration tests
```

---

## Prerequisites

| Component | Version |
|-----------|---------|
| .NET SDK | 8.0.x |
| Visual Studio 2022 | 17.9+ with **Windows application development** workload |
| Inno Setup | 6.x for installer builds |
| Target OS | Windows 10 1809 (build 17763) or later |

---

## Building

### Core libraries and tests

```powershell
# Build non-UI projects
dotnet build src/StorageMaster.Core/StorageMaster.Core.csproj
dotnet build src/StorageMaster.Storage/StorageMaster.Storage.csproj
dotnet build src/StorageMaster.Platform.Windows/StorageMaster.Platform.Windows.csproj

# Run tests
dotnet test tests/StorageMaster.Tests/StorageMaster.Tests.csproj
```

### WinUI desktop application

The unpackaged WinUI app is built with Visual Studio MSBuild, not plain `dotnet build`.

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
  -latest -products * -requires Microsoft.Component.MSBuild `
  -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

& $msbuild src\StorageMaster.UI\StorageMaster.UI.csproj `
  /t:Clean,Build `
  /restore `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:RuntimeIdentifier=win-x64 `
  /m:1 `
  /nr:false
```

### Build a release installer

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Release.ps1
```

Outputs:

- Staged app bundle: `artifacts\publish\win-x64\StorageMaster.UI.exe`
- Installer: `artifacts\installer\StorageMaster-1.2.0-win-x64-Setup.exe`

The release script builds the working framework-dependent WinUI desktop app, stages it into `artifacts\publish\win-x64`, copies the bundled Windows App SDK runtime installer into `artifacts\publish\win-x64\prereqs`, and then builds the Inno Setup installer from that folder. End users can either:

- run `StorageMaster.UI.exe` directly, or
- double-click the generated installer, let setup install the Windows App SDK runtime if needed, and launch StorageMaster from the desktop or Start menu shortcut

---

## Architecture

### MVVM

- **ViewModels** live in `StorageMaster.UI/Pages/` and inherit `ObservableObject` (CommunityToolkit.Mvvm).
- Commands use `[RelayCommand]` source-generated attributes.
- No business logic in code-behind — all in ViewModels.

### Dependency injection

`App.xaml.cs` builds a `Microsoft.Extensions.DependencyInjection` container wiring:
- Singletons: repositories, scanner, cleanup engine, drive info, file deleter
- Transients: ViewModels (created fresh per navigation)

### Scanner

`FileScanner` uses a `Channel<string>` producer/consumer pattern:
- **Producer**: BFS directory walk, skips symlinks/junctions and excluded paths
- **Consumers**: `MaxParallelism` concurrent workers (default 4) process directories
- **Progress**: `PeriodicTimer` fires every 300 ms to avoid UI blocking
- **Batching**: file entries buffered in `ConcurrentQueue`, flushed to SQLite every `DbBatchSize` records (default 500)

### Cleanup safety

Files are **never deleted without explicit user confirmation**. The flow is:
1. `CleanupEngine.GetSuggestionsAsync()` — rules produce suggestions only
2. User selects items in `CleanupPage`
3. User clicks "Clean Up…" → `ContentDialog` confirmation appears
4. Only on confirmation: `CleanupEngine.ExecuteAsync()` → `IFileDeleter.DeleteManyAsync()`
5. Every action logged to `CleanupLog` table in SQLite

### Storage

SQLite with WAL journal mode. Schema migrations are version-tracked in `SchemaVersion` table.
All bulk inserts use explicit transactions for throughput.

---

## Cleanup rules (v1)

| Rule | Category | Risk |
|------|----------|------|
| `RecycleBinCleanupRule` | Recycle Bin | Safe |
| `TempFilesCleanupRule` | Temp Files | Low |
| `DownloadedInstallersRule` | Downloads | Low |
| `CacheFolderCleanupRule` | App Caches | Safe–Low |
| `LargeOldFilesCleanupRule` | Large Old Files | Medium |

---

## Roadmap (v2+)

- USN Journal / NTFS MFT fast scanning
- Duplicate file detection (SHA-256 hashing)
- Treemap / sunburst visualisation
- Background Windows Service for scheduled scans
- Cloud storage awareness (OneDrive, Google Drive stubs)
- Plugin-based cleanup rule system
- Telemetry (opt-in only)
- Multi-language support

---

## Test coverage

`tests/StorageMaster.Tests/StorageMaster.Tests.csproj` currently contains 41 passing tests covering scanner behavior, cleanup rules, persistence, aggregation, compilation flow, and UI-adjacent view-model logic.
