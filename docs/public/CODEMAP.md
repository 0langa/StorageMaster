# StorageMaster — Codemap

<!-- schema-version: 15 -->

> **Version:** 2.2.1 | **Current-state review:** 2026-08-18
> High-level source map for major files, types, methods, and database tables. Source remains authoritative for exhaustive membership.
> **Current-state note:** current source uses schema v15, independent SQLite connection leases, exact UTC timestamp reads, persisted scan-time file identity, indexed folder parent/child lookups, scan-session ownership with startup recovery, managed/Turbo persistence hardening, dedicated duplicate deletion/recovery, fail-closed path/snapshot checks, and strengthened release gates. `DatabaseSchema.CurrentVersion` is authoritative for the schema number. See `RELIABILITY_AUDIT_2026-08-18.md` for evidence and limits as of that dated audit (which describes the schema at v12).

---

## Table of contents

- [Solution files](#solution-files)
- [StorageMaster.Core](#storagemastercore)
  - [Models](#models)
  - [Interfaces](#interfaces)
  - [Scanner](#scanner)
  - [Theming](#theming)
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
| `docs/public/ARCHITECTURE.md` | Deep architecture reference |
| `docs/public/CODEMAP.md` | This file |
| `docs/public/DOCUMENTATION.md` | Full API and configuration reference |
| `docs/public/ROADMAP.md` | Current roadmap and shipped baseline |
| `docs/public/RELIABILITY_AUDIT_2026-08-18.md` | Dated audit findings, completed evidence, residual risks, and release decision rule |
| `docs/public/STORAGEMASTER_3_AUDIT.md` | Earlier StorageMaster 3 audit snapshot; the dated reliability audit above is the current disposition |
| `docs/public/SAFETY_RECOVERY.md` | Deletion, quarantine, recovery journal, and undo/restore model |
| `docs/public/VISUAL_REGRESSION.md` | WinUI visual regression scenario matrix and desktop harness requirements |
| `archive/` | Archived local clutter and historical planning notes; `archive/project-notes/AIprojectcontext/` preserves the retired agent context pack |
| `.github/workflows/release.yml` | CI/CD: test → Rust build → publish → installer → Release |
| `installer/StorageMaster.iss` | Inno Setup 6 script (per-user installer, checks .NET Desktop Runtime 8 x64, stages Windows App Runtime 1.6 prereq) |
| `turbo-scanner/Cargo.toml` | Rust crate manifest |
| `turbo-scanner/src/main.rs` | Turbo Scanner entry point |

---

## StorageMaster.Core

**Project file:** `src/StorageMaster.Core/StorageMaster.Core.csproj`
**Target:** `net8.0`
**Packages:** `CommunityToolkit.Mvvm 8.4.0`, `Microsoft.Extensions.DI.Abstractions 10.0.0`, `Microsoft.Extensions.Logging.Abstractions 10.0.0`, `SixLabors.ImageSharp 3.1.12`

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
| `Identity` | `FileIdentity?` | Stable volume serial + file index; null legacy/unavailable rows cannot authorize deletion |
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

Root object for a scan run, including running/cancelled/failed states.

| Member | Type | Notes |
|--------|------|-------|
| `Id` | `long` | DB primary key |
| `RootPath` | `string` | Scanned root (e.g. `C:\`) |
| `StartedUtc` | `DateTime` | When scan began |
| `CompletedUtc` | `DateTime?` | Null while Running; stamped when recovery marks a session Interrupted |
| `Status` | `ScanStatus` | Running / Completed / Cancelled / Failed / Interrupted |
| `TotalSizeBytes` | `long` | Sum of all file sizes |
| `TotalFiles` | `long` | Total files found |
| `TotalFolders` | `long` | Total folders scanned |
| `AccessDeniedCount` | `long` | Paths that threw UnauthorizedAccess |
| `ErrorMessage` | `string?` | Set on Failed status, and on Interrupted to explain the partial result |
| `OwnerProcessId` | `int?` | Process that started the scan (schema v14). NULL on rows written before v14 |
| `OwnerProcessStartedUtc` | `DateTime?` | Start time of the owning process; needed because Windows recycles process ids |
| `Duration` | `TimeSpan?` (computed) | CompletedUtc - StartedUtc |

**Enum `ScanStatus`:** `Running`, `Completed`, `Cancelled`, `Failed`, `Interrupted`

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
| `ExcludedPaths` | (see below) | Case-insensitive boundary-aware exclusions |
| `FollowSymlinks` | `false` | Follow reparse points |
| `IncludeHiddenFiles` | `false` | Include hidden entries |
| `DeepScan` | `false` | When true, removes exclusions and includes hidden/system entries; configured parallelism still applies |

`DefaultExcludedPaths`: derived from `Environment.SpecialFolder.Windows` (`WinSxS`, `Installer`) and normalized by `ScanOptionValidator`.

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
| `OccurredAt` | `DateTime` |

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
| `EstimatedBytes` | `long` | Estimated logical bytes targeted; not guaranteed physical allocation reclaimed |
| `TargetPaths` | `IReadOnlyList<string>` | Paths to delete on confirmation |
| `ExpectedFileSnapshots` | `IReadOnlyDictionary<string, FileSnapshot>` | Optional analysis-time metadata/identity required to match before mutation |
| `SupportsPermanentDelete` / `SupportsRecycleBin` / `SupportsQuarantine` | `bool` | Per-suggestion deletion-method policy |
| `SafetyNotes` / `Confidence` | `string` / `double` | Review guidance and confidence metadata |
| `IsSystemPath` | `bool` | UI warning flag |

---

#### `CleanupResult` — `Models/CleanupResult.cs`

Outcome of executing one `CleanupSuggestion`.

| Member | Type | Notes |
|--------|------|-------|
| `SuggestionId` | `Guid` | |
| `Status` | `CleanupResultStatus` | |
| `BytesFreed` | `long` | Legacy/logical outcome field; Recycle Bin and quarantine moves normally report zero reclaimed bytes |
| `ExecutedUtc` | `DateTime` | |
| `WasDryRun` | `bool` | |
| `FailedPaths` | `IReadOnlyList<string>` | |
| `ErrorMessage` | `string?` | |
| `QuarantinedPaths` | `IReadOnlyList<QuarantineMove>` | |

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
| `ShowHiddenFiles` | `false` | Include hidden files in scans |
| `SkipSystemFolders` | `true` | Skip Windows dirs unless DeepScan |
| `ExcludedPaths` | `[]` | Custom path exclusions |
| `UseTurboScanner` | `false` | Use Rust-backed scanner |
| `Theme` | `Default` | `ThemePreference`: follow Windows, Light, or Dark |
| `AccentId` | `"aurora"` | Selected accent, resolved through `ThemeCatalog.ResolveAccent`. Stored as a string so a retired accent degrades to the default instead of failing to deserialize |
| `Language` | `System` | `UiLanguage`: `System` / `English` / `German`; pins the text WinUI supplies for built-in controls |
| `ScanHistoryRetentionDays` | `365` | Scan-history retention window |
| `CleanRecycleBin` | `true` | Enable RecycleBin rule |
| `CleanTempFiles` | `true` | Enable TempFiles rule |
| `CleanDownloadedInstallers` | `true` | Enable DownloadedInstallers rule |
| `ClearEntireDownloads` | `false` | Clear entire Downloads folder |
| `CleanCacheFolders` | `true` | Enable CacheFolders rule |
| `CleanBrowserCache` | `true` | Enable BrowserCache rule |
| `CleanWindowsUpdateCache` | `true` | Enable WindowsUpdateCache rule |
| `CleanDeliveryOptimization` | `true` | Enable DeliveryOptimization rule |
| `CleanWindowsErrorReports` | `true` | Enable WindowsErrorReporting rule |
| `CleanProgramLeftovers` | `false` | Enable UninstalledProgramLeftovers rule (high risk) |
| `CleanLargeOldFiles` | `false` | Enable LargeOldFiles rule (medium risk) |
| `CleanThumbnailCache` | `true` | Enable ThumbnailCache rule |
| `CleanIconCache` | `true` | Enable IconCache rule |
| `CleanFontCache` | `false` | Enable FontCache rule |
| `CleanDnsCache` | `true` | Enable DnsClientCache rule |
| `CleanPrefetchFiles` | `false` | Enable PrefetchFiles rule (medium risk) |
| `CleanStoreLogs` | `true` | Enable MicrosoftStoreLogs rule |

---

#### `CleanupCategory` — `Models/CleanupCategory.cs`

Enum (19 values): `RecycleBin`, `TempFiles`, `DownloadedInstallers`, `CacheFolders`, `LargeOldFiles`, `DuplicateFiles`, `LogFiles`, `BrowserCache`, `WindowsUpdateCache`, `ProgramLeftovers`, `DeliveryOptimization`, `WindowsErrorReporting`, `Custom`, `ThumbnailCache`, `IconCache`, `FontCache`, `DnsCache`, `PrefetchFiles`, `StoreLogs`

---

#### `FileTypeCategory` — `Models/FileTypeCategory.cs`

Enum (14 values): `Unknown`, `Document`, `Image`, `Video`, `Audio`, `Archive`, `Executable`, `SourceCode`, `Database`, `Temporary`, `SystemFile`, `Installer`, `Log`, `Cache`

---

#### `CleanupProgress` — `Models/CleanupProgress.cs`

Progress snapshot for cleanup operations.

| Member | Type |
|--------|------|
| `Completed` | `int` |
| `Total` | `int` |
| `CurrentTitle` | `string` |

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
Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
    IReadOnlyList<CleanupSuggestion>, bool dryRun, DeletionMethod,
    IProgress<CleanupProgress>?, CancellationToken)
```

---

#### `ISmartCleanerService` — `Interfaces/ISmartCleanerService.cs`

```csharp
Task<SmartCleanAnalysisResult> AnalyzeAsync(IProgress<string>? progress, CancellationToken)
Task<SmartCleanResult> CleanAsync(IReadOnlyList<SmartCleanGroup>, DeletionMethod, IProgress<string>?, CancellationToken)

record SmartCleanGroup(
    SmartCleanSource Source, string Category, string Description, string IconGlyph,
    long EstimatedBytes, IReadOnlyList<string> Paths,
    IReadOnlyDictionary<string, FileSnapshot> ExpectedFileSnapshots, bool IsSelected = true)
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
Task<IReadOnlyList<FileEntry>> SearchFilesAsync(long sessionId, string? filter,
    string? categoryFilter, string sortColumn, bool descending, int offset, int limit, CancellationToken)
Task<long> CountFilesAsync(long sessionId, string? filter, string? categoryFilter, CancellationToken)
Task<IReadOnlyList<FolderEntry>> SearchFoldersAsync(long sessionId, string? filter,
    string sortColumn, bool descending, int offset, int limit, CancellationToken)
Task<long> CountFoldersAsync(long sessionId, string? filter, CancellationToken)
Task<IReadOnlyDictionary<FileTypeCategory,(long Count, long Bytes)>> GetCategoryBreakdownAsync(long sessionId, CancellationToken)
Task DeleteSessionAsync(long sessionId, CancellationToken)
Task<IReadOnlyList<FolderEntry>> GetAllFolderPathsForSessionAsync(long sessionId, CancellationToken)
Task UpdateFolderTotalsAsync(long sessionId, IReadOnlyDictionary<string,long> totals, CancellationToken)
```

---

#### `IScanErrorRepository` — `Interfaces/IScanErrorRepository.cs`

```csharp
Task LogErrorsAsync(long sessionId, IReadOnlyList<ScanError> errors, CancellationToken)
Task<IReadOnlyList<ScanError>> GetErrorsForSessionAsync(long sessionId, CancellationToken)
Task<IReadOnlyList<ScanError>> GetErrorsPageForSessionAsync(long sessionId, int offset, int limit, CancellationToken)
Task<long> CountErrorsForSessionAsync(long sessionId, CancellationToken)
```

---

#### `IFileDeleter` — `Interfaces/IFileDeleter.cs`

```csharp
record DeletionRequest(string Path, DeletionMethod Method, bool DryRun,
    long? QuarantineRunId = null, FileSnapshot? ExpectedSnapshot = null)
record DeletionOutcome(string Path, bool Success, long BytesFreed,
    string? Error = null, string? QuarantinePath = null)
enum DeletionMethod { RecycleBin, Permanent, Quarantine }

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
record InstalledProgramInfo(string DisplayName, string? InstallLocation, string? Publisher)

IReadOnlyList<InstalledProgramInfo> GetInstalledPrograms()
```

---

#### `ICleanupLogRepository` — `Interfaces/ICleanupLogRepository.cs`

```csharp
record CleanupLogEntry { Id, SuggestionId, RuleId, Title, BytesFreed, WasDryRun, Status, ExecutedUtc, ErrorMessage, AuditDataJson }

Task LogResultAsync(CleanupResult, CleanupSuggestion, CancellationToken)
Task<IReadOnlyList<CleanupLogEntry>> GetRecentAsync(int count, CancellationToken)
```

---

#### `ISettingsRepository` — `Interfaces/ISettingsRepository.cs`

```csharp
Task<AppSettings> LoadAsync(CancellationToken)
Task SaveAsync(AppSettings, CancellationToken)
Task<AppSettings> UpdateAsync(Action<AppSettings>, CancellationToken)
```

---

#### `IRecycleBinInfoProvider` — declared with `Cleanup/Rules/RecycleBinCleanupRule.cs`, implemented in Platform.Windows

```csharp
record RecycleBinInfo(long SizeBytes, int ItemCount)
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
| `ProcessDirectoryAsync` | Enumerates files → FileEntry; builds FolderEntry; queues buffers |
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

Static class. Computes bottom-up folder size totals from a flat list of `FolderEntry` values.

```csharp
static Dictionary<string, long> Compute(IReadOnlyList<FolderEntry> folders)
```

Algorithm: sort paths descending by length (deepest first), then for each path add its `DirectSizeBytes` to itself and to every ancestor. Result: a dictionary mapping each `FullPath` → `TotalSizeBytes`.

---

#### `ScanSessionRecovery` — `Scanner/ScanSessionRecovery.cs`

Static class holding the pure liveness decision for sessions left `Running` by a process that never finished. Kept free of process APIs so the rules are unit testable; `Platform.Windows/ScanSessionRecoveryService.cs` supplies the observed process list and writes the results, and `App.xaml.cs` runs it once at startup.

| Member | Behaviour |
|--------|-----------|
| `FindAbandoned(sessions, liveProcesses, currentProcessId)` | Returns the `Running` sessions no live StorageMaster process claims. NULL ownership (pre-v14 rows) counts as unowned; the caller's own sessions are never condemned; a matching id whose start time drifts past `StartTimeTolerance` is treated as a recycled id |
| `ToInterrupted(session, nowUtc)` | Terminal form: `Status = Interrupted`, `CompletedUtc` stamped, explanatory `ErrorMessage`, partial totals preserved |
| `StartTimeTolerance` | 2 seconds — process start times as observed and as recorded need not agree exactly |

---

### Theming

#### `ThemeCatalog` — `Theming/ThemeCatalog.cs`

Static class holding the entire shipped palette as plain data: `Dark` / `Light` neutral ramps, `DarkSeverity` / `LightSeverity`, and the `Accents` list (`aurora` — the default — plus `ember`, `verdant`, `violet`), each with a dark and a light ramp. Living in Core rather than in XAML means every accent is contrast-checkable by a unit test, and adding one is a data change. `ResolveAccent(accentId)` falls back to `DefaultAccentId` for an unknown or retired id, which is why `AppSettings.AccentId` is persisted as a string.

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
| `RecycleBinCleanupRule` | `core.recycle-bin` | RecycleBin | Medium | Permanent-only empty operation; sentinel `"::RecycleBin::"` path |
| `TempFilesCleanupRule` | `core.temp-files` | TempFiles | Low | Canonical `%WINDIR%\Temp` and `%LOCALAPPDATA%\Temp`; redirected process temp is untrusted |
| `DownloadedInstallersRule` | `core.downloaded-installers` | DownloadedInstallers | Low | Installer exts in Downloads; optional `core.clear-downloads-folder` |
| `CacheFolderCleanupRule` | `core.cache-folders` | CacheFolders | Safe–Low | Edge, Chrome, Firefox, npm, pip, NuGet, Yarn caches |
| `BrowserCacheCleanupRule` | `core.browser-cache` | BrowserCache | Low | Chrome, Edge, Firefox, Brave, Opera — all cache sub-paths |
| `WindowsUpdateCacheRule` | `core.windows-update-cache` | WindowsUpdateCache | Low | `SoftwareDistribution\Download`; `IsSystemPath=true` |
| `DeliveryOptimizationRule` | `core.delivery-optimization` | DeliveryOptimization | Low | `SoftwareDistribution\DeliveryOptimization`; `IsSystemPath=true` |
| `WindowsErrorReportingRule` | `core.windows-error-reporting` | WindowsErrorReporting | Low | WER folders, `CrashDumps`, `.dmp` files; `IsSystemPath=true` |
| `UninstalledProgramLeftoversRule` | `core.program-leftovers` | ProgramLeftovers | High | Disabled/unselected; heuristic registry cross-reference; descendant-age checks; Recycle-Bin-only |
| `LargeOldFilesCleanupRule` | `core.large-old-files` | LargeOldFiles | Medium | Per-file suggestions; configurable MB + days; protected prefixes |
| `ThumbnailCacheRule` | `core.thumbnail-cache` | ThumbnailCache | Low | Explorer `thumbcache_*.db` files |
| `IconCacheRule` | `core.icon-cache` | IconCache | Low | Explorer `iconcache*.db` files |
| `FontCacheRule` | `core.font-cache` | FontCache | Low | Windows font-cache service data |
| `DnsClientCacheRule` | `core.dns-cache` | DnsCache | Low | Read-only suggestion using the `"::DnsFlush::"` execution sentinel |
| `PrefetchFilesRule` | `core.prefetch-files` | PrefetchFiles | Medium | Windows Prefetch entries; requires elevation |
| `MicrosoftStoreLogsRule` | `core.store-logs` | StoreLogs | Low | Microsoft Store package diagnostic logs |

---

### SmartCleaner

#### `SmartCleanerService` — `SmartCleaner/SmartCleanerService.cs`

Implements `ISmartCleanerService`. Scans 7 allow-listed junk sources directly without a session, skips reparse points, captures explicit file snapshots, revalidates source boundaries, and returns detailed partial outcomes.

| Source | Group name |
|--------|-----------|
| `%WINDIR%\Temp` and `%LOCALAPPDATA%\Temp` | Temporary Files |
| Browser cache dirs (Chrome/Edge/Firefox/Brave/Opera) | Browser Cache |
| `SoftwareDistribution\Download` | Windows Update Cache |
| WER directories | Windows Error Reports |
| `DeliveryOptimization` | Delivery Optimization |
| `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db` | Thumbnail Cache |
| `%LOCALAPPDATA%\D3DSCache` | DirectX Shader Cache |

---

## StorageMaster.Platform.Windows

**Target:** `net8.0-windows10.0.19041.0`
**Flags:** `AllowUnsafeBlocks=true`

---

#### `FileDeleter` — `FileDeleter.cs`

Implements `IFileDeleter`.

| Member | Behaviour |
|--------|-----------|
| `DeleteManyAsync` | Batches real Recycle Bin paths into one `IFileOperation` call; other paths use bounded per-path execution |
| `EmptyRecycleBin` | `SHEmptyRecycleBin` via `Shell32Interop` |
| `DeletePermanently` | Uses no-follow, handle-bound directory traversal where available; removes reparse directories as links and fails closed when classification/boundary checks fail |
| `EstimateSize` | Cancellable, reparse-aware traversal capped at 100,000 entries |

---

#### Identity and no-follow services — `FileIdentityProvider.cs`, `FileSnapshotProvider.cs`, `NoFollowFileEnumerator.cs`, `Interop/DirectoryTraversalInterop.cs`

`FileIdentityProvider` captures Windows volume/file identity. `FileSnapshotProvider` combines identity with size, write time, and attributes. `NoFollowFileEnumerator` performs read-only enumeration without crossing reparse points and, where Windows sharing rules permit, holds ancestor handles while validating one file. Weak-guard fallback is explicit in returned warnings/errors; callers must not treat it as strong deletion authorization.

---

#### `TurboFileScanner` — `TurboFileScanner.cs`

Implements `IFileScanner`.

| Member | Behaviour |
|--------|-----------|
| `static IsAvailable` | `File.Exists(AppContext.BaseDirectory + "turbo-scanner.exe")` |
| `ScanAsync` | Spawns hidden process; reads JSONL; batch-inserts; runs `FolderSizeAggregator`; falls back if binary absent |
| `GetLargestFilesAsync` | Delegates to `_fallback` (managed scanner reads from DB) |
| `GetLargestFoldersAsync` | Delegates to `_fallback` |

Contract-v3 JSONL record shape:

```json
{"path":"C:\\data\\a.bin","size":12,"modified_unix":1700000000,"created_unix":1690000000,"modified_utc_ticks":638355968000000000,"created_utc_ticks":638040672000000000,"attributes":32,"volume_serial":1234,"file_index":5678,"is_dir":false,"is_hidden":false}
```

The exact UTC tick and nullable Windows identity fields are authoritative for current hosts; Unix-second fields remain for compatibility.

The host tolerates an older binary: `is_hidden` is deserialized as `bool?`, so a pre-v2 record that omits it falls back to a per-file attribute check instead of failing the scan. With a v2-or-later binary and `--skip-hidden`, hidden directories are pruned during native enumeration, which matches the managed scanner's `EnumerationOptions.AttributesToSkip` semantics and needs no extra syscall.

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

#### Shell deletion interop — `Interop/Shell32Interop.cs`, `Interop/FileOperationInterop.cs`

Internal. Source-generated P/Invoke plus COM `IFileOperation` declarations.

| API | Notes |
|----------|-------|
| `IFileOperation` COM (`FileOperationInterop`) | Batch Recycle Bin moves with `FOFX_RECYCLEONDELETE` |
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

#### `ScanSessionRecoveryService` — `ScanSessionRecoveryService.cs`

Runs once at startup. Reads the 200 most recent sessions, hands them plus the live StorageMaster processes to `ScanSessionRecovery.FindAbandoned`, and writes each result back through `IScanRepository.UpdateSessionAsync` as `Interrupted`. Only processes with the same process name are considered live owners, so an unrelated program that inherited a recycled id cannot pin a dead session. A process whose start time cannot be read is treated as not live — the worst case is a live scan marked interrupted, which loses no data because partial totals are kept. Any failure is logged and swallowed: startup never fails because history could not be tidied.

---

## StorageMaster.Storage

**Target:** `net8.0`
**Packages:** `Microsoft.Data.Sqlite 9.0.4`, `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`

---

#### `StorageDbContext` — `StorageDbContext.cs`

Singleton context managing one serialized initialization/migration sequence, pooled SQLite configuration, and path-keyed in-process write coordination. Every repository operation receives and disposes an independent open connection lease.

| Member | Behaviour |
|--------|-----------|
| `GetConnectionAsync` | Ensures initialization, then returns a new configured caller-owned connection lease |
| `MigrateAsync` | Takes an immediate writer reservation, re-reads `SchemaVersion`, then applies missing batches atomically |
| **DB path** | `%LOCALAPPDATA%\StorageMaster\storagemaster.db` |

---

#### `DatabaseSchema` — `Schema/DatabaseSchema.cs`

Internal static class. `CurrentVersion = 15`; each level is a `V<N>Statements` array applied and stamped in one transaction, and a shipped level is never edited.

| Level | Adds |
|-------|------|
| v8 | `DuplicateOperationJournal` |
| v9 | Nullable `QuarantinedFiles.MemberId` so generic-cleanup quarantines get restore records |
| v10 | Quarantine member FK becomes `ON DELETE SET NULL` so restore records outlive duplicate rows |
| v11 | Unique normalized folder identity, collapsing case variants conservatively |
| v12 | Nullable scan-time volume/file identity on `FileEntries` |
| v13 | `FolderEntries.ParentNormalizedPath` + `(SessionId, ParentNormalizedPath, TotalSizeBytes DESC)` index |
| v14 | `ScanSessions.OwnerProcessId` / `OwnerProcessStartedUtc` |
| v15 | Indexes on the `FileEntries(Id)` cascade referrers, plus `DuplicateErrors(RunId, FileEntryId)` |

Path casing is never computed in SQL: SQLite's `upper()` is ASCII-only while the application uses `ToUpperInvariant`. v13's backfill therefore splits the parent off the already-normalized column instead of re-normalizing.

---

#### `ScanRepository` — `Repositories/ScanRepository.cs`

Implements `IScanRepository`. Includes normalized file path upserts, paged search, folder tree queries, `GetAllFolderPathsForSessionAsync`, `UpdateFolderTotalsAsync`, and `PRAGMA optimize` after session deletion.

---

#### `ScanErrorRepository` — `Repositories/ScanErrorRepository.cs`

Implements `IScanErrorRepository`.

| Method | SQL |
|--------|-----|
| `LogErrorsAsync` | Batched insert in explicit transaction |
| `GetErrorsForSessionAsync` | Delegates to the paged query with an effectively unbounded limit |
| `GetErrorsPageForSessionAsync` | `ORDER BY OccurredAt DESC, Id DESC LIMIT/OFFSET` |
| `CountErrorsForSessionAsync` | `COUNT(*) WHERE SessionId = $id` |

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

v2 UI primitives now live in `src/StorageMaster.UI/Styles/*.xaml` and `src/StorageMaster.UI/Controls/*`: `PageHeader`, `StateView`, `RadialGauge`, `SeverityBadge`, `SettingsCard`, `SettingsExpander`, and `TreemapTileControl`.

---

#### `App` — `App.xaml.cs`

| Member | Behaviour |
|--------|-----------|
| `static Services` | `IServiceProvider` built in constructor |
| `ServiceBootstrapper.BuildServices()` | Registers repositories/platform services, both scanners, 16 active cleanup rules, one singleton `ScanViewModel`, and transient page ViewModels |
| `StartWithDeepScan` | Set from `--deep-scan` command-line argument |
| `ApplyStartupLanguage()` | Reads the persisted `Language` synchronously in the constructor and applies it through `LanguageService`. The override must be set before any content exists, because the resource system reads it when a control is created |
| `OnLaunched` | `ThemeService.EnsureResources()` first — an unresolved `StaticResource` is a load-time failure, not a fallback — then resolves and activates `MainWindow`, attaches the theme root, and starts theme application, abandoned-scan reconciliation, and the startup update check |
| `ReconcileAbandonedScansAsync()` | Runs `ScanSessionRecoveryService`; failures are logged, never fatal |
| `OnUnhandledException` + `OnCurrentDomainUnhandledException` + `OnUnobservedTaskException` | Log to `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log` |

---

#### `MainWindow` — `MainWindow.xaml.cs`

| Member | Behaviour |
|--------|-----------|
| Constructor | Injects `INavigationService`; applies DPI-aware window sizing; navigates to Dashboard |
| `ApplyStartupWindowSize()` | `DisplayArea.GetFromWindowId` → 85% width, 90% height, clamped 900×700 min |
| `NavView_SelectionChanged` | Maps tag → page type, calls `_nav.NavigateTo(pageType)` |

Navigation tags include `Dashboard`, `Scan`, `ScanWorkspace`, `Results`, `Duplicates`, `Cleanup`, `SmartCleaner`, `SpaceMap`, `DriveHealth`, and `Settings`.

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

#### `ThemeService` — `Infrastructure/ThemeService.cs`

Applies `ThemeCatalog` to the live visual tree. Creates the brushes once under the `Sm` resource-key prefix (so they cannot collide with WinUI's own theme resources, which still drive control chrome) and then mutates each `SolidColorBrush.Color` in place. Elements resolve a `StaticResource` once at load and never look again, but they keep the brush reference — so an accent or light/dark change is visible immediately, without a page reload or a restart. Adding an accent to the catalogue requires no change here or in any XAML.

---

#### `LanguageService` — `Infrastructure/LanguageService.cs`

Sets `ApplicationLanguages.PrimaryLanguageOverride` from `AppSettings.Language`. This exists because WinUI supplies its own text for built-in controls — a `ToggleSwitch` renders "On"/"Off", dialogs their buttons — and that text follows the Windows display language, not the app's strings; on a German Windows install the result was an English app with German switches. Tags are an explicit allow-list (`en-US`, `de-DE`) so the set cannot silently widen to untranslated languages, and `UiLanguage.System` clears the override. The override is process-wide and read at control creation, so it is applied during startup and a change requires a restart to take full effect — `CurrentDisplayTag` reports what the resource system actually resolved rather than what was requested. A failure to apply never blocks startup.

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
| `ScanWorkspacePage` | `ScanWorkspaceViewModel` | "ScanWorkspace" tag or scan completion |
| `ResultsPage` | `ResultsViewModel` | "Results" tag or `GoToResultsCommand` (parameter: sessionId) |
| `DuplicatesPage` | `DuplicatesViewModel` | "Duplicates" tag |
| `CleanupPage` | `CleanupViewModel` | "Cleanup" tag |
| `SmartCleanerPage` | `SmartCleanerViewModel` | "SmartCleaner" tag |
| `SpaceMapPage` | `SpaceMapViewModel` | "SpaceMap" tag |
| `DriveHealthPage` | `DriveHealthViewModel` | "DriveHealth" tag |
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

| ObservableCollection | Type | Paging |
|---------------------|------|--------|
| `LargestFiles` | `ObservableCollection<FileEntry>` | 200-row pages |
| `LargestFolders` | `ObservableCollection<FolderEntry>` | 100-row pages |
| `CategoryBreakdown` | `ObservableCollection<CategoryRow>` | All categories |
| `ScanErrors` | `ObservableCollection<ScanError>` | 100-row pages |

ObservableProperties include per-section loading state, `ScanRoot`, `ScanDate`, `TotalSize`, `TotalFiles`, `FilterText`, `ErrorCount`, page/load-more state, selected category, session note, and lazy Errors/Folder Tree status.
Commands cover filtering/category filtering, sorting, load-more, Explorer/clipboard actions, session/file deletion, and return to Workspace.

---

##### CleanupViewModel

ObservableCollections:
- `Suggestions` — `SuggestionItem` (wraps suggestion + `IsSelected`)
- `RecentSessions` — `ScanSession` (completed only)
- `ExecutionResults` — `CleanupResultDisplay` records
- `CategoryOptions` — `CleanupCategoryOption` (16 review toggles spanning current rule categories)

ObservableProperties include `IsLoading`, `IsExecuting`, `IsDryRun`, `StatusMessage`, `SelectedSession`, `TotalSelectedSize`, `HasResults`, `HasExecutionResults`, `CleanupProgressText`, `CleanupProgressValue`, `ClearEntireDownloads`, and `LastPreviewAllowsExecution`. Routed completed-session ids and analysis options are captured before asynchronous work; scope changes cancel/invalidate stale suggestions. Partial preview is not a successful deletion authorization.

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

ObservableProperties map to persisted `AppSettings` members, including drive-health notification preferences, scheduler editor state, `IsLoaded`, and user-visible operation feedback. Failed loads disable editing. Save, purge, diagnostics, and scheduler command boundaries surface I/O failures. Enabled `CleanupExecuteSafe` jobs require dedicated versioned consent; headless policy enforces it.
Commands include `SaveCommand`, `ResetToDefaultsCommand`, updater/diagnostics/history commands, and scheduled-job CRUD.

---

## turbo-scanner (Rust)

**Crate:** `turbo-scanner/Cargo.toml`
**Binary:** `turbo-scanner.exe`
**Version:** 2.2.1

### Dependencies

| Crate | Version | Purpose |
|-------|---------|---------|
| `jwalk` | 0.8 | Parallel work-stealing directory walker |
| `serde` | 1 | Serialization derive macros |
| `serde_json` | 1 | JSON serialization |
| `clap` | 4 | CLI argument parsing |

### CLI interface

```
turbo-scanner --path <dir> [--threads N] [--min-size N] [--skip-hidden] [--follow-links]
```

| Argument | Default | Purpose |
|----------|---------|---------|
| `--path` | required | Root directory to scan |
| `--threads` | 0 (= all cores) | Rayon thread pool size |
| `--min-size` | 0 | Minimum file size to report |
| `--skip-hidden` | false | Skip/prune entries with the Windows Hidden attribute (dot names on non-Windows builds) |
| `--follow-links` | false | Explicitly follow symbolic links/junctions; otherwise reparse traversal is disabled |

### Output format (JSONL on stdout)

One JSON object per line. Errors on stderr (prefixed `WARN:`).

```json
{"path":"C:\\Users\\Alice\\photo.jpg","size":2048576,"modified_unix":1700000000,"created_unix":1690000000,"modified_utc_ticks":638355968000000000,"created_utc_ticks":638040672000000000,"attributes":32,"volume_serial":1234,"file_index":5678,"is_dir":false,"is_hidden":false}
```

This is contract v3. Warnings/errors are written to stderr as `WARN:` records and the managed host persists them to `ScanErrors` when a repository is available.

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
The suite count changes as regression guards are added. Run the documented Release commands for the current .NET and Rust totals; the dated reliability audit records the last whole-tree result actually executed.

| Test class | Tests |
|------------|-------|
| `FileScannerTests` | Scanner integration (real temp directories) |
| `LargeOldFilesRuleTests` | Rule analysis logic |
| `TempFilesRuleTests` | Rule analysis logic |
| `ScanRepositoryTests` | SQLite persistence round-trips |
| Additional rule tests | BrowserCache, CacheFolder, RecycleBin, DownloadedInstallers |
| Additional engine tests | `CleanupEngine` orchestration, partial failure |
| `SettingsRepositoryTests` | Settings round-trip |
| `DeletionSafetyHardeningTests` | root guards, partial results, race handling, and fail-closed reparse classification |
| `SmartCleanerServiceTests` | recursive-analysis cancellation propagation |

---

## Database schema

### Tables

| Table | Primary Key | Foreign Keys | Purpose |
|-------|-------------|--------------|---------|
| `SchemaVersion` | — | — | Migration tracking |
| `ScanSessions` | `Id` (AUTOINCREMENT) | — | One row per scan run; also records the owning process (v14) |
| `FileEntries` | `Id` (AUTOINCREMENT) | `SessionId → ScanSessions(Id)` CASCADE | One row per file |
| `FolderEntries` | `Id` (AUTOINCREMENT) | `SessionId → ScanSessions(Id)` CASCADE | One row per directory; `ParentNormalizedPath` (v13) stores the parent instead of deriving it per query |
| `ScanErrors` | `Id` (AUTOINCREMENT) | `SessionId → ScanSessions(Id)` CASCADE | Per-path scan errors |
| `CleanupLog` | `Id` (AUTOINCREMENT) | — | Append-only deletion audit |
| `Settings` | `Key` (TEXT) | — | JSON key-value store |
| `DuplicateRuns`, `DuplicateSignatures`, `DuplicateGroups`, `DuplicateGroupMembers`, `DuplicateErrors` | integer IDs | scan/run/file relationships | Duplicate analysis, cached signatures, groups, members, and errors |
| `QuarantinedFiles` | `Id` (AUTOINCREMENT) | nullable `MemberId → DuplicateGroupMembers(Id)` SET NULL | Duplicate and generic quarantine/restore records retained if a duplicate member is removed |
| `DuplicateOperationJournal` | `Id` (AUTOINCREMENT), `OperationId` (unique) | Run/group/member/quarantine IDs where available | Crash-recovery journal for duplicate cleanup and restore intent/outcome |
| `DriveHealthSnapshots` | `Id` (AUTOINCREMENT) | — | Latest/history readings from the local Windows storage-health APIs |

### Indexes

| Index | Table | Columns | Serves |
|-------|-------|---------|--------|
| `IX_FileEntries_Session_Size` | FileEntries | `(SessionId, SizeBytes DESC)` | Top-N largest files |
| `IX_FileEntries_Extension` | FileEntries | `(SessionId, Extension)` | Category breakdown |
| `IX_FolderEntries_Session_Size` | FolderEntries | `(SessionId, TotalSizeBytes DESC)` | Top-N largest folders |
| `IX_FolderEntries_Session_Parent_Size` | FolderEntries | `(SessionId, ParentNormalizedPath, TotalSizeBytes DESC)` | Folder tree, drill-down, child counts (v13) |
| `IX_DuplicateGroupMembers_FileEntryId` | DuplicateGroupMembers | `(FileEntryId)` | `FileEntries` cascade delete (v15) |
| `IX_DuplicateGroups_RepresentativeFileEntryId` | DuplicateGroups | `(RepresentativeFileEntryId)` | `FileEntries` cascade delete (v15) |
| `IX_DuplicateErrors_FileEntryId` | DuplicateErrors | `(FileEntryId)` | `FileEntries` cascade delete (v15) |
| `IX_DuplicateSignatures_FileEntryId` | DuplicateSignatures | `(FileEntryId)` | `FileEntries` cascade delete (v15) |
| `IX_DuplicateErrors_Run_File` | DuplicateErrors | `(RunId, FileEntryId)` | Errored-only duplicate filter (v15) |

`DatabaseSchema.cs` defines further duplicate, quarantine, journal, health, and path indexes; the table above covers the scan-query and cascade paths.

### Unique constraints

- `FolderEntries(SessionId, NormalizedFullPath)` — schema v11 enforces one normalized folder identity per scan session
- `FileEntries(SessionId, NormalizedFullPath)` — migration v6 protects one normalized path per scan session
- `DuplicateSignatures(FileEntryId, Method, Algorithm)` — one cached signature per file per method
- `DuplicateOperationJournal(OperationId)` — one journal record per operation

---

## NuGet packages

| Project | Package | Version | Purpose |
|---------|---------|---------|---------|
| Core | `CommunityToolkit.Mvvm` | 8.4.0 | MVVM source generators |
| Core | `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.0 | DI interfaces |
| Core | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | `ILogger<T>` |
| Core | `SixLabors.ImageSharp` | 3.1.12 | Image duplicate decoding and perceptual hashing |
| Platform.Windows | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | Logging |
| Platform.Windows | `System.Management` | 9.0.5 | Windows drive-health queries |
| Storage | `Microsoft.Data.Sqlite` | 9.0.4 | SQLite access |
| Storage | `SQLitePCLRaw.bundle_e_sqlite3` | 3.0.3 | Patched native SQLite bundle |
| Storage | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | Logging |
| UI | `Microsoft.WindowsAppSDK` | 1.6.250205002 | WinUI 3 runtime + XAML compiler |
| UI | `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.1742 | WinUI 3 build tools |
| UI | `CommunityToolkit.Mvvm` | 8.4.0 | MVVM source generators |
| UI | `H.NotifyIcon.WinUI` | 2.3.1 | System-tray integration |
| UI | `Microsoft.Extensions.DependencyInjection` | 10.0.0 | Full DI container |
| UI | `Microsoft.Extensions.Logging` | 10.0.0 | Logging infrastructure |
| UI | `Microsoft.Extensions.Logging.Debug` | 10.0.0 | Debug output provider |
| UI | `System.CommandLine` | 2.0.7 | CLI/headless command parsing |
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
