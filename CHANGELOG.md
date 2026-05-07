# Changelog

All notable changes to StorageMaster are documented here.

---

## [1.9.4] — 2026-05-07 — Real Startup Fix

- **Root cause of the 1.9.0–1.9.3 launch failure identified.** WinUI/WinAppSDK types in the body of `Program.Main` (e.g. `Application.Start`, `DispatcherQueueSynchronizationContext`) caused the CLR to resolve and load `Microsoft.UI.Xaml.dll` while JIT-compiling `Main` — *before* `Bootstrap.Initialize` could register the system-installed Windows App SDK 1.8 runtime in the dynamic dependency graph. The runtime DLL loaded from an unregistered location, `ms-appx://` URIs could not be resolved, and the first XAML load threw `XamlParseException: Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`.
- Split `Program.Main` so the GUI initialization (`XamlCheckProcessRequirements`, `Application.Start`, etc.) lives in a separate `LaunchGui` method marked `[MethodImpl(MethodImplOptions.NoInlining)]`. `Main` now only calls `Bootstrap.Initialize` and dispatches; `LaunchGui` is JIT-compiled (and its WinAppSDK type references resolved) only after Bootstrap has succeeded. This is the documented Microsoft pattern for unpackaged framework-dependent WinUI 3 apps with a custom Main.

---

## [1.9.3] — 2026-05-07 — Startup Diagnostic

- Wrapped `Program.Main` GUI initialization in a top-level try/catch that writes the original exception to `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log` before rethrowing. Fixes a diagnostic gap where pre-`App` constructor crashes (e.g. `Bootstrap.Initialize` failures, `XamlCheckProcessRequirements` failures) bypassed the registered `Application.UnhandledException` handler and produced no log entry.

---

## [1.9.2] — 2026-05-07 — Startup Fix (Corrected)

- Fixed persistent launch failure introduced in 1.9.0 and incorrectly addressed in 1.9.1. Root cause: switching to `WindowsAppSDKSelfContained=true` (SDK 1.8) required the WinAppSDK MSIX to be installed on the system, but the installer no longer included it. `Bootstrap.Initialize()` (added in 1.9.1) compounded the failure by throwing on startup when the system MSIX was absent.
- Reverted `WindowsAppSDKSelfContained` to `false` (framework-dependent deployment, matching 1.8.0/1.7.x). The installer now bundles and installs the WinAppSDK 1.8 runtime MSIX as a prerequisite step, matching the proven approach used for SDK 1.6.
- Improved SpaceMapPage error handling: session load and PNG export failures now log via `ILogger<SpaceMapPage>` and display a user-visible status message instead of silently swallowing exceptions.
- Hardened ScanPage folder picker: double-click guard prevents opening multiple concurrent pickers; COM errors surface as a visible error message.

---

## [1.9.1] — 2026-05-07 — Startup Fix

- Fixed critical startup crash on 1.9.0: `ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml` XamlParseException caused by missing `Bootstrap.Initialize()` call. With `WindowsAppSDKSelfContained=true` and `DISABLE_XAML_GENERATED_MAIN`, the XAML-generated bootstrap step was absent; now called explicitly before `XamlCheckProcessRequirements()`.

---

## [1.9.0] — 2026-05-06 — Space Map, Delta Insights, And Hardening

- Added **Space Map**, a new WinUI page with an interactive treemap for completed scans, folder drill-down, breadcrumb context, file/folder filtering, minimum-size filtering, selection details, Explorer/copy actions, and CSV/HTML/PNG export.
- Added **Scan Delta Insights** to compare a completed scan with the previous completed scan of the same root, surfacing growing folders, new large files, and removed files.
- Added `ISpaceMapRepository`, `SpaceMapRepository`, treemap layout models, and core layout tests so the visualization queries scan data by selected folder instead of loading the entire scan into memory.
- Added SQLite schema v6 with normalized path columns/indexes, duplicate file path upsert protection per scan session, tolerant scan status parsing, and `PRAGMA optimize` after session deletes.
- Hardened scanner option validation: root existence checks, canonical root paths, parallelism and DB batch clamping, environment-aware default Windows exclusions, and boundary-safe exclusion matching.
- Hardened folder aggregation to tolerate duplicate and mixed-case Windows paths, drive roots, malformed paths, and missing parents.
- Hardened Turbo Scanner option parity for normalized options, boundary-safe exclusions, and persisted stderr warning/error records as scan errors.
- Hardened deletion behavior: bounded cancellable size estimation, read-only permanent delete handling, shell Recycle Bin HRESULT propagation, disposed parallel delete semaphore, and explicit directory-quarantine rejection.
- Added cleanup policy metadata on suggestions and engine-level blocking for high-risk permanent delete and unsupported quarantine modes.
- Upgraded the UI to Windows App SDK `1.8.260416003`, switched release builds to app-local Windows App SDK deployment, and changed the Inno installer to per-user privilege mode.
- Packaging now includes `turbo-scanner.exe` and copies `ffmpeg.exe`/`ffprobe.exe` from `installer\ffmpeg` or a shared PATH folder when available.
- Wired duplicate review defaults and video frame-count settings into runtime behavior, and changed duplicate JSON/HTML exports to stream pages instead of materializing all groups.
- Centralized product version metadata in `Directory.Build.props` and updated app, installer, manifest, Rust crate, docs, and release references to `1.9.0`.
- Expanded automated tests from 113 to 136 tests covering scanner validation, hidden handling, exclusion boundaries, schema v6, normalized duplicate paths, Space Map/delta repository logic, treemap layout, and deletion hardening.

