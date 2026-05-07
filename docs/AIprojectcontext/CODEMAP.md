# StorageMaster Codemap

Version: 2.0.0-prerelease. Compact source map for the deployed repository. Source code is authoritative.

## Root

| Path | Purpose |
|---|---|
| `StorageMaster.sln`, `.slnx` | solution files |
| `global.json` | .NET SDK 8.0 latest patch |
| `Directory.Build.props` | centralized semantic version (`2.0.0-prerelease`) plus numeric assembly version (`2.0.0.0`) |
| `Directory.Build.targets` | forces executable WinUI XAML compiler |
| `README.md`, `CHANGELOG.md` | public docs; changelog current through 2.0.0-prerelease |
| `.github/workflows/ci.yml` | PR/push build/test/format/Rust checks |
| `.github/workflows/release.yml` | tag release pipeline, optional signing |
| `installer/StorageMaster.iss` | Inno Setup, `AppVersion=2.0.0-prerelease`, `PrivilegesRequired=lowest`, checks .NET Desktop Runtime 8 x64, stages Windows App Runtime 1.6 MSIX prereq, `DefaultDirName={localappdata}\Programs\StorageMaster` |
| `turbo-scanner/` | Rust binary crate, package version 2.0.0-prerelease |

## Project files and packages

| Project | Target | Key packages |
|---|---|---|
| `StorageMaster.Core` | `net8.0` | CommunityToolkit.Mvvm 8.4.0, DI.Abstractions 10.0.0, Logging.Abstractions 10.0.0, SixLabors.ImageSharp 3.1.12 |
| `StorageMaster.Storage` | `net8.0` | Microsoft.Data.Sqlite 9.0.4, Logging.Abstractions 10.0.0 |
| `StorageMaster.Platform.Windows` | `net8.0-windows10.0.19041.0` | Logging.Abstractions 10.0.0, System.Management 9.0.5 |
| `StorageMaster.UI` | `net8.0-windows10.0.19041.0` | WindowsAppSDK 1.6.250205002, SDK BuildTools 10.0.26100.1742, Toolkit.Mvvm, H.NotifyIcon.WinUI 2.3.1, Microsoft.Extensions.* 10.0.0, System.CommandLine 2.0.7 |
| `StorageMaster.Tests` | `net8.0-windows10.0.19041.0` | xUnit 2.9.3, runner 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, Moq 4.20.72, FluentAssertions 7.2.0 |

`System.CommandLine` is referenced, but the current `CommandRunner` uses manual option parsing.

`src/StorageMaster.Core/Safety/SafeTempDirectory.cs` guards app-created temporary recursive deletes and refuses paths outside direct `%TEMP%` children.

## `StorageMaster.Core/Models`

| Type | Key facts |
|---|---|
| `AppSettings` | persisted JSON settings; deletion, thresholds, scan, theme, retention, cleanup toggles, updater, tray/scheduler, dedupe defaults, FFmpeg path, extension data |
| `ScanOptions` | `RootPath`, `MaxParallelism=4`, `DbBatchSize=500`, environment-aware Windows default excludes, `FollowSymlinks=false`, `IncludeHiddenFiles=false`, `DeepScan=false`; normalized by `ScanOptionValidator` before managed/turbo scans |
| `FileEntry` | required file snapshot fields; computed `ParentPath` |
| `FolderEntry` | direct/total bytes, counts, reparse/access denied, computed nullable `ParentPath` |
| `ScanSession` | root/status/timestamps/totals/access denied/error; computed `Duration` |
| `ScanProgress` | current path, files/folders/bytes/errors, completion flag, UTC timestamp |
| `ScanError` | `OccurredAt` field, not `OccurredUtc` |
| `CleanupSuggestion` | transient action; has optional `AuditDataJson` |
| `CleanupResult` | status/bytes/time/dry-run/failed paths/error |
| `CleanupProgress` | positional record `(Completed, Total, CurrentTitle)` |
| Duplicate models | `DuplicateRun`, `DuplicateSignature`, `DuplicateGroup`, `DuplicateGroupMember`, `DuplicateError`, `DuplicateRunSummary`, query/filter/sort models, `QuarantinedFile` |
| File identity models | `FileIdentity`, `FileSnapshot` for race detection |
| FFmpeg models | `FfmpegPathNormalizer`, `FfmpegToolResolver`, `FfmpegToolPaths` |
| Update models | `UpdateInfo`, `UpdateFailureKind`, `InstallerTrustVerificationResult` |

