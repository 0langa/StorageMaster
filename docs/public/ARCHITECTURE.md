# StorageMaster — Architecture Overview

> **Version:** 2.2.0 | **Current-state review:** 2026-06-22 | **Framework:** .NET 8 / WinUI 3 / Windows App SDK 1.6
> **v2 update:** StorageMaster now includes the v2 UI foundation, Drive Health & Storage Sentinel, schema v7 health snapshots, stable/prerelease-safe versioning, .NET Desktop Runtime setup checks, elevated CLI deep-scan workers, and GitHub release automation.

---

## Table of contents

1. [Solution overview](#1-solution-overview)
2. [Dependency graph](#2-dependency-graph)
3. [Layer responsibilities](#3-layer-responsibilities)
4. [Core domain model](#4-core-domain-model)
5. [Scanner architecture](#5-scanner-architecture)
6. [Turbo Scanner (Rust backend)](#6-turbo-scanner-rust-backend)
7. [Smart Cleaner architecture](#7-smart-cleaner-architecture)
8. [Database architecture](#8-database-architecture)
9. [Cleanup safety system](#9-cleanup-safety-system)
10. [UI architecture](#10-ui-architecture)
11. [Dependency injection wiring](#11-dependency-injection-wiring)
12. [Data flows](#12-data-flows)
13. [Performance design decisions](#13-performance-design-decisions)
14. [Extension points](#14-extension-points)
15. [Known limitations](#15-known-limitations-v17)

---

## 1. Solution overview

StorageMaster is a **layered, interface-driven Windows desktop utility** whose architecture enforces a strict separation between business logic, platform concerns, persistence, and UI. No business logic exists in XAML code-behind or ViewModels beyond what is needed to bind data and issue commands.

```
┌─────────────────────────────────────────────────────┐
│                   StorageMaster.UI                  │  WinUI 3 / MVVM / unpackaged
│  (Pages, ViewModels, Converters, Navigation)        │
└───────────────────────┬─────────────────────────────┘
                        │ calls via DI interfaces
        ┌───────────────┼───────────────┐
        ▼               ▼               ▼
┌──────────────┐ ┌──────────────┐ ┌────────────────────────────────────┐
│ Core         │ │ Storage      │ │ Platform.Windows                   │
│ (scanner,    │ │ (SQLite,     │ │ (Shell32, RecycleBin, elevation,   │
│  rules,      │ │  repos,      │ │  drive enum, InstalledPrograms,    │
│  interfaces) │ │  schema)     │ │  TurboFileScanner)                 │
└──────┬───────┘ └──────┬───────┘ └──────────────┬──────────────────────┘
       │ defines        │ implements              │ implements
       └────────────────┴─────────────────────────┘
                All implement interfaces in Core

                ┌──────────────────┐
                │  turbo-scanner   │  Rust binary (jwalk)
                │  (subprocess)    │  ← spawned by TurboFileScanner
                └──────────────────┘
```

---

## 2. Dependency graph

```
StorageMaster.Core
    (no project references — pure domain)

StorageMaster.Storage
    → StorageMaster.Core

StorageMaster.Platform.Windows
    → StorageMaster.Core

StorageMaster.UI
    → StorageMaster.Core
    → StorageMaster.Storage
    → StorageMaster.Platform.Windows

StorageMaster.Tests
    → StorageMaster.Core
    → StorageMaster.Storage
    → StorageMaster.Platform.Windows

turbo-scanner  (Rust crate — independent binary)
    jwalk 0.8, serde, serde_json, clap
```

**Key invariant:** `Core` references nothing in the solution. All platform and persistence details flow inward via interfaces defined in Core. This makes Core fully portable and testable in isolation.

---

## 3. Layer responsibilities

### StorageMaster.Core

The heart of the system. Contains:

| Component | Responsibility |
|-----------|---------------|
| **Models/** | Immutable data records (`FileEntry`, `FolderEntry`, `ScanSession`, `ScanProgress`, `CleanupSuggestion`, `CleanupResult`, `AppSettings`, `ScanError`, `ScheduledJobDefinition`, `DuplicatePreviewResult`, `QuarantinedFile`, `DuplicateOperationJournalEntry`, `DriveHealthSnapshot`) |
| **Interfaces/** | All cross-layer contracts (`IFileScanner`, `ICleanupRule`, `IFileDeleter`, `ISmartCleanerService`, `IInstalledProgramProvider`, `IScheduledTaskService`, `IDuplicatePreviewService`, `ICommandRunner`, `INotificationService`, etc.) |
| **Scanner/FileScanner** | Parallel BFS directory walker; writes results via `IScanRepository` |
| **Scanner/FileTypeCategorizor** | Extension → `FileTypeCategory` lookup (80+ mappings) |
| **Scanner/FolderSizeAggregator** | Post-scan bottom-up folder size propagation |
| **Scanner/ScanScopeResolver** | Builds excluded-path list from settings for both shallow and deep scans |
| **Cleanup/CleanupEngine** | Orchestrates `ICleanupRule` list; delegates execution to `IFileDeleter` |
| **Cleanup/Rules/** | 17 cleanup strategies; pure analysis, never delete |
| **SmartCleaner/SmartCleanerService** | Direct junk scan without session; implements `ISmartCleanerService` |

**What Core does NOT do:** database I/O, file deletion, Win32 calls, UI rendering, subprocess spawning.

### StorageMaster.Platform.Windows

Windows-specific implementations behind Core interfaces:

| Class | Interface | Notes |
|-------|-----------|-------|
| `FileDeleter` | `IFileDeleter` | Batch `IFileOperation` for Recycle Bin; fail-closed, reparse-safe permanent deletion |
| `DriveInfoProvider` | `IDriveInfoProvider` | Wraps `System.IO.DriveInfo`; filters to Fixed/Network/Removable |
| `RecycleBinInfoProvider` | `IRecycleBinInfoProvider` | `SHQueryRecycleBin` P/Invoke |
| `AdminService` | `IAdminService` | `IsRunningAsAdmin`, `RestartAsAdmin(enableDeepScan)` |
| `InstalledProgramProvider` | `IInstalledProgramProvider` | Registry HKLM+HKCU uninstall keys (32+64 bit); used by leftovers rule |
| `TurboFileScanner` | `IFileScanner` | Spawns `turbo-scanner.exe`; parses JSONL; falls back to `FileScanner` |
| `KnownFolders` | — | Static helper; `GetDownloadsPath` via `SHGetKnownFolderPath` |
| `Shell32Interop` | — | Internal; `LibraryImport` P/Invoke (`SHFileOperation`, `SHEmptyRecycleBin`, `SHQueryRecycleBin`, `SHGetKnownFolderPath`) |

Target framework: `net8.0-windows10.0.19041.0`. Requires `AllowUnsafeBlocks=true` for source-generated P/Invoke.

### StorageMaster.Storage

Pure SQLite persistence:

| Class | Responsibility |
|-------|---------------|
| `StorageDbContext` | Connection lifecycle, WAL setup, schema migration orchestration |
| `DatabaseSchema` | Single source of truth for table DDL and index creation |
| `ScanRepository` | CRUD for `ScanSession`, `FileEntry`, `FolderEntry`; folder total updates |
| `ScanErrorRepository` | Per-path scan error logging and retrieval |
| `CleanupLogRepository` | Append-only audit log |
| `SettingsRepository` | JSON-serialized `AppSettings` as a key-value row |

Schema v8 adds `DuplicateOperationJournal`, an append/update recovery ledger for duplicate cleanup and quarantine restore. Duplicate deletion writes intent before filesystem operations and records final state afterward, so interrupted or partial duplicate operations can be inspected after restart.

### StorageMaster.UI

WinUI 3 MVVM application (unpackaged, `WindowsPackageType=None`). The v2 shell uses grouped `NavigationView` sections, Mica Alt when available, a global status strip, shared style dictionaries under `StorageMaster.UI/Styles`, and reusable controls under `StorageMaster.UI/Controls`. `ScanWorkspacePage` centralizes persisted scan context while existing Results, Duplicates, Cleanup, Space Map, and Drive Health pages remain directly routable.

| Component | Pattern |
|-----------|---------|
| `Program.cs` | Entry point; routes `--cli` / `--headless` to `CommandRunner`, otherwise launches WinUI app |
| `ServiceBootstrapper.cs` | DI container wiring; all singletons and transients registered here |
| `App.xaml.cs` | `OnLaunched` activates `MainWindow`; startup arg flags (`StartInTray`, `StartWithDeepScan`) |
| `MainWindow` | grouped `NavigationView` shell + `Frame` host; Mica fallback; global status; DPI-aware sizing; tray icon; low-disk monitor |
| `NavigationService` | `INavigationService` abstraction over `Frame.Navigate` |
| `*ViewModel` | `ObservableObject` + `[ObservableProperty]` + `[RelayCommand]` source-gen |
| `*Page.xaml` | `{x:Bind}` compiled bindings; no logic in code-behind |
| Converters | `ByteSizeConverter`, `BoolToVisibilityConverter`, `BoolNegationConverter`, `FilePathToBitmapImageConverter` |
| `Infrastructure/CommandRunner` | CLI dispatcher; scan/report/dedupe/cleanup/health/jobs commands; structured `CommandLineException` exit codes |
| `Infrastructure/DesktopNotificationService` | Event-based notification hub; consumed by `MainWindow` for tray balloons |
| `Infrastructure/DuplicatePreviewService` | Builds rich preview items per duplicate method; FFmpeg keyframe extraction for video |
| `Infrastructure/ScheduledTaskService` | CRUD wrapper around `schtasks.exe`; safe `/TR` argument construction |
| `Infrastructure/StartupRegistrationService` | Adds/removes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry |

### turbo-scanner (Rust)

A standalone Rust binary (`turbo-scanner.exe`) that enumerates the file system using the **jwalk** work-stealing thread pool. It is invoked as a hidden subprocess by `TurboFileScanner`. It has no knowledge of the C# application — it simply writes JSONL to stdout and exits. The integration seam is entirely owned by `TurboFileScanner.cs`.

---

## 4. Core domain model

```
ScanSession (1)
    ├── FileEntry[] (N)       ─── identified by SessionId FK
    ├── FolderEntry[] (N)     ─── identified by SessionId FK
    └── ScanError[] (N)       ─── identified by SessionId FK

CleanupSuggestion (transient, not persisted)
    └── TargetPaths: string[] ─── paths to be deleted on confirmation

CleanupResult (persisted via CleanupLog)
    └── SuggestionId, BytesFreed, Status, WasDryRun

SmartCleanGroup (transient, returned by ISmartCleanerService.AnalyzeAsync)
    └── Category, Description, IconGlyph, EstimatedBytes, Paths[]

AppSettings (singleton, persisted as JSON in Settings table)
```

### Key enums

```
FileTypeCategory (14 values)
    Unknown | Document | Image | Video | Audio | Archive | Executable
    SourceCode | Database | Temporary | SystemFile | Installer | Log | Cache

CleanupCategory (13 values)
    RecycleBin | TempFiles | DownloadedInstallers | CacheFolders | LargeOldFiles
    DuplicateFiles | LogFiles | Custom | BrowserCache | WindowsUpdateCache
    ProgramLeftovers | DeliveryOptimization | WindowsErrorReporting

CleanupRisk (4 values)
    Safe | Low | Medium | High

ScanStatus (4 values)
    Running | Completed | Cancelled | Failed
```

---

## 5. Scanner architecture

### High-level flow (managed `FileScanner`)

```
ScanAsync()
    │
    ├── CreateSessionAsync()                    ← persists session row
    │
    ├── ScanState initialized                   ← thread-safe counters
    │
    ├── ReportProgressLoopAsync()               ← PeriodicTimer 300ms (background Task)
    │
    ├── ScanDirectoryTreeAsync()
    │       │
    │       ├── ProduceDirectoriesAsync()       ← single producer task
    │       │       BFS queue → Channel<string> (bounded 1024)
    │       │       Skips reparse points unless FollowSymlinks=true
    │       │       Skips excluded paths (case-insensitive prefix match)
    │       │
    │       └── ConsumeDirectoriesAsync() × MaxParallelism
    │               Reads from Channel
    │               ProcessDirectory(dir)
    │                   ├── EnumerateFiles() → FileEntry → FileBuffer.Enqueue()
    │                   └── Build FolderEntry → FolderBuffer.Enqueue()
    │               Flush when FileBuffer ≥ DbBatchSize (500)
    │               Flush when FolderBuffer ≥ DbBatchSize/5 (100)
    │
    ├── Final flush of both buffers
    ├── FolderSizeAggregator.Compute() + UpdateFolderTotalsAsync()
    └── UpdateSessionAsync(Completed)
```

### Concurrency model

```
Thread: Producer (1)
    BFS walk → Channel<string>

Thread Pool: Consumers (MaxParallelism, default 4)
    Channel.ReadAllAsync → ProcessDirectory → ConcurrentQueue<FileEntry/FolderEntry>

Thread: Progress Timer
    PeriodicTimer(300ms) → IProgress<ScanProgress>.Report()

UI Thread (via DispatcherQueue.TryEnqueue):
    Progress applied to ViewModel ObservableProperties
    (No SynchronizationContext in unpackaged WinUI 3; DispatcherQueue used explicitly)
```

### Channel backpressure

The channel has a bounded capacity of **1024 directories**. If consumers fall behind the producer (slow SQLite flush), the producer blocks on `WriteAsync`. This prevents unbounded memory growth on wide directory trees.

### Folder size aggregation

After all folder entries are flushed, `FolderSizeAggregator.Compute()` does a single-pass bottom-up tree walk using the stored `FullPath` hierarchy: for each folder, `TotalSizeBytes = DirectSizeBytes + sum(children.TotalSizeBytes)`. Results are bulk-applied via `IScanRepository.UpdateFolderTotalsAsync()` in a single transaction.

---

## 6. Turbo Scanner (Rust backend)

### Motivation

The managed `FileScanner` makes one Win32 `FindFirstFile`/`FindNextFile` call per directory entry. On a modern SSD with 500K files, this takes 15–30 seconds. The Rust `turbo-scanner` binary uses **jwalk**'s work-stealing Rayon thread pool, which parallelizes directory traversal across all CPU cores — typically 3–5× faster on SSDs and 2× faster on HDDs.

### Data flow

```
C# TurboFileScanner.ScanAsync()
    │
    ├── CreateSessionAsync()           ← same as managed scanner
    │
    ├── ProcessStartInfo("turbo-scanner.exe")
    │       --path <rootPath>
    │       --threads <MaxParallelism>
    │       RedirectStandardOutput = true
    │       CreateNoWindow = true      ← completely invisible
    │
    ├── Task.Run: ReadLineAsync() loop
    │       JsonSerializer.Deserialize<TurboRecord>(line)
    │           ├── IsDir=true  → FolderEntry → folderBuffer
    │           └── IsDir=false → FileEntry  → fileBuffer
    │               FileTypeCategorizor.Categorize(ext)
    │       Flush fileBuffer every 500 records
    │       Flush folderBuffer every 100 records
    │       IProgress<ScanProgress>.Report() every 300ms
    │
    ├── WaitForExitAsync()
    │
    ├── FolderSizeAggregator.Compute() + UpdateFolderTotalsAsync()
    │
    └── UpdateSessionAsync(Completed)
```

### JSONL format (turbo-scanner stdout)

```json
{"path":"C:\\Users\\Alice\\file.txt","size":12345,"modified_unix":1700000000,"created_unix":1690000000,"is_dir":false}
{"path":"C:\\Users\\Alice\\Documents","size":0,"modified_unix":1700000000,"created_unix":1690000000,"is_dir":true}
```

### Fallback behaviour

If `turbo-scanner.exe` is not present in `AppContext.BaseDirectory` (common in local debug builds), `TurboFileScanner` logs a warning and immediately delegates to the managed `FileScanner`. The caller (`ScanViewModel`) is unaware — it receives a `ScanSession` either way.

### Stderr handling

Turbo Scanner writes errors and warnings (access denied, I/O failures) to stderr as plain text prefixed with `WARN:`. The C# host drains stderr on a background task and logs each line at `Debug` level. This prevents the subprocess from blocking on a full stderr pipe.

---

## 7. Smart Cleaner architecture

The Smart Cleaner (`ISmartCleanerService` → `SmartCleanerService`) provides a scan-and-clean path that does **not** require a prior database scan session. It scans junk locations directly on the filesystem (without writing to the database) and returns `SmartCleanGroup` objects grouped by category.

### Analysis flow

```
SmartCleanerService.AnalyzeAsync()
    │
    ├── Scan each junk source independently (parallel or sequential):
    │       %TEMP%                      → Temp Files group
    │       Browser cache dirs          → Browser Cache group
    │       SoftwareDistribution\Download → Windows Update group
    │       WER report dirs             → Error Reports group
    │       DeliveryOptimization dirs   → Delivery Optimization group
    │       %LOCALAPPDATA%\Temp         → Thumbnail Cache / Shader Cache
    │       Shell:RecycleBinFolder      → Recycle Bin group
    │
    ├── For each junk source: enumerate files, sum bytes, collect paths
    │
    └── Return IReadOnlyList<SmartCleanGroup>
            Category, Description, IconGlyph, EstimatedBytes, Paths[]
```

### Cleanup flow

```
SmartCleanerService.CleanAsync(groups, method, progress)
    │
    ├── For each selected SmartCleanGroup:
    │       Build DeletionRequest(path, method, dryRun=false)
    │
    └── IFileDeleter.DeleteManyAsync(requests)
            → DeletionOutcome per path
```

**Key difference from session-based cleanup:** The Smart Cleaner does not write to `FileEntries` or `FolderEntries`. It does not create a `ScanSession`. It uses the same `IFileDeleter` (with Recycle Bin support) and therefore benefits from the same safety system.

---

## 8. Database architecture

### Schema (v1.3)

```sql
SchemaVersion     (Version INTEGER, AppliedUtc TEXT)

ScanSessions      (Id PK, RootPath, StartedUtc, CompletedUtc, Status,
                   TotalSizeBytes, TotalFiles, TotalFolders,
                   AccessDeniedCount, ErrorMessage)

FileEntries       (Id PK, SessionId FK→CASCADE, FullPath, FileName, Extension,
                   SizeBytes, CreatedUtc, ModifiedUtc, AccessedUtc,
                   Attributes, Category, IsReparsePoint)

FolderEntries     (Id PK, SessionId FK→CASCADE, FullPath UNIQUE+SessionId,
                   FolderName, DirectSizeBytes, TotalSizeBytes,
                   FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied)

ScanErrors        (Id PK, SessionId FK→CASCADE, Path, ErrorType, Message, OccurredUtc)

CleanupLog        (Id PK, SuggestionId, RuleId, Title,
                   BytesFreed, WasDryRun, Status, ExecutedUtc, ErrorMessage)

Settings          (Key PK, Value TEXT)
```

### Indexes

```sql
IX_FileEntries_Session_Size    ON FileEntries(SessionId, SizeBytes DESC)
IX_FileEntries_Extension       ON FileEntries(SessionId, Extension)
IX_FolderEntries_Session_Size  ON FolderEntries(SessionId, TotalSizeBytes DESC)
```

These indexes directly serve the most common queries: top-N largest files and folders per session.

### SQLite configuration

```sql
PRAGMA journal_mode=WAL;      -- readers never block writers
PRAGMA synchronous=NORMAL;    -- balance durability vs. speed
PRAGMA foreign_keys=ON;       -- cascade deletes enforce referential integrity
PRAGMA temp_store=MEMORY;     -- temp tables stay in RAM
PRAGMA cache_size=-32000;     -- 32 MB page cache
```

WAL mode is critical: it allows the UI to read results from an in-progress scan session without blocking the scanner's write stream.

### Migration strategy

```
SchemaVersion.Version = 0 (table missing)  →  apply V1Statements  →  Version = 1
```

Migrations run inside a transaction. Future versions add a `V2Statements` array; the runner checks `current < 2` and applies them. Columns are only ever added — never renamed or dropped without a version bump.

### Batch insert pattern

```csharp
using var tx = await conn.BeginTransactionAsync(ct);
foreach (var e in entries)
{
    pSid.Value  = e.SessionId;
    pPath.Value = e.FullPath;
    // ...
    await cmd.ExecuteNonQueryAsync(ct);
}
await tx.CommitAsync(ct);
```

A single transaction wrapping N inserts reduces SQLite's fsync overhead by ~100× versus autocommit mode.

---

## 9. Cleanup safety system

This is the most safety-critical part of the application. The design ensures a file cannot be deleted by accident.

App-created temporary recursive deletes use a guarded `%TEMP%` direct-child sentinel so diagnostics and video-hash cleanup cannot accidentally target arbitrary directories.

### Three-stage safety model

```
Stage 1: Analysis (read-only)
─────────────────────────────
ICleanupRule.AnalyzeAsync()  /  SmartCleanerService.AnalyzeAsync()
    → Reads filesystem / database
    → Evaluates heuristics
    → Yields CleanupSuggestion / SmartCleanGroup objects
    → NEVER touches the filesystem destructively

Stage 2: User Selection (UI)
────────────────────────────
CleanupPage / SmartCleanerPage presents item list
    → User ticks/unticks individual categories
    → Deletion method toggle (RecycleBin vs. Permanent)
    → "Clean" button (enabled only when items are selected)

Stage 3: Confirmation + Execution
──────────────────────────────────
Button click → ContentDialog (modal, must be explicitly confirmed)
    → On Primary button only:
    ICleanupEngine.ExecuteAsync() / ISmartCleanerService.CleanAsync()
        → IFileDeleter.DeleteManyAsync()
            → RecycleBin by default (recoverable)
            → Batch SHFileOperation for RecycleBin efficiency
            → Every attempt logged to CleanupLog
```

### Protected paths

Rules that could affect system directories implement their own guards:

```csharp
// LargeOldFilesCleanupRule, UninstalledProgramLeftoversRule
private static readonly string[] ProtectedPrefixes =
[
    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    Environment.GetFolderPath(Environment.SpecialFolder.System),
    Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
];
```

`UninstalledProgramLeftoversRule` additionally maintains a safelist of known system folder names (e.g., `Microsoft`, `Common Files`, `Windows NT`) and applies a 90-day inactivity threshold and 10 MB minimum size.

### Batch deletion (SHFileOperation)

`FileDeleter.DeleteManyAsync()` submits an all-Recycle-Bin batch through one `IFileOperation` call, then verifies each source path disappeared so silent shell skips become per-path failures. Permanent directory deletion removes reparse points as links and refuses recursion when attributes cannot be classified safely.

### Audit log

Every `CleanupEngine.ExecuteAsync()` and `SmartCleanerService.CleanAsync()` call results in `CleanupLog` rows regardless of success or failure. This log is append-only and is never deleted by the application.

---

## 10. UI architecture

### Navigation model

```
MainWindow
  └── NavigationView (PaneDisplayMode=Left)
        ├── NavigationViewItem: Dashboard     → DashboardPage
        ├── NavigationViewItem: Scan          → ScanPage
        ├── NavigationViewItem: Results       → ResultsPage
        ├── NavigationViewItem: Cleanup       → CleanupPage
        ├── NavigationViewItem: Smart Cleaner → SmartCleanerPage
        └── SettingsItem                      → SettingsPage
                │
                └── Frame (ContentFrame)
                      NavigationService.NavigateTo(Type)
```

### Window sizing

On startup, `MainWindow` uses `DisplayArea.GetFromWindowId()` to get the physical-pixel work area of the primary monitor, then sizes the window to 85% width × 90% height, clamped between 900×700 and the full work area. This ensures the window is always appropriately sized regardless of DPI scaling.

### ViewModel lifecycle

```
Page.OnNavigatedTo()
    → App.Services.GetRequiredService<XxxViewModel>()  [Transient → new instance]
    → ViewModel.LoadAsync() or InitializeAsync()
    → XAML {x:Bind} binds to ViewModel properties
    → Commands update properties → UI reacts via INotifyPropertyChanged
```

### Binding strategy

All page bindings use `{x:Bind}` (compiled bindings) rather than `{Binding}` (reflection-based):
- Checked at compile time (fewer runtime surprises)
- ~2× faster at runtime
- `Mode=OneWay` default for `INotifyPropertyChanged` properties

### Progress marshalling (no SynchronizationContext)

Unpackaged WinUI 3 apps do not install a `SynchronizationContext`. This means `Progress<T>` callbacks execute on the thread pool, not the UI thread. All ViewModels capture `DispatcherQueue.GetForCurrentThread()` before starting background work and use `dq.TryEnqueue(Apply)` inside progress callbacks.

---

## 11. Dependency injection wiring

All DI configuration lives in `ServiceBootstrapper.BuildServices()`.

```
Singletons (one instance for app lifetime):
────────────────────────────────────────────
StorageDbContext               → manages SQLite connection
IScanRepository                → ScanRepository
IScanErrorRepository           → ScanErrorRepository
ICleanupLogRepository          → CleanupLogRepository
IDuplicateRepository           → DuplicateRepository
ISettingsRepository            → SettingsRepository
ILocalDiagnosticsService       → LocalDiagnosticsService
IDriveInfoProvider             → DriveInfoProvider
IFileDeleter                   → FileDeleter
IRecycleBinInfoProvider        → RecycleBinInfoProvider
IAdminService                  → AdminService
IInstalledProgramProvider      → InstalledProgramProvider
FileScanner                    → concrete singleton (managed scanner)
TurboFileScanner               → concrete singleton (Rust-backed; wraps FileScanner as fallback)
IFileScanner                   → FileScanner (default; ScanViewModel selects turbo at runtime)
ICleanupRule (×17)             → all 17 rules in registration order
ICleanupEngine                 → CleanupEngine
ISmartCleanerService           → SmartCleanerService
IDuplicateFinderService        → DuplicateFinderService
IDuplicatePreviewService       → DuplicatePreviewService
IScheduledTaskService          → ScheduledTaskService
INotificationService           → DesktopNotificationService
ICommandRunner                 → CommandRunner
StartupRegistrationService     → (concrete; no interface)
DesktopNotificationService     → (concrete; also satisfies INotificationService)
INavigationService             → NavigationService
MainWindow                     → singleton window

Transients (new instance per resolve):
───────────────────────────────────────
DashboardViewModel
ScanViewModel     ← factory lambda; injects FileScanner + TurboFileScanner explicitly
ResultsViewModel
CleanupViewModel
DuplicatesViewModel
SettingsViewModel
SmartCleanerViewModel
```

**Why ScanViewModel is Singleton:** The scan operation owns a long-running `CancellationTokenSource` and must survive page navigation. All other ViewModels are Transient (new instance per navigation → clean state).

### CLI dispatch

`Program.Main()` runs before WinUI starts. When `--cli` or `--headless` is present, it allocates (or attaches) a console, calls `ICommandRunner.RunAsync()`, and exits — the WinUI application is never created. This keeps the GUI and the CLI entirely separate at runtime. Deep scans use this CLI path when elevation is required, so the WinUI shell stays unelevated.

### Tray and background

`MainWindow` owns a `TaskbarIcon` (H.NotifyIcon.WinUI). When `MinimizeToTray` is enabled, the `AppWindowClosing` event is cancelled and the window is hidden instead. A `DispatcherQueueTimer` running every 15 minutes drives low-disk checks; results are dispatched as `NotificationRaised` events from `DesktopNotificationService`, which `MainWindow` converts to tray balloon notifications with a 12-hour per-drive-per-level debounce.

### Scheduler

`ScheduledTaskService` wraps `schtasks.exe` to create, update, and delete Windows Task Scheduler entries. Each scheduled job triggers `StorageMaster.UI.exe --headless jobs run --id <job-id>`. The `/TR` value uses inner `\"` escaping for paths with spaces so `CommandLineToArgvW` parses arguments correctly.

---

## 12. Data flows

### Scan flow (managed)

```
User clicks "Start Scan"
    → ScanViewModel.StartScanAsync()
    → activeScanner = UseTurboScanner ? _turboScanner : _scanner
    → IFileScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>)
        → IScanRepository.CreateSessionAsync()
        → BFS walk → Channel → Workers → ConcurrentQueue
        → IScanRepository.InsertFileEntriesAsync(batch)    [every 500 files]
        → IScanRepository.UpsertFolderEntriesAsync(batch)  [every 100 folders]
        → IProgress<ScanProgress>.Report() → DispatcherQueue → ViewModel
        → FolderSizeAggregator.Compute() + UpdateFolderTotalsAsync()
        → IScanRepository.UpdateSessionAsync(Completed)
    → ScanComplete = true → "View Results" button visible
```

### Turbo scan flow (additional steps)

```
TurboFileScanner.ScanAsync()
    → ProcessStartInfo("turbo-scanner.exe --path ... --threads ...")
    → Task.Run: ReadLineAsync() → JSON.Deserialize<TurboRecord>()
        → FileEntry / FolderEntry → fileBuffer / folderBuffer
        → Flush buffers → IScanRepository (same as above)
    → WaitForExitAsync()
    → FolderSizeAggregator (same as above)
```

### Smart Cleaner flow

```
User clicks "Scan & Analyse"
    → SmartCleanerViewModel.AnalyseAsync()
    → ISmartCleanerService.AnalyzeAsync(progress)
        → Enumerate junk locations directly on filesystem
        → Return IReadOnlyList<SmartCleanGroup>
    → Groups → ObservableCollection<SmartCleanGroupItem>

User clicks "Clean Selected"
    → SmartCleanerViewModel.CleanAsync()
    → ISmartCleanerService.CleanAsync(selectedGroups, method, progress)
    → IFileDeleter.DeleteManyAsync(requests)
    → FreedText, StatusText updated
```

### Results display flow

```
User navigates to Results (parameter: sessionId)
    → ResultsViewModel.LoadAsync(sessionId)
        → IScanRepository.GetSessionAsync()
        → IScanRepository.GetLargestFilesAsync()    [top 500]
        → IScanRepository.GetLargestFoldersAsync()  [top 200]
        → IScanRepository.GetCategoryBreakdownAsync()
        → IScanErrorRepository.GetErrorsForSessionAsync()
    → ObservableCollections updated → {x:Bind} refreshes
```

---

## 13. Performance design decisions

| Decision | Rationale |
|----------|-----------|
| `Channel<string>` bounded at 1024 | Backpressure prevents unlimited memory on wide trees |
| `MaxParallelism = 4` default | Avoids HDD seek thrashing; SSDs benefit from 8–16 |
| `ConcurrentQueue<FileEntry>` + batch flush | ~100× throughput gain over per-file inserts |
| SQLite WAL mode | UI reads never block scanner writes |
| `PRAGMA cache_size=-32000` (32 MB) | Keeps hot indexes in memory |
| `PeriodicTimer(300ms)` for progress | Progress reporting never preempts the scanner |
| Pre-compiled parameterized SQL commands | Avoids re-parse overhead per row in bulk inserts |
| `volatile`/`Interlocked` for counters | Lock-free from parallel workers |
| Rust + jwalk for Turbo Scanner | Work-stealing across all cores; I/O-bound parallelism better than managed |
| Batch `IFileOperation` for Recycle Bin | One shell operation for all paths, followed by per-path outcome verification |
| Bottom-up `FolderSizeAggregator` | Correct folder totals in one O(n) pass after scan completes |

---

## 14. Extension points

### Adding a new cleanup rule

1. Create `class MyRule : ICleanupRule` in `Core/Cleanup/Rules/`
2. Implement `RuleId`, `DisplayName`, `Category`, `AnalyzeAsync()`
3. Register: `services.AddSingleton<ICleanupRule, MyRule>()`
4. Add a corresponding `CleanupCategoryOption` entry in `CleanupViewModel.BuildCategoryOptions()`

The `CleanupEngine` discovers all `IEnumerable<ICleanupRule>` from DI automatically.

### Adding a new scan backend

1. Create a class implementing `IFileScanner`
2. Register it alongside `FileScanner` and `TurboFileScanner`
3. Augment `ScanViewModel` to select it based on user preference

`IScanRepository` is unchanged; the new scanner writes the same data model.

### Adding a new page

1. Create `MyPage.xaml` + `MyPage.xaml.cs` in `Pages/`
2. Create `MyViewModel : ObservableObject` in `Pages/`
3. Register `services.AddTransient<MyViewModel>()`
4. Add a `NavigationViewItem` to `MainWindow.xaml`
5. Add a `case "MyPage":` to the `NavView_SelectionChanged` switch

---

## 15. Known limitations (v1.7)

| Area | Limitation | Notes |
|------|-----------|-------|
| **Symlink detection** | Path-based dedup only; no NTFS FileId deduplication | Use `FILE_ID_INFO` via `GetFileInformationByHandleEx` in a future version |
| **Turbo Scanner folders** | Folder `DirectSizeBytes` not populated by jwalk | Mitigated by `FolderSizeAggregator` post-pass |
| **Visualization scope** | Native Space Map treemap exists with CSV/HTML/PNG export; no WebView2/D3 dependency | Continue with scale polish and richer reports |
| **Localization** | English only | v2.0 |
| **Smart Cleaner log** | Smart Cleaner cleanup uses `IFileDeleter` directly; not routed through `CleanupEngine` | `SmartCleanerService` writes synthetic results to `CleanupLog` |
| **pHash threshold changes** | Image/video pHash similarity thresholds require app restart to take effect (singleton snapshot at startup) | Known trade-off; settings UI notes this |
| **FFmpeg for video previews** | Video preview keyframes require user to configure FFmpeg path in Settings | Clear guidance shown in UI when FFmpeg is absent |
