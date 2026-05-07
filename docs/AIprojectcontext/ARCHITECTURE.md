# StorageMaster Architecture

Version: 1.9.6. Stack: .NET 8, WinUI 3, Windows App SDK 1.6.250205002, SQLite schema v6, optional Rust `turbo-scanner`.

StorageMaster is a layered Windows disk analyzer/cleanup app. `StorageMaster.Core` is the inward-facing domain layer; UI, storage, and platform projects depend on Core interfaces. Core currently contains pure domain logic plus scanner/cleanup/dedup/update services, but no WinUI, SQLite, Win32, or subprocess hosting.

## Projects

| Project | Target | Role | Depends on |
|---|---:|---|---|
| `StorageMaster.Core` | `net8.0` | Models, interfaces, managed scanner, cleanup rules, Smart Cleaner, dedup strategies, update service | none |
| `StorageMaster.Storage` | `net8.0` | SQLite context/schema/repositories | Core |
| `StorageMaster.Platform.Windows` | `net8.0-windows10.0.19041.0` | deletion, drives, admin, registry, Recycle Bin, file identity/snapshot, installer trust, Turbo host | Core |
| `StorageMaster.UI` | `net8.0-windows10.0.19041.0` | WinUI 3 unpackaged shell, pages/ViewModels, CLI/headless, tray, scheduler, update UI | Core, Storage, Platform |
| `StorageMaster.Tests` | `net8.0-windows10.0.19041.0` | xUnit tests | Core, Storage, Platform |
| `turbo-scanner` | Rust 2021 | Native JSONL file enumerator using `jwalk` | independent binary |

Version metadata is centralized in `Directory.Build.props` (`StorageMasterVersion=1.9.6`). UI uses `WindowsPackageType=None`, `WindowsAppSDKSelfContained=false`, `SelfContained=false`, runtime IDs `win-x86;win-x64;win-arm64`, min OS `10.0.17763`. Release pipeline currently publishes `win-x64` and stages the Windows App Runtime 1.6 x64 MSIX prereq beside the app.

## Runtime startup

`Program.Main` is `[STAThread]`. If first arg is `--cli`, it allocates/attaches a console, builds DI, runs `ICommandRunner`, sets `Environment.ExitCode`, and exits before WinUI. If first arg is `--headless`, it attaches to the parent console and does the same. Otherwise it initializes WinUI/COM wrappers, sets a `DispatcherQueueSynchronizationContext`, and starts `App`.

`App` reads startup flags `--deep-scan` and `--start-in-tray`, logs unhandled exceptions to `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log`, launches `MainWindow`, applies persisted theme, and runs a silent GitHub update check when `AppSettings.CheckOnStartup` is true.

`MainWindow` hosts `NavigationView` + `Frame`, sets the icon, sizes to 85% of the display work area clamped to `1200x750..1800x1100`, starts on `DashboardPage`, owns tray behavior, and checks low disk space every 15 minutes when enabled. Tray menu: Open, Run Smart Clean, Start Scan, Review Duplicates, Pause Notifications for 12 h, Exit.

## DI graph

`ServiceBootstrapper.BuildServices()` wires the app. Important lifetimes:

| Lifetime | Registrations |
|---|---|
| Singleton | `StorageDbContext`, repositories, `ISpaceMapRepository`, platform services, `FileScanner`, `TurboFileScanner`, 17 cleanup rules, `CleanupEngine`, Smart Cleaner, duplicate strategies/services, scheduler, notifications, updater, navigation, dialogs, command runner, `MainWindow` |
| Transient | Dashboard, Results, Duplicates, Cleanup, Settings, SmartCleaner, SpaceMap VMs |
| Singleton VM | `ScanViewModel`, because it owns scan lifetime/cancellation |

`IFileScanner` resolves to managed `FileScanner`; `ScanViewModel` explicitly receives both managed and turbo scanners and chooses based on settings/UI availability.

## Core data model

`ScanSession` owns `FileEntry`, `FolderEntry`, `ScanError`, and `DuplicateRun` rows by `SessionId`. `CleanupSuggestion` is transient and becomes a persisted `CleanupLog` row only after execution. `AppSettings` is persisted as JSON under `Settings.Key='AppSettings'` and uses `[JsonExtensionData]` for forward-compatible unknown fields.