Important defaults in `AppSettings`: `CleanProgramLeftovers=true`, `CleanLargeOldFiles=false`, `CleanFontCache=false`, `CleanPrefetchFiles=false`, `CheckOnStartup=true`, `EnableLowDiskNotifications=true`, `LowDiskWarningPercent=15`, `LowDiskCriticalPercent=5`, `DuplicateMinimumSizeMb=1`, `DuplicateKeeperPolicy=Newest`, pHash feature toggles false, `DuplicateMaxVideoDurationSeconds=1800`, `ScanHistoryRetentionDays=365`.

## `StorageMaster.Core/Interfaces`

| Interface | Implementations / notes |
|---|---|
| `IFileScanner` | `FileScanner`, `TurboFileScanner`; scan plus top files/folders streams |
| `IScanRepository` | `ScanRepository`; sessions, file/folder insert/search/count/paging, category breakdown, folder tree, stale mark, delete file/session |
| `ISpaceMapRepository` | `SpaceMapRepository`; direct-child treemap queries, largest files under folder, comparable scan lookup, delta queries |
| `IScanErrorRepository` | `ScanErrorRepository`; log, full read, paged read, count |
| `ICleanupRule` | 17 rule classes |
| `ICleanupEngine` | `CleanupEngine`; `ExecuteAsync(suggestions, dryRun, DeletionMethod, progress, ct)` |
| `IFileDeleter` | `FileDeleter`; RecycleBin/Permanent/Quarantine |
| `ICleanupLogRepository` | `CleanupLogRepository`; append and recent read, includes `AuditDataJson` |
| `ISmartCleanerService` | `SmartCleanerService` |
| Dedup | `IDuplicateFinderService`, `IDuplicateRepository`, `IDuplicateCandidateProvider`, `IDuplicateDetectionStrategy`, `IDuplicateDeletionService`, `IDuplicatePreviewService`, `IDuplicateKeeperPolicy`, `IFileContentHasher`, `IFileSnapshotProvider`, `IFileIdentityProvider` |
| Platform/app | `IDriveInfoProvider`, `IDriveHealthProvider`, `IDriveHealthRepository`, `IAdminService`, `IInstalledProgramProvider`, `IInstallerTrustVerifier`, `INotificationService`, `IScheduledTaskService`, `ISettingsRepository`, `ISettingsSnapshotProvider`, `IUpdateService`, `ICommandRunner`, `IScanResultDeletionService` |

`IRecycleBinInfoProvider` and `RecycleBinInfo` are declared at bottom of `Cleanup/Rules/RecycleBinCleanupRule.cs`.

## `StorageMaster.Core/Scanner`

| File | Purpose |
|---|---|
| `FileScanner.cs` | managed parallel BFS scanner; channel capacity 1024; 300 ms progress; queue flush locks; post-scan folder aggregation; cancellation/failure session states |
| `FileTypeCategorizor.cs` | static extension-to-`FileTypeCategory` map; spelling in code is `Categorizor` |
| `FolderSizeAggregator.cs` | bottom-up total propagation; normalizes, folds duplicate/mixed-case paths, handles drive roots and malformed paths defensively |
| `ScanOptionValidator.cs` | validates root existence, canonicalizes paths, clamps scan parallelism/batch sizes, and provides boundary-safe exclusion matching |
| `ScanScopeResolver.cs` | builds normalized exclusions from defaults, system folders when `SkipSystemFolders`, and custom settings; returns empty for deep scan |

## `StorageMaster.Core/Cleanup`

| File | Purpose |
|---|---|
| `CleanupEngine.cs` | sequential rule analysis/execution, deletion delegation, audit logging |
| `ScanResultDeletionService.cs` | deletes a `FileEntry` through `IFileDeleter` |

Cleanup rules and IDs:

