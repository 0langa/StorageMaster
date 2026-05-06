# StorageMaster Technical Reference

Version: 1.7.4. This reference matches the deployed repository snapshot.

## Build prerequisites

Windows 10 1809 build 17763 or later, .NET SDK 8.0.x, Visual Studio/MSBuild with Windows app workload for WinUI, stable Rust for `turbo-scanner`, Inno Setup 6 for installer builds.

Core/test build:

```powershell
dotnet restore StorageMaster.sln
dotnet build StorageMaster.sln -c Release
dotnet test StorageMaster.sln -c Release --no-build
```

Turbo build:

```powershell
cargo build --release --manifest-path turbo-scanner/Cargo.toml --target x86_64-pc-windows-msvc
```

UI publish profile:

```powershell
dotnet publish src/StorageMaster.UI/StorageMaster.UI.csproj /p:PublishProfile=win-x64 -c Release /p:UseXamlCompilerExecutable=true
```

Publish is framework-dependent: `SelfContained=false`, `WindowsAppSDKSelfContained=false`. Inno installs Windows App Runtime from `prereqs`, but no .NET runtime bootstrapper is defined in the installer script.

## Data locations

| Data | Path |
|---|---|
| SQLite DB | `%LOCALAPPDATA%\StorageMaster\storagemaster.db` |
| Startup crash log | `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log` |
| Quarantine | `%LOCALAPPDATA%\StorageMaster\Quarantine\<runId>\...` |
| Exports | `%LOCALAPPDATA%\StorageMaster\exports` |
| Update downloads | `%TEMP%\StorageMaster\Updates` |

## CLI/headless

```text
StorageMaster.UI.exe --cli scan --path <abs-path> [--turbo] [--deep] [--json <file>]
StorageMaster.UI.exe --cli report last-scan [--json <file>] [--csv <file>]
StorageMaster.UI.exe --cli dedupe scan --session <id> --methods exact,text,image,video --min-size <mb> [--extensions csv] [--json <file>]
StorageMaster.UI.exe --cli cleanup analyze --session <id> [--json <file>]
StorageMaster.UI.exe --cli cleanup execute --session <id> --rules <csv> --recycle-bin|--quarantine --confirm
StorageMaster.UI.exe --headless jobs run --id <job-id>
```

Exit codes: `0` success, `1` unexpected/cancelled, `2` invalid args, `3` missing `--confirm`, `4` not found/not elevated. `--headless` is mainly used by Task Scheduler. `cleanup execute --quarantine` routes general cleanup suggestions through `IFileDeleter` quarantine mode, but only duplicate deletion records quarantine rows for restore.

## AppSettings reference

Persisted as JSON in table `Settings` under `Key='AppSettings'`.

| Area | Properties/defaults |
|---|---|
| Deletion | `PreferRecycleBin=true`, `DryRunByDefault=false` |
| Thresholds | `LargeFileSizeMb=500`, `OldFileAgeDays=365` |
| Scan | `DefaultScanPath="C:\\"`, `ScanParallelism=4`, `ShowHiddenFiles=false`, `SkipSystemFolders=true`, `UseTurboScanner=false`, `ExcludedPaths=[]`, `ScanHistoryRetentionDays=365` |
| Theme | `Theme=Default` (`Default`, `Light`, `Dark`) |
| Cleanup toggles | Recycle/Temp/DownloadedInstallers/Cache/BrowserCache/WindowsUpdate/DeliveryOptimization/WER/ProgramLeftovers/Thumbnail/Icon/DNS/StoreLogs true; LargeOldFiles/Font/Prefetch false; `ClearEntireDownloads=false` |
| Updates | `CheckOnStartup=true`, `IncludePrerelease=false`, `RequireSignedUpdates=false` |
| Tray/notifications | `MinimizeToTray=false`, `StartTrayOnLogin=false`, `EnableLowDiskNotifications=true`, warning 15%, critical 5%, persisted debounce map |
| Scheduler | `ScheduledTasksEnabled=false`, `ScheduledJobs=[]` |
| Dedupe | `DuplicateMinimumSizeMb=1`, `DuplicateKeeperPolicy=Newest`, normalized/image/video toggles false, image threshold 6, video frame threshold 8, max video duration 1800 s, `FfmpegPath=""` |

Settings page validation exists for scan path and FFmpeg path. Save also manages startup registration and scheduled-job state.

## ScanOptions reference

`ScanOptions`: `RootPath` required, `MaxParallelism=4`, `DbBatchSize=500`, `ExcludedPaths=DefaultExcludedPaths`, `FollowSymlinks=false`, `IncludeHiddenFiles=false`, `DeepScan=false`.

Default excluded paths are hardcoded to `C:\Windows\WinSxS` and `C:\Windows\Installer`. `ScanScopeResolver.BuildExcludedPaths(settings, deepScan)` returns empty for deep scan; otherwise includes defaults, Windows/System/SystemX86 when `SkipSystemFolders`, plus custom `ExcludedPaths`.

Important: `FileScanner` itself does not clamp `MaxParallelism` or `DbBatchSize`; UI/CLI callers clamp parallelism. Direct callers must pass `MaxParallelism >= 1` and positive batch size.

## Scanner APIs

`IFileScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>, CancellationToken)` creates a persisted session and returns the final `ScanSession`. Progress is emitted roughly every 300 ms. UI callers must marshal to `DispatcherQueue` before mutating WinUI state.

`GetLargestFilesAsync(sessionId, topN, ct)` and `GetLargestFoldersAsync(sessionId, topN, ct)` wrap repository calls and yield results.

