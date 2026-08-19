# StorageMaster    [![Release](https://github.com/0langa/StorageMaster/actions/workflows/release.yml/badge.svg)](https://github.com/0langa/StorageMaster/actions/workflows/release.yml) [![CI](https://github.com/0langa/StorageMaster/actions/workflows/ci.yml/badge.svg)](https://github.com/0langa/StorageMaster/actions/workflows/ci.yml)

> **Current version:** 2.4.1 — Windows disk analyzer, junk cleaner, visual space map, drive health sentinel, and storage automation tool.

A Windows disk analyzer and storage cleaner built with **C# / .NET 8 / WinUI 3**, with an optional native Rust scan engine for maximum throughput on multi-core systems.

---

## Features

| Feature | Details |
|---------|---------|
| **Parallel scanner** | Managed BFS directory walker with a bounded producer/consumer worker pool |
| **Turbo Scanner** | Optional Rust-powered `jwalk` scanner for parallel native enumeration |
| **Smart Cleaner** | Direct review-and-clean workflow for 7 allow-listed junk sources; no prior scan session needed |
| **16 cleanup rules** | Temp files, browser caches, Windows Update, WER, Delivery Optimization, downloaded installers, app caches, program leftovers, Recycle Bin, large old files, thumbnail cache, icon cache, font cache, DNS cache, prefetch files, and Microsoft Store logs |
| **Deep scan worker** | Protected-directory scans launch through an elevated CLI worker so the WinUI shell remains unelevated |
| **Recycle Bin integration** | General file cleanup prefers Recycle Bin when the suggestion supports it; emptying Recycle Bin is explicitly permanent |
| **Quarantine + recovery journal** | Duplicate deletions can be quarantined with one-click restore from the UI; duplicate cleanup writes operation intent/outcome records before and after filesystem changes |
| **Audit trail** | Cleanup outcomes are written to SQLite `CleanupLog`; a database-write failure is surfaced instead of being reported as full success |
| **Scan history** | Every scan session stored; browse and compare historical results |
| **Duplicate analysis** | Pluggable dedupe engine with exact SHA-256, normalized-text review, image pHash, optional video pHash (auto-detects bundled or PATH FFmpeg), quarantine/recycle deletion, cleanup audit trail, and recovery journal |
| **Duplicate previews** | Inline previews for image and video groups; first-difference highlight for text duplicates |
| **Results visualization** | Largest files, largest folders, file-type breakdown, error log, category filters, scan workspace handoff, and paged loading |
| **Visual Space Map** | Interactive treemap for completed scans, with native tile controls, folder drill-down, category colors, size filters, CSV/HTML/PNG export, and safe review-only actions |
| **Scan Delta Insights** | Compare a scan to the previous scan of the same root to find growing folders, new large files, and removed files |
| **Folder size aggregation** | Bottom-up propagation gives accurate folder totals |
| **CLI / headless mode** | Full-featured command-line interface for scripting and automation |
| **System tray** | Minimize to tray; tray menu for common actions; balloon notifications |
| **Low-disk notifications** | Configurable warning/critical thresholds; per-drive 12 h debounce |
| **Drive Health & Storage Sentinel** | Windows WMI/storage health snapshots, Dashboard warnings, dedicated Drive Health page, tray alerts, and CLI JSON reports |
| **Scheduled tasks** | Windows Task Scheduler integration — daily/weekly scans and reviewable cleanup jobs; enabled unattended cleanup requires dedicated versioned consent |
| **GitHub release updater** | Checks GitHub Releases, verifies digest/signature policy, downloads setup EXE, and launches installer on demand |
| **Theme, language + retention settings** | Persisted light/dark/default theme with four selectable accents applied live, an interface-language pin (System/English/German) so WinUI's built-in control text matches the app, scan-history retention window, and uninstall-safe user data |

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
| **Dashboard** | Command-center overview with health summary, drive gauges, reclaimable space, file-type composition, and quick actions |
| **Scan** | Guided scan flow with scope, mode, live progress metrics, cancellation, and managed/Turbo backend selection |
| **Scan Workspace** | Unified completed-scan workspace for overview, files, folders, Space Map, Duplicates, Delta, and Errors |
| **Results** | Largest files, largest folders, file types, scan errors |
| **Cleanup** | Session-based cleanup with per-category toggles and dry-run |
| **Duplicates** | Scope by folders/categories/extensions, run exact or fuzzy methods, review previews, delete/quarantine selected copies, restore quarantined files |
| **Smart Cleaner** | Direct one-click scan → review → clean, no session needed |
| **Space Map** | Interactive treemap and scan delta comparison for completed scans |
| **Drive Health** | Read-only Windows storage telemetry, latest health snapshots, warnings, and unsupported/unknown fallbacks |
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
StorageMaster.UI.exe --cli health report [--json <file>]
StorageMaster.UI.exe --cli version
StorageMaster.UI.exe --headless jobs run --id <job-id>
```

Exit codes: `0` success · `1` failed/cancelled operation · `2` bad arguments · `3` missing `--confirm` · `4` not found, disabled by policy, or not elevated.

---

## Tray and background mode

- **Minimize to tray:** when enabled in Settings, the close button hides the window instead of exiting. Right-click the tray icon for Open, Run Smart Clean, Start Scan, Review Duplicates, Pause Notifications, and Exit.
- **Start in tray:** launch with `--start-in-tray` to open minimized (used by the startup registry entry).
- **Low-disk and drive-health notifications:** tray balloons when a drive falls below the warning (default 15 %) / critical (default 5 %) threshold or Windows reports unhealthy storage telemetry. Checked every 15 minutes with a 12-hour debounce per drive per level.

---

## Prerequisites

| Component | Version |
|-----------|---------|
| .NET SDK | 8.0.x |
| Visual Studio 2022 | 17.9+ with **Windows application development** workload |
| Rust toolchain | stable (for building turbo-scanner from source) |
| Inno Setup | 6.x (for local installer builds) |
| Target OS | Configured minimum: Windows 10 1809 (build 17763); the full Windows build matrix remains a release/lab gate |
| Runtime on installed machines | .NET Desktop Runtime 8 x64 and Windows App Runtime 1.6 x64 |

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

`Directory.Build.targets` enables the executable WinUI XAML compiler path used by CI. Build the UI project directly and name the platform:

```powershell
dotnet build src/StorageMaster.UI/StorageMaster.UI.csproj -c Release -p:Platform=x64
# Runnable exe: src\StorageMaster.UI\bin\x64\Release\net8.0-windows10.0.19041.0\StorageMaster.UI.exe
```

`dotnet build StorageMaster.sln -c Release` builds every library and the test project, but the solution maps the UI project's `Any CPU` configuration to `x86` — so it does **not** refresh the x64 executable, which is the shipped configuration. If you are testing live app behaviour, build the UI csproj as above and check the exe's timestamp before trusting what you see.

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
# Output: artifacts/installer/StorageMaster-2.4.1-win-x64-Setup.exe
```

