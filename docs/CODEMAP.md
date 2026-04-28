# StorageMaster — Codemap

> **Version:** 1.3.0 | **Date:** 2026-04-28
> Quick-reference for every file, type, method, and database table in the project.

---

## Table of contents

- [Solution files](#solution-files)
- [StorageMaster.Core](#storagemastercore)
  - [Models](#models)
  - [Interfaces](#interfaces)
  - [Scanner](#scanner)
  - [Cleanup](#cleanup)
  - [SmartCleaner](#smartcleaner)
- [StorageMaster.Platform.Windows](#storagemasterplatformwindows)
- [StorageMaster.Storage](#storagemasterstorage)
- [StorageMaster.UI](#storagemasterui)
- [turbo-scanner (Rust)](#turbo-scanner-rust)
- [StorageMaster.Tests](#storagemastertests)
- [Database schema](#database-schema)
- [NuGet packages](#nuget-packages)
- [Build targets](#build-targets)

---

## Solution files

| File | Purpose |
|------|---------|
| `StorageMaster.sln` | Solution descriptor linking all 5 projects |
| `StorageMaster.slnx` | New-format solution file |
| `global.json` | Pins SDK to 8.0.x; rollForward=latestPatch |
| `README.md` | Quick-start, build instructions, architecture summary |
| `docs/ARCHITECTURE.md` | Deep architecture reference |
| `docs/CODEMAP.md` | This file |
| `docs/DOCUMENTATION.md` | Full API and configuration reference |
| `docs/ROADMAP.md` | v1.3 → v1.5 development plan |
| `.github/workflows/release.yml` | CI/CD: test → Rust build → publish → installer → Release |
| `installer/StorageMaster.iss` | Inno Setup 6 script (admin-required installer) |
| `turbo-scanner/Cargo.toml` | Rust crate manifest |
| `turbo-scanner/src/main.rs` | Turbo Scanner entry point |

---

## StorageMaster.Core

**Project file:** `src/StorageMaster.Core/StorageMaster.Core.csproj`
**Target:** `net8.0`
**Packages:** `CommunityToolkit.Mvvm 8.4.0`, `Microsoft.Extensions.DI.Abstractions 10.0.0`, `Microsoft.Extensions.Logging.Abstractions 10.0.0`

---

### Models

#### `FileEntry` — `Models/FileEntry.cs`

Immutable `record` representing one file discovered during a scan.

| Member | Type | Notes |
|--------|------|-------|
| `Id` | `long` | DB primary key (0 before insert) |
| `SessionId` | `long` | FK → `ScanSession.Id` |
| `FullPath` | `string` | Absolute path |
| `FileName` | `string` | `Path.GetFileName(FullPath)` |
| `Extension` | `string` | Including dot (`.mp4`) |
| `SizeBytes` | `long` | File size in bytes |
| `CreatedUtc` | `DateTime` | UTC creation time |
| `ModifiedUtc` | `DateTime` | UTC last-write time |
| `AccessedUtc` | `DateTime` | UTC last-access time |
| `Attributes` | `FileAttributes` | From `FileInfo.Attributes` |
| `Category` | `FileTypeCategory` | Mapped by `FileTypeCategorizor` |
| `IsReparsePoint` | `bool` | True if accessed via symlink/junction |
| `ParentPath` | `string` (computed) | `Path.GetDirectoryName(FullPath)` |

---

#### `FolderEntry` — `Models/FolderEntry.cs`

Aggregated size record for one directory.

| Member | Type | Notes |
|--------|------|-------|
| `Id` | `long` | DB primary key |
| `SessionId` | `long` | FK → ScanSession |
| `FullPath` | `string` | Absolute directory path |
| `FolderName` | `string` | `Path.GetFileName(FullPath)` |
| `DirectSizeBytes` | `long` | Sum of files directly in this dir |
| `TotalSizeBytes` | `long` | DirectSizeBytes + all descendants (after aggregation pass) |
| `FileCount` | `int` | Count of files directly in this dir |
| `SubFolderCount` | `int` | Count of immediate subdirectories |
| `IsReparsePoint` | `bool` | Dir is a junction/symlink |
| `WasAccessDenied` | `bool` | True if UnauthorizedAccessException was thrown |
| `ParentPath` | `string?` (computed) | `Path.GetDirectoryName(FullPath)` |

---

#### `ScanSession` — `Models/ScanSession.cs`

Root object for a complete scan run.

| Member | Type | Notes |
|--------|------|-------|
| `Id` | `long` | DB primary key |
| `RootPath` | `string` | Scanned root (e.g. `C:\`) |
| `StartedUtc` | `DateTime` | When scan began |
| `CompletedUtc` | `DateTime?` | Null while Running |
| `Status` | `ScanStatus` | Running / Completed / Cancelled / Failed |
| `TotalSizeBytes` | `long` | Sum of all file sizes |
| `TotalFiles` | `long` | Total files found |
| `TotalFolders` | `long` | Total folders scanned |
| `AccessDeniedCount` | `long` | Paths that threw UnauthorizedAccess |
| `ErrorMessage` | `string?` | Set on Failed status |
| `Duration` | `TimeSpan?` (computed) | CompletedUtc - StartedUtc |

**Enum `ScanStatus`:** `Running`, `Completed`, `Cancelled`, `Failed`

---

#### `ScanProgress` — `Models/ScanProgress.cs`

Progress snapshot emitted every ~300ms via `IProgress<T>`.

| Member | Type |
|--------|------|
| `CurrentPath` | `string` |
| `FilesScanned` | `long` |
| `FoldersScanned` | `long` |
| `BytesScanned` | `long` |
| `ErrorCount` | `int` |
| `IsComplete` | `bool` |
| `Timestamp` | `DateTime` (default = UtcNow) |

---

#### `ScanOptions` — `Models/ScanOptions.cs`

Controls scan behaviour. Passed to `IFileScanner.ScanAsync`.

| Member | Default | Purpose |
|--------|---------|---------|
| `RootPath` | `""` | Required: path to scan |
| `MaxParallelism` | `4` | Concurrent directory workers |
| `DbBatchSize` | `500` | Flush to DB every N files |
| `ExcludedPaths` | (see below) | Case-insensitive prefix exclusions |
| `FollowSymlinks` | `false` | Follow reparse points |
| `DeepScan` | `false` | When true, excludes nothing and uses all CPU cores |

`DefaultExcludedPaths`: `C:\Windows\WinSxS`, `C:\Windows\Installer`

---

#### `ScanError` — `Models/ScanError.cs`

One per-path error recorded during a scan.

| Member | Type |
|--------|------|
| `Id` | `long` |
| `SessionId` | `long` |
| `Path` | `string` |
| `ErrorType` | `string` (e.g. "UnauthorizedAccess") |
| `Message` | `string` |
| `OccurredUtc` | `DateTime` |

---

#### `CleanupSuggestion` — `Models/CleanupSuggestion.cs`

One actionable cleanup recommendation. Produced by `ICleanupRule`.

| Member | Type | Notes |
|--------|------|-------|
| `Id` | `Guid` | Unique per suggestion |
| `RuleId` | `string` | Stable identifier, e.g. `"core.temp-files"` |
| `Title` | `string` | Short display name |
| `Description` | `string` | Human-readable detail |
| `Category` | `CleanupCategory` | Grouping enum |
| `Risk` | `CleanupRisk` | Safe / Low / Medium / High |
| `EstimatedBytes` | `long` | Expected bytes freed |
| `TargetPaths` | `IReadOnlyList<string>` | Paths to delete on confirmation |
| `IsSystemPath` | `bool` | UI warning flag |

---

#### `CleanupResult` — `Models/CleanupResult.cs`

Outcome of executing one `CleanupSuggestion`.

| Member | Type |
|--------|------|
| `SuggestionId` | `Guid` |
| `Status` | `CleanupResultStatus` |
| `BytesFreed` | `long` |
| `ExecutedUtc` | `DateTime` |
| `WasDryRun` | `bool` |
| `FailedPaths` | `IReadOnlyList<string>` |
| `ErrorMessage` | `string?` |

**Enum `CleanupResultStatus`:** `Success`, `PartialSuccess`, `Failed`, `Skipped`

---

#### `AppSettings` — `Models/AppSettings.cs`

Persisted user preferences. Serialized as JSON to SQLite.

| Property | Default | Purpose |
|----------|---------|---------|
| `PreferRecycleBin` | `true` | Send files to Recycle Bin |
| `DryRunByDefault` | `false` | Preview without deleting |
| `LargeFileSizeMb` | `500` | Threshold for LargeOldFiles rule |
| `OldFileAgeDays` | `365` | Age threshold for LargeOldFiles rule |
| `DefaultScanPath` | `C:\` | Pre-filled in Scan page |
| `ScanParallelism` | `4` | Concurrent workers |
| `ShowHiddenFiles` | `false` | Include hidden files (reserved) |
| `SkipSystemFolders` | `true` | Skip Windows dirs unless DeepScan |
| `ExcludedPaths` | `[]` | Custom path exclusions |
| `UseTurboScanner` | `false` | Use Rust-backed scanner |
| `CleanRecycleBin` | `true` | Enable RecycleBin rule |
| `CleanTempFiles` | `true` | Enable TempFiles rule |
| `CleanDownloadedInstallers` | `true` | Enable DownloadedInstallers rule |
| `ClearEntireDownloads` | `false` | Clear entire Downloads folder |
| `CleanCacheFolders` | `true` | Enable CacheFolders rule |
| `CleanBrowserCache` | `true` | Enable BrowserCache rule |
| `CleanWindowsUpdateCache` | `true` | Enable WindowsUpdateCache rule |
| `CleanDeliveryOptimization` | `true` | Enable DeliveryOptimization rule |
| `CleanWindowsErrorReports` | `true` | Enable WindowsErrorReporting rule |
| `CleanProgramLeftovers` | `false` | Enable UninstalledProgramLeftovers rule (medium risk) |
| `CleanLargeOldFiles` | `false` | Enable LargeOldFiles rule (medium risk) |

---

#### `CleanupCategory` — `Models/CleanupCategory.cs`

Enum (13 values): `RecycleBin`, `TempFiles`, `DownloadedInstallers`, `CacheFolders`, `LargeOldFiles`, `DuplicateFiles`, `LogFiles`, `Custom`, `BrowserCache`, `WindowsUpdateCache`, `ProgramLeftovers`, `DeliveryOptimization`, `WindowsErrorReporting`

---

#### `FileTypeCategory` — `Models/FileTypeCategory.cs`

Enum (14 values): `Unknown`, `Document`, `Image`, `Video`, `Audio`, `Archive`, `Executable`, `SourceCode`, `Database`, `Temporary`, `SystemFile`, `Installer`, `Log`, `Cache`

---

#### `CleanupProgress` — `Models/CleanupProgress.cs`

Progress snapshot for cleanup operations.

| Member | Type |
|--------|------|
| `CurrentPath` | `string` |
| `FilesDeleted` | `long` |
| `BytesFreed` | `long` |
| `IsComplete` | `bool` |

---

### Interfaces

#### `IFileScanner` — `Interfaces/IFileScanner.cs`

```csharp
Task<ScanSession> ScanAsync(ScanOptions, IProgress<ScanProgress>, CancellationToken)
IAsyncEnumerable<FileEntry> GetLargestFilesAsync(long sessionId, int topN, CancellationToken)
IAsyncEnumerable<FolderEntry> GetLargestFoldersAsync(long sessionId, int topN, CancellationToken)
```

---

#### `ICleanupRule` — `Interfaces/ICleanupRule.cs`

```csharp
string RuleId { get; }
string DisplayName { get; }
CleanupCategory Category { get; }
IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(long sessionId, AppSettings, CancellationToken)
```

---

#### `ICleanupEngine` — `Interfaces/ICleanupEngine.cs`

```csharp
IAsyncEnumerable<CleanupSuggestion> GetSuggestionsAsync(long sessionId, AppSettings, CancellationToken)
Task<IReadOnlyList<CleanupResult>> ExecuteAsync(IReadOnlyList<CleanupSuggestion>, bool dryRun, CancellationToken)
```

---

#### `ISmartCleanerService` — `Interfaces/ISmartCleanerService.cs`

```csharp
Task<IReadOnlyList<SmartCleanGroup>> AnalyzeAsync(IProgress<string>? progress, CancellationToken)
Task<long> CleanAsync(IReadOnlyList<SmartCleanGroup>, DeletionMethod, IProgress<string>?, CancellationToken)

record SmartCleanGroup(
    string Category, string Description, string IconGlyph,
    long EstimatedBytes, IReadOnlyList<string> Paths, bool IsSelected = true)
```

---

#### `IScanRepository` — `Interfaces/IScanRepository.cs`

```csharp
Task<ScanSession>  CreateSessionAsync(string rootPath, CancellationToken)
Task<ScanSession?> GetSessionAsync(long sessionId, CancellationToken)
Task<IReadOnlyList<ScanSession>> GetRecentSessionsAsync(int count, CancellationToken)
Task UpdateSessionAsync(ScanSession, CancellationToken)
Task InsertFileEntriesAsync(IReadOnlyList<FileEntry>, CancellationToken)
Task UpsertFolderEntriesAsync(IReadOnlyList<FolderEntry>, CancellationToken)
Task<IReadOnlyList<FileEntry>> GetLargestFilesAsync(long sessionId, int topN, CancellationToken)
Task<IReadOnlyList<FolderEntry>> GetLargestFoldersAsync(long sessionId, int topN, CancellationToken)
Task<IReadOnlyDictionary<FileTypeCategory,(long Count, long Bytes)>> GetCategoryBreakdownAsync(long sessionId, CancellationToken)
Task DeleteSessionAsync(long sessionId, CancellationToken)
Task<IReadOnlyList<(string FullPath, long DirectSizeBytes)>> GetAllFolderPathsForSessionAsync(long sessionId, CancellationToken)
Task UpdateFolderTotalsAsync(long sessionId, IReadOnlyDictionary<string,long> totals, CancellationToken)
```

---

#### `IScanErrorRepository` — `Interfaces/IScanErrorRepository.cs`

```csharp
Task LogErrorsAsync(long sessionId, IReadOnlyList<ScanError> errors, CancellationToken)
Task<IReadOnlyList<ScanError>> GetErrorsForSessionAsync(long sessionId, CancellationToken)
```

---

#### `IFileDeleter` — `Interfaces/IFileDeleter.cs`

```csharp
record DeletionRequest(string Path, DeletionMethod Method, bool DryRun)
record DeletionOutcome(string Path, bool Success, long BytesFreed, string? Error)
enum DeletionMethod { RecycleBin, Permanent }

Task<DeletionOutcome> DeleteAsync(DeletionRequest, CancellationToken)
IAsyncEnumerable<DeletionOutcome> DeleteManyAsync(IReadOnlyList<DeletionRequest>, CancellationToken)
```

---

#### `IDriveInfoProvider` — `Interfaces/IDriveInfoProvider.cs`

```csharp
record DriveDetail(string Name, string VolumeLabel, string DriveFormat,
                   long TotalBytes, long FreeBytes, long UsedBytes, bool IsReady)

IReadOnlyList<DriveDetail> GetAvailableDrives()
DriveDetail? GetDrive(string rootPath)
```

---

#### `IAdminService` — `Interfaces/IAdminService.cs`

```csharp
bool IsRunningAsAdmin { get; }
void RestartAsAdmin(bool enableDeepScan)
```

---

#### `IInstalledProgramProvider` — `Interfaces/IInstalledProgramProvider.cs`

```csharp
record InstalledProgramInfo(string DisplayName, string? InstallLocation, DateTime? InstallDate)

IReadOnlyList<InstalledProgramInfo> GetInstalledPrograms()
```

---

#### `ICleanupLogRepository` — `Interfaces/ICleanupLogRepository.cs`

```csharp
record CleanupLogEntry { Id, SuggestionId, RuleId, Title, BytesFreed, WasDryRun, Status, ExecutedUtc, ErrorMessage }

Task LogResultAsync(CleanupResult, CleanupSuggestion, CancellationToken)
Task<IReadOnlyList<CleanupLogEntry>> GetRecentAsync(int count, CancellationToken)
```

---

#### `ISettingsRepository` — `Interfaces/ISettingsRepository.cs`

```csharp
Task<AppSettings> LoadAsync(CancellationToken)
Task SaveAsync(AppSettings, CancellationToken)
```

---

#### `IRecycleBinInfoProvider` — `Interfaces/IRecycleBinInfoProvider.cs` *(in Platform.Windows)*

```csharp
record RecycleBinInfo(long SizeBytes, long ItemCount)
RecycleBinInfo GetRecycleBinInfo()
```

---

### Scanner

#### `FileScanner` — `Scanner/FileScanner.cs`

Implements `IFileScanner`. Parallel BFS directory walker.

| Private member | Purpose |
|----------------|---------|
| `ScanDirectoryTreeAsync` | Sets up Channel + producer + consumers |
| `ProduceDirectoriesAsync` | BFS walk, feeds Channel (bounded 1024) |
| `ConsumeDirectoriesAsync` | Reads Channel, calls ProcessDirectory, triggers flushes |
| `ProcessDirectory` | Enumerates files → FileEntry; builds FolderEntry; queues buffers |
| `FlushFileBufferAsync` | Drains `ConcurrentQueue<FileEntry>`, calls `InsertFileEntriesAsync` |
| `FlushFolderBufferAsync` | Drains `ConcurrentQueue<FolderEntry>`, calls `UpsertFolderEntriesAsync` |
| `ReportProgressLoopAsync` | PeriodicTimer(300ms) → `IProgress<ScanProgress>.Report` |
| `ScanState` (inner class) | Thread-safe counters + `ConcurrentQueue` buffers |

---

#### `FileTypeCategorizor` — `Scanner/FileTypeCategorizor.cs`

Static class with 80+ extension → `FileTypeCategory` mappings.

```csharp
static FileTypeCategory Categorize(string extension)
```

---

#### `FolderSizeAggregator` — `Scanner/FolderSizeAggregator.cs`

Static class. Computes bottom-up folder size totals from a flat list of `(FullPath, DirectSizeBytes)` pairs.

```csharp
static IReadOnlyDictionary<string, long> Compute(
    IReadOnlyList<(string FullPath, long DirectSizeBytes)> folders)
```

Algorithm: sort paths descending by length (deepest first), then for each path add its `DirectSizeBytes` to itself and to every ancestor. Result: a dictionary mapping each `FullPath` → `TotalSizeBytes`.

---

### Cleanup

#### `CleanupEngine` — `Cleanup/CleanupEngine.cs`

Implements `ICleanupEngine`. Receives `IEnumerable<ICleanupRule>` from DI.

| Method | Behaviour |
|--------|-----------|
| `GetSuggestionsAsync` | Iterates all rules, yields suggestions in order |
| `ExecuteAsync` | Builds `DeletionRequest` per target path, calls `IFileDeleter.DeleteManyAsync`, logs results |
| `ExecuteSuggestionAsync` | Handles one suggestion; aggregates outcomes; determines status |

---

#### Cleanup Rules

| Class | RuleId | Category | Risk | Key behaviour |
|-------|--------|----------|------|---------------|
| `RecycleBinCleanupRule` | `core.recycle-bin` | RecycleBin | Safe | Queries `IRecycleBinInfoProvider`; sentinel `"::RecycleBin::"` path |
| `TempFilesCleanupRule` | `core.temp-files` | TempFiles | Low | `%TEMP%`, `C:\Windows\Temp`, `%LOCALAPPDATA%\Temp`; known temp extensions |
| `DownloadedInstallersRule` | `core.downloaded-installers` | DownloadedInstallers | Low | Installer exts in Downloads; optional `core.clear-downloads-folder` |
| `CacheFolderCleanupRule` | `core.cache-folders` | CacheFolders | Safe–Low | Edge, Chrome, Firefox, npm, pip, NuGet, Yarn caches |
| `BrowserCacheCleanupRule` | `core.browser-cache` | BrowserCache | Low | Chrome, Edge, Firefox, Brave, Opera — all cache sub-paths |
| `WindowsUpdateCacheRule` | `core.windows-update-cache` | WindowsUpdateCache | Low | `SoftwareDistribution\Download`; `IsSystemPath=true` |
| `DeliveryOptimizationRule` | `core.delivery-optimization` | DeliveryOptimization | Low | `SoftwareDistribution\DeliveryOptimization`; `IsSystemPath=true` |
| `WindowsErrorReportingRule` | `core.windows-error-reporting` | WindowsErrorReporting | Low | WER folders, `CrashDumps`, `.dmp` files; `IsSystemPath=true` |
| `UninstalledProgramLeftoversRule` | `core.program-leftovers` | ProgramLeftovers | Medium | Registry cross-reference; 90-day + 10 MB thresholds; safelist |
| `LargeOldFilesCleanupRule` | `core.large-old-files` | LargeOldFiles | Medium | Per-file suggestions; configurable MB + days; protected prefixes |

---

### SmartCleaner

#### `SmartCleanerService` — `SmartCleaner/SmartCleanerService.cs`

Implements `ISmartCleanerService`. Scans 8 junk sources directly without a session.

| Source | Group name |
|--------|-----------|
| `%TEMP%` | Temporary Files |
| Browser cache dirs (Chrome/Edge/Firefox/Brave/Opera) | Browser Cache |
| `SoftwareDistribution\Download` | Windows Update Cache |
| WER directories | Windows Error Reports |
| `DeliveryOptimization` | Delivery Optimization |
| `%LOCALAPPDATA%\Temp` (thumbnail, shader caches) | Thumbnail & Shader Cache |
| Shell RecycleBin | Recycle Bin |

---

## StorageMaster.Platform.Windows

**Target:** `net8.0-windows10.0.19041.0`
**Flags:** `AllowUnsafeBlocks=true`

---

#### `FileDeleter` — `FileDeleter.cs`

Implements `IFileDeleter`.

| Member | Behaviour |
|--------|-----------|
| `DeleteManyAsync` | Batches all RecycleBin paths into one `SHFileOperation` call; permanent paths deleted individually |
| `BuildPathListHGlobal` | Packs paths into double-null-terminated native string buffer for `SHFileOperation` |
| `EmptyRecycleBin` | `SHEmptyRecycleBin` via `Shell32Interop` |
| `DeletePermanently` | `File.Delete` / `Directory.Delete(recursive: true)` |
| `EstimateSize` | `FileInfo.Length` or recursive `EnumerateFiles().Sum()` |

---

#### `TurboFileScanner` — `TurboFileScanner.cs`

Implements `IFileScanner`.

| Member | Behaviour |
|--------|-----------|
| `static IsAvailable` | `File.Exists(AppContext.BaseDirectory + "turbo-scanner.exe")` |
| `ScanAsync` | Spawns hidden process; reads JSONL; batch-inserts; runs `FolderSizeAggregator`; falls back if binary absent |
| `GetLargestFilesAsync` | Delegates to `_fallback` (managed scanner reads from DB) |
| `GetLargestFoldersAsync` | Delegates to `_fallback` |

JSONL record shape: `{"path":"...","size":N,"modified_unix":N,"created_unix":N,"is_dir":false}`

---

#### `AdminService` — `AdminService.cs`

Implements `IAdminService`.

| Member | Behaviour |
|--------|-----------|
| `IsRunningAsAdmin` | `WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)` |
| `RestartAsAdmin(enableDeepScan)` | `ProcessStartInfo { Verb = "runas" }` with `--deep-scan` arg if `enableDeepScan` |

---

#### `InstalledProgramProvider` — `InstalledProgramProvider.cs`

Implements `IInstalledProgramProvider`. Reads `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` from HKLM + HKCU (32 + 64 bit registry views). Skips entries with `SystemComponent=1`.

---

#### `KnownFolders` — `KnownFolders.cs`

Static helper.

```csharp
static string GetDownloadsPath()   // SHGetKnownFolderPath(FOLDERID_Downloads)
```

---

#### `Shell32Interop` — `Interop/Shell32Interop.cs`

Internal. Source-generated P/Invoke via `[LibraryImport]`.

| P/Invoke | Notes |
|----------|-------|
| `SHFileOperation` | Used for batch RecycleBin moves |
| `SHEmptyRecycleBin` | Empties all recycle bins |
| `SHQueryRecycleBin` | Gets size + item count |
| `SHGetKnownFolderPath` | Gets Downloads folder |

---

#### `DriveInfoProvider` — `DriveInfoProvider.cs`

Implements `IDriveInfoProvider`. Wraps `DriveInfo.GetDrives()`. Filters to `Fixed | Network | Removable`.

---

#### `RecycleBinInfoProvider` — `RecycleBinInfoProvider.cs`

Implements `IRecycleBinInfoProvider`. Calls `Shell32Interop.SHQueryRecycleBin(null, ...)`.

---

## StorageMaster.Storage

**Target:** `net8.0`
**Package:** `Microsoft.Data.Sqlite 9.0.4`

---

#### `StorageDbContext` — `StorageDbContext.cs`

Singleton. Manages one `SqliteConnection`.

| Member | Behaviour |
|--------|-----------|
| `GetConnectionAsync` | Lazy init; opens connection, applies WAL PRAGMAs, runs migrations |
| `MigrateAsync` | Reads `SchemaVersion`, applies missing migration batches in transaction |
| **DB path** | `%LOCALAPPDATA%\StorageMaster\storagemaster.db` |

---

#### `DatabaseSchema` — `Schema/DatabaseSchema.cs`

Internal static class. `CurrentVersion = 1`. Contains `V1Statements` (DDL for all tables + indexes).

---

#### `ScanRepository` — `Repositories/ScanRepository.cs`

Implements `IScanRepository`. Includes `GetAllFolderPathsForSessionAsync` and `UpdateFolderTotalsAsync` for the folder aggregation pipeline.

---

#### `ScanErrorRepository` — `Repositories/ScanErrorRepository.cs`

Implements `IScanErrorRepository`.

| Method | SQL |
|--------|-----|
| `LogErrorsAsync` | Batched insert in explicit transaction |
| `GetErrorsForSessionAsync` | `SELECT * WHERE SessionId = $id ORDER BY OccurredUtc` |

---

#### `CleanupLogRepository` — `Repositories/CleanupLogRepository.cs`

Implements `ICleanupLogRepository`.

---

#### `SettingsRepository` — `Repositories/SettingsRepository.cs`

Implements `ISettingsRepository`. Uses `System.Text.Json`. Key `"AppSettings"` in `Settings` table.

---

## StorageMaster.UI

**Target:** `net8.0-windows10.0.19041.0`
**WindowsPackageType:** `None` (unpackaged)
**WindowsAppSDKSelfContained:** `false`

---

#### `App` — `App.xaml.cs`

| Member | Behaviour |
|--------|-----------|
| `static Services` | `IServiceProvider` built in constructor |
| `BuildServices()` | Registers all singletons, transients, both scanners, 10 rules |
| `StartWithDeepScan` | Set from `--deep-scan` command-line argument |
| `OnLaunched` | Resolves `MainWindow`, calls `Activate()` |
| `OnUnhandledException` + `OnCurrentDomainUnhandledException` + `OnUnobservedTaskException` | Log to `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log` |

---

#### `MainWindow` — `MainWindow.xaml.cs`

| Member | Behaviour |
|--------|-----------|
| Constructor | Injects `INavigationService`; applies DPI-aware window sizing; navigates to Dashboard |
| `ApplyStartupWindowSize()` | `DisplayArea.GetFromWindowId` → 85% width, 90% height, clamped 900×700 min |
| `NavView_SelectionChanged` | Maps tag → page type, calls `_nav.NavigateTo(pageType)` |

Navigation tags: `Dashboard`, `Scan`, `Results`, `Cleanup`, `SmartCleaner`, `Settings`

---

#### `NavigationService` — `Infrastructure/NavigationService.cs`

Implements `INavigationService`.

```csharp
void Initialize(Frame frame)
bool NavigateTo(Type pageType, object? parameter)  // no-op if already on page
bool CanGoBack
void GoBack()
```

---

#### Converters

| Class | Behaviour |
|-------|-----------|
| `ByteSizeConverter` | Formats `long` bytes to `"4.50 GB"` etc; `static Format(long)` for ViewModels |
| `BoolToVisibilityConverter` | `Invert` property; `true` → Visible (or Collapsed when Invert=true) |
| `BoolNegationConverter` | Returns `!value`; used for `IsEnabled` inversions |

---

#### Pages & ViewModels

| Page | ViewModel | Navigation |
|------|-----------|-----------|
| `DashboardPage` | `DashboardViewModel` | Launch + "Dashboard" tag |
| `ScanPage` | `ScanViewModel` | "Scan" tag |
| `ResultsPage` | `ResultsViewModel` | "Results" tag or `GoToResultsCommand` (parameter: sessionId) |
| `CleanupPage` | `CleanupViewModel` | "Cleanup" tag |
| `SmartCleanerPage` | `SmartCleanerViewModel` | "SmartCleaner" tag |
| `SettingsPage` | `SettingsViewModel` | Settings item |

---

##### DashboardViewModel

| ObservableProperty | Type | Source |
|--------------------|------|--------|
| `LastSession` | `ScanSession?` | `GetRecentSessionsAsync(1)` |
| `TotalScannedSize` | `string` | Formatted from session |
| `TotalFiles` | `long` | Session.TotalFiles |
| `StatusMessage` | `string` | Derived |
| `HasLastSession` | `bool` | `LastSession != null` |
| `Drives` | `IReadOnlyList<DriveDetail>` | `IDriveInfoProvider.GetAvailableDrives()` |

Commands: `GoToScanCommand`, `GoToResultsCommand`

---

##### ScanViewModel

Singleton (owns CancellationTokenSource for long-running scan).

| ObservableProperty | Type | Notes |
|--------------------|------|-------|
| `SelectedPath` | `string` | Default from settings |
| `IsScanning` | `bool` | |
| `ScanComplete` | `bool` | |
| `ProgressText` | `string` | Summary line |
| `CurrentFile` | `string` | Truncated to 80 chars |
| `FilesScanned` | `long` | |
| `FoldersScanned` | `long` | |
| `BytesScanned` | `string` | Formatted |
| `ErrorCount` | `int` | |
| `ProgressValue` | `double` | 0–100, estimated from drive usage |
| `ErrorMessage` | `string` | |
| `HasError` | `bool` | |
| `AvailableDrives` | `IReadOnlyList<DriveDetail>` | |
| `DeepScan` | `bool` | Requires admin elevation |
| `UseTurboScanner` | `bool` | Persisted in settings |
| `TurboScannerAvailable` | `bool` | `TurboFileScanner.IsAvailable` |
| `IsRunningAsAdmin` | `bool` (computed) | `IAdminService.IsRunningAsAdmin` |
| `NeedsElevation` | `bool` (computed) | `DeepScan && !IsRunningAsAdmin` |

Commands: `StartScanCommand`, `CancelScanCommand`, `ViewResultsCommand`, `RequestElevationCommand`

---

##### ResultsViewModel

| ObservableCollection | Type | Limit |
|---------------------|------|-------|
| `LargestFiles` | `ObservableCollection<FileEntry>` | 500 |
| `LargestFolders` | `ObservableCollection<FolderEntry>` | 200 |
| `CategoryBreakdown` | `ObservableCollection<CategoryRow>` | All categories |
| `ScanErrors` | `ObservableCollection<ScanError>` | All for session |

ObservableProperties: `IsLoading`, `ScanRoot`, `ScanDate`, `TotalSize`, `TotalFiles`, `FilterText`, `ErrorCount`, `HasErrors`
Commands: `ApplyFilterCommand`

---

##### CleanupViewModel

ObservableCollections:
- `Suggestions` — `SuggestionItem` (wraps suggestion + `IsSelected`)
- `RecentSessions` — `ScanSession` (completed only)
- `ExecutionResults` — `CleanupResultDisplay` records
- `CategoryOptions` — `CleanupCategoryOption` (10 items, one per rule category)

ObservableProperties: `IsLoading`, `IsExecuting`, `IsDryRun`, `StatusMessage`, `SelectedSession`, `TotalSelectedSize`, `HasResults`, `HasExecutionResults`, `CleanupProgressText`, `CleanupProgressValue`, `ClearEntireDownloads`

Commands: `AnalyseCommand`, `ExecuteCleanupCommand`

`CleanupCategoryOption` properties: `Category`, `DisplayName`, `Description`, `IconGlyph`, `IsEnabled`

---

##### SmartCleanerViewModel

ObservableCollections:
- `Groups` — `SmartCleanGroupItem`

ObservableProperties: `IsScanning`, `IsCleaning`, `HasResults`, `CleaningDone`, `StatusText`, `ProgressText`, `TotalSizeText`, `FreedText`, `UseRecycleBin`

Computed: `CanClean`

Commands: `AnalyseCommand`, `CleanCommand`

`SmartCleanGroupItem` properties: `Group`, `IsSelected`, `Category`, `Description`, `IconGlyph`, `SizeDisplay`

---

##### SettingsViewModel

ObservableProperties map 1:1 to `AppSettings` members (all 21 settings).
Commands: `SaveCommand`, `ResetToDefaultsCommand`

---

## turbo-scanner (Rust)

**Crate:** `turbo-scanner/Cargo.toml`
**Binary:** `turbo-scanner.exe`
**Version:** 1.3.0

### Dependencies

| Crate | Version | Purpose |
|-------|---------|---------|
| `jwalk` | 0.8 | Parallel work-stealing directory walker |
| `serde` | 1 | Serialization derive macros |
| `serde_json` | 1 | JSON serialization |
| `clap` | 4 | CLI argument parsing |

### CLI interface

```
turbo-scanner --path <dir> [--threads N] [--min-size N] [--skip-hidden]
```

| Argument | Default | Purpose |
|----------|---------|---------|
| `--path` | required | Root directory to scan |
| `--threads` | 0 (= all cores) | Rayon thread pool size |
| `--min-size` | 0 | Minimum file size to report |
| `--skip-hidden` | false | Skip dotfile directories |

### Output format (JSONL on stdout)

One JSON object per line. Errors on stderr (prefixed `WARN:`).

```json
{"path":"C:\\Users\\Alice\\photo.jpg","size":2048576,"modified_unix":1700000000,"created_unix":1690000000,"is_dir":false}
```

### Release profile

```toml
[profile.release]
opt-level = 3
lto       = true
codegen-units = 1
strip     = true
```

---

## StorageMaster.Tests

**Target:** `net8.0-windows10.0.19041.0`

| Test class | Tests |
|------------|-------|
| `FileScannerTests` | Scanner integration (real temp directories) |
| `LargeOldFilesRuleTests` | Rule analysis logic |
| `TempFilesRuleTests` | Rule analysis logic |
| `ScanRepositoryTests` | SQLite persistence round-trips |
| Additional rule tests | BrowserCache, CacheFolder, RecycleBin, DownloadedInstallers |
| Additional engine tests | `CleanupEngine` orchestration, partial failure |
| `SettingsRepositoryTests` | Settings round-trip |

---

## Database schema

### Tables

| Table | Primary Key | Foreign Keys | Purpose |
|-------|-------------|--------------|---------|
| `SchemaVersion` | — | — | Migration tracking |
| `ScanSessions` | `Id` (AUTOINCREMENT) | — | One row per scan run |
| `FileEntries` | `Id` (AUTOINCREMENT) | `SessionId → ScanSessions(Id)` CASCADE | One row per file |
| `FolderEntries` | `Id` (AUTOINCREMENT) | `SessionId → ScanSessions(Id)` CASCADE | One row per directory |
| `ScanErrors` | `Id` (AUTOINCREMENT) | `SessionId → ScanSessions(Id)` CASCADE | Per-path scan errors |
| `CleanupLog` | `Id` (AUTOINCREMENT) | — | Append-only deletion audit |
| `Settings` | `Key` (TEXT) | — | JSON key-value store |

### Indexes

| Index | Table | Columns | Serves |
|-------|-------|---------|--------|
| `IX_FileEntries_Session_Size` | FileEntries | `(SessionId, SizeBytes DESC)` | Top-N largest files |
| `IX_FileEntries_Extension` | FileEntries | `(SessionId, Extension)` | Category breakdown |
| `IX_FolderEntries_Session_Size` | FolderEntries | `(SessionId, TotalSizeBytes DESC)` | Top-N largest folders |

### Unique constraints

- `FolderEntries(SessionId, FullPath)` — enables ON CONFLICT upsert for parallel folder writes

---

## NuGet packages

| Project | Package | Version | Purpose |
|---------|---------|---------|---------|
| Core | `CommunityToolkit.Mvvm` | 8.4.0 | MVVM source generators |
| Core | `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.0 | DI interfaces |
| Core | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | `ILogger<T>` |
| Platform.Windows | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | Logging |
| Storage | `Microsoft.Data.Sqlite` | 9.0.4 | SQLite access |
| Storage | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | Logging |
| UI | `Microsoft.WindowsAppSDK` | 1.6.250205002 | WinUI 3 runtime + XAML compiler |
| UI | `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.1742 | WinUI 3 build tools |
| UI | `CommunityToolkit.Mvvm` | 8.4.0 | MVVM source generators |
| UI | `Microsoft.Extensions.DependencyInjection` | 10.0.0 | Full DI container |
| UI | `Microsoft.Extensions.Logging` | 10.0.0 | Logging infrastructure |
| UI | `Microsoft.Extensions.Logging.Debug` | 10.0.0 | Debug output provider |
| Tests | `xunit` | 2.9.3 | Test framework |
| Tests | `xunit.runner.visualstudio` | 3.1.4 | VS test runner adapter |
| Tests | `Microsoft.NET.Test.Sdk` | 17.14.1 | Test SDK |
| Tests | `Moq` | 4.20.72 | Mocking framework |
| Tests | `FluentAssertions` | 7.2.0 | Assertion DSL |

---

## Build targets

| Project | Target Framework | Platform-specific |
|---------|-----------------|-------------------|
| Core | `net8.0` | No |
| Storage | `net8.0` | No |
| Platform.Windows | `net8.0-windows10.0.19041.0` | Yes |
| UI | `net8.0-windows10.0.19041.0` | Yes (WinUI 3) |
| Tests | `net8.0-windows10.0.19041.0` | Yes |
| turbo-scanner | Rust stable / `x86_64-pc-windows-msvc` | Yes |

UI build flags: `WindowsPackageType=None`, `SelfContained=false`
Platform.Windows build flags: `AllowUnsafeBlocks=true`