`TurboFileScanner.IsAvailable` is true only when `turbo-scanner.exe` exists beside the app. Missing binary falls back to managed scanner. Rust JSONL format:

```json
{"path":"C:\\Users\\Alice\\file.txt","size":12345,"modified_unix":1700000000,"created_unix":1690000000,"is_dir":false}
```

## Cleanup APIs

`ICleanupRule` contract:

```csharp
string RuleId { get; }
string DisplayName { get; }
CleanupCategory Category { get; }
IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(long sessionId, AppSettings settings, CancellationToken ct = default);
```

Rules must not delete. UI filters by enabled categories after suggestions are emitted.

`ICleanupEngine.ExecuteAsync(suggestions, dryRun, deletionMethod, progress, ct)` executes suggestions sequentially, logs every result, and returns `CleanupResult` rows. UI confirmation is handled in page/dialog code; CLI requires `--confirm`.

## Cleanup rule IDs

`core.recycle-bin`, `core.temp-files`, `core.downloaded-installers`, `core.cache-folders`, `core.browser-cache`, `core.windows-update-cache`, `core.delivery-optimization`, `core.windows-error-reporting`, `core.program-leftovers`, `core.large-old-files`, `core.thumbnail-cache`, `core.icon-cache`, `core.font-cache`, `core.dns-cache`, `core.prefetch-files`, `core.store-logs`, `duplicates.cleanup`.

## Smart Cleaner API

`ISmartCleanerService.AnalyzeAsync(progress, ct)` scans known junk locations directly and returns `SmartCleanGroup(Category, Description, IconGlyph, EstimatedBytes, Paths, IsSelected=true)`. It does not create a scan session. `CleanAsync(groups, method, progress, ct)` deletes paths with `IFileDeleter`, returns bytes freed, and logs synthetic cleanup audit entries.

## Deduplication API

`IDuplicateFinderService.RunAsync(DuplicateScanOptions, progress, ct)` creates a `DuplicateRun`. Options include session, min size bytes, methods, extensions, categories, included/excluded paths, max/per-drive concurrency, keeper policy, include reparse points, include hidden files.

Method tokens in CLI: `exact`, `text`, `image`, `video`. `DuplicateMethod.AudioFingerprint` exists but has no strategy and will fail validation if requested programmatically.

`IDuplicateRepository` owns run creation/completion, result persistence, paging, cached signatures, candidates, member deletion marking, quarantine records. `IDuplicateDeletionService.DeleteSelectedAsync` validates keeper and selected files before deletion/quarantine. `RestoreFromQuarantineAsync` moves a quarantined file back to original/target path and marks the row restored.

## Duplicate UI behavior

Duplicates page supports whole session, included folders, or excluded folders; file type categories or custom extensions; optional text/image/video methods; hidden/reparse toggles; keeper policies; paged group/error loading; debounced filters; preview panel; current-page selection; Recycle Bin/permanent/quarantine deletion; quarantine restore; CSV/JSON/HTML exports to `%LOCALAPPDATA%\StorageMaster\exports\dedupe-<runId>-report.<ext>`.

## Storage API and schema

`StorageDbContext` is a singleton; do not create competing app-level contexts for the same DB. Repositories use shared `WriteLock` for transactional writes.

Tables in schema v5:

`SchemaVersion`, `ScanSessions`, `FileEntries`, `FolderEntries`, `CleanupLog`, `Settings`, `ScanErrors`, `DuplicateRuns`, `DuplicateSignatures`, `DuplicateGroups`, `DuplicateGroupMembers`, `DuplicateErrors`, `QuarantinedFiles`.

Notable constraints/indexes: `FolderEntries UNIQUE(SessionId, FullPath)`, `FileEntries(SessionId, SizeBytes DESC)`, `FileEntries(SessionId, Extension)`, `FolderEntries(SessionId, TotalSizeBytes DESC)`, duplicate indexes for run/reclaimable/method/confidence/member/error/signature-cache queries. `FileEntries` has no uniqueness constraint on `(SessionId, FullPath)`. Status columns are TEXT without CHECK constraints.

## Platform APIs

`IFileDeleter`: dry-run estimate, `RecycleBin`, `Permanent`, `Quarantine`, `DeleteManyAsync`. `DeletionRequest` includes optional `QuarantineRunId`; `DeletionOutcome` may include `QuarantinePath`.

`IAdminService.RestartAsAdmin(enableDeepScan)` starts current EXE with `Verb="runas"`, optional `--deep-scan`, then calls `Environment.Exit(0)`.

`IScheduledTaskService` stores job definitions in `AppSettings.ScheduledJobs` and creates/deletes tasks through `schtasks.exe`. Task command is `<exe> --headless jobs run --id <job.Id>` with `/RL HIGHEST`.

`IUpdateService` checks GitHub Releases, downloads installer, validates digest/signature policy, and launches installer elevated.

## UI implementation notes

Pages use CommunityToolkit.Mvvm source generators (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) and a mix of `{x:Bind}` and `{Binding}`. Do not update WinUI state from background tasks without `DispatcherQueue`. Current XAML contains no `AutomationProperties.*`, so accessibility work should add names/help text rather than assuming they exist.

## Testing

Static count: 113 `[Fact]`/`[Theory]` tests across 25 files. CI also runs Rust format/tests/build. No ViewModel test suite exists in the current repo.

## Known gotchas

`FileTypeCategorizor` is misspelled in source. `IRecycleBinInfoProvider` is not in `Core/Interfaces`. `CleanupEngine` is sequential, not concurrent. `FolderSizeAggregator` does not tolerate duplicate paths defensively. `FileDeleter.EstimateSize` is unbounded for large directories. Installer is per-user path but admin-required.