## [1.8.0] — 2026-05-06 — Settings Redesign And Accessibility

- Redesigned Settings page into a category-tile hub with 9 categories: General & Appearance, Scanning & Performance, Cleanup & Safety, Duplicates & Matching, Results & History, Scheduling & Automation, Background/Tray & Notifications, Updates & Security, Advanced Diagnostics & About.
- Clicking a tile opens a modal overlay editor with category-specific settings, Save, Cancel, Reset Category, and close actions.
- Added real-time search/filter on the hub to find categories quickly.
- Added cancellation-based dirty-state handling: Cancel reverts changes made since the editor opened.
- Added `AutomationProperties.Name` and `AutomationProperties.HelpText` to every toggle, slider, text box, combo box, and button in Settings.
- Added lightweight UI preference properties to `AppSettings`: UI density, reduce animations, default landing page, default results page size, default duplicates review mode, and expand-advanced-by-default.
- Added shared styles for settings tiles, status chips, and empty states in `App.xaml`.
- Updated ROADMAP to reflect shipped 1.7.4 baseline and 1.8.0 scope.

## [1.7.4] — 2026-05-06 — Results Hardening And UX Polish

- Fixed the broken duplicate candidate category filter so image/video/document filters use real stored category values.
- Reworked Results loading to avoid rebuilding the full folder tree and full error list on every navigation.
- Added paged scan error loading, lazy folder-tree expansion, and cached repeat navigation for large sessions.
- Hardened duplicate review with debounced filters, cancellable preview/export work, explicit current-page selection semantics, and cached quarantine data.
- Promoted the executable WinUI XAML compiler path so `dotnet build` works again locally and in CI.
- Refreshed dashboard and cleanup layouts for clearer next steps and better full-width behavior.
- Replaced racing `Task.Delay` settings toasts with cancellable status messaging.

## [1.7.2] — 2026-05-06 — Updater Button Fix

### Fixed

- Fixed Settings update check state so **Download & Install** re-enables after a compatible update is found.
- Added updater command `CanExecute` guards so update buttons stay synchronized during checks and downloads.

---

## [1.7.1] — 2026-05-06 — Duplicates Crash Fix

### Fixed

- Fixed Duplicates navigation crash caused by binding a boolean negation converter to a WinUI `Visibility` property.
- Refreshed repository ignore rules for .NET/WinUI, Rust, installer, diagnostics, and local runtime output.
- Committed the Rust scanner lockfile for reproducible release builds.

---

## [1.7.0] — 2026-05-06 — Power Automation Release

### Added

**CLI / headless interface**
- `--cli` flag allocates a console and runs the command dispatcher; exits without launching the GUI.
- `--headless` attaches to the parent console (used by scheduled tasks).
- Subcommands: `scan`, `report last-scan`, `dedupe scan`, `cleanup analyze`, `cleanup execute`, `jobs run`.
- Structured exit codes: 0 success · 1 unexpected error · 2 bad arguments · 3 missing `--confirm` · 4 not found / not elevated.
- JSON and CSV export flags on all reporting commands.

**System tray**
- H.NotifyIcon.WinUI tray icon with context menu: Open, Run Smart Clean, Start Scan, Review Duplicates, Pause Notifications, Exit.
- **Minimize to tray** setting: close button hides the window instead of exiting.
- **Start in tray** (`--start-in-tray` arg): launch minimized, set by the startup registry entry.

**Low-disk notifications**
- Configurable warning (default 15 %) and critical (default 5 %) free-space thresholds.
- Checked every 15 minutes via `DispatcherQueueTimer`.
- Per-drive per-level 12-hour debounce prevents notification spam.
- Tray balloon shows drive letter, percentage free, and GB remaining.
- "Pause Notifications" tray menu item silences balloons for 12 hours.