Enums present in code:

| Enum | Values |
|---|---|
| `ScanStatus` | `Running`, `Completed`, `Cancelled`, `Failed` |
| `FileTypeCategory` | `Unknown`, `Document`, `Image`, `Video`, `Audio`, `Archive`, `Executable`, `SourceCode`, `Database`, `Temporary`, `SystemFile`, `Installer`, `Log`, `Cache` |
| `CleanupCategory` | `RecycleBin`, `TempFiles`, `DownloadedInstallers`, `CacheFolders`, `LargeOldFiles`, `DuplicateFiles`, `LogFiles`, `BrowserCache`, `WindowsUpdateCache`, `ProgramLeftovers`, `DeliveryOptimization`, `WindowsErrorReporting`, `Custom`, `ThumbnailCache`, `IconCache`, `FontCache`, `DnsCache`, `PrefetchFiles`, `StoreLogs` |
| `CleanupRisk` | `Safe`, `Low`, `Medium`, `High` |
| `DeletionMethod` | `RecycleBin`, `Permanent`, `Quarantine` |
| `DuplicateMethod` | `ExactSha256`, `NormalizedText`, `ImagePHash`, `VideoPHash`; `AudioFingerprint` remains a tolerated legacy enum token with no registered strategy |
| `KeeperPolicy` | `Newest`, `Oldest`, `ShortestPath`, `LongestPath` |
| `ScheduledJobKind` | `Scan`, `ScanAndReport`, `CleanupAnalyze`, `CleanupExecuteSafe` |
| `ScheduledJobFrequency` | `Daily`, `Weekly` |
| `ThemePreference` | `Default`, `Light`, `Dark` |

`DefaultDuplicatesReviewMode` now preselects the duplicate method filter. `DuplicateVideoFrameThreshold` is clamped and used as the video pHash frame sample count. `AudioFingerprint` is not exposed in UI/CLI and has no registered strategy.

## Managed scan flow

`FileScanner.ScanAsync`:

1. Runs `ScanOptionValidator.NormalizeAndValidate`: root must exist, root/exclusions are canonicalized, `MaxParallelism` and `DbBatchSize` are clamped, default Windows exclusions are derived from `Environment.SpecialFolder.Windows`.
2. Starts `PeriodicTimer(300 ms)` for `ScanProgress`.
3. Walks with one BFS producer feeding a bounded `Channel<string>` capacity `1024`.
4. Starts validated `MaxParallelism` consumers.
5. `ProcessDirectory` emits `FileEntry` and `FolderEntry` records, skips reparse-point subdirectories unless `FollowSymlinks=true`, skips hidden files unless `IncludeHiddenFiles`/`DeepScan`.
6. Flushes file queue when `FileBuffer.Count >= DbBatchSize`; folder queue when `FolderBuffer.Count >= DbBatchSize / 5`. Flush drains are serialized by per-buffer `SemaphoreSlim`.
7. Loads all folders, runs `FolderSizeAggregator.Compute`, updates totals, logs buffered scan errors, marks session `Completed`.
8. On cancellation it flushes current buffers and marks `Cancelled`; on other exception it marks `Failed` then rethrows. Flush semaphores are disposed after scan shutdown.

`FolderSizeAggregator.Compute` normalizes folder paths with an ordinal-ignore-case comparer, sums duplicate/mixed-case rows, skips malformed paths, sorts by descending length, and propagates direct bytes to parents. It handles drive roots and missing parents defensively.

## Turbo scan flow

`TurboFileScanner` checks for `turbo-scanner.exe` in `AppContext.BaseDirectory`. If absent, it falls back to managed `FileScanner`.

When available it starts hidden process:

```text
turbo-scanner.exe --path <RootPath> --threads <MaxParallelism>
```

C# validates the same `ScanOptions` as the managed scanner, drains stderr to Debug logs and persisted `ScanError` rows for WARN/ERROR lines, wraps stdout with a 1 MB `StreamReader`, parses JSONL into `TurboRecord`, filters exclusions/hidden records in C# using the same boundary-aware exclusion matcher, writes to a bounded `Channel<TurboRecord>` capacity `2000`, and batch-inserts files every `500` and folders every `100`. It accumulates per-parent direct sizes in C# and patches folders before aggregation. Non-zero process exit marks the scan `Failed`; cancellation kills the whole process tree.

