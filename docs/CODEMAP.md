# StorageMaster — Codemap

> **Version:** 1.0.0 | **Date:** 2026-04-25
> Quick-reference for every file, type, method, and database table in the project.

---

## Table of contents

- [Solution files](#solution-files)
- [StorageMaster.Core](#storagemastercore)
  - [Models](#models)
  - [Interfaces](#interfaces)
  - [Scanner](#scanner)
  - [Cleanup](#cleanup)
- [StorageMaster.Platform.Windows](#storagemasterplatformwindows)
- [StorageMaster.Storage](#storagemasterstorage)
- [StorageMaster.UI](#storagemasterui)
- [StorageMaster.Tests](#storagemastertests)
- [Database schema](#database-schema)
- [NuGet packages](#nuget-packages)
- [Build targets](#build-targets)

---

## Solution files

| File | Purpose |
|------|---------|
| `StorageMaster.sln` | Solution descriptor linking all 5 projects |
| `StorageMaster.slnx` | New-format solution file (generated alongside .sln) |
| `global.json` | Pins SDK to 10.0.203; rollForward=latestFeature |
| `README.md` | Quick-start, build instructions, architecture summary |
| `docs/ARCHITECTURE.md` | Deep architecture reference (this sibling doc) |
| `docs/CODEMAP.md` | This file |
| `docs/DOCUMENTATION.md` | Full API and configuration reference |
| `docs/ROADMAP.md` | v1 → enterprise path |

---

## StorageMaster.Core

**Project file:** [`src/StorageMaster.Core/StorageMaster.Core.csproj`](../src/StorageMaster.Core/StorageMaster.Core.csproj)
**Target:** `net10.0`
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
| `Extension` | `string` | Including dot (e.g. `.mp4`) |
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
| `TotalSizeBytes` | `long` | v1: same as DirectSizeBytes (ancestor propagation pending) |
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
| `ExcludedPaths` | See below | Case-insensitive prefix exclusions |
| `FollowSymlinks` | `false` | Follow reparse points |

Default excluded paths: `C:\Windows\WinSxS`, `C:\Windows\Installer`

---

#### `CleanupSuggestion` — `Models/CleanupSuggestion.cs`

One actionable cleanup recommendation. Produced by `ICleanupRule`, consumed by `ICleanupEngine`.

| Member | Type | Notes |
|--------|------|-------|
| `Id` | `Guid` | Unique per suggestion |
| `RuleId` | `string` | e.g. `"core.temp-files"` |
| `Title` | `string` | Short display name |
| `Description` | `string` | Human-readable detail |
| `Category` | `CleanupCategory` | Grouping enum |
| `Risk` | `CleanupRisk` | Safe / Low / Medium / High |
| `EstimatedBytes` | `long` | Expected bytes freed |
| `TargetPaths` | `IReadOnlyList<string>` | Paths to delete on confirmation |
| `IsSystemPath` | `bool` | UI warning flag |

**Enum `CleanupRisk`:** `Safe`, `Low`, `Medium`, `High`

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

| Property | Default | UI Section |
|----------|---------|------------|
| `PreferRecycleBin` | `true` | Deletion Behaviour |
| `DryRunByDefault` | `false` | Deletion Behaviour |
| `LargeFileSizeMb` | `500` | Thresholds |
| `OldFileAgeDays` | `365` | Thresholds |
| `DefaultScanPath` | `C:\` | Scan Options |
| `ScanParallelism` | `4` | Scan Options |
| `ShowHiddenFiles` | `false` | Scan Options |
| `SkipSystemFolders` | `true` | Scan Options |
| `ExcludedPaths` | `[]` | Scan Options |

---

#### `FileTypeCategory` — `Models/FileTypeCategory.cs`

Enum (14 values): `Unknown`, `Document`, `Image`, `Video`, `Audio`, `Archive`, `Executable`, `SourceCode`, `Database`, `Temporary`, `SystemFile`, `Installer`, `Log`, `Cache`

---

#### `CleanupCategory` — `Models/CleanupCategory.cs`

Enum (8 values): `RecycleBin`, `TempFiles`, `DownloadedInstallers`, `CacheFolders`, `LargeOldFiles`, `DuplicateFiles`, `LogFiles`, `Custom`

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
```

---

#### `IFileDeleter` — `Interfaces/IFileDeleter.cs`

```csharp
// Supporting types:
record DeletionRequest(string Path, DeletionMethod Method, bool DryRun)
record DeletionOutcome(string Path, bool Success, long BytesFreed, string? Error)
enum DeletionMethod { RecycleBin, Permanent }

// Interface:
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

### Scanner

#### `FileScanner` — `Scanner/FileScanner.cs`

Implements `IFileScanner`. Primary implementation of the recursive parallel scanner.

| Private member | Purpose |
|----------------|---------|
| `ScanDirectoryTreeAsync` | Sets up Channel + producer + consumers |
| `ProduceDirectoriesAsync` | BFS walk, feeds Channel |
| `ConsumeDirectoriesAsync` | Reads Channel, calls ProcessDirectory, triggers flushes |
| `ProcessDirectory` | Enumerates files, builds FileEntry/FolderEntry, queues to buffers |
| `FlushFileBufferAsync` | Drains `ConcurrentQueue<FileEntry>`, calls `InsertFileEntriesAsync` |
| `FlushFolderBufferAsync` | Drains `ConcurrentQueue<FolderEntry>`, calls `UpsertFolderEntriesAsync` |
| `ReportProgressLoopAsync` | PeriodicTimer(300ms) → `IProgress<ScanProgress>.Report` |
| `ScanState` (inner class) | Thread-safe counters + `ConcurrentQueue` buffers |

---

#### `FileTypeCategorizor` — `Scanner/FileTypeCategorizor.cs`

Static class. Contains 80+ extension mappings.

```csharp
static FileTypeCategory Categorize(string extension)
```

Notable mappings:
- `.tmp`, `.temp`, `.bak` → `Temporary`
- `.msi`, `.msix`, `.appx` → `Installer`
- `.log`, `.evtx`, `.etl` → `Log`
- `.db`, `.sqlite`, `.mdf` → `Database`

---

### Cleanup

#### `CleanupEngine` — `Cleanup/CleanupEngine.cs`

Implements `ICleanupEngine`. Receives `IEnumerable<ICleanupRule>` from DI (all registered rules).

| Method | Behaviour |
|--------|-----------|
| `GetSuggestionsAsync` | Iterates all rules, yields suggestions |
| `ExecuteAsync` | Builds `DeletionRequest` per target path, calls `IFileDeleter.DeleteManyAsync`, logs all results |
| `ExecuteSuggestionAsync` | Handles one suggestion; aggregates outcomes; determines status |

---

#### `TempFilesCleanupRule` — `Cleanup/Rules/TempFilesCleanupRule.cs`

| Property | Value |
|----------|-------|
| `RuleId` | `"core.temp-files"` |
| `Category` | `TempFiles` |
| `Risk` | `Low` |

Targets: files with extensions `.tmp .temp .chk .$$$  .gid` OR files under:
- `%TEMP%`
- `C:\Windows\Temp`
- `%LOCALAPPDATA%\Temp`

Yields one aggregate suggestion covering all matching files.

---

#### `RecycleBinCleanupRule` — `Cleanup/Rules/RecycleBinCleanupRule.cs`

| Property | Value |
|----------|-------|
| `RuleId` | `"core.recycle-bin"` |
| `Category` | `RecycleBin` |
| `Risk` | `Safe` |

Uses `IRecycleBinInfoProvider.GetRecycleBinInfo()` to estimate size.
Target path: `"::RecycleBin::"` (sentinel → triggers `SHEmptyRecycleBin` in FileDeleter).

---

#### `CacheFolderCleanupRule` — `Cleanup/Rules/CacheFolderCleanupRule.cs`

| Property | Value |
|----------|-------|
| `RuleId` | `"core.cache-folders"` |
| `Category` | `CacheFolders` |
| `Risk` | `Safe` to `Low` per entry |

Known cache paths under `%LOCALAPPDATA%`:

| Subpath | Display Name | Risk |
|---------|-------------|------|
| `Microsoft\Windows\INetCache` | IE/Edge Internet Cache | Safe |
| `Microsoft\Windows\WebCache` | Windows Web Cache | Low |
| `Google\Chrome\User Data\Default\Cache` | Chrome Cache | Safe |
| `Microsoft\Edge\User Data\Default\Cache` | Edge Cache | Safe |
| `Mozilla\Firefox\Profiles` | Firefox Cache | Low |
| `Temp` | Local AppData Temp | Low |
| `npm-cache` | npm Cache | Safe |
| `pip\Cache` | pip Cache | Safe |
| `NuGet\Cache` | NuGet Package Cache | Safe |
| `Yarn\Cache` | Yarn Cache | Safe |

Yields one suggestion per found cache folder.

---

#### `DownloadedInstallersRule` — `Cleanup/Rules/DownloadedInstallersRule.cs`

| Property | Value |
|----------|-------|
| `RuleId` | `"core.downloaded-installers"` |
| `Category` | `DownloadedInstallers` |
| `Risk` | `Low` |

Targets files in `%USERPROFILE%\Downloads` with extensions:
`.exe .msi .msp .msix .appx .appxbundle .pkg .dmg .iso .img`

Yields one aggregate suggestion.

---

#### `LargeOldFilesCleanupRule` — `Cleanup/Rules/LargeOldFilesCleanupRule.cs`

| Property | Value |
|----------|-------|
| `RuleId` | `"core.large-old-files"` |
| `Category` | `LargeOldFiles` |
| `Risk` | `Medium` |

Thresholds from `AppSettings`: `LargeFileSizeMb` (default 500) AND `OldFileAgeDays` (default 365).
Protected prefixes (never suggested): `Windows`, `System32`, `SysWOW64`.
Yields one suggestion **per file** (fine-grained user selection).

---

## StorageMaster.Platform.Windows

**Project file:** [`src/StorageMaster.Platform.Windows/StorageMaster.Platform.Windows.csproj`](../src/StorageMaster.Platform.Windows/StorageMaster.Platform.Windows.csproj)
**Target:** `net10.0-windows10.0.19041.0`
**Flags:** `AllowUnsafeBlocks=true`

---

#### `FileDeleter` — `FileDeleter.cs`

Implements `IFileDeleter`.

| Member | Behaviour |
|--------|-----------|
| `DeleteAsync` | Single-path delete; dry-run logs estimate only |
| `DeleteManyAsync` | Parallel with `SemaphoreSlim(4)` |
| `DeleteToRecycleBin` | `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile/Directory` |
| `DeletePermanently` | `File.Delete` / `Directory.Delete(recursive: true)` |
| `EmptyRecycleBin` | `SHQueryRecycleBin` (size) → `SHEmptyRecycleBin` |
| `EstimateSize` | `FileInfo.Length` or recursive `EnumerateFiles().Sum()` |

---

#### `DriveInfoProvider` — `DriveInfoProvider.cs`

Implements `IDriveInfoProvider`. Wraps `DriveInfo.GetDrives()`. Filters to `Fixed | Network | Removable`.

---

#### `RecycleBinInfoProvider` — `RecycleBinInfoProvider.cs`

Implements `IRecycleBinInfoProvider`. Calls `Shell32Interop.SHQueryRecycleBin(null, ...)`.

---

#### `Shell32Interop` — `Interop/Shell32Interop.cs`

Internal. Source-generated P/Invoke via `[LibraryImport]`.

| P/Invoke | Signature |
|----------|-----------|
| `SHEmptyRecycleBin` | `(IntPtr hwnd, string? pszRootPath, EmptyRecycleBinFlags) → uint` |
| `SHQueryRecycleBin` | `(string? pszRootPath, ref SHQUERYRBINFO) → int` |

| Struct | Fields |
|--------|--------|
| `SHQUERYRBINFO` | `cbSize: int`, `i64Size: long`, `i64NumItems: long` |

| Enum | Values |
|------|--------|
| `EmptyRecycleBinFlags` | `NoConfirmation`, `NoProgressUI`, `NoSound` |

---

## StorageMaster.Storage

**Project file:** [`src/StorageMaster.Storage/StorageMaster.Storage.csproj`](../src/StorageMaster.Storage/StorageMaster.Storage.csproj)
**Target:** `net10.0`
**Package:** `Microsoft.Data.Sqlite 9.0.4`

---

#### `StorageDbContext` — `StorageDbContext.cs`

Singleton. Manages one `SqliteConnection` shared across all repositories.

| Member | Behaviour |
|--------|-----------|
| `GetConnectionAsync` | Lazy init; double-checked lock; opens connection, runs migrations |
| `OpenConnectionAsync` | Creates DB file, applies WAL + performance PRAGMAs |
| `MigrateAsync` | Reads `SchemaVersion`, applies missing migration batches |
| `DisposeAsync` | Closes + disposes connection |

**DB path (default):** `%LOCALAPPDATA%\StorageMaster\storagemaster.db`

---

#### `DatabaseSchema` — `Schema/DatabaseSchema.cs`

Internal static class.

| Member | Value |
|--------|-------|
| `CurrentVersion` | `1` |
| `V1Statements` | 10-element `string[]` array of DDL statements |

---

#### `ScanRepository` — `Repositories/ScanRepository.cs`

Implements `IScanRepository`.

| Method | SQL |
|--------|-----|
| `CreateSessionAsync` | `INSERT INTO ScanSessions ... SELECT last_insert_rowid()` |
| `GetSessionAsync` | `SELECT * WHERE Id = $id` |
| `GetRecentSessionsAsync` | `SELECT * ORDER BY StartedUtc DESC LIMIT $n` |
| `UpdateSessionAsync` | `UPDATE ScanSessions SET ... WHERE Id = $id` |
| `InsertFileEntriesAsync` | Batched insert in explicit transaction |
| `UpsertFolderEntriesAsync` | `INSERT ... ON CONFLICT DO UPDATE` in explicit transaction |
| `GetLargestFilesAsync` | `SELECT * ORDER BY SizeBytes DESC LIMIT $n` |
| `GetLargestFoldersAsync` | `SELECT * ORDER BY TotalSizeBytes DESC LIMIT $n` |
| `GetCategoryBreakdownAsync` | `SELECT Category, COUNT(*), SUM(SizeBytes) GROUP BY Category` |
| `DeleteSessionAsync` | `DELETE FROM ScanSessions WHERE Id = $id` (cascade) |

---

#### `CleanupLogRepository` — `Repositories/CleanupLogRepository.cs`

Implements `ICleanupLogRepository`.

| Method | SQL |
|--------|-----|
| `LogResultAsync` | `INSERT INTO CleanupLog ...` |
| `GetRecentAsync` | `SELECT * ORDER BY ExecutedUtc DESC LIMIT $n` |

---

#### `SettingsRepository` — `Repositories/SettingsRepository.cs`

Implements `ISettingsRepository`. Uses `System.Text.Json`.

| Method | SQL |
|--------|-----|
| `LoadAsync` | `SELECT Value FROM Settings WHERE Key = 'AppSettings'` |
| `SaveAsync` | `INSERT INTO Settings ... ON CONFLICT(Key) DO UPDATE` |

---

## StorageMaster.UI

**Project file:** [`src/StorageMaster.UI/StorageMaster.UI.csproj`](../src/StorageMaster.UI/StorageMaster.UI.csproj)
**Target:** `net10.0-windows10.0.19041.0`
**WindowsPackageType:** `None` (unpackaged)
**WindowsAppSDKSelfContained:** `true`

---

#### `App` — `App.xaml.cs`

| Member | Behaviour |
|--------|-----------|
| `static Services` | `IServiceProvider` built in constructor |
| `BuildServices()` | Registers all singletons, transients, and rule registrations |
| `OnLaunched` | Resolves `MainWindow`, calls `Activate()` |

---

#### `MainWindow` — `MainWindow.xaml.cs`

| Member | Behaviour |
|--------|-----------|
| Constructor | Injects `INavigationService`, initializes Frame, resizes to 1280×800, navigates to Dashboard |
| `NavView_SelectionChanged` | Maps tag to page type, calls `_nav.NavigateTo(pageType)` |

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

#### `ByteSizeConverter` — `Converters/ByteSizeConverter.cs`

| Input | Output |
|-------|--------|
| ≥ 1 TB | `"1.23 TB"` |
| ≥ 1 GB | `"4.50 GB"` |
| ≥ 1 MB | `"512.0 MB"` |
| ≥ 1 KB | `"128 KB"` |
| else | `"512 B"` |

Also exposes `static Format(long bytes)` for use in ViewModels.

---

#### `BoolToVisibilityConverter` — `Converters/BoolToVisibilityConverter.cs`

| Property | Default |
|----------|---------|
| `Invert` | `false` |

`true` → `Visible` (or `Collapsed` when Invert=true)

---

#### Pages & ViewModels

| Page | ViewModel | Navigated via |
|------|-----------|---------------|
| `DashboardPage` | `DashboardViewModel` | Launch + Nav "Dashboard" |
| `ScanPage` | `ScanViewModel` | Nav "Scan" |
| `ResultsPage` | `ResultsViewModel` | Nav "Results" or `GoToResultsCommand` (parameter: sessionId) |
| `CleanupPage` | `CleanupViewModel` | Nav "Cleanup" |
| `SettingsPage` | `SettingsViewModel` | Nav Settings item |

---

##### DashboardViewModel

| ObservableProperty | Type | Source |
|--------------------|------|--------|
| `LastSession` | `ScanSession?` | `GetRecentSessionsAsync(1)` |
| `TotalScannedSize` | `string` | Formatted from session |
| `TotalFiles` | `long` | Session.TotalFiles |
| `StatusMessage` | `string` | Derived |
| `HasLastSession` | `bool` | `LastSession != null` |
| `Drives2` | `IReadOnlyList<DriveDetail>` | `IDriveInfoProvider.GetAvailableDrives()` |

Commands: `GoToScanCommand`, `GoToResultsCommand`

---

##### ScanViewModel

| ObservableProperty | Type | Notes |
|--------------------|------|-------|
| `SelectedPath` | `string` | Default `C:\` |
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

Commands: `StartScanCommand`, `CancelScanCommand`, `ViewResultsCommand`

---

##### ResultsViewModel

| ObservableCollection | Type | Limit |
|---------------------|------|-------|
| `LargestFiles` | `ObservableCollection<FileEntry>` | 200 after filter |
| `LargestFolders` | `ObservableCollection<FolderEntry>` | 100 after filter |
| `CategoryBreakdown` | `ObservableCollection<CategoryRow>` | All categories |

ObservableProperties: `IsLoading`, `ScanRoot`, `ScanDate`, `TotalSize`, `TotalFiles`, `FilterText`
Commands: `ApplyFilterCommand`
`record CategoryRow(string Category, long FileCount, string TotalSize)`

---

##### CleanupViewModel

| ObservableCollection | Contents |
|---------------------|---------|
| `Suggestions` | `SuggestionItem` (wraps suggestion + `IsSelected`) |
| `RecentSessions` | `ScanSession` (completed only) |
| `ExecutionResults` | `CleanupResultDisplay` records |

ObservableProperties: `IsLoading`, `IsExecuting`, `IsDryRun`, `StatusMessage`, `SelectedSessionId`, `TotalSelectedSize`, `HasResults`
Commands: `AnalyseCommand`, `ExecuteCleanupCommand`

`SuggestionItem` properties: `Suggestion`, `IsSelected`, `SizeDisplay`, `RiskDisplay`, `CategoryDisplay`

---

##### SettingsViewModel

ObservableProperties map 1:1 to `AppSettings` members.
Commands: `SaveCommand`, `ResetToDefaultsCommand`

---

## StorageMaster.Tests

**Project file:** [`tests/StorageMaster.Tests/StorageMaster.Tests.csproj`](../tests/StorageMaster.Tests/StorageMaster.Tests.csproj)
**Target:** `net10.0-windows10.0.19041.0`

| Test class | File | Tests |
|------------|------|-------|
| `FileScannerTests` | `Scanner/FileScannerTests.cs` | 4 |
| `LargeOldFilesRuleTests` | `Cleanup/CleanupRuleTests.cs` | 3 |
| `TempFilesRuleTests` | `Cleanup/CleanupRuleTests.cs` | 1 |
| `ScanRepositoryTests` | `Storage/ScanRepositoryTests.cs` | 4 |
| `UnitTest1` | `UnitTest1.cs` | 1 (placeholder) |

**Total: 13 tests, 13 passing**

---

## Database schema

### Tables

| Table | Primary Key | Foreign Keys | Purpose |
|-------|-------------|--------------|---------|
| `SchemaVersion` | — | — | Migration tracking |
| `ScanSessions` | `Id` (AUTOINCREMENT) | — | One row per scan run |
| `FileEntries` | `Id` (AUTOINCREMENT) | `SessionId → ScanSessions(Id)` CASCADE | One row per file |
| `FolderEntries` | `Id` (AUTOINCREMENT) | `SessionId → ScanSessions(Id)` CASCADE | One row per directory |
| `CleanupLog` | `Id` (AUTOINCREMENT) | — | Append-only audit |
| `Settings` | `Key` (TEXT) | — | JSON key-value store |

### Indexes

| Index | Table | Columns | Serves |
|-------|-------|---------|--------|
| `IX_FileEntries_Session_Size` | FileEntries | `(SessionId, SizeBytes DESC)` | Top-N largest files |
| `IX_FileEntries_Extension` | FileEntries | `(SessionId, Extension)` | Category breakdown |
| `IX_FolderEntries_Session_Size` | FolderEntries | `(SessionId, TotalSizeBytes DESC)` | Top-N largest folders |

### Unique constraints

- `FolderEntries(SessionId, FullPath)` — enables the ON CONFLICT upsert for parallel folder writes

---

## NuGet packages

| Project | Package | Version | Purpose |
|---------|---------|---------|---------|
| Core | `CommunityToolkit.Mvvm` | 8.4.0 | MVVM source generators |
| Core | `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.0 | DI interfaces |
| Core | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | ILogger<T> |
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
| Core | `net10.0` | No |
| Storage | `net10.0` | No |
| Platform.Windows | `net10.0-windows10.0.19041.0` | Yes |
| UI | `net10.0-windows10.0.19041.0` | Yes (WinUI 3) |
| Tests | `net10.0-windows10.0.19041.0` | Yes (references Platform.Windows) |

UI build flags: `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `SelfContained=true`
Platform.Windows build flags: `AllowUnsafeBlocks=true`