| Class | RuleId | Category |
|---|---|---|
| `RecycleBinCleanupRule` | `core.recycle-bin` | `RecycleBin` |
| `TempFilesCleanupRule` | `core.temp-files` | `TempFiles` |
| `DownloadedInstallersRule` | `core.downloaded-installers` | `DownloadedInstallers` |
| `CacheFolderCleanupRule` | `core.cache-folders` | `CacheFolders` |
| `BrowserCacheCleanupRule` | `core.browser-cache` | `BrowserCache` |
| `WindowsUpdateCacheRule` | `core.windows-update-cache` | `WindowsUpdateCache` |
| `DeliveryOptimizationRule` | `core.delivery-optimization` | `DeliveryOptimization` |
| `WindowsErrorReportingRule` | `core.windows-error-reporting` | `WindowsErrorReporting` |
| `UninstalledProgramLeftoversRule` | `core.program-leftovers` | `ProgramLeftovers` |
| `LargeOldFilesCleanupRule` | `core.large-old-files` | `LargeOldFiles` |
| `ThumbnailCacheRule` | `core.thumbnail-cache` | `ThumbnailCache` |
| `IconCacheRule` | `core.icon-cache` | `IconCache` |
| `FontCacheRule` | `core.font-cache` | `FontCache` |
| `DnsClientCacheRule` | `core.dns-cache` | `DnsCache` |
| `PrefetchFilesRule` | `core.prefetch-files` | `PrefetchFiles` |
| `MicrosoftStoreLogsRule` | `core.store-logs` | `StoreLogs` |
| `DuplicateFilesCleanupRule` | `duplicates.cleanup` | `DuplicateFiles` |

## `StorageMaster.Core/SmartCleaner`

`SmartCleanerService.cs`: direct filesystem junk analyzer/cleaner, not tied to scan sessions. Scans temp folders, browser caches, Windows Update cache, WER/crash dumps, Delivery Optimization, Explorer thumbnail cache, DirectX shader cache. Cleaning writes synthetic `CleanupLog` entries.

## `StorageMaster.Core/Deduplication`

| File | Purpose |
|---|---|
| `DuplicateFinderService.cs` | pipeline orchestration, cache reuse, bounded hashing, persistence |
| `ExactSha256Strategy.cs` | exact method, same-size candidates, partial-hash prefilter, auto-selectable |
| `NormalizedTextStrategy.cs` | normalized text SHA-256, review-only, algorithm v2 |
| `ImagePHashStrategy.cs` | ImageSharp perceptual hash, review-only |
| `VideoPHashStrategy.cs` | FFmpeg/ffprobe frame pHash, 10 samples, review-only |
| `DuplicateKeeperPolicy.cs` | marks keeper and selected members by policy |
| `DuplicateDeletionService.cs` | validates keeper/member state, delete/quarantine, audit, restore |
| `FileContentHasher.cs` | SHA-256 and partial hash |
| `ExactDuplicateSignatureProvider.cs`, `NormalizedTextSignatureProvider.cs` | legacy signature providers still present |

## `StorageMaster.Core/Update`

| File | Purpose |
|---|---|
| `GitHubUpdateService.cs` | check releases, find installer asset, download, digest/signature validation, elevated launch |
| `SemanticVersion.cs` | internal semver parser/comparer; supports prerelease ordering |
| `UpdateException.cs` | exception with `UpdateFailureKind` |

## `StorageMaster.Storage`

| File | Purpose |
|---|---|
| `StorageDbContext.cs` | single SQLite connection, WAL PRAGMAs, migration lock, write lock, atomic version stamping |
| `Schema/DatabaseSchema.cs` | schema version 7 DDL/migrations |
| `Repositories/ScanRepository.cs` | session/file/folder queries, normalized file path upsert, paged results, folder tree, deletion/stale marking |
| `Repositories/SpaceMapRepository.cs` | Space Map direct children, largest files under folder, comparable sessions, delta insights |
| `ScanErrorRepository.cs` | scan error logging/paging/count |
| `CleanupLogRepository.cs` | audit log |
| `SettingsRepository.cs` | JSON settings load/save plus current snapshot |
| `DuplicateRepository.cs` | duplicate runs/results/signatures/groups/members/errors/quarantine/candidates |

## `StorageMaster.Platform.Windows`