Rust CLI also supports `--min-size` and `--skip-hidden`, but `TurboFileScanner` does not pass those switches; hidden filtering is handled after JSONL read.

## Cleanup architecture

`ICleanupRule.AnalyzeAsync` is read-only. `CleanupEngine.GetSuggestionsAsync` runs registered rules sequentially in DI order and yields suggestions. `ExecuteAsync` runs selected suggestions sequentially, maps every target path to `DeletionRequest`, calls `IFileDeleter.DeleteManyAsync`, aggregates one `CleanupResult` per suggestion, reports `CleanupProgress(Completed, Total, CurrentTitle)`, and writes `CleanupLog`.

Registered rules: Recycle Bin, Temp files, Downloaded installers, Application caches, Browser cache, Windows Update cache, Delivery Optimization, WER, Program leftovers, Large old files, Thumbnail cache, Icon cache, Font cache, DNS client cache, Prefetch files, Microsoft Store logs, Duplicate files.

Cleanup UI stores per-category toggles in `AppSettings`, then filters emitted suggestions by enabled category. Most rule classes do not read their own toggle directly.

Smart Cleaner does not require a scan session. `SmartCleanerService.AnalyzeAsync` scans temp roots, browser caches, Windows Update cache, WER/crash dumps, Delivery Optimization, thumbnail cache, DirectX shader cache, and returns `SmartCleanGroup` objects. `CleanAsync` deletes selected group paths via `IFileDeleter` and logs synthetic cleanup suggestions/results.

## Deletion architecture

`FileDeleter` is the only platform deletion implementation. Dry-run estimates size and does not delete. Special target paths: `::RecycleBin::` calls `SHEmptyRecycleBin`; `::DnsFlush::` runs `ipconfig /flushdns`.

For all-real Recycle Bin batches, it uses one `IFileOperation` COM batch with `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI`; on batch failure it falls back to per-file. Permanent delete recursively deletes directories while deleting reparse-point directories as links only. Quarantine moves files to `%LOCALAPPDATA%\StorageMaster\Quarantine\<runId>\...` and records the moved path through duplicate deletion flow.

`EstimateSize` recursively enumerates directory contents without an explicit timeout or item cap.

## Deduplication architecture

`DuplicateFinderService` coordinates strategy passes:

1. Validate requested methods exist and are available.
2. Create `DuplicateRun`.
3. For each strategy, build a `DuplicateCandidateQuery`, fetch candidates from `DuplicateRepository`, load valid cached signatures, compute stale/missing signatures with bounded concurrency, group matches, apply keeper policy, and persist signatures/groups/members/errors.
4. Mark the run `Completed`, `Cancelled`, or `Failed`.

Strategies registered in DI:

| Method | Algorithm | Version | Auto-select | Candidate shape |
|---|---|---:|---:|---|
| `ExactSha256` | `SHA-256` | 1 | yes | same-size buckets; partial-hash prefilter |
| `NormalizedText` | `TEXT-NORM-SHA256` | 2 | no | text/source/log extensions; no same-size requirement |
| `ImagePHash` | `IMAGE-PHASH-DCT64` | 1 | no | image category/extensions; ImageSharp DCT hash |
| `VideoPHash` | `VIDEO-PHASH-FRAMES` | 1 | no | video category/extensions; requires ffmpeg + ffprobe |

Duplicate signatures cache validity stores algorithm version, source size, source modified UTC, and optional NTFS identity. Duplicate deletion refuses groups without an existing keeper, validates selected member size/mtime before delete/quarantine, logs audit JSON, marks deleted members, and supports quarantine restore.

## Storage

SQLite DB: `%LOCALAPPDATA%\StorageMaster\storagemaster.db`. `StorageDbContext` owns one connection, enables WAL, and exposes a shared `WriteLock` used by write repositories.

PRAGMAs: `journal_mode=WAL`, `synchronous=NORMAL`, `foreign_keys=ON`, `temp_store=MEMORY`, `cache_size=-32000`.

Schema `CurrentVersion = 6` with migrations:

