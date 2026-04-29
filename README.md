# StorageMaster  [![Release](https://github.com/0langa/StorageMaster/actions/workflows/release.yml/badge.svg)](https://github.com/0langa/StorageMaster/actions/workflows/release.yml)

> **Current version:** 1.3.3 — Windows disk analyzer, junk cleaner, and storage health tool.

A Windows disk analyzer and storage cleaner built with **C# / .NET 8 / WinUI 3**, with an optional native Rust scan engine for maximum throughput on multi-core systems.

---

## Features

| Feature | Details |
|---------|---------|
| **Parallel scanner** | BFS directory walker with bounded work-stealing concurrency |
| **Turbo Scanner** | Optional Rust-powered scanner (jwalk) — up to 4× faster on SSDs |
| **Smart Cleaner** | One-click scan & clean — no prior scan session needed |
| **10 cleanup rules** | Temp files, browser caches, Windows Update, WER, Delivery Optimization, downloaded installers, app caches, program leftovers, Recycle Bin, large old files |
| **Deep scan / Admin elevation** | Restart-as-admin flow to scan protected directories |
| **Recycle Bin integration** | All deletions go to Recycle Bin by default (recoverable) |
| **Audit trail** | Every deletion logged to SQLite `CleanupLog` — forever |
| **Scan history** | Every scan session stored; browse and compare historical results |
| **Results visualization** | Largest files, largest folders, file-type breakdown, error log |
| **Folder size aggregation** | Bottom-up propagation gives accurate folder totals |

---

## Solution structure

```
StorageMaster/
├── src/
│   ├── StorageMaster.Core/               # Domain models, interfaces, scanner, cleanup rules
│   ├── StorageMaster.Platform.Windows/   # Windows-specific: deletion, drives, elevation, Turbo Scanner
│   ├── StorageMaster.Storage/            # SQLite persistence (Microsoft.Data.Sqlite)
│   └── StorageMaster.UI/                 # WinUI 3 unpackaged desktop application
├── tests/
│   └── StorageMaster.Tests/              # xUnit unit + integration tests
├── turbo-scanner/                        # Rust crate — native parallel file enumeration
│   ├── Cargo.toml
│   └── src/main.rs
├── installer/
│   └── StorageMaster.iss                 # Inno Setup 6 script
└── .github/workflows/
    └── release.yml                       # CI/CD: test → publish → Rust build → installer → GitHub Release
```

---

## Navigation pages

| Page | Purpose |
|------|---------|
| **Dashboard** | Disk health overview, drive usage bars, last scan summary |
| **Scan** | Configure and run a full directory scan (managed or Turbo) |
| **Results** | Largest files, largest folders, file types, scan errors |
| **Cleanup** | Session-based cleanup with per-category toggles and dry-run |
| **Smart Cleaner** | Direct one-click scan → review → clean, no session needed |
| **Settings** | All user preferences, scanner options, cleanup thresholds |

---

## Prerequisites

| Component | Version |
|-----------|---------|
| .NET SDK | 8.0.x |
| Visual Studio 2022 | 17.9+ with **Windows application development** workload |
| Rust toolchain | stable (for building turbo-scanner from source) |
| Inno Setup | 6.x (for local installer builds) |
| Target OS | Windows 10 1809 (build 17763) or later |

---

## Building

### Core libraries and tests

```powershell
dotnet build src/StorageMaster.Core/StorageMaster.Core.csproj
dotnet build src/StorageMaster.Storage/StorageMaster.Storage.csproj
dotnet build src/StorageMaster.Platform.Windows/StorageMaster.Platform.Windows.csproj

dotnet test tests/StorageMaster.Tests/StorageMaster.Tests.csproj
```

### WinUI desktop application

Build the UI project with MSBuild (plain `dotnet build` does not drive the XAML compiler for WinUI 3):

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
  -latest -products * -requires Microsoft.Component.MSBuild `
  -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

& $msbuild src\StorageMaster.UI\StorageMaster.UI.csproj `
  /t:Clean,Build /restore `
  /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 `
  /m:1 /nr:false
