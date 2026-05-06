# StorageMaster v2 Plan

Source of truth: current v1.9.0 repository snapshot. This is a forward plan from the actual code, not older speculative audit.

## Current strengths to preserve

Layering is mostly clean; Core contains contracts and domain services, Storage/Platform/UI depend inward. SQLite migrations are additive and version-stamped atomically through schema v6. WAL, normalized path indexes, and batched inserts are in place. Managed scanner and Turbo scanner share validated scan options and the same persisted model. Results, Duplicates, and Space Map use paging/lazy or bounded queries in important paths. Deletion is centralized behind `IFileDeleter`. Duplicate deletion has keeper validation, size/mtime revalidation, audit JSON, file quarantine, and restore. CI exists for .NET/Rust and release signing is optional.

## Remaining correctness/hardening work

| ID | Finding in current code | Fix |
|---|---|---|
| H-01 | Done in v1.9.0: `ScanOptionValidator` validates root, normalizes paths, clamps `MaxParallelism`/`DbBatchSize` for managed and Turbo scanners | Continue wiring validation messages into UI/CLI surfaces |
| H-02 | Done in v1.9.0: `FolderSizeAggregator.Compute` groups paths case-insensitively and sums duplicates | Add million-folder benchmark coverage |
| H-03 | Done in v1.9.0: `FileDeleter.EmptyRecycleBin` checks shell HRESULTs | Add shell abstraction tests around simulated HRESULTs |
| H-04 | `IFileOperation` calls have no explicit STA-thread boundary | run shell COM recycle operations on an STA helper thread or document/verify COM apartment safety |
| H-05 | Done in v1.9.0: size estimation is bounded, cancellable, and reparse-point safe | Surface partial-estimate metadata if needed |
| H-06 | Done in v1.9.0: `ParallelDeleteAsync` disposes its `SemaphoreSlim` | No follow-up |
| H-07 | `AdminService.RestartAsAdmin` calls `Environment.Exit(0)` immediately | add app shutdown service or explicit flush path before exit |
| H-08 | Done in v1.9.0: default excluded paths derive from `Environment.SpecialFolder.Windows` and use boundary-safe matching | No follow-up |
| H-09 | `IRecycleBinInfoProvider` is declared inside `RecycleBinCleanupRule.cs` | move to `Core/Interfaces` and keep namespace stable |
| H-10 | browser cache discovery is duplicated in cleanup rule and Smart Cleaner | extract shared path service |
| H-11 | bare catches remain in filesystem helpers | keep best-effort behavior but avoid swallowing fatal exceptions; log where useful |
| H-12 | Done in v1.9.0: schema v6 adds `NormalizedFullPath` and unique file path protection per session | Consider path identity columns in future |
| H-13 | Partly done in v1.9.0: scan status parsing is tolerant | Extend tolerant parsing to every status enum field |
| H-14 | Done in v1.9.0: session delete runs `PRAGMA optimize` | Consider manual vacuum prompt/maintenance path |
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

Keep existing strategy interface. Method availability display exists; default review mode and video frame sampling settings flow into runtime behavior. Treat `AudioFingerprint` as unsupported legacy enum data unless a future strategy is added. Add deeper strategy tests for image/video availability, cache invalidation, file-changed races, and threshold behavior. Improve duplicate cleanup rule so the Cleanup page clearly distinguishes previous dedupe findings from a fresh dedupe run.

## Safety policy

Default deletion should remain Recycle Bin. Quarantine should remain preferred for duplicate deletions where restore metadata exists. General cleanup quarantine either needs restore metadata or should not be exposed as restorable. Permanent delete must be visually separated and require stronger confirmation. All deletion results must continue to write `CleanupLog` with enough audit metadata to reconstruct what happened.

## Testing plan

Current static count is 113 tests. Add ViewModel tests for Scan/Results/Duplicates/Cleanup/Settings, CLI tests for all command validation branches, scheduler tests with `schtasks.exe` mocked/wrapped, platform tests around deletion sentinels and Shell32 return handling, migration tests for schema v6+, and benchmark smoke tests. CI should keep `dotnet format`, `dotnet build`, `dotnet test`, `cargo fmt`, `cargo test`, and Rust release build.

## v2 acceptance criteria

No known data-loss path from junctions/symlinks. Invalid scanner settings cannot hang. Every destructive action has clear explanation and confirmation. Every setting/action/category has inline help. Results navigation remains responsive on large sessions. Settings are tile/overlay based. XAML controls have accessibility metadata. Installer either bootstraps required runtimes or publishes self-contained. Release artifacts and updater trust behavior are documented and tested.
