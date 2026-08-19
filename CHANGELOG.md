# Changelog

All notable changes to StorageMaster are documented here.

---

## [Unreleased]

No unreleased changes.

---

## [2.5.1] — 2026-08-19 — Capture at a real window size

### Added

- `--capture-screens` takes `--width` and `--height`, which set the window's
  logical size. That is what reproduces a display-scale problem: scaling does not
  change layout, which is in logical units — it changes how many logical units the
  screen has. A 2880x1920 panel at 200 % leaves an app about 1440x900, so
  capturing at the default window size showed 23 % more room than a user has and
  would have hidden exactly the clipping being looked for.

  With it, the German interface is confirmed intact at 200 %: the navigation rail
  collapses to icons as designed, all five Results tabs still fit, and no text is
  clipped.

---

## [2.5.0] — 2026-08-19 — Off-screen screen capture

### Added

- `--capture-screens <dir>` renders every page to a PNG with the window parked
  off-screen, so the interface can be reviewed without owning the desktop. Takes
  `--language`, `--theme`, `--pages` and `--settle`; see
  docs/public/VISUAL_REGRESSION.md.

  Reviewing by screenshot needs the foreground window and a session nobody else
  is using, and that failed three times during the localization work — an
  installer opened over the app, then the Windows text-input host kept taking
  focus and blocking clicks. This makes reviewing a matter of reading files.

### Fixed

Found by the harness on its first run, on pages that had never been reviewed:

- `ScanStatus` rendered as "Completed" in the Scan Workspace subtitle.
- `FileTypeCategory` rendered as "Unknown", "SourceCode" and "Executable" in the
  Results type column and both composition breakdowns — fourteen categories, in
  the two places a user reads them most.
- The Spanish settings tiles clipped their summary line.
- The Spanish duplicates summary read "Conservar Más reciente", a capital
  mid-phrase where German already used a colon.

---

## [2.4.1] — 2026-08-19 — Follow Windows by default

### Fixed

- A fresh install now follows the Windows display language instead of defaulting
  to English. 2.4.0 shipped German and Spanish but left the language setting
  pinned to English, so a German user installed the app, saw an English
  interface, and would reasonably conclude there was no German.

  The pin was deliberate while English was the only language there was — following
  Windows then produced the exact defect the setting exists to fix, an English app
  whose switches read "Ein"/"Aus". The note on the property said to move it back
  once real translations shipped; 2.4.0 shipped them without doing so.

  Upgrading users have no language stored and so pick this up: a German install
  that showed English before comes up in German. Anyone who wants English
  regardless can choose it, and that choice is then stored explicitly.

---

## [2.4.0] — 2026-08-19 — German and Spanish

StorageMaster now speaks German and Spanish as well as English. Around a thousand
strings, translated against the terminology Windows itself uses rather than
invented per screen.

### Added

- **Full German (de-DE) and Spanish (es-ES) interface.** Every page, dialog,
  confirmation, empty state, status line and screen-reader description. German
  uses the formal register throughout, matching Windows and Microsoft's German
  style guide; Spanish is es-ES with the inverted opening punctuation.
- **Terminology taken from Windows.** The glossary in `docs/public/LOCALIZATION.md`
  is not a set of translation preferences — it was read out of the German MUI
  resources on a German install, with the resource ids cited. StorageMaster says
  Papierkorb, Laufwerk, Freier Speicherplatz and Größe auf Datenträger because
  that is what File Explorer says next to it.
- **Language picker gains Spanish.** Language names stay in their own language in
  every locale, as Windows does, so a user can find theirs without being able to
  read the one on screen.

### Changed

- Logs, the diagnostics export, CLI output and exception text stay English by
  policy. They are read by whoever is debugging rather than by the user, and
  stable output matters more than locale.
- Settings drop-downs show words instead of enum identifiers. "Run safe cleanup"
  replaces "CleanupExecuteSafe", "Whole session" replaces "WholeSession". These
  were never authored English strings — they were identifiers leaking through.

