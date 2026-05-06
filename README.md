# StorageMaster    [![Release](https://github.com/0langa/StorageMaster/actions/workflows/release.yml/badge.svg?event=release)](https://github.com/0langa/StorageMaster/actions/workflows/release.yml)

> **Current version:** 1.7.2 — Windows disk analyzer, junk cleaner, and storage health tool.

A Windows disk analyzer and storage cleaner built with **C# / .NET 8 / WinUI 3**, with an optional native Rust scan engine for maximum throughput on multi-core systems.

---

## Features

| Feature | Details |
|---------|---------|
| **Parallel scanner** | BFS directory walker with bounded work-stealing concurrency |
| **Turbo Scanner** | Optional Rust-powered scanner (jwalk) — up to 4× faster on SSDs |
| **Smart Cleaner** | One-click scan & clean — no prior scan session needed |
| **17 cleanup rules** | Temp files, browser caches, Windows Update, WER, Delivery Optimization, downloaded installers, app caches, program leftovers, Recycle Bin, large old files, thumbnail cache, icon cache, font cache, DNS cache, prefetch files, Microsoft Store logs, duplicate files |
| **Deep scan / Admin elevation** | Restart-as-admin flow to scan protected directories |
| **Recycle Bin integration** | All deletions go to Recycle Bin by default (recoverable) |
| **Quarantine** | Duplicate deletions can be quarantined with one-click restore from the UI |
| **Audit trail** | Every deletion logged to SQLite `CleanupLog` — forever |
| **Scan history** | Every scan session stored; browse and compare historical results |
| **Duplicate analysis** | Pluggable dedupe engine with exact SHA-256, normalized-text review, image pHash, optional video pHash (auto-detects bundled or PATH FFmpeg), quarantine/recycle deletion, and audit trail |
| **Duplicate previews** | Inline previews for image and video groups; first-difference highlight for text duplicates |
| **Results visualization** | Largest files, largest folders, file-type breakdown, error log, category filters, and paged loading |
| **Folder size aggregation** | Bottom-up propagation gives accurate folder totals |
| **CLI / headless mode** | Full-featured command-line interface for scripting and automation |
| **System tray** | Minimize to tray; tray menu for common actions; balloon notifications |
| **Low-disk notifications** | Configurable warning/critical thresholds; per-drive 12 h debounce |
| **Scheduled tasks** | Windows Task Scheduler integration — daily/weekly scans and cleanups |
| **GitHub release updater** | Checks GitHub Releases, verifies digest/signature policy, downloads setup EXE, and launches installer on demand |
| **Theme + retention settings** | Persisted light/dark/default theme, scan-history retention window, and uninstall-safe user data |

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
| **Duplicates** | Scope by folders/categories/extensions, run exact or fuzzy methods, review previews, delete/quarantine selected copies, restore quarantined files |
| **Smart Cleaner** | Direct one-click scan → review → clean, no session needed |
| **Settings** | All user preferences, scanner options, cleanup thresholds, scheduler, tray, and app update controls |

---

## CLI interface

StorageMaster ships a full command-line interface. Use `--cli` for an interactive console session or `--headless` to attach to the parent process console (used by scheduled tasks).

```
StorageMaster.UI.exe --cli scan --path <abs-path> [--turbo] [--deep] [--json <file>]
StorageMaster.UI.exe --cli report last-scan [--json <file>] [--csv <file>]
StorageMaster.UI.exe --cli dedupe scan --session <id> --methods exact,text,image,video [--min-size <mb>] [--extensions ...] [--json <file>]
StorageMaster.UI.exe --cli cleanup analyze --session <id> [--json <file>]
StorageMaster.UI.exe --cli cleanup execute --session <id> --rules <csv> --recycle-bin|--quarantine --confirm
StorageMaster.UI.exe --headless jobs run --id <job-id>
```

Exit codes: `0` success · `1` unexpected error · `2` bad arguments · `3` missing `--confirm` · `4` not found / not elevated.

---

## Tray and background mode

- **Minimize to tray:** when enabled in Settings, the close button hides the window instead of exiting. Right-click the tray icon for Open, Run Smart Clean, Start Scan, Review Duplicates, Pause Notifications, and Exit.
- **Start in tray:** launch with `--start-in-tray` to open minimized (used by the startup registry entry).
- **Low-disk notifications:** tray balloon when a drive falls below the warning (default 15 %) or critical (default 5 %) threshold. Checked every 15 minutes with a 12-hour debounce per drive per level.

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
# Output: artifacts/installer/StorageMaster-1.7.2-win-x64-Setup.exe
```

Optional: place `ffmpeg.exe` and `ffprobe.exe` in `installer\ffmpeg\` before packaging. Release builds copy them into `tools\ffmpeg\` beside the app so video pHash works out of the box.

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
│  (Pages, ViewModels, Converters, Navigation,        │
│   Infrastructure, ServiceBootstrapper)              │
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

`ServiceBootstrapper.BuildServices()` wires a `Microsoft.Extensions.DependencyInjection` container:

- **Singletons:** repositories, scanners, cleanup engine, drives, file deleter, notification service, scheduled task service, duplicate preview service, command runner, Smart Cleaner service
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

Duplicate cleanup follows the same audit path. Quarantine moves files to a safe directory; the Duplicates page lists all quarantined files with a **Restore** button that moves them back to their original paths.

---

## Cleanup rules

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
| `ThumbnailCacheRule` | App Caches | Safe | `%LOCALAPPDATA%\Microsoft\Windows\Explorer` thumb cache |
| `IconCacheRule` | App Caches | Safe | `iconcache*.db` — rebuilt automatically by Explorer |
| `FontCacheRule` | App Caches | Safe | Windows font cache service data files |
| `DnsClientCacheRule` | App Caches | Safe | Flushes DNS resolver cache via `ipconfig /flushdns` |
| `PrefetchFilesRule` | Temp Files | Low | `C:\Windows\Prefetch` — rebuilt on next launch |
| `MicrosoftStoreLogsRule` | Log Files | Safe | `%LOCALAPPDATA%\Packages\*\LocalState\DiagOutputDir` |
| `DuplicateFilesCleanupRule` | Duplicate Files | Medium | Surfaces duplicate groups from last dedupe run |

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
| `DuplicateRuns` / `DuplicateGroups` / `DuplicateGroupMembers` | Saved dedupe runs, groups, members, selection state |
| `DuplicateSignatures` | Cached method signatures with source-size/mtime/identity validity metadata |
| `DuplicateErrors` | Per-file dedupe errors and skipped reasons |
| `QuarantinedFiles` | Original-to-quarantine path mapping for restore |
| `DiagnosticsLog` | Internal event log for scheduler and CLI operations |

Uninstall keeps `%LOCALAPPDATA%\StorageMaster` by default, so the database and settings survive reinstall/upgrade cycles.

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
- [`CHANGELOG.md`](CHANGELOG.md) — Release history