| File | Purpose |
|---|---|
| `FileDeleter.cs` | dry-run, Recycle Bin batch via `IFileOperation`, read-only permanent delete, file quarantine only, bounded size estimate, DNS flush, Recycle Bin empty |
| `TurboFileScanner.cs` | hidden Rust process host, shared scan option validation, JSONL parse, stderr scan errors, fallback to managed scanner |
| `DriveInfoProvider.cs` | fixed/network/removable drive data |
| `DriveHealthProvider.cs` | WMI/MSFT_PhysicalDisk/Win32_DiskDrive health snapshots with Unknown/Unsupported fallbacks |
| `RecycleBinInfoProvider.cs` | `SHQueryRecycleBin` wrapper |
| `AdminService.cs` | admin check and `runas` restart with optional `--deep-scan`, then `Environment.Exit(0)` |
| `InstalledProgramProvider.cs` | reads uninstall registry keys in HKLM/HKCU and WOW6432Node |
| `KnownFolders.cs` | Downloads folder resolver through Shell32 |
| `FileIdentityProvider.cs` | NTFS identity lookup |
| `FileSnapshotProvider.cs` | race-detection snapshot |
| `InstallerTrustVerifier.cs` | PowerShell `Get-AuthenticodeSignature` JSON probe |
| `Interop/Shell32Interop.cs` | `SHEmptyRecycleBin`, `SHQueryRecycleBin`, `SHGetKnownFolderPath` |
| `Interop/FileOperationInterop.cs` | COM interfaces for `IFileOperation`/shell items |

## `StorageMaster.UI`

| Area | Files |
|---|---|
| Startup/DI | `Program.cs`, `App.xaml(.cs)`, `ServiceBootstrapper.cs`, `MainWindow.xaml(.cs)` |
| Infrastructure | `CommandRunner`, `NavigationService`, `NavigationRoutes`, `DialogService`, `DesktopNotificationService`, `DuplicatePreviewService`, `ScheduledTaskService`, `StartupRegistrationService`, `LocalDiagnosticsService` |
| Pages/ViewModels | Dashboard, Scan, Results, Duplicates, Cleanup, SmartCleaner, SpaceMap, DriveHealth, Settings |
| Converters | `BoolNegation`, `BoolToChevron`, `BoolToVisibility`, `ByteSize`, `FilePathToBitmapImage` |

Navigation tags: `Dashboard`, `Scan`, `Results`, `Duplicates`, `Cleanup`, `SmartCleaner`, `SpaceMap`, `DriveHealth`, `Settings`.

## UI ViewModel command map

| VM | Commands |
|---|---|
| `DashboardViewModel` | go to Scan/Results/Duplicates/Cleanup/SmartCleaner/SpaceMap/DriveHealth/Settings, scan drive |
| `ScanViewModel` | request elevation, start/cancel scan, view results |
| `ResultsViewModel` | clear/apply/category filters, sort files/folders, load more files/folders/errors, delete file, delete session |
| `CleanupViewModel` | analyze, execute cleanup |
| `DuplicatesViewModel` | run/cancel analysis, page groups/errors, export CSV/JSON/HTML, keeper shortcuts, select/deselect current page, delete selected, restore quarantine, cancel export, open export folder |
| `SettingsViewModel` | remove excluded path, save/reset, purge old history, export diagnostics, refresh/new/save/delete scheduled job, cancel download |
| `SpaceMapViewModel` | load sessions, drill folders, filter nodes, export CSV/HTML/PNG, reveal/copy paths, route to cleanup/duplicates/results |
| `SmartCleanerViewModel` | analyze, clean |

## `turbo-scanner`

Rust args: `--path/-p`, `--threads/-t` default 0, `--min-size` default 0, `--skip-hidden`. Output is one JSON object per line with `path`, `size`, `modified_unix`, `created_unix`, `is_dir`. Errors are `WARN:` lines on stderr.

## Tests

Current test count by static marker count is above the 1.9.6 baseline and includes drive-health repository and prerelease updater coverage. Areas: cleanup rules/engine, critical fixes, deduplication, scanner/aggregator, settings FFmpeg helpers, repositories, drive health, updater. No broad ViewModel test suite is present.
