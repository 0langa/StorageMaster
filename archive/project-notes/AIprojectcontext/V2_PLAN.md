# StorageMaster v2 Plan

Source of truth: current v2.1.3 release metadata plus verified Unreleased hardening changes. This file records remaining work after the v2 implementation.

## Current strengths to preserve

Layering is mostly clean; Core contains contracts and domain services, Storage/Platform/UI depend inward. SQLite migrations are additive and version-stamped atomically through schema v7. WAL, normalized path indexes, drive-health snapshots, and batched inserts are in place. Managed scanner and Turbo scanner share validated scan options and the same persisted model. Results, Duplicates, Space Map, and Drive Health use bounded queries in important paths. Deletion is centralized behind `IFileDeleter`. Duplicate deletion has keeper validation, size/mtime revalidation, audit JSON, file quarantine, and restore. CI exists for .NET/Rust and release signing is optional.

The v2 UI foundation now includes shared style dictionaries, reusable controls (`PageHeader`, `StateView`, `RadialGauge`, `SeverityBadge`, `SettingsCard`, `TreemapTileControl`), grouped navigation with Mica fallback, a guided Scan page, and a new Scan Workspace route.

## Remaining correctness/hardening work

| ID | Finding in current code | Fix |
|---|---|---|
| H-01 | Done in v1.9.0: `ScanOptionValidator` validates root, normalizes paths, clamps `MaxParallelism`/`DbBatchSize` for managed and Turbo scanners | Continue wiring validation messages into UI/CLI surfaces |
| H-02 | Done in v1.9.0: `FolderSizeAggregator.Compute` groups paths case-insensitively and sums duplicates | Add million-folder benchmark coverage |
| H-03 | Done in v1.9.0: `FileDeleter.EmptyRecycleBin` checks shell HRESULTs | Add shell abstraction tests around simulated HRESULTs |
| H-04 | Done in v2.0.0: `IFileOperation` calls run through an STA helper thread when needed | Add shell abstraction tests around simulated COM failures |
| H-05 | Done in v1.9.0: size estimation is bounded, cancellable, and reparse-point safe | Surface partial-estimate metadata if needed |
| H-06 | Done in v2.0.0: app-created temp recursive deletes use `SafeTempDirectory` and refuse paths outside a direct `%TEMP%` child | Keep adding sentinels if new temp cleanup paths are introduced |
| H-07 | Done in v2.0.0: deep scan starts an elevated CLI worker and no longer exits/relaunches the WinUI shell | Consider a richer elevated helper progress bridge |
| H-08 | Done in v1.9.0: default excluded paths derive from `Environment.SpecialFolder.Windows` and use boundary-safe matching | No follow-up |
| H-09 | `IRecycleBinInfoProvider` is declared inside `RecycleBinCleanupRule.cs` | move to `Core/Interfaces` and keep namespace stable |
| H-10 | browser cache discovery is duplicated in cleanup rule and Smart Cleaner | extract shared path service |
| H-11 | Done in v2.1.0: `StorageDbContext` schema-version catch logs Warning; `TurboFileScanner` JSON parse catch logs Debug; filesystem best-effort catches have comments | keep best-effort behavior; add logging if new bare catches are introduced |
| H-12 | Done in v1.9.0: schema v6 adds `NormalizedFullPath` and unique file path protection per session | Consider path identity columns in future |
| H-13 | Partly done in v1.9.0: scan status parsing is tolerant | Extend tolerant parsing to every status enum field |
| H-14 | Done in v1.9.0: session delete runs `PRAGMA optimize` | Consider manual vacuum prompt/maintenance path |
| H-15 | Done in v1.9.6/v2: installer remains per-user/lowest-privilege while staging Windows App Runtime 1.6 MSIX prereq | Continue verifying fresh-machine install behavior |
| H-16 | Done in v2.0.0: setup blocks with actionable text when .NET Desktop Runtime 8 x64 is missing | Decide later whether to ship a full bootstrapper |
| H-17 | Drive Health uses Windows WMI/storage telemetry and explicit Unknown/Unsupported fallbacks | Add broader hardware-lab coverage for NVMe/SATA/USB/network drives |

## UX work required by current product state

| Area | Current state | v2 requirement |
|---|---|---|
| Settings | Done in v2.1.0: category tiles with modal overlay, correct per-category template rendering, all settings accessible (duplicate slider removed, ClearEntireDownloads added) | accessibility and keyboard-nav pass remain |
| Duplicates | feature-rich but dense | add in-app explanations for methods, thresholds, keeper policy, scope modes, quarantine/recycle/permanent consequences; improve empty and first-run guidance |
| Cleanup | grouped category toggles exist | add explanation/help text for every category, risk, method, dry-run, and threshold; ensure full-screen alignment across all cards |
| Dashboard | v2 command-center layout exists | keep tuning responsive behavior, real composition bars, and final visual QA |
| Results | paged files/folders/errors and lazy tree exist; workspace handoff added | keep paging; avoid any full-tree rebuild on navigation; add more explicit loading/empty/error states |
| Accessibility | Primary pages/navigation have automation names; Settings has extensive field-level Name/HelpText coverage | finish remaining interactive controls, focus restoration, Narrator verification, high contrast/text-scale pass |

## Performance/scale plan

First measure with a benchmark project and a large synthetic dataset. Preserve paging in Results/Duplicates. Add repository query tests around filters/sorts. Investigate `UpdateFolderTotalsAsync` cost and consider temp-table batch update. Add indexes only when query plans prove need. Keep scan progress throttled. Make export/previews cancellable everywhere, continuing the current Duplicates pattern.

## Deduplication v2 plan

Keep existing strategy interface. Method availability display exists; default review mode and video frame sampling settings flow into runtime behavior. Treat `AudioFingerprint` as unsupported legacy enum data unless a future strategy is added. Add deeper strategy tests for image/video availability, cache invalidation, file-changed races, and threshold behavior. Improve duplicate cleanup rule so the Cleanup page clearly distinguishes previous dedupe findings from a fresh dedupe run.

## Safety policy

Default deletion should remain Recycle Bin. Quarantine should remain preferred for duplicate deletions where restore metadata exists. General cleanup quarantine either needs restore metadata or should not be exposed as restorable. Permanent delete must be visually separated and require stronger confirmation. All deletion results must continue to write `CleanupLog` with enough audit metadata to reconstruct what happened.

## Testing plan

The Release suite discovers 196 .NET tests and the Rust scanner has three CLI/JSONL contract tests. Add ViewModel tests for Scan/Results/Duplicates/Cleanup/Settings/DriveHealth after those classes are extracted from the WinUI runtime assembly; directly loading `StorageMaster.UI.dll` from the current test host requires WinRT bootstrap initialization and previously prevented CI completion. CLI orchestration, scheduler tests with `schtasks.exe` wrapped, deeper shell abstraction tests, migration tests for schema v7+, and benchmark smoke tests remain needed.

## v2 acceptance criteria

No known data-loss path from junctions/symlinks. Invalid scanner settings cannot hang. Every destructive action has clear explanation and confirmation. Every setting/action/category has inline help. Results navigation remains responsive on large sessions. Settings are tile/overlay based. XAML controls have accessibility metadata. Installer checks .NET Desktop Runtime 8, stages Windows App Runtime 1.6, and keeps the small framework-dependent artifact. Release artifacts and updater trust/prerelease behavior are documented and tested.