Optional: place `ffmpeg.exe` and `ffprobe.exe` in `installer\ffmpeg\` before packaging. If that folder is absent, release builds also look for both tools together on PATH and copy them into `tools\ffmpeg\` beside the app so video pHash works out of the box.

The automated release pipeline (`release.yml`) runs all three steps on every `v*.*.*` git tag, marks tags containing `-` as GitHub prereleases, verifies installer shape/size/prereqs, and attaches the installer plus checksums to the release.

---

## Turbo Scanner — how it works

The Turbo Scanner is a native Rust binary (`turbo-scanner.exe`) that uses **jwalk**'s work-stealing thread pool for parallel filesystem enumeration. Relative performance depends on the storage device, filesystem, exclusions, and directory shape.

**Integration is completely transparent to the user:**

1. `ScanViewModel` holds references to both `FileScanner` (managed) and `TurboFileScanner` (Rust-backed).
2. When a scan starts, the active scanner is selected based on the user's toggle in the Scan page (`UseTurboScanner && TurboScannerAvailable`).
3. `TurboFileScanner` spawns `turbo-scanner.exe` as an invisible background process (no console window). It reads JSONL from stdout, maps each record to the same `FileEntry` / `FolderEntry` models, and writes to the database in batches — exactly as the managed scanner does.
4. If `turbo-scanner.exe` is missing at execution time, `TurboFileScanner` logs the fallback and delegates to managed `FileScanner`; the Scan page separately exposes native-backend availability.
5. Both backends persist the same scan model and feed the same UI. Backend-specific warnings and a filesystem changing during enumeration can still produce different observations.

The Rust child process has no console window; both backends use the same scan progress/results UI.

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
- XAML code-behind is limited to UI lifecycle, dialogs, navigation, and control events; ViewModels own presentation state and commands.
- Pages use both compiled `{x:Bind}` and ordinary `{Binding}` where template/element binding requires it.

### Dependency injection

`ServiceBootstrapper.BuildServices()` wires a `Microsoft.Extensions.DependencyInjection` container:

- **Singletons:** repositories, scanners, cleanup engine, drives, file deleter, notification service, scheduled task service, duplicate preview service, command runner, Smart Cleaner service
- **ViewModels:** `ScanViewModel` is a singleton so an active scan survives navigation; the other page ViewModels are transient

### Scanner concurrency model

```
Thread: Producer (1)
    BFS walk → Channel<string> (bounded, 1024 capacity)