```

### Build the Turbo Scanner (Rust)

```powershell
cargo build --release --manifest-path turbo-scanner/Cargo.toml
# Binary: turbo-scanner/target/release/turbo-scanner.exe
```

Copy the binary next to `StorageMaster.UI.exe` to enable it at runtime.

### Build a release installer

```powershell
# 1. Publish the .NET application
dotnet publish src/StorageMaster.UI/StorageMaster.UI.csproj /p:PublishProfile=win-x64 -c Release

# 2. Build the Rust binary
cargo build --release --manifest-path turbo-scanner/Cargo.toml --target x86_64-pc-windows-msvc
Copy-Item turbo-scanner\target\x86_64-pc-windows-msvc\release\turbo-scanner.exe artifacts\publish\win-x64\

# 3. Build the installer
iscc installer\StorageMaster.iss
# Output: artifacts/installer/StorageMaster-1.3.0-win-x64-Setup.exe
```

The automated release pipeline (`release.yml`) runs all three steps on every `v*.*.*` git tag and attaches the installer to a GitHub Release.

---

## Turbo Scanner — how it works

The Turbo Scanner is a native Rust binary (`turbo-scanner.exe`) that uses **jwalk**'s work-stealing thread pool to enumerate the file system across all CPU cores simultaneously — significantly faster than the managed C# scanner on multi-core systems with SSDs.

**Integration is completely transparent to the user:**

1. `ScanViewModel` holds references to both `FileScanner` (managed) and `TurboFileScanner` (Rust-backed).
2. When a scan starts, the active scanner is selected based on the user's toggle in the Scan page (`UseTurboScanner && TurboScannerAvailable`).
3. `TurboFileScanner` spawns `turbo-scanner.exe` as an invisible background process (no console window). It reads JSONL from stdout, maps each record to the same `FileEntry` / `FolderEntry` models, and writes to the database in batches — exactly as the managed scanner does.
4. If `turbo-scanner.exe` is missing (e.g., a local F5 debug run without a published build), `TurboFileScanner` silently falls back to the managed `FileScanner`. The user sees no error.
5. Progress reporting, cancellation, and results are identical regardless of which backend ran.

The Rust process runs completely hidden. There is no user-visible indication that a second executable is involved — only faster results.

---

## Architecture

### Layering

```
┌─────────────────────────────────────────────────────┐
│                   StorageMaster.UI                  │  WinUI 3 / MVVM
│  (Pages, ViewModels, Converters, Navigation)        │
└───────────────────────┬─────────────────────────────┘
                        │ calls via DI interfaces
        ┌───────────────┼───────────────┐
        ▼               ▼               ▼
┌──────────────┐ ┌──────────────┐ ┌────────────────────────────────┐
│ Core         │ │ Storage      │ │ Platform.Windows               │
│ (scanner,    │ │ (SQLite,     │ │ (FileDeleter, DriveInfo,       │
│  rules,      │ │  repos,      │ │  elevation, InstalledPrograms, │
│  interfaces) │ │  schema)     │ │  TurboFileScanner)             │
└──────────────┘ └──────────────┘ └────────────────────────────────┘
```

**Key invariant:** `Core` has no project references. All platform and persistence details flow inward via interfaces defined in Core.

### MVVM

- ViewModels live in `StorageMaster.UI/Pages/` and inherit `ObservableObject` (CommunityToolkit.Mvvm).
- Commands use `[RelayCommand]` source-generated attributes.
- No business logic in XAML code-behind.
- All page bindings use `{x:Bind}` compiled bindings for type safety and performance.

### Dependency injection

`App.xaml.cs::BuildServices()` wires a `Microsoft.Extensions.DependencyInjection` container:

- **Singletons:** repositories, scanners, cleanup engine, drives, file deleter, Smart Cleaner service
- **Transients:** ViewModels (fresh per navigation to keep state clean)

### Scanner concurrency model

```
Thread: Producer (1)
    BFS walk → Channel<string> (bounded, 1024 capacity)

