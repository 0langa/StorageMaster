# StorageMaster — Architecture Overview

> **Version:** 1.0.0 | **Date:** 2026-04-25 | **Framework:** .NET 10 / WinUI 3

---

## Table of contents

1. [Solution overview](#1-solution-overview)
2. [Dependency graph](#2-dependency-graph)
3. [Layer responsibilities](#3-layer-responsibilities)
4. [Core domain model](#4-core-domain-model)
5. [Scanner architecture](#5-scanner-architecture)
6. [Database architecture](#6-database-architecture)
7. [Cleanup safety system](#7-cleanup-safety-system)
8. [UI architecture](#8-ui-architecture)
9. [Dependency injection wiring](#9-dependency-injection-wiring)
10. [Data flows](#10-data-flows)
11. [Performance design decisions](#11-performance-design-decisions)
12. [Extension points](#12-extension-points)
13. [Known limitations (v1)](#13-known-limitations-v1)

---

## 1. Solution overview

StorageMaster is a **layered, interface-driven Windows desktop utility** whose architecture enforces a strict separation between business logic, platform concerns, persistence, and UI. No business logic exists in XAML code-behind or ViewModels beyond what is needed to bind data and issue commands.

```
┌─────────────────────────────────────────────────────┐
│                   StorageMaster.UI                  │  WinUI 3 / MVVM
│  (Pages, ViewModels, Converters, Navigation)        │
└───────────────────────┬─────────────────────────────┘
                        │ calls via DI interfaces
        ┌───────────────┼───────────────┐
        ▼               ▼               ▼
┌──────────────┐ ┌──────────────┐ ┌────────────────────────┐
│ Core         │ │ Storage      │ │ Platform.Windows        │
│ (scanner,    │ │ (SQLite,     │ │ (Shell32, RecycleBin,   │
│  rules,      │ │  repos,      │ │  file deletion,         │
│  interfaces) │ │  schema)     │ │  drive enumeration)     │
└──────┬───────┘ └──────┬───────┘ └────────────┬───────────┘
       │ defines        │ implements            │ implements
       └────────────────┴───────────────────────┘
                    All implement interfaces in Core
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
```

**Key invariant:** `Core` references nothing in the solution. All platform and persistence details flow inward via interfaces defined in Core. This makes Core fully portable and testable in isolation.

---

## 3. Layer responsibilities

### StorageMaster.Core

The heart of the system. Contains:

| Component | Responsibility |
|-----------|---------------|
| **Models/** | Immutable data transfer records (`FileEntry`, `FolderEntry`, `ScanSession`, etc.) |
| **Interfaces/** | All cross-layer contracts (`IFileScanner`, `ICleanupRule`, `IFileDeleter`, etc.) |
| **Scanner/FileScanner** | Parallel BFS directory walker; writes results via `IScanRepository` |
| **Scanner/FileTypeCategorizor** | Extension → `FileTypeCategory` lookup table |
| **Cleanup/CleanupEngine** | Orchestrates `ICleanupRule` list; delegates execution to `IFileDeleter` |
| **Cleanup/Rules/** | Individual cleanup strategies; pure analysis, never delete |

**What Core does NOT do:** database I/O, file deletion, Win32 calls, UI rendering.

### StorageMaster.Platform.Windows

Windows-specific implementations behind Core interfaces:

| Class | Interface | Notes |
|-------|-----------|-------|
| `FileDeleter` | `IFileDeleter` | `Microsoft.VisualBasic.FileIO` for Recycle Bin; Shell32 P/Invoke for SHEmptyRecycleBin |
| `DriveInfoProvider` | `IDriveInfoProvider` | Wraps `System.IO.DriveInfo`; filters to Fixed/Network/Removable |
| `RecycleBinInfoProvider` | `IRecycleBinInfoProvider` | `SHQueryRecycleBin` via P/Invoke |
| `Shell32Interop` | — | Internal; `LibraryImport` source-generated P/Invoke stubs |

Target framework: `net10.0-windows10.0.19041.0`. Requires `AllowUnsafeBlocks=true` for source-generated P/Invoke.

### StorageMaster.Storage

Pure SQLite persistence:

| Class | Responsibility |
|-------|---------------|
| `StorageDbContext` | Connection lifecycle, WAL setup, schema migration orchestration |
| `DatabaseSchema` | Single source of truth for table DDL and index creation |
| `ScanRepository` | CRUD for `ScanSession`, `FileEntry`, `FolderEntry` |
| `CleanupLogRepository` | Append-only audit log (never deleted by the app) |
| `SettingsRepository` | JSON-serialized `AppSettings` stored as a key-value row |

### StorageMaster.UI

WinUI 3 MVVM application:

| Component | Pattern |
|-----------|---------|
| `App.xaml.cs` | DI container composition root |
| `MainWindow` | `NavigationView` shell with `Frame` host |
| `NavigationService` | `INavigationService` abstraction over `Frame.Navigate` |
| `*ViewModel` | `ObservableObject` + `[ObservableProperty]` + `[RelayCommand]` source-gen |
| `*Page.xaml` | `{x:Bind}` compiled bindings; no logic in code-behind |
| `*Page.xaml.cs` | Only `OnNavigatedTo` lifecycle and event handlers that cannot be commands |
| Converters | `ByteSizeConverter`, `BoolToVisibilityConverter` |

---

## 4. Core domain model

```
ScanSession (1)
    ├── FileEntry[] (N)   ─── identified by SessionId FK
    └── FolderEntry[] (N) ─── identified by SessionId FK

CleanupSuggestion (transient, not persisted in v1)
    └── TargetPaths: string[] ─── paths to be deleted on confirmation

CleanupResult (persisted via CleanupLog)
    └── SuggestionId, BytesFreed, Status, WasDryRun

AppSettings (singleton, persisted as JSON)
```

### Enum relationships

```
FileEntry.Category : FileTypeCategory
    Document | Image | Video | Audio | Archive | Executable
    SourceCode | Database | Temporary | SystemFile | Installer | Log | Cache

CleanupSuggestion.Category : CleanupCategory
    RecycleBin | TempFiles | DownloadedInstallers | CacheFolders
    LargeOldFiles | DuplicateFiles | LogFiles | Custom

CleanupSuggestion.Risk : CleanupRisk
    Safe | Low | Medium | High

ScanSession.Status : ScanStatus
    Running | Completed | Cancelled | Failed
```

---

## 5. Scanner architecture

### High-level flow

```
ScanAsync()
    │
    ├── CreateSessionAsync()               ← persists session row
    │
    ├── ScanState initialized              ← thread-safe counters
    │
    ├── ReportProgressLoopAsync()          ← PeriodicTimer 300ms (background Task)
    │
    ├── ScanDirectoryTreeAsync()
    │       │
    │       ├── ProduceDirectoriesAsync()  ← single producer task
    │       │       BFS queue
    │       │       EnumerateDirectories per level
    │       │       Reparse point check (skip junctions by default)
    │       │       Exclusion list check (prefix match)
    │       │       Channel.WriteAsync(directoryPath)
    │       │       Channel.Complete() when done
    │       │
    │       └── ConsumeDirectoriesAsync() × MaxParallelism
    │               reads from Channel
    │               ProcessDirectory(dir)
    │                   │
    │                   ├── EnumerateFiles() → FileEntry → FileBuffer.Enqueue()
    │                   └── Build FolderEntry → FolderBuffer.Enqueue()
    │               Flush when FileBuffer ≥ DbBatchSize
    │
    ├── Final FlushFileBufferAsync()
    ├── Final FlushFolderBufferAsync()
    │
    └── UpdateSessionAsync(Completed)
```

### Concurrency model

```
Thread: Producer (1)
    BFS walk → Channel<string>

Thread Pool: Consumers (MaxParallelism = 4 default)
    Channel.ReadAllAsync → ProcessDirectory → ConcurrentQueue<FileEntry/FolderEntry>

Thread: Progress Timer
    PeriodicTimer → IProgress<ScanProgress>.Report()

Main Thread (periodic):
    FlushFileBufferAsync() → IScanRepository.InsertFileEntriesAsync()
```

### Channel backpressure

The channel has a bounded capacity of **1024 directories**. If consumers fall behind the producer (e.g., slow SQLite flush), the producer blocks on `WriteAsync`. This prevents unbounded memory growth when scanning very wide directory trees.

### Symlink/junction cycle prevention

```csharp
// Visited set using path normalization (case-insensitive)
var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// Reparse point attribute check (no follow by default)
if (IsReparsePoint(sub) && !options.FollowSymlinks)
    continue;
```

**v1 limitation:** Only path-based deduplication; no NTFS `FileId`/MFT index comparison. Two hard-linked directories with different paths could theoretically both be scanned. Extremely rare in practice on user systems.

### Buffer flush strategy

```
ConcurrentQueue<FileEntry> FileBuffer   → flush every 500 records (DbBatchSize)
ConcurrentQueue<FolderEntry> FolderBuffer → flush every 100 records (DbBatchSize / 5)
```

Separate flush ratios reflect that there are far fewer folders than files. The smaller ratio keeps folder data fresher for the in-flight cleanup rules.

---

## 6. Database architecture

### Schema (v1)

```sql
SchemaVersion     (Version INTEGER, AppliedUtc TEXT)

ScanSessions      (Id PK, RootPath, StartedUtc, CompletedUtc, Status,
                   TotalSizeBytes, TotalFiles, TotalFolders,
                   AccessDeniedCount, ErrorMessage)

FileEntries       (Id PK, SessionId FK, FullPath, FileName, Extension,
                   SizeBytes, CreatedUtc, ModifiedUtc, AccessedUtc,
                   Attributes, Category, IsReparsePoint)

FolderEntries     (Id PK, SessionId FK, FullPath UNIQUE+SessionId,
                   FolderName, DirectSizeBytes, TotalSizeBytes,
                   FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied)

CleanupLog        (Id PK, SuggestionId, RuleId, Title,
                   BytesFreed, WasDryRun, Status, ExecutedUtc, ErrorMessage)

Settings          (Key PK, Value TEXT)
```

### Indexes

```sql
IX_FileEntries_Session_Size  ON FileEntries(SessionId, SizeBytes DESC)
IX_FileEntries_Extension     ON FileEntries(SessionId, Extension)
IX_FolderEntries_Session_Size ON FolderEntries(SessionId, TotalSizeBytes DESC)
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

Migrations run inside a transaction. Future versions add statements to a `V2Statements` array; the runner checks `current < 2` and applies them. Columns are only added, never renamed or dropped without a version bump.

### Bulk insert pattern

```csharp
// Pre-compiled command with named parameters — avoids re-parse overhead
using var cmd = conn.CreateCommand();
cmd.CommandText = "INSERT INTO FileEntries (...) VALUES ($sid, $path, ...);";
var pSid  = cmd.Parameters.Add("$sid",  SqliteType.Integer);
var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
// ...

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

A single transaction wrapping N inserts reduces SQLite's fsync overhead by ~100× versus autocommit mode. On a spinning disk, this is the difference between ~100 inserts/sec and ~50,000 inserts/sec.

---

## 7. Cleanup safety system

This is the most safety-critical part of the application. The entire system is designed so that a file cannot be deleted by accident.

### Three-stage safety model

```
Stage 1: Analysis (read-only)
─────────────────────────────
ICleanupRule.AnalyzeAsync()
    → Reads from IScanRepository
    → Evaluates heuristics
    → Yields CleanupSuggestion objects
    → NEVER touches the filesystem

Stage 2: User Selection (UI)
────────────────────────────
CleanupPage presents SuggestionItem list
    → User ticks/unticks individual items
    → Dry-run checkbox
    → "Clean Up…" button

Stage 3: Confirmation + Execution
──────────────────────────────────
Button click → ContentDialog (modal, must be explicitly confirmed)
    → On Primary button only:
    CleanupEngine.ExecuteAsync()
        → IFileDeleter.DeleteManyAsync()
            → RecycleBin preferred (recoverable)
            → Permanent only if RecycleBin unavailable
            → Every attempt logged to CleanupLog
```

### Protected paths

Rules that could affect system directories implement their own guards:

```csharp
// LargeOldFilesCleanupRule
private static readonly string[] ProtectedPrefixes =
[
    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    Environment.GetFolderPath(Environment.SpecialFolder.System),
    Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
];
```

### Dry-run mode

When `DryRun = true`, `FileDeleter.DeleteAsync()`:
1. Estimates the size of the target
2. Logs the intended action prefixed with `[DryRun]`
3. Returns a successful `DeletionOutcome` with the estimated bytes
4. **Does not touch the filesystem**

This lets users preview the effect of a cleanup operation before committing.

### Audit log

Every `CleanupEngine.ExecuteAsync()` call results in log entries in `CleanupLog` regardless of success or failure. This log is append-only and is never deleted by the application. It provides a durable record of what was deleted and when.

---

## 8. UI architecture

### Navigation model

```
MainWindow
  └── NavigationView (PaneDisplayMode=Left)
        ├── NavigationViewItem: Dashboard   → DashboardPage
        ├── NavigationViewItem: Scan        → ScanPage
        ├── NavigationViewItem: Results     → ResultsPage
        ├── NavigationViewItem: Cleanup     → CleanupPage
        └── SettingsItem                    → SettingsPage
                │
                └── Frame (ContentFrame)
                      NavigationService.NavigateTo(Type)
```

### ViewModel lifecycle

```
Page.OnNavigatedTo()
    → App.Services.GetRequiredService<XxxViewModel>()  [Transient → new instance]
    → ViewModel.LoadAsync() or Initialize()
    → XAML {x:Bind} binds to ViewModel properties
    → Commands update properties → UI reacts via INotifyPropertyChanged
```

### MVVM pattern details

Source generators from CommunityToolkit.Mvvm 8.x:

```csharp
// [ObservableProperty] generates:
//   private string _progressText = string.Empty;
//   public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }
[ObservableProperty] private string _progressText = string.Empty;

// [RelayCommand] generates:
//   public IAsyncRelayCommand StartScanCommand { get; }
//   private async Task StartScanAsync() { ... }
[RelayCommand]
private async Task StartScanAsync() { ... }
```

### Binding strategy

All page bindings use `{x:Bind}` (compiled bindings) rather than `{Binding}` (reflection-based). Compiled bindings:
- Are checked at compile time (fewer runtime errors)
- Perform ~2× faster at runtime
- Support `Mode=OneWay` by default for INotifyPropertyChanged properties

---

## 9. Dependency injection wiring

All DI configuration lives in `App.xaml.cs::BuildServices()`. No service locator pattern is used except in Page constructors where `App.Services.GetRequiredService<T>()` is called (WinUI 3 pages cannot accept constructor parameters from the framework).

```
Singletons (one instance for app lifetime):
────────────────────────────────────────────
StorageDbContext          → manages SQLite connection
ScanRepository            → IScanRepository
CleanupLogRepository      → ICleanupLogRepository
SettingsRepository        → ISettingsRepository
DriveInfoProvider         → IDriveInfoProvider
FileDeleter               → IFileDeleter
RecycleBinInfoProvider    → IRecycleBinInfoProvider
FileScanner               → IFileScanner
RecycleBinCleanupRule     → ICleanupRule (1 of 5)
TempFilesCleanupRule      → ICleanupRule (2 of 5)
DownloadedInstallersRule  → ICleanupRule (3 of 5)
CacheFolderCleanupRule    → ICleanupRule (4 of 5)
LargeOldFilesCleanupRule  → ICleanupRule (5 of 5)
CleanupEngine             → ICleanupEngine
NavigationService         → INavigationService
MainWindow                → (direct singleton)

Transients (new instance per resolve):
───────────────────────────────────────
DashboardViewModel
ScanViewModel
ResultsViewModel
CleanupViewModel
SettingsViewModel
```

**Why ViewModels are Transient:** Each navigation to a page creates a fresh ViewModel with clean state, avoiding stale data from a previous visit. The cost is negligible since ViewModels hold no heavy resources.

---

## 10. Data flows

### Scan flow

```
User clicks "Start Scan"
    → ScanViewModel.StartScanAsync()
    → IFileScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>)
        → StorageDbContext.GetConnectionAsync() [lazy init + migration]
        → IScanRepository.CreateSessionAsync()  [inserts row, returns session]
        → BFS walk → Channel → Workers → ConcurrentQueue
        → Periodic: IScanRepository.InsertFileEntriesAsync(batch)   [bulk insert]
        → Periodic: IScanRepository.UpsertFolderEntriesAsync(batch) [upsert]
        → PeriodicTimer: IProgress<ScanProgress>.Report()           [UI update]
        → IScanRepository.UpdateSessionAsync(Completed)
    → ScanViewModel receives completed ScanSession
    → ScanComplete = true → "View Results" button visible
```

### Results display flow

```
User navigates to Results
    → ResultsPage.OnNavigatedTo(parameter: sessionId)
    → ResultsViewModel.LoadAsync(sessionId)
        → IScanRepository.GetSessionAsync()         [summary row]
        → IScanRepository.GetLargestFilesAsync()    [top 500 files]
        → IScanRepository.GetLargestFoldersAsync()  [top 200 folders]
        → IScanRepository.GetCategoryBreakdownAsync() [GROUP BY category]
    → ObservableCollections updated
    → {x:Bind} refreshes ListView, Pivot, etc.
```

### Cleanup flow

```
User navigates to Cleanup
    → CleanupViewModel.InitializeAsync()
        → IScanRepository.GetRecentSessionsAsync()
    → User clicks "Analyse"
    → ICleanupEngine.GetSuggestionsAsync(sessionId, settings)
        → foreach rule in registered ICleanupRule[]
            → rule.AnalyzeAsync()
                → IScanRepository.GetLargestFilesAsync() or GetLargestFoldersAsync()
                → yield CleanupSuggestion objects
    → Suggestions → ObservableCollection<SuggestionItem>

User selects items → clicks "Clean Up…"
    → ContentDialog shown (blocking)
    → On confirmation:
    → ICleanupEngine.ExecuteAsync(selectedSuggestions, dryRun)
        → IFileDeleter.DeleteManyAsync(requests)
            → SemaphoreSlim (max 4 concurrent)
            → FileSystem.DeleteFile(...RecycleOption.SendToRecycleBin)
            → yield DeletionOutcome
        → ICleanupLogRepository.LogResultAsync()
    → ExecutionResults ObservableCollection updated
```

---

## 11. Performance design decisions

| Decision | Rationale |
|----------|-----------|
| `Channel<string>` with bounded capacity 1024 | Backpressure prevents unlimited memory growth on wide directory trees |
| `MaxParallelism = 4` default | Avoids HDD seek thrashing; on SSDs users should set 8–16 |
| `ConcurrentQueue<FileEntry>` + batch flush | Amortizes SQLite fsync cost; ~100× throughput gain vs. per-file inserts |
| SQLite WAL mode | UI reads never block scanner writes; no reader/writer contention |
| `PRAGMA cache_size=-32000` (32 MB) | Keeps hot indexes in memory across queries |
| `PeriodicTimer(300ms)` for progress | Progress reporting never preempts the scanner's critical path |
| Pre-compiled parameterized commands | Avoids SQL re-parse for every row in batch; significant speedup |
| `volatile string LastScannedPath` | Lock-free last-path tracking across threads |
| `Interlocked.*` for counters | Lock-free counter increments from parallel workers |
| Category index on `FileEntries(SessionId, Extension)` | Supports fast `GROUP BY Category` without full scan |

---

## 12. Extension points

The architecture is designed to be extended without modifying existing code (Open/Closed principle).

### Adding a new cleanup rule

1. Create `class MyRule : ICleanupRule` in Core or a plugin assembly
2. Implement `RuleId`, `DisplayName`, `Category`, `AnalyzeAsync()`
3. Register: `services.AddSingleton<ICleanupRule, MyRule>()`

The `CleanupEngine` discovers all `IEnumerable<ICleanupRule>` from DI automatically.

### Adding a new scan backend (USN Journal, MFT)

1. Create a new class implementing `IFileScanner`
2. Replace or augment the DI registration

`IScanRepository` is unchanged; the new scanner writes the same data model.

### Replacing SQLite with another database

1. Implement `IScanRepository`, `ICleanupLogRepository`, `ISettingsRepository` against the new store
2. Replace the Storage project registrations in `App.xaml.cs`

Core never references `Microsoft.Data.Sqlite` directly.

### Adding a platform target (macOS, Linux)

1. Create a `StorageMaster.Platform.Mac` or `StorageMaster.Platform.Linux` project
2. Implement `IFileDeleter`, `IDriveInfoProvider`, `IRecycleBinInfoProvider`
3. Create a separate UI project (MAUI or Avalonia) for that platform

`Core` and `Storage` are already targeting `net10.0` (not Windows-specific).

---

## 13. Known limitations (v1)

| Area | Limitation | Planned fix |
|------|-----------|-------------|
| **Symlink detection** | Path-based dedup only; no NTFS FileId | v2: use `FILE_ID_INFO` via `GetFileInformationByHandleEx` |
| **Folder sizes** | `TotalSizeBytes = DirectSizeBytes` only; ancestor propagation not implemented | v2: post-scan bottom-up aggregation pass |
| **Duplicate detection** | Interface exists (`DuplicateFiles` category) but no rule | v2: SHA-256 hash-based rule |
| **Downloads path** | Falls back to `%USERPROFILE%\Downloads`; should use `SHGetKnownFolderPath` | Minor; fix in next patch |
| **Error recovery** | Failed mid-batch inserts may lose partial data | v2: per-entry error handling |
| **Category queries** | `GetCategoryBreakdownAsync` does a full session table scan | v2: category-specific index or materialized view |
| **CLI safety gate** | Confirmation dialog is UI-only; CLI callers could bypass | v2: confirmation argument required for headless mode |
| **No scheduled scans** | Manual scan only | v2: Background Windows Service |
| **Placeholder files** | `Class1.cs` in 3 projects | Cleanup task |