### Fixed

- Toggle switches read "On"/"Off" in every language, and drive-health badges read
  "Healthy" beside German text.
- Drive-health messages were written as English literals in the platform layer
  despite being read by the user on a drive card.
- The storage-health explanation on the Dashboard sat in the health gauge's 132px
  column and was cut off — in English too, at "low-space…". It spans the card now.
- The navigation pane's status line was clipped rather than wrapped, and the
  settings tiles cut off their summary when a description wrapped to a third line.

### Notes for contributors

- Strings are authored as `.resw` but resolved by `LocalizationCatalog`, not MRT.
  Building `resources.pri` needs a task assembly that ships only with Visual
  Studio, so `dotnet build` and CI cannot produce one. `x:Uid` is therefore
  unavailable; XAML uses `{i18n:Loc Key=...}`.
- `LocalizationTests`, `LocalizationScopeTests` and `EnumDisplayTests` enforce key
  parity, placeholder parity, register, safety wording, the English-only surfaces,
  and that every enum bound to a drop-down has a display string.

---

## [2.3.1] — 2026-08-19 — Visual Verification Pass

The 2.3.0 interface work was never seen rendered before release. Looking at it
found five defects that a clean build, 482 passing tests and verified contrast
maths had all missed.

### Fixed

- Cards, gauges and borders were painted with transparent brushes, so the whole
  page structure was invisible. Each style dictionary merges the palette itself —
  it must, because a merged dictionary cannot see its siblings while it is parsed
  — and merging the same source in several places creates a separate brush object
  per merge. Only the copy reachable from `Application.Resources` was being
  coloured. Every instance is now updated.
- The Mica backdrop tinted the entire shell with the desktop wallpaper, which
  destroyed the step from base to raised surface that the palette exists to
  create. It is off; the window is painted from the palette so it looks the same
  on every machine.
- Choosing Light left the app dark, and choosing an accent silently reverted to
  the saved one a moment later. Setting `RequestedTheme` raises `ActualThemeChanged`,
  whose handler reloaded persisted settings and overwrote the unsaved preview.
- Toggle switches read "Ein"/"Aus" and the navigation entry read "Einstellungen"
  in an otherwise English app. `ApplicationLanguages.PrimaryLanguageOverride` is
  the documented fix but is silently ignored for an unpackaged app — the call
  succeeds and nothing changes. The affected strings are now set explicitly.
- Treemap blocks used categorical colours at full strength. Those are tuned for
  legend swatches and thin bars; at treemap scale they overpowered the panel.
  Fills are blended toward the chart well, the legend keeps the unblended colour,
  and size rather than saturation carries the signal.
- The Type column on Results was clipped by the action buttons beside it.
- With the backdrop disabled and no background on the window root, any pixel
  neither the pane nor the page covered fell through to the bare window and
  rendered black — hard black seams between the panels in light theme.
- The modal veil was an opaque grey fill, which read as a broken page rather than
  as content behind glass. It is translucent now, heavier in dark theme than light
  because the content beneath it is already dark.
- The page now sits on the shell with a rounded leading edge and a hairline
  stroke, instead of butting against the rail and header on hard seams.

### Changed

- Interface language defaults to English rather than following Windows. The app's
  own strings are English and no translation ships yet, so following Windows
  produced exactly the half-translated interface the setting exists to prevent.
  When real translations land this should move back to System.

---

## [2.3.0] — 2026-08-19 — Performance, Correctness, And A Dark-First Interface

### Fixed

