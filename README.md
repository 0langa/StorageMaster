# StorageMaster

A production-quality Windows disk analyser and storage cleaner built with C# / .NET 10 / WinUI 3.

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
| .NET SDK | 10.0+ |
| Windows App SDK runtime | 1.6+ |
| Visual Studio 2022 | 17.9+ with **Windows application development** workload |
| Target OS | Windows 10 1903 (build 18362) or later |

---

## Building

### Backend / tests (dotnet CLI, no VS required)

```powershell
# All backend projects compile with plain dotnet
dotnet build src/StorageMaster.Core/StorageMaster.Core.csproj
dotnet build src/StorageMaster.Storage/StorageMaster.Storage.csproj
dotnet build src/StorageMaster.Platform.Windows/StorageMaster.Platform.Windows.csproj

# Run all tests
dotnet test tests/StorageMaster.Tests/StorageMaster.Tests.csproj
```

### Full solution including WinUI 3 UI

Open `StorageMaster.sln` in **Visual Studio 2022** and press F5, or:

```powershell
# Build for x64 (required for WinUI 3 / Windows App SDK)
dotnet build src/StorageMaster.UI/StorageMaster.UI.csproj -r win-x64 -c Release
```

> **Note:** The UI project requires the Windows App SDK NuGet packages
> (`Microsoft.WindowsAppSDK 1.6`). These are restored automatically via NuGet.
> The app uses `WindowsAppSDKSelfContained=true` so no separate runtime installer
> is needed on end-user machines.

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

```
13 tests | 0 failures | 0 skipped
- Scanner: cancellation, batching, invalid path, real filesystem scan
- Cleanup rules: large/old files, protected paths, temp extensions
- Storage: session CRUD, file entry round-trip, category aggregation
```