| Version | Adds |
|---:|---|
| 1 | `SchemaVersion`, `ScanSessions`, `FileEntries`, `FolderEntries`, `CleanupLog`, `Settings`, base indexes |
| 2 | `ScanErrors` |
| 3 | duplicate runs/signatures/groups/members/errors and duplicate indexes |
| 4 | `CleanupLog.AuditDataJson` |
| 5 | duplicate signature cache metadata, `QuarantinedFiles`, more duplicate indexes |
| 6 | normalized path columns/indexes, unique file path protection per session, path search indexes |

Each migration batch and schema-version stamp run in one transaction. `ScanRepository.DeleteSessionAsync` runs `PRAGMA optimize` after large session deletes.

`SpaceMapRepository` reads scan data by selected folder using direct-child queries, largest-file-under-folder queries, previous comparable scan lookup, and scan delta queries. It treats moves/renames as removed + added unless a future identity layer proves equivalence.

## UI pages

| Page | ViewModel | Current behavior |
|---|---|---|
| Dashboard | `DashboardViewModel` | drive health, recommended action, latest scan summary, quick links |
| Scan | `ScanViewModel` | choose path/drive, deep scan, turbo scanner, elevation, progress/cancel/view results |
| Results | `ResultsViewModel` | paged largest files/folders/errors, file types, lazy folder tree, filters/sorts, copy/open/delete file, delete session |
| Cleanup | `CleanupViewModel` | session-based suggestions, grouped category toggles, dry run, Recycle Bin/permanent execution |
| Duplicates | `DuplicatesViewModel` | session selection, scope/category/extensions/methods, paged groups/errors, previews, selection, deletion/quarantine/restore, CSV/JSON/HTML export |
| Smart Cleaner | `SmartCleanerViewModel` | direct junk analysis/cleaning without scan session |
| Space Map | `SpaceMapViewModel` | selected completed scan treemap, folder drill-down, size/kind filters, selection details, CSV/HTML/PNG export, scan delta insights |
| Settings | `SettingsViewModel` | deletion, thresholds, scan, theme, retention, excluded paths, cleanup defaults, dedupe defaults, FFmpeg, tray, scheduler, updates, diagnostics |

Main navigation and all primary pages expose page-level automation names. Settings retains field-level automation help text; Space Map has named scan selection, canvas, and export controls. Remaining accessibility work is deeper UI automation coverage, not unlabeled primary navigation.

## CLI/headless

Entry forms:

```text
StorageMaster.UI.exe --cli <command>
StorageMaster.UI.exe --headless <command>
```

Supported commands are manually parsed in `CommandRunner`: `scan`, `report last-scan`, `dedupe scan`, `cleanup analyze`, `cleanup execute`, `jobs run --id`. Exit codes: `0` success, `1` unexpected/cancelled, `2` invalid args, `3` missing cleanup `--confirm`, `4` not found or not elevated.

## Updater and release pipeline

`GitHubUpdateService` queries `0langa/StorageMaster` GitHub releases, accepts asset name `StorageMaster-{version}-win-x64-Setup.exe`, enforces HTTPS downloads, validates GitHub asset digest when provided, verifies Authenticode signature/timestamp when signed, and blocks unsigned installers only when `RequireSignedUpdates=true`. Installer launch uses `runas`.

CI has `ci.yml` for PR/push: restore, `dotnet format --verify-no-changes`, build solution, test solution, `cargo fmt --check`, `cargo test`, Rust release build. `release.yml` runs on `v*.*.*` tags, builds Rust, tests, publishes win-x64, copies optional FFmpeg bundle, optionally signs binaries/installer from secrets, builds Inno installer, verifies signatures when signing is enabled, and generates checksums/release notes.

## Current architectural limitations to preserve in docs

No WebView/D3 visualization exists; Space Map is a native WinUI Canvas treemap with PNG export through `RenderTargetBitmap`. No Serilog file logger exists; logging is Debug provider plus startup crash log and local diagnostics. `FileTypeCategorizor` is intentionally misspelled in code. `IRecycleBinInfoProvider` is declared in `RecycleBinCleanupRule.cs`, not under `Core/Interfaces`. Installer installs to LocalAppData with `PrivilegesRequired=lowest`. Release builds are .NET and Windows App SDK framework-dependent; the installer stages `Microsoft.WindowsAppRuntime.1.6.msix` plus `Install-WindowsAppRuntime.ps1`.
