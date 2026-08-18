# StorageMaster — Architecture Overview

> **Version:** 2.2.1 | **Current-state review:** 2026-08-18 | **Framework:** .NET 8 / WinUI 3 / Windows App SDK 1.6
> **Current-state note:** StorageMaster uses schema v12, independent pooled SQLite connection leases, persisted scan-time file identity, a managed/Rust scanner boundary, dedicated duplicate recovery, and fail-closed deletion guards. See `RELIABILITY_AUDIT_2026-08-18.md` for verified hardening and remaining limits.

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
15. [Known limitations](#15-known-limitations)

---

## 1. Solution overview

StorageMaster is a **layered, interface-driven Windows desktop utility** separating domain/services, Windows-specific operations, SQLite persistence, and WinUI presentation. XAML code-behind handles UI lifecycle, dialogs, navigation, and control events; ViewModels coordinate presentation state and commands.

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

**Key invariant:** `Core` has no project reference to UI, Storage, or Platform.Windows. Platform and persistence implementations flow inward via interfaces defined in Core, while Core retains generic filesystem/process responsibilities.

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
| **Cleanup/Rules/** | 16 registered cleanup strategies; pure analysis, never delete |
| **SmartCleaner/SmartCleanerService** | Direct junk scan without session; implements `ISmartCleanerService` |

**What Core does NOT do directly:** SQLite access, Win32 calls, or UI rendering. Core does contain portable filesystem scanning, update staging, duplicate restore orchestration, and generic subprocess orchestration behind abstractions; Windows-specific deletion, identity, trust, and shell behavior remains in `Platform.Windows`.

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
| `Shell32Interop` / `FileOperationInterop` | — | Shell P/Invoke plus COM `IFileOperation`; Recycle Bin batches require `FOFX_RECYCLEONDELETE` |

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

Schema v8 adds `DuplicateOperationJournal`; v9 adds generic-quarantine restore support; v10 repairs the quarantine-member foreign key; v11 normalizes folder identity and collapses Windows case variants conservatively; v12 persists nullable volume/file identity for scan rows. Historical identity-less rows remain readable for analysis but cannot authorize scan-backed deletion until rescanned. Duplicate deletion writes intent before filesystem operations and records final state afterward, so interrupted or partial duplicate operations can be inspected after restart.

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
| `*Page.xaml` / code-behind | `{x:Bind}` plus template/element `{Binding}`; lifecycle, dialogs, navigation, and control events only |
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
    ├── FileEntry[] (N)       ─── SessionId FK + optional stable FileIdentity
    ├── FolderEntry[] (N)     ─── identified by SessionId FK
    └── ScanError[] (N)       ─── identified by SessionId FK

CleanupSuggestion (transient, not persisted)
    └── TargetPaths: string[] ─── absolute paths or recognized execution sentinels

CleanupResult (persisted via CleanupLog)
    └── SuggestionId, BytesFreed, Status, WasDryRun

SmartCleanGroup (transient, returned by ISmartCleanerService.AnalyzeAsync)
    └── Source, Category, EstimatedBytes, explicit Paths[], ExpectedFileSnapshots

AppSettings (one persisted JSON document; loaded/snapshotted through SettingsRepository)
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

After folder entries are flushed, `FolderSizeAggregator.Compute()` normalizes paths, sorts deepest-first, and propagates each accumulated direct size to its parent. Results are bulk-applied via `IScanRepository.UpdateFolderTotalsAsync()` in one repository transaction.

---

## 6. Turbo Scanner (Rust backend)

### Motivation

The managed `FileScanner` uses .NET directory enumeration with a bounded producer/consumer worker pool. The Rust `turbo-scanner` binary uses **jwalk**'s work-stealing Rayon thread pool to parallelize native directory traversal. Actual speed depends on storage, filesystem, exclusions, and directory shape; no fixed multiplier is guaranteed.

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

Contract v3 keeps legacy Unix-second fields and adds exact Windows UTC ticks, raw attributes, hidden state, and stable file identity:

```json
{"path":"C:\\Users\\Alice\\file.txt","size":12345,"modified_unix":1700000000,"created_unix":1690000000,"modified_utc_ticks":638355968000000000,"created_utc_ticks":638269568000000000,"attributes":32,"volume_serial":305419896,"file_index":123456789,"is_dir":false,"is_hidden":false}
{"path":"C:\\Users\\Alice\\Documents","size":0,"modified_unix":1700000000,"created_unix":1690000000,"modified_utc_ticks":638355968000000000,"created_utc_ticks":638269568000000000,"attributes":16,"volume_serial":null,"file_index":null,"is_dir":true,"is_hidden":false}
```

### Fallback behaviour

If `turbo-scanner.exe` is not present in `AppContext.BaseDirectory` (common in local debug builds), `TurboFileScanner` logs a warning and immediately delegates to the managed `FileScanner`. The caller (`ScanViewModel`) is unaware — it receives a `ScanSession` either way.

### Stderr handling

Turbo Scanner writes errors and warnings (access denied, I/O failures) to stderr as plain text prefixed with `WARN:`. The C# host drains stderr on a background task, logs each line at `Debug`, and persists warning/error lines as scan errors when a repository is available. This prevents the subprocess from blocking on a full stderr pipe.

---

## 7. Smart Cleaner architecture

The Smart Cleaner (`ISmartCleanerService` → `SmartCleanerService`) provides a scan-and-clean path that does **not** require a prior database scan session. It scans junk locations directly on the filesystem (without writing to the database) and returns `SmartCleanGroup` objects grouped by category.

### Analysis flow

```
SmartCleanerService.AnalyzeAsync()
    │
    ├── Scan each junk source sequentially:
    │       %WINDIR%\Temp + %LOCALAPPDATA%\Temp → Temp Files group
    │       Browser cache dirs          → Browser Cache group
    │       SoftwareDistribution\Download → Windows Update group
    │       WER report dirs             → Error Reports group
    │       DeliveryOptimization dirs   → Delivery Optimization group
    │       Explorer thumbcache files   → Thumbnail Cache group
    │       %LOCALAPPDATA%\D3DSCache    → Shader Cache group
    │
    ├── Hold strong no-follow boundary/ancestor handles where available
    │   (weak fallback is reported as partial and cannot authorize cleanup)
    ├── Capture size/time/attributes/stable identity for execution-time revalidation
    ├── Return path-specific warnings for inaccessible or unsafe source branches
    │
    └── Return SmartCleanAnalysisResult
            Groups[] + Warnings[] + IsPartial
```

### Cleanup flow

```
SmartCleanerService.CleanAsync(groups, method, progress)
    │
    ├── For each frozen selected SmartCleanGroup and path:
    │       Validate canonical path beneath source allow-list
    │       Acquire no-follow ancestry lease
    │       Require matching analysis-time identity snapshot
    │       Build DeletionRequest(path, method, ExpectedSnapshot)
    │
    └── IFileDeleter.DeleteAsync(request), one validated lease at a time
            → DeletionOutcome per path
            → SmartCleanResult with processed/reclaimed bytes, cancellation,
              per-path failures, and audit warnings
```

**Key difference from session-based cleanup:** The Smart Cleaner does not write to `FileEntries` or `FolderEntries`. It does not create a `ScanSession`. It uses the same `IFileDeleter` (with Recycle Bin support) and therefore benefits from the same safety system.

---

## 8. Database architecture

### Schema (v12 current)

```sql
SchemaVersion     (Version INTEGER, AppliedUtc TEXT)

ScanSessions      (Id PK, RootPath, StartedUtc, CompletedUtc, Status,
                   TotalSizeBytes, TotalFiles, TotalFolders,
                   AccessDeniedCount, ErrorMessage)

FileEntries       (Id PK, SessionId FK→CASCADE, FullPath, FileName, Extension,
                   SizeBytes, CreatedUtc, ModifiedUtc, AccessedUtc,
                   Attributes, Category, IsReparsePoint,
                   IdentityVolumeSerial NULL, IdentityFileIndex NULL)

FolderEntries     (Id PK, SessionId FK→CASCADE, FullPath, NormalizedFullPath,
                   FolderName, DirectSizeBytes, TotalSizeBytes,
                   FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied,
                   UNIQUE(SessionId, NormalizedFullPath))

ScanErrors        (Id PK, SessionId FK→CASCADE, Path, ErrorType, Message, OccurredAt)

CleanupLog        (Id PK, SuggestionId, RuleId, Title,
                   BytesFreed, WasDryRun, Status, ExecutedUtc, ErrorMessage,
                   AuditDataJson)

Settings          (Key PK, Value TEXT)
```

### Indexes

```sql
IX_FileEntries_Session_Size    ON FileEntries(SessionId, SizeBytes DESC)
IX_FileEntries_Extension       ON FileEntries(SessionId, Extension)
UX_FileEntries_Session_NormalizedFullPath ON FileEntries(SessionId, NormalizedFullPath)
IX_FolderEntries_Session_Size  ON FolderEntries(SessionId, TotalSizeBytes DESC)
UX_FolderEntries_Session_NormalizedFullPath ON FolderEntries(SessionId, NormalizedFullPath)
```

These are primary scan-query and identity indexes; `DatabaseSchema.cs` defines additional duplicate, quarantine, journal, health, and path indexes.

### SQLite configuration

```sql
PRAGMA journal_mode=WAL;      -- readers and a writer normally proceed concurrently
PRAGMA synchronous=NORMAL;    -- balance durability vs. speed
PRAGMA foreign_keys=ON;       -- cascade deletes enforce referential integrity
PRAGMA temp_store=MEMORY;     -- temp tables stay in RAM
PRAGMA cache_size=-32000;     -- 32 MB page cache
```

WAL mode normally allows the UI to read committed results while a scan writes. SQLite still serializes writers, and schema/checkpoint activity can impose waits.

### Migration strategy

`StorageDbContext` obtains an immediate SQLite writer reservation, re-reads `SchemaVersion` inside that reservation, then applies every missing version through v12 and stamps each version in the same transaction. Some migrations rebuild tables to change constraints, so migrations are not append-column-only.

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

A single transaction wrapping N inserts avoids per-row autocommit and substantially reduces SQLite transaction overhead.

---

## 9. Cleanup safety system

This is the most safety-critical part of the application. The design layers user intent, path policy, reparse checks, live metadata/identity checks where available, recoverable deletion, and per-path outcomes. It reduces risk but does not claim that filesystem races are impossible.

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
    → Cleanup dry-run follow-up is enabled only after every selected preview result succeeds completely

Stage 3: Confirmation + Execution
──────────────────────────────────
Button click → ContentDialog (modal, must be explicitly confirmed)
    → On Primary button only:
    ICleanupEngine.ExecuteAsync() → IFileDeleter.DeleteManyAsync()
    ISmartCleanerService.CleanAsync() → one lease-held IFileDeleter.DeleteAsync() per path
        → RecycleBin by default when the suggestion/source supports it
        → Batch IFileOperation for generic all-Recycle-Bin batches
    Orchestrator attempts CleanupLog audit; write failure is returned as warning/partial result

Scheduled CleanupExecuteSafe:
    → separate versioned plan consent, revalidated by headless policy
    → Safe/Low + SupportsRecycleBin suggestions only; no live dialog required
```

### Protected paths

Rules use category-specific canonical roots and policy flags. `UninstalledProgramLeftoversRule` only inspects top-level LocalAppData, Roaming AppData, and ProgramData children, skips reparse points, scans descendants for recent activity without following links, applies a safelist plus 90-day/10 MB thresholds, and remains disabled/unselected/high-risk/Recycle-Bin-only by default.

### Batch deletion (`IFileOperation`)

`FileDeleter.DeleteManyAsync()` submits an all-Recycle-Bin batch through one `IFileOperation` call, then verifies each source path disappeared so silent shell skips become per-path failures. Permanent directory deletion removes reparse points as links and refuses recursion when attributes cannot be classified safely.

### Audit log

`CleanupEngine.ExecuteAsync()` and `SmartCleanerService.CleanAsync()` attempt to persist success, partial success, failure, cancellation, and dry-run outcomes. A database-write failure is surfaced to the caller; filesystem work is not repeated merely to recreate a missing audit row.

---

## 10. UI architecture

### Navigation model

```
MainWindow
  └── NavigationView (PaneDisplayMode=Left)
        ├── NavigationViewItem: Dashboard     → DashboardPage
        ├── NavigationViewItem: Scan          → ScanPage
        ├── NavigationViewItem: Scan Workspace→ ScanWorkspacePage
        ├── NavigationViewItem: Results       → ResultsPage
        ├── NavigationViewItem: Space Map     → SpaceMapPage
        ├── NavigationViewItem: Duplicates    → DuplicatesPage
        ├── NavigationViewItem: Cleanup       → CleanupPage
        ├── NavigationViewItem: Smart Cleaner → SmartCleanerPage
        ├── NavigationViewItem: Drive Health  → DriveHealthPage
        └── SettingsItem                      → SettingsPage
                │
                └── Frame (ContentFrame)
                      NavigationService.NavigateTo(Type)
```

### Window sizing

On startup, `MainWindow` uses `DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)` to get the window-associated physical-pixel work area (primary as fallback), then requests 85% width × 90% height, clamped between 900×700 and the available work area.

### ViewModel lifecycle

```
Page.OnNavigatedTo()
    → App.Services.GetRequiredService<XxxViewModel>()
      [most page ViewModels transient; ScanViewModel singleton]
    → ViewModel.LoadAsync() or InitializeAsync()
    → XAML {x:Bind} binds to ViewModel properties
    → Commands update properties → UI reacts via INotifyPropertyChanged
```

### Binding strategy

Pages prefer `{x:Bind}` for page/ViewModel members and use `{Binding}` inside data templates or for element bindings. `{x:Bind}` defaults to `OneTime`; changing properties explicitly request `Mode=OneWay` or `Mode=TwoWay`.

### Progress marshalling

Before creating `App`, `Program.Main()` installs a `DispatcherQueueSynchronizationContext` for the UI thread. `Progress<T>` instances created on that context post callbacks back to it. Scan, Cleanup, and Smart Cleaner also capture the current `DispatcherQueue` and explicitly enqueue progress updates as a defensive boundary.

---

## 11. Dependency injection wiring

All DI configuration lives in `ServiceBootstrapper.BuildServices()`.

```
Singletons (one instance for app lifetime):
────────────────────────────────────────────
StorageDbContext               → initialization/migration and independent connection leases
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
IFileIdentityProvider          → FileIdentityProvider
INoFollowFileEnumerator        → NoFollowFileEnumerator
FileScanner                    → concrete singleton (managed scanner)
TurboFileScanner               → concrete singleton (Rust-backed; wraps FileScanner as fallback)
IFileScanner                   → FileScanner (default; ScanViewModel selects turbo at runtime)
ICleanupRule (×16)             → registered session-cleanup rules in registration order
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
ScanViewModel                  → singleton; owns long-running scan cancellation/lifetime

Transients (new instance per resolve):
───────────────────────────────────────
DashboardViewModel
ScanWorkspaceViewModel
ResultsViewModel
CleanupViewModel
DuplicatesViewModel
SettingsViewModel
SmartCleanerViewModel
SpaceMapViewModel
DriveHealthViewModel
```

**Why ScanViewModel is Singleton:** The scan operation owns a long-running `CancellationTokenSource` and must survive page navigation. All other ViewModels are Transient (new instance per navigation → clean state).

### CLI dispatch

`Program.Main()` runs before WinUI starts. When `--cli` or `--headless` is present, it allocates (or attaches) a console, calls `ICommandRunner.RunAsync()`, and exits — the WinUI application is never created. This keeps the GUI and the CLI entirely separate at runtime. Deep scans use this CLI path when elevation is required, so the WinUI shell stays unelevated.

### Tray and background

`MainWindow` owns a `TaskbarIcon` (H.NotifyIcon.WinUI). When `MinimizeToTray` is enabled, the `AppWindowClosing` event is cancelled and the window is hidden instead. A `DispatcherQueueTimer` running every 15 minutes drives low-disk checks; results are dispatched as `NotificationRaised` events from `DesktopNotificationService`, which `MainWindow` converts to tray balloon notifications with a 12-hour per-drive-per-level debounce.

### Scheduler

`ScheduledTaskService` wraps `schtasks.exe` to create, update, and delete Windows Task Scheduler entries. Each scheduled job triggers `StorageMaster.UI.exe --headless jobs run --id <job-id>`. Task identities are preflighted before mutation; OS-task/settings failures trigger compensating rollback, and incomplete rollback is surfaced. Enabled unattended cleanup also requires a dedicated UI confirmation plus the current consent version and plan fingerprint; the headless runner refuses missing, outdated, or plan-mismatched consent and applies Safe/Low Recycle-Bin-only selection. The `/TR` value uses Windows-compatible argument quoting for executable paths and job IDs.

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
    → validate/guard root ancestry without following links
    → ProcessStartInfo("turbo-scanner.exe --path ... --threads ...")
    → Task.Run: ReadLineAsync() → JSON.Deserialize<TurboRecord>()
        → exact UTC ticks + raw attributes + stable file identity
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
        → Enumerate junk locations with boundary-held no-follow guards
        → Return SmartCleanAnalysisResult(groups, warnings)
    → Groups → ObservableCollection<SmartCleanGroupItem>

User clicks "Clean Selected"
    → SmartCleanerViewModel.CleanAsync()
    → ISmartCleanerService.CleanAsync(selectedGroups, method, progress)
    → validate one file under a held ancestry lease
    → IFileDeleter.DeleteAsync(request)
    → processed/moved/reclaimed wording and exact partial status updated
```

### Results display flow

```
User navigates to Results (parameter: sessionId)
    → ResultsViewModel.LoadAsync(sessionId)
        → IScanRepository.GetSessionAsync()
        → IScanRepository.SearchFilesAsync()        [200-row pages]
        → IScanRepository.SearchFoldersAsync()      [100-row pages]
        → IScanRepository.GetCategoryBreakdownAsync()
        → IScanErrorRepository.GetErrorsPageForSessionAsync() [100-row pages]
    → ObservableCollections updated → {x:Bind} refreshes
```

---

## 13. Performance design decisions

| Decision | Rationale |
|----------|-----------|
| `Channel<string>` bounded at 1024 | Backpressure prevents unlimited memory on wide trees |
| `MaxParallelism = 4` default | Avoids HDD seek thrashing; SSDs benefit from 8–16 |
| `ConcurrentQueue<FileEntry>` + batch flush | Reduces transaction and command overhead versus per-file autocommit |
| SQLite WAL mode | Usually allows readers to observe committed rows while scanning; writers still serialize and checkpoints/schema work can wait |
| `PRAGMA cache_size=-32000` (32 MB) | Keeps hot indexes in memory |
| `PeriodicTimer(300ms)` for progress | Rate-limits managed UI progress work relative to the scan loop |
| Pre-compiled parameterized SQL commands | Avoids re-parse overhead per row in bulk inserts |
| `volatile`/`Interlocked` for counters | Lock-free from parallel workers |
| Rust + jwalk for Turbo Scanner | Work-stealing at configured parallelism; actual managed/native performance depends on filesystem and storage |
| Batch `IFileOperation` for Recycle Bin | One shell operation for all paths, followed by per-path outcome verification |
| Bottom-up `FolderSizeAggregator` | Deterministic deepest-first aggregation in O(n log n) time after enumeration/final flush |

---

## 14. Extension points

### Adding a new cleanup rule

1. Create `class MyRule : ICleanupRule` in `Core/Cleanup/Rules/`
2. Implement `RuleId`, `DisplayName`, `Category`, `AnalyzeAsync()`
3. Register: `services.AddSingleton<ICleanupRule, MyRule>()`
4. Add a corresponding `CleanupCategoryOption` through `CleanupViewModel.BuildCategoryGroups()` / `AddItem(...)`

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

## 15. Known limitations

| Area | Limitation | Notes |
|------|-----------|-------|
| **Scanner boundary races** | Turbo holds strong no-follow ancestor locks where Windows permits them; ACL-protected ancestors and queued descendants retain a narrow same-privilege swap window | Persisted identity and downstream snapshot/no-follow deletion fail closed; see reliability audit |
| **Turbo Scanner folders** | Native records do not carry aggregate folder totals | Host computes and persists direct/subtree metrics after enumeration |
| **Visualization scope** | Native Space Map treemap exists with CSV/HTML/PNG export; no WebView2/D3 dependency | Continue with scale polish and richer reports |
| **Localization** | English only | Future localization work |
| **Smart Cleaner log** | Smart Cleaner cleanup uses `IFileDeleter` directly; not routed through `CleanupEngine` | `SmartCleanerService` writes synthetic results to `CleanupLog` |
| **pHash threshold changes** | A duplicate-analysis run uses one settings snapshot for consistency | Saved threshold changes apply to the next analysis, not an already-running one |
| **FFmpeg for video previews** | Video preview/keyframe support requires both `ffmpeg.exe` and `ffprobe.exe` | Resolver checks the configured executable, bundled app tools, then PATH; guidance appears when absent |