Thread Pool: Consumers (MaxParallelism, default 4)
    Channel.ReadAllAsync → ProcessDirectory → ConcurrentQueue<FileEntry/FolderEntry>

Thread: Progress Timer
    PeriodicTimer(300ms) → IProgress<ScanProgress>.Report()

UI Thread (via DispatcherQueue):
    Progress updates applied — no SynchronizationContext needed (unpackaged WinUI 3)
```

### Cleanup safety

Files are **never deleted without explicit user confirmation**:

1. `ICleanupRule.AnalyzeAsync()` — reads DB, produces `CleanupSuggestion` objects (never touches filesystem)
2. User reviews and selects suggestions in `CleanupPage` or `SmartCleanerPage`
3. User clicks "Clean" → `ContentDialog` (modal confirmation gate)
4. On confirmation only: `CleanupEngine.ExecuteAsync()` → `IFileDeleter.DeleteManyAsync()`
5. Every deletion attempt logged to `CleanupLog` table (append-only, never deleted)

---

## Cleanup rules (v1.3)

| Rule | Category | Risk | Notes |
|------|----------|------|-------|
| `RecycleBinCleanupRule` | Recycle Bin | Safe | Uses `SHEmptyRecycleBin` |
| `TempFilesCleanupRule` | Temp Files | Low | `%TEMP%`, `C:\Windows\Temp` |
| `DownloadedInstallersRule` | Downloads | Low | Installer exts in Downloads; optional full-folder clear |
| `CacheFolderCleanupRule` | App Caches | Safe–Low | Edge, npm, pip, NuGet, Yarn |
| `BrowserCacheCleanupRule` | Browser Cache | Low | Chrome, Edge, Firefox, Brave, Opera |
| `WindowsUpdateCacheRule` | Windows Update | Low | `SoftwareDistribution\Download` |
| `DeliveryOptimizationRule` | Delivery Opt. | Low | `SoftwareDistribution\DeliveryOptimization` |
| `WindowsErrorReportingRule` | Error Reports | Low | WER folders, crash dumps, `.dmp` files |
| `UninstalledProgramLeftoversRule` | Program Leftovers | Medium | Registry cross-reference; 90-day, 10 MB thresholds |
| `LargeOldFilesCleanupRule` | Large Old Files | Medium | Per-file suggestions; configurable size and age |

---

## Database

SQLite with WAL journal mode at `%LOCALAPPDATA%\StorageMaster\storagemaster.db`.

Schema auto-migrates on first launch. Key tables:

| Table | Purpose |
|-------|---------|
| `ScanSessions` | One row per scan run |
| `FileEntries` | One row per file, FK → session |
| `FolderEntries` | One row per directory with aggregated sizes |
| `ScanErrors` | Per-path errors (access denied, I/O) |
| `CleanupLog` | Append-only deletion audit |
| `Settings` | JSON-serialised `AppSettings` |

---

## Test coverage

```powershell
dotnet test tests/StorageMaster.Tests/StorageMaster.Tests.csproj --verbosity normal
```

Tests cover scanner behaviour, cleanup rules, persistence, folder aggregation, ViewModel logic, and schema migrations.

---

## CI/CD

Every push of a `v*.*.*` tag triggers `release.yml`:

1. Restore and run all tests
2. Build `turbo-scanner.exe` (Rust, `x86_64-pc-windows-msvc`)
3. `dotnet publish` the WinUI app (`win-x64`)
4. Copy `turbo-scanner.exe` into the publish output
5. Build Inno Setup installer
6. Optionally Authenticode-sign binaries (requires `CODE_SIGNING_PFX` / `CODE_SIGNING_PFX_PASSWORD` secrets)
7. Attach installer to a GitHub Release

---

## Further reading

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — Deep architecture reference
- [`docs/CODEMAP.md`](docs/CODEMAP.md) — Every file, class, and method
- [`docs/DOCUMENTATION.md`](docs/DOCUMENTATION.md) — Full API and configuration reference
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — v1.3 → v1.5 development plan
