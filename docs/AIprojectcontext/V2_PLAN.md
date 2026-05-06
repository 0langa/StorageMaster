# StorageMaster v2 Plan

Source of truth: deployed v1.7.4 repository snapshot. This is a forward plan from the actual code, not the older speculative audit.

## Current strengths to preserve

Layering is mostly clean; Core contains contracts and domain services, Storage/Platform/UI depend inward. SQLite migrations are additive and version-stamped atomically. WAL and batched inserts are in place. Managed scanner and Turbo scanner share the same persisted model. Results and Duplicates already use paging/lazy loading in important paths. Deletion is centralized behind `IFileDeleter`. Duplicate deletion has keeper validation, size/mtime revalidation, audit JSON, quarantine, and restore. CI exists for .NET/Rust and release signing is optional.

## Remaining correctness/hardening work

| ID | Finding in current code | Fix |
|---|---|---|
| H-01 | `ScanOptions.MaxParallelism` and `DbBatchSize` are not guarded inside `FileScanner`; direct callers can hang/throw with invalid values | validate in `ScanAsync`: root exists, `MaxParallelism>=1`, `DbBatchSize>=1`; add tests |
| H-02 | `FolderSizeAggregator.Compute` uses `ToDictionary`; duplicate `FullPath` throws | group by path case-insensitively and sum/choose direct bytes deterministically |
| H-03 | `FileDeleter.EmptyRecycleBin` ignores `SHQueryRecycleBin` and `SHEmptyRecycleBin` return values | check HRESULT/return, throw/return failed `DeletionOutcome` on error |
| H-04 | `IFileOperation` calls have no explicit STA-thread boundary | run shell COM recycle operations on an STA helper thread or document/verify COM apartment safety |
| H-05 | `FileDeleter.EstimateSize` recursively traverses directories without timeout/item cap | add bounded estimator with cancellation, max entries, symlink skip, and best-effort partial result |
| H-06 | `ParallelDeleteAsync` creates `SemaphoreSlim` without disposal | wrap in `using`/`await using` compatible pattern |
| H-07 | `AdminService.RestartAsAdmin` calls `Environment.Exit(0)` immediately | add app shutdown service or explicit flush path before exit |
| H-08 | default excluded paths hardcode `C:\` | derive Windows drive from `Environment.SpecialFolder.Windows` |
| H-09 | `IRecycleBinInfoProvider` is declared inside `RecycleBinCleanupRule.cs` | move to `Core/Interfaces` and keep namespace stable |
| H-10 | browser cache discovery is duplicated in cleanup rule and Smart Cleaner | extract shared path service |
| H-11 | bare catches remain in filesystem helpers | keep best-effort behavior but avoid swallowing fatal exceptions; log where useful |
| H-12 | `FileEntries` has no uniqueness on `(SessionId, FullPath)` | add migration/index or document duplicate-path semantics |
| H-13 | status fields are unrestricted TEXT | add application-level validation now; CHECK constraints only if migration can be safe |
| H-14 | no VACUUM/optimize after large session deletes | add `PRAGMA optimize`; consider manual vacuum prompt/maintenance path |
| H-15 | installer uses LocalAppData install path but still requests admin | remove admin where possible or justify with prereq/runtime install path |
| H-16 | publish is framework-dependent and installer only installs Windows App Runtime | add .NET Desktop Runtime detection/bootstrapper or self-contained publish decision |

## UX work required by current product state

| Area | Current state | v2 requirement |
|---|---|---|
| Settings | long stacked settings form | category tiles; clicking a tile opens overlay/flyout with focused settings, validation, descriptions, reset defaults per category |
| Duplicates | feature-rich but dense | add in-app explanations for methods, thresholds, keeper policy, scope modes, quarantine/recycle/permanent consequences; improve empty and first-run guidance |
| Cleanup | grouped category toggles exist | add explanation/help text for every category, risk, method, dry-run, and threshold; ensure full-screen alignment across all cards |
| Dashboard | quick links and drive health exist | make it a true quickstart: scan most relevant drive, last results, duplicate review, smart clean, scheduled jobs, update/status/diagnostics |
| Results | paged files/folders/errors and lazy tree exist | keep paging; avoid any full-tree rebuild on navigation; add more explicit loading/empty/error states |
| Accessibility | XAML has no `AutomationProperties.*` | add Name/HelpText to all interactive controls, focus restoration, Narrator verification, high contrast/text-scale pass |

## Performance/scale plan

First measure with a benchmark project and a large synthetic dataset. Preserve paging in Results/Duplicates. Add repository query tests around filters/sorts. Investigate `UpdateFolderTotalsAsync` cost and consider temp-table batch update. Add indexes only when query plans prove need. Keep scan progress throttled. Make export/previews cancellable everywhere, continuing the current Duplicates pattern.

## Deduplication v2 plan

Keep existing strategy interface. Add method availability display and per-method settings that actually flow into strategy constructors/options without app restart. Treat `AudioFingerprint` as unsupported until implemented or remove it. Add strategy tests for image/video availability, cache invalidation, file-changed races, and threshold behavior. Improve duplicate cleanup rule so the Cleanup page clearly distinguishes previous dedupe findings from a fresh dedupe run.

## Safety policy

Default deletion should remain Recycle Bin. Quarantine should remain preferred for duplicate deletions where restore metadata exists. General cleanup quarantine either needs restore metadata or should not be exposed as restorable. Permanent delete must be visually separated and require stronger confirmation. All deletion results must continue to write `CleanupLog` with enough audit metadata to reconstruct what happened.

## Testing plan

Current static count is 113 tests. Add ViewModel tests for Scan/Results/Duplicates/Cleanup/Settings, CLI tests for all command validation branches, scheduler tests with `schtasks.exe` mocked/wrapped, platform tests around deletion sentinels and Shell32 return handling, migration tests for schema v6+, and benchmark smoke tests. CI should keep `dotnet format`, `dotnet build`, `dotnet test`, `cargo fmt`, `cargo test`, and Rust release build.

## v2 acceptance criteria

No known data-loss path from junctions/symlinks. Invalid scanner settings cannot hang. Every destructive action has clear explanation and confirmation. Every setting/action/category has inline help. Results navigation remains responsive on large sessions. Settings are tile/overlay based. XAML controls have accessibility metadata. Installer either bootstraps required runtimes or publishes self-contained. Release artifacts and updater trust behavior are documented and tested.
