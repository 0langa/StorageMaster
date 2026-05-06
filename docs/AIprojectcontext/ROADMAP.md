# StorageMaster Roadmap

Current deployed repository state: v1.7.4 on 2026-05-06. This roadmap replaces older v1.3-v1.7 planning text with the actual shipped state.

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

## Current feature inventory

Present: WinUI shell; Dashboard/Scan/Results/Duplicates/Cleanup/Smart Cleaner/Settings pages; managed scanner; Turbo scanner host; SQLite schema v5; 17 cleanup rules; Recycle Bin/permanent/quarantine deletion paths; Smart Cleaner; dedupe strategies exact/text/image/video; preview/export/restore UX; CLI/headless commands; tray; low-disk monitor; scheduled jobs via `schtasks.exe`; GitHub release updater; optional signing in release workflow; 113 tests.

Not present: treemap/sunburst visualization, WebView2/D3 assets, Serilog rolling logs, ViewModel tests, full accessibility annotations, app-local .NET runtime bootstrapper, dual-arch release assets, complete ARM64 installer flow.

## Next priorities

| Priority | Work | Why |
|---:|---|---|
| 1 | Fix remaining correctness/data-loss edges in `FileDeleter`, scanner option validation, folder aggregation, and Shell32 return checking | prevents destructive or hanging behavior |
| 2 | Accessibility pass: `AutomationProperties.Name/HelpText`, keyboard/focus, text scaling, high contrast | current XAML has zero automation properties |
| 3 | Settings UX redesign into category tiles + overlays | current Settings page is a long stacked form |
| 4 | Results/Duplicates/Cleanup performance profiling on million-file sessions | current code has paging/lazy loading but no benchmark suite |
| 5 | Structured logging + diagnostics package | app uses Debug logger and local diagnostics, not production file logs |
| 6 | Installer/runtime hardening | per-user install still requests admin; publish is framework-dependent |
| 7 | Visualization: native charts first, then treemap/sunburst if still needed | currently absent and high effort |
| 8 | Test expansion: ViewModels, platform wrappers, CLI, scheduler, updater failure modes | current tests are useful but not UI-heavy |

## Suggested release plan

| Target | Scope |
|---|---|
| 1.7.5 | hotfixes: `ScanOptions` validation, `SHEmptyRecycleBin`/`SHQueryRecycleBin` return handling, `SemaphoreSlim` disposal in delete path, defensive folder aggregation, updater/CLI edge tests |
| 1.8.0 | accessibility + Settings redesign + UI explanations for every setting/action/category |
| 1.8.1 | Results/Duplicates scale hardening, benchmark project, repository query/index tuning |
| 1.9.0 | diagnostics/logging, installer/runtime cleanup, optional ARM64 release support |
| 2.0.0 | visualization, first-run experience, broader export/reporting, polish pass |

## Implementation rules for future agents

Keep Core free of WinUI/SQLite/Win32 dependencies. Route cross-layer work through existing interfaces. Never delete without UI confirmation or CLI `--confirm`. Keep migrations additive and version-stamped in the same transaction. Use `StorageDbContext.WriteLock` for transactional writes. Preserve audit logging for every cleanup/duplicate deletion. Avoid `.Result`/`.Wait()`. UI state updates from background work require `DispatcherQueue`. Treat reparse points as dangerous unless explicitly opted in. Add tests with every behavior change.