Thread Pool: Consumers (MaxParallelism, default 4)
    Channel.ReadAllAsync → ProcessDirectory → ConcurrentQueue<FileEntry/FolderEntry>

Thread: Progress Timer
    PeriodicTimer(300ms) → IProgress<ScanProgress>.Report()

UI Thread:
    Program installs DispatcherQueueSynchronizationContext;
    critical scan/cleanup paths also enqueue progress explicitly
```

### Cleanup safety

Interactive deletion requires explicit user intent. Scheduled cleanup instead requires prior, versioned consent which the headless runner revalidates on every run.

1. Generic cleanup rules analyze the database and/or filesystem read-only and produce `CleanupSuggestion` objects.
2. The user reviews suggestions in `CleanupPage`; only Safe/Low items supporting Recycle Bin start selected.
3. `CleanupPage` confirms the selected operation, then calls `CleanupEngine.ExecuteAsync()` → `IFileDeleter`.
4. Smart Cleaner separately analyzes seven allow-listed sources, confirms selected groups, then calls `SmartCleanerService.CleanAsync()` → `IFileDeleter`.
5. Cleanup outcomes are written to `CleanupLog`; audit-write failures are returned as warnings/partial results.

Duplicate cleanup follows the same audit path. Quarantine moves files to a safe directory; the Duplicates page lists all quarantined files with a **Restore** button that moves them back to their original paths.

Duplicate cleanup uses a dedicated recovery journal. It records planned operations before filesystem changes and records completed, quarantined, restored, or failed outcomes afterward. This makes partial failures and crash-restart recovery states inspectable instead of ambiguous. See `docs/public/SAFETY_RECOVERY.md`.

Cleanup dry-run results unlock immediate deletion only when every selected suggestion completed its preview successfully. Partial/failed previews stay non-destructive. Scheduled cleanup is disabled by default when first selected, requires a separate confirmation summarizing target/rules/schedule, persists a versioned plan fingerprint, and is denied by the headless runner when consent is absent, outdated, or no longer matches the plan.

### Benchmarks and visual regression

Performance benchmarks live in `benchmarks/StorageMaster.Benchmarks`:

```powershell
dotnet run -c Release --project benchmarks/StorageMaster.Benchmarks/StorageMaster.Benchmarks.csproj -- --filter *
```

The WinUI visual regression plan is documented in `docs/public/VISUAL_REGRESSION.md`. The xUnit readiness test is intentionally skipped unless an interactive Windows desktop screenshot harness is available.

---

## Cleanup rules

| Rule | Category | Risk | Notes |
|------|----------|------|-------|
| `RecycleBinCleanupRule` | Recycle Bin | Medium | Permanent-only `SHEmptyRecycleBin`; never represented as recoverable |
| `TempFilesCleanupRule` | Temp Files | Low | Canonical `%WINDIR%\Temp` and `%LOCALAPPDATA%\Temp`; redirected `TMP`/`TEMP` is not trusted |
| `DownloadedInstallersRule` | Downloads | Low | Installer exts in Downloads; optional full-folder clear |
| `CacheFolderCleanupRule` | App Caches | Safe–Low | Edge, npm, pip, NuGet, Yarn |
| `BrowserCacheCleanupRule` | Browser Cache | Low | Chrome, Edge, Firefox, Brave, Opera |
| `WindowsUpdateCacheRule` | Windows Update | Low | `SoftwareDistribution\Download` |
| `DeliveryOptimizationRule` | Delivery Opt. | Low | `SoftwareDistribution\DeliveryOptimization` |
| `WindowsErrorReportingRule` | Error Reports | Low | WER folders, crash dumps, `.dmp` files |
| `UninstalledProgramLeftoversRule` | Program Leftovers | High | Heuristic; disabled, unselected, and Recycle-Bin-only by default; 90-day/10 MB thresholds |
| `LargeOldFilesCleanupRule` | Large Old Files | Medium | Per-file suggestions; configurable size and age |
| `ThumbnailCacheRule` | App Caches | Low | `%LOCALAPPDATA%\Microsoft\Windows\Explorer` thumb cache |
| `IconCacheRule` | App Caches | Low | `iconcache*.db` — rebuilt automatically by Explorer |
| `FontCacheRule` | App Caches | Low | Windows font cache service data files |
| `DnsClientCacheRule` | App Caches | Low | Flushes DNS resolver cache via `ipconfig /flushdns` |
| `PrefetchFilesRule` | Temp Files | Medium | `C:\Windows\Prefetch` — rebuilt on next launch |
| `MicrosoftStoreLogsRule` | Log Files | Low | `%LOCALAPPDATA%\Packages\*\LocalState\DiagOutputDir` |

Duplicate deletion is intentionally excluded from the generic Cleanup engine. Use the Duplicates page, which revalidates the keeper and each selected member, journals intent before deletion, and supports quarantine restore.

Only Safe/Low suggestions that declare Recycle Bin support start selected. Medium/High suggestions, including large user files and optional whole-Downloads cleanup, require an explicit per-item selection. Recycle Bin/quarantine figures describe logical bytes moved; allocation is not reclaimed until data is permanently removed.

---

## Database

SQLite with WAL journal mode at `%LOCALAPPDATA%\StorageMaster\storagemaster.db`.

Schema auto-migrates on first launch. Key tables:

| Table | Purpose |
|-------|---------|
| `ScanSessions` | One row per scan run, including the owning process so a scan abandoned by a crash can be told apart from a live one (schema v14) |
| `FileEntries` | One row per file, FK → session, normalized path, nullable stable volume/file identity (schema v12) |
| `FolderEntries` | One row per directory with aggregated sizes and a materialised parent path (schema v13) |
| `ScanErrors` | Per-path errors (access denied, I/O) |
| `CleanupLog` | Append-only deletion audit |
| `Settings` | JSON-serialised `AppSettings` |
| `DuplicateRuns` / `DuplicateGroups` / `DuplicateGroupMembers` | Saved dedupe runs, groups, members, selection state |
| `DuplicateSignatures` | Cached method signatures with source-size/mtime/identity validity metadata |
| `DuplicateErrors` | Per-file dedupe errors and skipped reasons |
| `QuarantinedFiles` | Original-to-quarantine path mapping for restore |
| `DuplicateOperationJournal` | Planned and completed duplicate cleanup/restore operations for recovery inspection |
| `DriveHealthSnapshots` | Per-drive health readings captured from the local Windows storage APIs |

Scheduled jobs are not a table of their own: they live inside the `AppSettings` JSON document alongside their consent fingerprint, and the OS-side trigger lives in Windows Task Scheduler.

Schema migrations run on the first database access after an upgrade, each level applied and stamped in one transaction. To check which level a database is on:

```powershell
sqlite3 "$env:LOCALAPPDATA\StorageMaster\storagemaster.db" "SELECT MAX(Version) FROM SchemaVersion;"
```

<!-- schema-version: 15 -->
The expected value is `DatabaseSchema.CurrentVersion` for the build you are running — schema v15 at the time of writing. A lower number means migration has not run yet; it happens on first database access after an upgrade, not at install time.

Uninstall keeps `%LOCALAPPDATA%\StorageMaster` by default, so the database and settings survive reinstall/upgrade cycles.

---

## Test coverage

```powershell
dotnet test tests/StorageMaster.Tests/StorageMaster.Tests.csproj --verbosity normal
```

Tests cover scanner behaviour, cleanup/deletion safety, deduplication, persistence, folder aggregation, scheduling policy, updater logic, and schema migrations. WinUI page/ViewModel races are build-reviewed and require interactive desktop verification because the test project does not load the WinUI runtime assembly.

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

- [`docs/public/ARCHITECTURE.md`](docs/public/ARCHITECTURE.md) — Architecture reference
- [`docs/public/CODEMAP.md`](docs/public/CODEMAP.md) — Source map
- [`docs/public/DOCUMENTATION.md`](docs/public/DOCUMENTATION.md) — API and configuration reference
- [`docs/public/RELIABILITY_AUDIT_2026-08-18.md`](docs/public/RELIABILITY_AUDIT_2026-08-18.md) — Reliability audit, fixes, evidence, and known limits
- [`CHANGELOG.md`](CHANGELOG.md) — Release history