- Turbo scans of a whole drive returned nothing. Windows drive roots carry the Hidden and System attributes, and jwalk hands the scan root to the hidden filter as a synthetic child, so `--skip-hidden` pruned the entire walk. Scanning `C:\` reported success with zero files while scanning `C:\Users` worked. The scan root is now always walked.
- The exact-match duplicate pre-filter dropped candidates whose same-size partners came from the signature cache, so real duplicates were silently never grouped.
- Files that are exclusively locked or ACL-denied are reported from their enumeration metadata instead of being dropped from turbo scans. The pagefile was previously missing from every turbo scan.
- Scan sessions abandoned by a crash, a kill or a power loss stayed `Running` forever and were indistinguishable from a scan in progress. They are now reconciled at startup, matching the owning process id together with its start time so a recycled id cannot keep a dead scan looking alive.
- The Settings editor grew past the window and arranged its Reset, Cancel and Save buttons below the viewport, with no way to reach them.
- The cleanup suggestion list virtualised nothing. A list measured with unbounded height has an unbounded viewport, so every row was realised; one rule alone can emit a thousand.
- Severity colours were tuned for dark theme only and failed contrast in light theme.

### Performance

Measured on real data, not estimated.

- Folder-total finalisation after a large scan: about 2.9 hours down to 5.75 seconds for a 213,256-folder session. Path equality could not use any index, because the only one covering it was `COLLATE NOCASE` while the query compared with binary collation.
- Space Map drill-down: 248 ms down to 5 ms per navigation.
- Deleting a scan session: 4 m 17 s down to 0.45 s for 20,000 files. Three tables cascade from `FileEntries` and none of the referencing columns was indexed.
- Managed scan of 46,406 small files: 165 s down to 17.2 s. Each file cost three filesystem round trips; it now costs one, with identity captured once per directory.
- Turbo scanner thread scaling: 4.60 s down to 1.00 s on 55,301 files. Per-entry metadata ran on the serial drain loop, so `--threads` bought almost nothing.
- Repository queries no longer run on the UI thread. Microsoft.Data.Sqlite has no real asynchronous I/O, so awaiting it continued inline — the await looked asynchronous and was not. This is what froze navigation after a large scan.
- Duplicate deletion, duplicate export, cleanup analysis and scan-path validation moved off the dispatcher. Smart Cleaner resolves its allow-listed roots once per group instead of re-enumerating browser profiles for every deleted file.

### Added

- Selectable accents (Aurora, Ember, Verdant, Violet) over a shared dark-first neutral base, applied instantly without a restart. Every accent is contrast-checked in both themes by an automated test, so one cannot ship unreadable.
- A file-rate readout beside the byte rate. Bytes per second is the wrong meter for trees of small files, where throughput is bound by file count rather than bytes.
- An interface language setting. WinUI supplies its own text for built-in controls and follows Windows, which produced an English app with German switches; pinning the language makes both halves agree.
- A space-used readout and a compact action in Settings. Deleting scan history frees pages inside the database but never shrinks the file on disk.
- `STORAGEMASTER_DATA_DIR` redirects the database, so a development build cannot migrate a released install's data past what that install understands.

### Changed

- The interface follows a dark-first instrument-panel identity across every page.
- ETA is offered only for whole-drive scans, where a trustworthy total exists, instead of extrapolating drive usage onto a subtree.
- Drive-health history is bounded rather than growing without limit.

---

## [2.2.1] — 2026-08-18 — Reliability And Data-Safety Overhaul

### Fixed

- Hardened deletion path validation: canonical root guards, explicit Recycle Bin operation flags, expected-file snapshots, quarantine containment, and handle-bound no-follow permanent directory traversal.
- Routed duplicate removal exclusively through the dedicated duplicate workflow. Keeper and selected members are revalidated against live size, timestamp, attributes, identity, and strategy signature; the exact confirmed plan is frozen before execution; quarantine completion atomically records terminal journal and restore state.
- Invalidated cached duplicate signatures when the live file is missing, replaced, or metadata/identity no longer matches.
- Made cleanup analysis isolate failing rules and made execution preserve partial results across cancellation, batch failure, and audit-write failure. High-risk program-leftover suggestions are disabled, unselected, high-risk, and Recycle-Bin-only by default.
- Reworked Smart Cleaner to enumerate explicit files through boundary-held no-follow guards, validate every target against stable identity and its source boundary, preserve partial/cancelled outcomes, and report per-path failures/audit warnings instead of presenting partial cleanup as full success.
- Prevented managed and Turbo scans from losing buffered rows on cancellation/failure; terminal counts now describe confirmed persistence. Turbo folder metrics, cancellation, fatal exits, explicit link-following, root ancestry checks, and lossless one-handle timestamp/attribute/identity output were corrected.
- Replaced shared SQLite connection reuse with independent configured leases, cross-context write coordination, transactional settings updates, strict schema validation, and cancellation-safe rollback.
- Added schema v10 quarantine foreign-key repair, schema v11 normalized folder identity/case-variant collapse, and schema v12 scan-time file identity. All stored timestamps now deserialize as exact UTC; legacy identity-less rows require a fresh scan before destructive use. Migration version is re-read after acquiring the SQLite writer reservation, preventing stale cross-process migration replay.
- Enforced disabled scheduled-job policy inside the runner, distinguished completed/cancelled/failed scans in UI and CLI, stabilized Task Scheduler identity/argument handling, and stopped treating access-denied/transient task queries as proof that a task is absent.
- Made Task Scheduler mutations preflight as one unit and attempt compensating task/settings rollback on failure, surfacing incomplete rollback explicitly. New unattended cleanup jobs start disabled; enabling them requires a dedicated target/rules/schedule confirmation and a versioned plan fingerprint, which the headless runner revalidates.
- Removed redirected `TMP`/`TEMP` from trusted cleanup roots, made medium/high-risk suggestions opt-in, surfaced quarantine-catalog failures as partial outcomes, and stopped describing Recycle Bin/quarantine moves as reclaimed disk space.
- Added generation/lifetime cancellation guards to Results and Duplicates loading so stale navigation, filter, preview, export, analysis, and deletion work cannot overwrite current UI state.
- Hardened Cleanup, Scan, Space Map, Dashboard/Workspace, and Settings lifecycle/error state: incomplete previews cannot unlock deletion, partial outcomes stay visible, routed sessions/config snapshots are immutable, nonterminal scans are not actionable, and expected I/O/cancellation failures remain user-visible instead of escaping UI commands.
- Hardened updater staging and launch: validate temporary download length/hash/trust before publish, then lock, rehash, and revalidate the exact installer before starting it.
- Stopped the in-app updater from requesting elevation. StorageMaster ships a per-user installer under `%LOCALAPPDATA%\Programs`, so the old `runas` launch raised an unnecessary UAC prompt and could have installed the update into a different user's profile.
- Hardened FFmpeg and PowerShell child-process execution with argument lists, bounded output, pipe draining, exit checks, and process-tree cancellation.

### Build and release

- CI now validates Rust format, lint, tests, release build, and the required real Turbo Scanner contract before .NET release gates.
- Release scripts verify tag/product/native-scanner version agreement and require the native scanner in published output.
- Full evidence and remaining limits are tracked in `docs/public/RELIABILITY_AUDIT_2026-08-18.md`.

## [2.2.0] — 2026-07-11 — Quarantine Restore, Turbo Scanner Parity, And Deletion-Safety Hardening

- Quarantine restore now works for generic cleanup, not just duplicate cleanup: schema v9 adds a new "All quarantined files" restorable view on the Duplicates page with one-click restore for every quarantined file. Verified live end-to-end (5/5 restores).
- Turbo Scanner (Rust) now honors hidden-file/folder skipping to match the managed scanner, with a safe fallback for older scanner binaries that predate the flag.
- Settings writes are now atomic, fixing a race where concurrent writers — the low-disk monitor, the scheduler, and the settings editor — could silently drop each other's changes.
- Fixed the Duplicates page collapsing to zero height at 200% display scaling.
- Fixed several launch/runtime defects: cancelling the UAC elevation prompt no longer crashes the app; elevated deep-scan of drive roots like `C:\` no longer fails on argument-quoting; a database failure during scanning no longer deadlocks the scan; enabling "minimize to tray" no longer hides the window on normal launch; and quarantine-restore / per-group deletion failures now show an error dialog instead of crashing.
- Deep scan without elevation now explains why it can't proceed instead of failing silently.
- Fixed byte-size formatting truncation (e.g., 1.9 GB no longer displayed as "1.0 GB").
- Added a duplicate-file recovery journal and beta safety documentation.

---

## [2.1.4] — 2026-06-22 — Hardening & Repository Context Cleanup

- Updated the bundled SQLite native dependency to remove `GHSA-2m69-gcr7-jv3q`.
- Permanent recursive deletion now refuses paths whose reparse-point status cannot be verified.
- Smart Cleaner now propagates cancellation during recursive analysis.
- Added Turbo Scanner CLI and JSONL contract tests.
- Retired the active `docs/AIprojectcontext/` agent context pack and archived it under `archive/project-notes/AIprojectcontext/`.
- Updated `AGENTS.md` so durable project-specific agent memory belongs in RECALL.
- Synchronized README and technical documentation with version 2.1.4 and schema v7.

---

## [2.1.3] — 2026-05-07 — Updater Failure-Mode Test Coverage & Test Naming Cleanup

### Added — test coverage
- `GitHubUpdateServiceFailureModeTests` (12 tests): `CheckAsync` 404/malformed-JSON/empty-list/cancelled/same-version/older-version, `DownloadAsync` insecure URL/404-missing-asset/network-timeout/user-cancellation, `LastCheckResult`/`LastFailureKind` state after errors. Uses fake `HttpMessageHandler` so no real network calls.

### Changed — test file naming
- Renamed `CriticalFixes/C1_FlushLockTests` → `FlushLockTests`, `C2C3_TurboScannerTests` → `TurboScannerCriticalTests`, `C2_FolderSizeAggregatorDupeTests` → `FolderSizeAggregatorDupeTests`, `C4_WriteLockTests` → `WriteLockTests`, `C5_AtomicMigrationTests` → `AtomicMigrationTests`, `C7_JunctionSafeDeleteTests` → `JunctionSafeDeleteTests`, `C8_SuggestionUnsubscribeTests` → `SuggestionUnsubscribeTests`. Historical bug-tracking IDs no longer leak into public test identifiers.
- Renamed `Update/UpdateFailureModeTests` → `Update/GitHubUpdateServiceFailureModeTests` to match the `{SubjectClass}Tests.cs` convention used elsewhere.

### Notes — deferred ViewModel/CLI test coverage
- ViewModel and CLI tests originally planned for this release (CommandRunner, ScheduledTaskService, ScanViewModel, SettingsViewModel, ResultsViewModel) were withdrawn after CI hangs were traced to a fundamental WinUI assembly-load issue: loading `StorageMaster.UI.dll` from a non-WinUI process triggers WinRT/COM initialization that requires `Bootstrap.Initialize` (only the WinUI app entrypoint calls this). Restoring these tests requires extracting the testable classes (`CommandRunner`, `ScheduledTaskService`, ViewModels) into a `StorageMaster.UI.Core` library that has no WinUI runtime dependency. Tracked as a follow-up.

---

## [2.1.2] — 2026-05-07 — Deletion Safety Hardening

- `FileDeleter` now refuses to operate on drive roots (`C:\`) and UNC share roots (`\\server\share`) — any such path returns a clear failure instead of silently wiping an entire volume.
- Fixed `IFileOperation` batch recycle reporting silent partial failures: `FOF_NOERRORUI` causes the shell to skip locked or access-denied files while returning success; each path is now existence-checked after `PerformOperations` and failures are surfaced individually to the user.
- Fixed `DeleteDirectoryRecursiveSafe` race condition: switched from lazy `EnumerateDirectories`/`EnumerateFiles` (which throw `DirectoryNotFoundException` mid-iteration) to eager `GetDirectories`/`GetFiles` snapshots; per-operation `DirectoryNotFoundException` and `FileNotFoundException` are caught so a folder vanishing during delete no longer aborts the entire tree.
- Fixed `TempFilesCleanupRule` path boundary: temp root `C:\Windows\Temp` no longer incorrectly matches `C:\Windows\Temporary Internet Files` — each root is now normalized with a trailing separator before prefix comparison.
- Fixed `DownloadedInstallersRule` with the same separator boundary fix for the Downloads root; the "Clear entire Downloads folder" option now targets individual file paths instead of the folder path itself, so `FileDeleter` can report per-file failures and the Downloads folder is preserved.
- `DuplicateFilesCleanupRule` now checks `File.Exists(keeper.FullPath)` before suggesting duplicate deletion; if the keeper file was removed between scan time and cleanup analysis the entire group is skipped, preventing total data loss.
- 34 new adversarial tests: `IsRootOrUncPrefix` boundary cases, root-guard for all deletion methods, read-only directory delete, vanished-file/directory race conditions, `EstimateSize` edge cases, quarantine collision counter suffix, path boundary correctness for Temp and Downloads rules, keeper-gone duplicate group skip.

---

## [2.1.1] — 2026-05-07 — UI Polish

- Fixed settings category tiles clipping description and badge text on two-line descriptions: the `GridView.ItemsWrapGrid` now measures each item at a fixed 350 px column width so wrapping text correctly reports its required height before the row slot is committed.
- Fixed sidebar status block rendering character-by-character in the 56 px compact pane strip: the `PaneFooter` border is now hidden when the navigation pane is closed or in compact icon-only mode, and restored when the pane expands or is opened by the user.
- Fixed "File Types" category rows in Results appearing to do nothing when clicked: selecting a category now applies the filter AND switches the pivot to the Largest Files tab so the filtered list is immediately visible.

---

## [2.1.0] — 2026-05-07 — Code Hardening, Settings Fixes, And GitHub Infrastructure

- Fixed Settings page so each category tile renders its own settings: `ContentTemplateSelector` was only evaluated once because `Content` was always the same ViewModel reference; code-behind now calls `SelectTemplate` manually whenever `SelectedCategory` or `IsEditorOpen` changes.
- Removed the duplicate scan history retention slider that appeared in both the Scanning and Results & History categories; the slider now only appears in Results & History where it belongs.
- Added the missing "Clear entire Downloads folder" toggle to the Cleanup settings category; the `ClearEntireDownloads` setting was already persisted and applied but had no UI control.
- Fixed `DuplicatesViewModel` to unsubscribe `PropertyChanged` handlers from member items before rebuilding the group list on each page load; replaced anonymous lambda subscriptions with a named handler so unsubscription is correct.
- `StorageDbContext.GetSchemaVersionAsync` now logs a Warning with the full exception before returning 0 when the schema version query fails, making database corruption visible in logs instead of silently treating the database as uninitialized.
- `TurboFileScanner` now logs a Debug entry with the malformed line when JSON deserialization of a Rust scanner output line fails, aiding diagnosis of output contract mismatches between the Rust and C# layers.
- Rewrote `docs/public/ROADMAP.md` and its AI context mirror: removed all stale v1.x milestone planning content and replaced with a v2.x roadmap covering structured logging, ARM64 CI, Drive Health hardware-lab validation, accessibility, and future phases.
- Added GitHub issue templates (`bug_report.md`, `feature_request.md`) and a pull request template (`PULL_REQUEST_TEMPLATE.md`).

---

## [2.0.1] — 2026-05-07 — Responsive UI And Loading Hotfix

- Fixed narrow-window layout failures by letting the WinUI navigation pane auto-collapse, clamping page header text, and removing fragile fixed-width header behavior.
- Moved active scan progress and all live scan metrics near the top of the Scan page, added a usable ETA based on estimated drive used bytes plus smoothed scan throughput, and replaced the permanent "Calculating" state with actionable fallback text.
- Reduced perceived freezes on Results, Scan Workspace, and Space Map by yielding before heavy loads, coalescing Space Map canvas renders, and removing folder-tree N+1 child-count queries during expansion.
- Fixed the Duplicates advanced-options area so expanded filters remain scrollable instead of being clipped by the fixed results layout.
- Reworked Drive Health cards to stop chip/date/recommendation clipping and replaced the hardcoded Dashboard "72% ready" gauge with a described storage health score.

---

## [2.0.0] — 2026-05-07 — UI Overhaul, Drive Health, And Release Hardening

- Added the v2 UI foundation: shared WinUI style dictionaries, reusable page/state/card/gauge/badge/settings controls, grouped shell navigation with Mica fallback, global status strip, refreshed Dashboard and Drive Health pages, guided Scan flow, unified Scan Workspace, and native Space Map tile control.
- Added Drive Health & Storage Sentinel: Core health contracts, Windows WMI/storage provider, SQLite schema v7 health snapshots, Dashboard warnings, dedicated Drive Health page, tray health notifications, and `--cli health report`.
- Split semantic product versioning from numeric assembly/file/manifest versions so stable and prerelease tags build, display, and update-compare correctly.
- Hardened release automation: GitHub Releases are marked prerelease only when the tag contains `-`, release tests run in Release configuration, and the workflow verifies installer size plus staged runtime prereqs.
- Added installer .NET Desktop Runtime 8 x64 detection with an actionable setup failure message, while keeping the smaller framework-dependent Windows App SDK 1.6 deployment path.
- Changed the deep-scan elevation path so the WinUI shell remains unelevated and protected scans start through an elevated CLI worker.
- Hardened shell Recycle Bin deletion by forcing `IFileOperation` through an STA helper thread, removed a remaining `NotImplementedException` converter path, guarded app-created temporary recursive deletes, logged background tray monitor exceptions, and improved Windows App Runtime prereq logging.

---

## [1.9.6] — 2026-05-07 — Runtime Rollback Release

- Rolled WinUI deployment back to the proven framework-dependent Windows App SDK `1.6.250205002` runtime path while keeping the 1.9.x app features and hardening work.
- Removed the Windows App SDK 1.8 redist installer flow and the 1.8-specific bootstrap/UndockedRegFreeWinRT startup workaround. Startup now uses the simpler framework-dependent WinUI path that previously launched reliably.
- Restored installer staging for `Microsoft.WindowsAppRuntime.1.6.msix` plus `Install-WindowsAppRuntime.ps1`, reducing the release installer size compared with the 1.9.5 redist-based package.
- Added installer cleanup for obsolete 1.9.0-1.9.5 app-local WinUI/Windows App Runtime payload files so upgrades cannot keep loading stale 1.8 DLLs from the app directory.

---

## [1.9.5] — 2026-05-07 — Actual Real Startup Fix

- **True root cause of the 1.9.0–1.9.4 launch failure identified.** WinAppSDK 1.8's `Microsoft.WindowsAppSDK.UndockedRegFreeWinRTCommon.targets` only auto-enables the `UndockedRegFreeWinRT` initializer when `WindowsAppSDKSelfContained=true`. This initializer is a `[ModuleInitializer]` that loads `Microsoft.WindowsAppRuntime.dll`, which in turn registers the SxS WinRT activation classes that make `ms-appx:///Microsoft.UI.Xaml/...` URIs resolvable. With our framework-dependent build (`WindowsAppSDKSelfContained=false`), the initializer was silently skipped, leaving WinRT activation contexts unregistered. `Bootstrap.Initialize` succeeded (registering the runtime in the package graph), but the WinRT URI handler chain was never set up — so the very first XAML load (`XamlControlsResources` → `themeresources.xaml`) threw `XamlParseException: Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`.
- Added `<WindowsAppSdkBootstrapInitialize>true</WindowsAppSdkBootstrapInitialize>` and `<WindowsAppSdkUndockedRegFreeWinRTInitialize>true</WindowsAppSdkUndockedRegFreeWinRTInitialize>` to `StorageMaster.UI.csproj`. Both initializers run via `[ModuleInitializer]` before `Main`, which also makes the JIT-order split from 1.9.4 belt-and-suspenders rather than load-bearing.

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