**Windows Task Scheduler integration**
- Create, update, and delete scheduled jobs from the Settings page.
- Daily and weekly frequency support with configurable start time.
- Job kinds: Scan, Scan + Report, Cleanup Analyze, Cleanup Execute (safe rules only).
- Each job triggers `StorageMaster.UI.exe --headless jobs run --id <id>`.
- Last-run status and next-run time displayed in the scheduler list.

**Duplicate previews**
- Image groups: thumbnail strip with dimensions and size per member.
- Video groups: FFmpeg keyframe extraction at 3 s (configurable FFmpeg path in Settings); falls back gracefully if FFmpeg is absent.
- Text groups: normalized-text comparison; first differing line highlighted in subtitle.
- Exact groups: file name and byte size shown per member.

**Quarantine restore UI**
- Duplicates page shows a restorable-files panel listing all quarantined files with original path and quarantine timestamp.
- Per-file **Restore** button moves the file back to its original path.

**7 new cleanup rules** (total now 17)
- `ThumbnailCacheRule` — Windows Explorer thumbnail cache database files.
- `IconCacheRule` — icon cache database files; rebuilt automatically by Explorer.
- `FontCacheRule` — Windows font cache service data files.
- `DnsClientCacheRule` — flushes DNS resolver cache via `ipconfig /flushdns`.
- `PrefetchFilesRule` — `C:\Windows\Prefetch` files; rebuilt on next launch.
- `MicrosoftStoreLogsRule` — Store package diagnostic output directories.
- `DuplicateFilesCleanupRule` — surfaces duplicate groups from the last dedupe run as cleanup suggestions.

**Startup registration**
- Settings toggle to register StorageMaster in `HKCU\...\Run` so it starts with Windows.
- Uses `--start-in-tray` so it launches silently to the tray.

### Changed

- `ServiceBootstrapper.BuildServices()` replaces the inline DI setup in `App.xaml.cs`.
- `Program.Main()` now handles CLI dispatch before WinUI initialization.
- Settings page reorganized to include Tray, Notifications, and Scheduler sections.
- Window sizing clamped between 1200×750 and 1800×1100 (up from 900×700).

### Fixed

- `ScheduledTaskService`: `/TR` argument double-quoting bug — paths with spaces in the executable path were wrapped in mismatched quotes, causing `schtasks.exe` to fail. Now uses inner `\"` escaping so `CommandLineToArgvW` parses the value correctly.
- `DuplicatePreviewService`: FFmpeg subprocess switched from `Arguments` string (broken for paths with quotes or spaces) to `ProcessStartInfo.ArgumentList` for correct per-argument escaping.
- `CommandRunner`: invalid CLI inputs (`--path` not found, `--session` not an integer, conflicting deletion flags, no matching rules, unknown duplicate method) now throw `CommandLineException` (exit code 2) instead of untyped `InvalidOperationException` or `FormatException`, so the error is printed and usage is shown rather than generating an unhandled-exception stack trace.

---

## [1.6.1] — 2026-05-05 — Hardening Sweep

- Fixed tray icon not appearing on cold launch.
- Fixed database migration failing on schema version 4 → 5 when `QuarantinedFiles` table already existed.
- Fixed `DuplicateGroupMembers` selection state not persisted after page navigation.
- Fixed admin elevation restart losing the `--deep-scan` flag.

---

## [1.6.0] — 2026-05-04 — Duplicate Analysis

- Full duplicate detection engine: exact SHA-256, normalized-text comparison, image pHash, optional video pHash via FFmpeg.
- Quarantine deletion mode: moves files to `%LOCALAPPDATA%\StorageMaster\Quarantine` instead of Recycle Bin.
- `DuplicateSignatures` table with source-size / mtime / identity validity metadata for incremental re-use.
- Duplicates page with scope filters (folder, extension, category), method selection, group review, and delete/quarantine actions.
- `DuplicateFilesCleanupRule` surfaces duplicate groups in the Cleanup page.

---

## [1.5.0] — 2026-04-30 — Results & Turbo Fix

- Fixed Turbo Scanner `DirectSizeBytes` always zero (accumulate in C# post-pass).
- Results page: sortable columns, folder tree expansion, Open-in-Explorer action, delete-from-results action.
- Smart Cleaner: scan-and-clean flow without a prior session.
- 6 new cleanup rules (Thumbnail Cache, Icon Cache, Font Cache, DNS Cache, Prefetch, Store Logs).
- Admin elevation restart preserves `--deep-scan` flag.

---

## [1.4.0] — 2026-04-28 — Foundation

- Initial public release.
- Parallel BFS scanner (managed C#) + Rust Turbo Scanner (jwalk).
- 10 cleanup rules with Recycle Bin / permanent deletion.
- SQLite schema v1 with WAL mode.
- CI/CD pipeline: test → publish → installer → GitHub Release.
