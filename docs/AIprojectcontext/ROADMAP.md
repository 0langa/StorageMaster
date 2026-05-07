# StorageMaster Roadmap

Current repository state: v2.0.0-prerelease on 2026-05-07. This roadmap replaces older speculative planning text with the actual shipped state.

## Shipped baseline

| Release | Actual state in repo |
|---|---|
| 1.4.0 | managed parallel scanner, Rust Turbo scanner, SQLite/WAL, initial cleanup rules, CI/CD installer path |
| 1.5.0 | Smart Cleaner, Results interactions, folder tree, Turbo direct-size fix, extra cleanup rules, admin deep-scan preservation |
| 1.6.0 | duplicate engine: exact SHA-256, normalized text, image pHash, optional video pHash, quarantine mode, Duplicates page, duplicate cleanup rule |
| 1.7.0 | CLI/headless, tray, low-disk notifications, Windows Task Scheduler, duplicate previews, quarantine restore UI, startup registration, 17 cleanup rules |
| 1.7.1 | fixed Duplicates Visibility binding crash; committed Rust lockfile |
| 1.7.2 | fixed updater Download & Install button state |
| 1.7.4 | Results hardening: paged scan errors, lazy folder tree, cached repeat navigation, duplicate filter/export/previews hardening, dashboard/cleanup layout refresh, settings toast cancellation |
| 1.8.0 | Settings redesign into category-tile hub with modal overlays, accessibility annotations, UI preferences layer, shared styles |
| 1.9.0 | Space Map native treemap, Scan Delta Insights, scanner validation/exclusion hardening, schema v6 normalized path integrity, folder aggregation hardening, deletion safety fixes |
| 1.9.6 | Runtime rollback: kept 1.9.x features while returning release builds to framework-dependent Windows App SDK 1.6 with staged runtime MSIX prereq |
| 2.0.0-prerelease | Drive Health & Storage Sentinel, schema v7 health snapshots, prerelease-safe versioning/updater/release flow, .NET runtime setup check, elevated CLI deep-scan worker |

## Current feature inventory

Present: WinUI shell; Dashboard/Scan/Results/Duplicates/Cleanup/Smart Cleaner/Space Map/Drive Health/Settings pages; managed scanner; Turbo scanner host; SQLite schema v7; 17 cleanup rules; Recycle Bin/permanent/delete and file quarantine deletion paths; Smart Cleaner; dedupe strategies exact/text/image/video; preview/export/restore UX; CLI/headless commands including health reports; tray; low-disk and drive-health monitor; scheduled jobs via `schtasks.exe`; GitHub release updater; optional signing in release workflow; 140+ tests.

Not present: WebView2/D3 visualization assets, Serilog rolling logs, broad ViewModel tests, app-local .NET runtime bootstrapper, dual-arch release assets, complete ARM64 installer flow.

## Next priorities

| Priority | Work | Why |
|---:|---|---|
| 1 | Fix remaining correctness/data-loss edges in `FileDeleter`, scanner option validation, folder aggregation, and Shell32 return checking | prevents destructive or hanging behavior |
| 2 | Accessibility pass for remaining pages: keyboard/focus, text scaling, high contrast, Narrator verification | Settings is done; Dashboard, Scan, Results, Duplicates, Cleanup remain |
| 3 | Results/Duplicates/Cleanup performance profiling on million-file sessions | current code has paging/lazy loading but no benchmark suite |
| 4 | Structured logging + diagnostics package | app uses Debug logger, startup crash log, prereq log, and local diagnostics, not production rolling file logs |
| 5 | Installer/runtime hardening lab | setup checks .NET Desktop Runtime 8 and stages Windows App Runtime 1.6, but clean-VM install coverage should stay mandatory |
| 6 | Space Map scale polish: virtualization benchmarks and richer reports | native treemap and PNG screenshot export exist; next work is scale/UX depth |
| 7 | Test expansion: ViewModels, platform wrappers, CLI, scheduler, updater failure modes | current tests are useful but not UI-heavy |

## Suggested release plan

| Target | Scope |
|---|---|
| 1.9.x | accessibility for remaining pages, Results/Duplicates/Space Map scale hardening, benchmark project |
| 2.0.x | diagnostics/logging, first-run experience, broader export/reporting, optional ARM64 release support |

## Implementation rules for future agents

Keep Core free of WinUI/SQLite/Win32 dependencies. Route cross-layer work through existing interfaces. Never delete without UI confirmation or CLI `--confirm`. Keep migrations additive and version-stamped in the same transaction. Use `StorageDbContext.WriteLock` for transactional writes. Preserve audit logging for every cleanup/duplicate deletion. Avoid `.Result`/`.Wait()`. UI state updates from background work require `DispatcherQueue`. Treat reparse points as dangerous unless explicitly opted in. Add tests with every behavior change.
