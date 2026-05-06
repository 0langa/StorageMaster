# StorageMaster — v2.0.0 Development Plan

> **Baseline:** v1.4.1 (2026-04-30)
> **Target:** v2.0.0 — feature-rich, fully hardened, production-grade Windows disk utility.
> **Estimated total effort:** 12–18 weeks (1 developer)

---

## Audit Summary (v1.4.1 findings)

### Critical severity
| # | Issue | Location |
|---|-------|----------|
| C1 | FileScanner: concurrent consumers race on `FlushFileBufferAsync`/`FlushFolderBufferAsync` — no sync | `FileScanner.cs:251-255` |
| C2 | TurboFileScanner: zombie `turbo-scanner.exe` on cancellation — process never killed | `TurboFileScanner.cs:82-206` |
| C3 | TurboFileScanner: exit code ignored — crashed scan marked `Completed` | `TurboFileScanner.cs:206` |
| C4 | WriteLock not acquired on `CreateSessionAsync`, `UpdateSessionAsync`, `DeleteSessionAsync`, `LogResultAsync`, `SaveAsync` → SQLITE_BUSY under concurrency | `ScanRepository.cs`, `CleanupLogRepository.cs`, `SettingsRepository.cs` |
| C5 | Schema version stamp outside migration transaction → crash leaves DB ambiguous | `StorageDbContext.cs:94-100` |
| C6 | `SHGetKnownFolderPath` ignores HRESULT → `AccessViolationException` on failure | `Shell32Interop.cs:32` |
| C7 | `Directory.Delete(recursive: true)` follows junctions/symlinks → destroys data outside target | `FileDeleter.cs:208-213` |
| C8 | `SuggestionItem.PropertyChanged` handler never unsubscribed → memory leak | `CleanupViewModel.cs:205` |

### High severity
| # | Issue | Location |
|---|-------|----------|
| H1 | `ScanOptions.MaxParallelism` accepts 0/negative → hang | `ScanOptions.cs:9-10` |
| H2 | `FolderSizeAggregator.Compute` crashes on duplicate paths | `FolderSizeAggregator.cs:15` |
| H3 | No STA thread guarantee for IFileOperation COM calls | `FileDeleter.cs` |
| H4 | `SHEmptyRecycleBin` return value never checked | `FileDeleter.cs:229` |
| H5 | CleanupEngine runs rules sequentially — unnecessarily slow | `CleanupEngine.cs:36` |
| H6 | `ResultsViewModel` holds `XamlRoot` — MVVM violation blocking testability | `ResultsViewModel.cs:57-59` |
| H7 | `SettingsViewModel` Task.Delay continuation on wrong thread | `SettingsViewModel.cs:103-106` |
| H8 | Double-navigation guard ignores parameters → can't navigate to different sessions | `NavigationService.cs:17` |
| H9 | Zero `AutomationProperties.Name` across all XAML | All pages |
| H10 | 5 of 10 cleanup rules untested; 0 ViewModel tests; 0 platform tests | `tests/` |
| H11 | No PR check CI workflow — broken code can merge without gate | `.github/workflows/` |
| H12 | `FormatBytes` duplicated in 9 files | All cleanup rules |
| H13 | `RestartAsAdmin` calls `Environment.Exit(0)` with no cleanup | `AdminService.cs:35` |
| H14 | `EstimateSize` unbounded directory traversal — blocks for minutes on large dirs | `FileDeleter.cs:250` |
| H15 | `RecyclePathsViaIFileOperation`: COM item leaks on `DeleteItem` exception | `FileDeleter.cs:189` |
| H16 | No empty/loading/error states in most pages | All pages |
| H17 | `FileTypeCategorizor` — misspelled class name | `FileTypeCategorizor.cs` |
| H18 | Installer requests admin for per-user install location; no .NET runtime prereq check | `StorageMaster.iss` |
| H19 | `UpdateFolderTotalsAsync` issues 1 UPDATE per folder — extremely slow at scale | `ScanRepository.cs:304-348` |
| H20 | `async void OnNavigatedTo` in all pages — unhandled exceptions leave page in broken state | All code-behind |
| H21 | `CleanupPage` hardcoded `CategoryOptions[9]` index binding | `CleanupPage.xaml:100` |

### Medium severity
| # | Issue | Location |
|---|-------|----------|
| M1 | `ScanOptions.DefaultExcludedPaths` hardcodes `C:\` drive letter | `ScanOptions.cs:27-30` |
| M2 | `IRecycleBinInfoProvider` defined inside `RecycleBinCleanupRule.cs` | `RecycleBinCleanupRule.cs:58` |
| M3 | Browser cache discovery duplicated between `BrowserCacheCleanupRule` and `SmartCleanerService` | Multiple files |
| M4 | Bare `catch { }` swallows all exceptions including OOM | Multiple rules |
| M5 | `CleanupCategory` has dead enum values (`DuplicateFiles`, `LogFiles`, `Custom`) | `CleanupCategory.cs` |
| M6 | Dates stored as TEXT in SQLite — no range index | Schema |
| M7 | `CleanupLog` no FK to `ScanSessions` | Schema |
| M8 | `FileEntries` no UNIQUE constraint on `(SessionId, FullPath)` | Schema |
| M9 | `ScanSessions.Status` free text — no CHECK constraint | Schema |
| M10 | No VACUUM after large deletes → DB file grows monotonically | Runtime |
| M11 | `NavigationService.GoBack()` exists but never used — back stack leaks memory | `NavigationService.cs` |
| M12 | `{Binding}` (runtime) used throughout DataTemplates instead of `{x:Bind}` | All pages |
| M13 | No `cargo test` in CI | `release.yml` |
| M14 | Uninstaller destroys user DB without warning | `StorageMaster.iss:47` |
| M15 | Empty stub `UnitTest1.cs` inflates count | `UnitTest1.cs` |
| M16 | `SemaphoreSlim` in `ParallelDeleteAsync` never disposed | `FileDeleter.cs:141` |
| M17 | No connection resilience — stale connection after disk unmount | `StorageDbContext.cs` |

---

## Phase overview

```
v1.5.0  Hardening & correctness      ← fix all Critical + High bugs
  │
v1.6.0  Test coverage & CI           ← 150+ tests, PR checks, signing
  │
v1.7.0  Performance & scale          ← DB perf, large scan optimization
  │
v1.8.0  Accessibility & UX           ← WCAG 2.1 AA, empty/error states
  │
v1.9.0  Visualization                ← treemap, sunburst, charts
  │
v2.0.0  Power features + polish      ← CLI, scheduling, duplicates, tray, export
```

---

## Phase 1 — Hardening & Correctness (v1.5.0)

**Goal:** Fix every Critical + High severity bug. Zero data loss paths. Zero crash paths.

| Task | Fixes | Complexity | Files |
|------|-------|------------|-------|
| **1.1** Lock flush buffers in FileScanner | C1 | S | `FileScanner.cs` |
| **1.2** Kill turbo-scanner on cancel + check exit code | C2, C3 | S | `TurboFileScanner.cs` |
| **1.3** Uniform WriteLock on all write operations | C4 | M | `ScanRepository.cs`, `CleanupLogRepository.cs`, `SettingsRepository.cs` |
| **1.4** Atomic migration version stamp | C5 | S | `StorageDbContext.cs` |
| **1.5** Check HRESULT from SHGetKnownFolderPath | C6 | S | `Shell32Interop.cs` |
| **1.6** Junction-safe deletion (detect + skip reparse points) | C7 | S | `FileDeleter.cs` |
| **1.7** Unsubscribe PropertyChanged in CleanupViewModel | C8 | S | `CleanupViewModel.cs` |
| **1.8** Validate ScanOptions (min parallelism=1, batch>0) | H1 | S | `ScanOptions.cs` |
| **1.9** Handle duplicate paths in FolderSizeAggregator | H2 | S | `FolderSizeAggregator.cs` |
| **1.10** Ensure STA thread for IFileOperation | H3 | S | `FileDeleter.cs` |
| **1.11** Check SHEmptyRecycleBin HRESULT | H4 | S | `FileDeleter.cs`, `Shell32Interop.cs` |
| **1.12** Run cleanup rules concurrently (Task.WhenAll) | H5 | S | `CleanupEngine.cs` |
| **1.13** Extract dialog service — remove XamlRoot from VMs | H6 | M | `ResultsViewModel.cs`, new `IDialogService` |
| **1.14** Fix SettingsVM thread marshal | H7 | S | `SettingsViewModel.cs` |
| **1.15** Fix NavigationService parameter-aware dedup | H8 | S | `NavigationService.cs` |
| **1.16** try/finally on COM item in RecyclePathsViaIFileOperation | H15 | S | `FileDeleter.cs` |
| **1.17** Wrap async void OnNavigatedTo in try/catch | H20 | S | All code-behind |
| **1.18** Fix hardcoded CategoryOptions index binding | H21 | S | `CleanupPage.xaml` |
| **1.19** Extract FormatBytes to shared utility | H12 | S | New `SizeFormatter.cs`, all rules |
| **1.20** Rename FileTypeCategorizor → FileTypeCategorizer | H17 | S | Rename + update refs |
| **1.21** Graceful shutdown before admin restart | H13 | S | `AdminService.cs` |
| **1.22** Cap EstimateSize traversal (timeout + size limit) | H14 | S | `FileDeleter.cs` |
| **1.23** Move IRecycleBinInfoProvider to Core/Interfaces | M2 | S | Move file |
| **1.24** Extract browser path discovery to shared service | M3 | S | New `BrowserPathService.cs` |
| **1.25** Replace bare `catch {}` with `catch (Exception ex) when (ex is not OutOfMemoryException)` | M4 | S | Multiple rules |
| **1.26** Remove dead CleanupCategory enum values (or wire up) | M5 | S | `CleanupCategory.cs` |
| **1.27** Dispose SemaphoreSlim in ParallelDeleteAsync | M16 | S | `FileDeleter.cs` |
| **1.28** Use system drive letter for default excluded paths | M1 | S | `ScanOptions.cs` |
| **1.29** Connection resilience — reconnect on stale | M17 | S | `StorageDbContext.cs` |

**Estimated effort:** 3–4 weeks

---

## Phase 2 — Test Coverage & CI (v1.6.0)

**Goal:** 150+ meaningful tests. CI on every PR. Code signing. No dead tests.

| Task | Fixes | Complexity | Details |
|------|-------|------------|---------|
| **2.1** Delete UnitTest1.cs stub | M15 | S | |
| **2.2** Add PR check workflow (test on push/PR to main) | H11 | S | New `ci.yml` |
| **2.3** Add `cargo test` to CI | M13 | S | `release.yml` |
| **2.4** ViewModel unit tests (all 6 VMs) | H10 | L | ~40 tests |
| **2.5** Cleanup rule tests (5 missing rules) | H10 | M | ~30 tests |
| **2.6** Platform service tests (DriveInfo, RecycleBin, KnownFolders) | H10 | M | ~15 tests |
| **2.7** Repository tests (CleanupLog, Settings, ScanError) | H10 | M | ~15 tests |
| **2.8** StorageDbContext migration tests | H10 | S | ~5 tests |
| **2.9** FileTypeCategorizer tests | H10 | S | ~10 tests |
| **2.10** Converter tests (ByteSize, BoolVis, etc.) | H10 | S | ~8 tests |
| **2.11** Integration tests: scan-to-cleanup flow | H10 | M | ~5 tests |
| **2.12** Code coverage collection + badge | — | S | Coverlet + report |
| **2.13** Code signing setup (Authenticode) | — | M | CI secrets + sign step |
| **2.14** Fix flaky timestamp tests (explicit timestamps, not delays) | — | S | `ScanRepositoryTests.cs` |
| **2.15** Tighten mock assertions (verify session IDs, batch sizes) | — | S | All test files |

**Estimated effort:** 3–4 weeks

---

## Phase 3 — Performance & Scale (v1.7.0)

**Goal:** Handle 1M+ file scans. Sub-second UI. Efficient DB.

| Task | Fixes | Complexity | Details |
|------|-------|------------|---------|
| **3.1** Batch UPDATE for folder totals (temp table approach) | H19 | M | `ScanRepository.cs` |
| **3.2** Add covering index on `FileEntries(SessionId, Category, SizeBytes)` | M6 | S | Migration V3 |
| **3.3** Add index on `FileEntries(SessionId, FullPath)` | M8 | S | Migration V3 |
| **3.4** Add UNIQUE constraint `FileEntries(SessionId, FullPath)` | M8 | S | Migration V3 |
| **3.5** CHECK constraints on Status columns | M9 | S | Migration V3 |
| **3.6** CleanupLog FK to ScanSessions | M7 | S | Migration V3 |
| **3.7** Auto-VACUUM after session deletion | M10 | S | `ScanRepository.cs` |
| **3.8** Streaming folder load (IAsyncEnumerable) instead of `List<>` | — | M | `ScanRepository.cs` |
| **3.9** UI virtualization audit — ensure all ListViews use virtualization | — | S | All XAML |
| **3.10** Benchmark suite (BenchmarkDotNet) | — | M | New project |
| **3.11** Replace `SELECT *` with specific columns in hot paths | — | S | `ScanRepository.cs` |
| **3.12** Turbo-scanner timeout (configurable max runtime) | — | S | `TurboFileScanner.cs` |

**Estimated effort:** 2–3 weeks

---

## Phase 4 — Accessibility & UX Polish (v1.8.0)

**Goal:** WCAG 2.1 AA compliant. Keyboard-navigable. Empty/error/loading states everywhere.

| Task | Fixes | Complexity | Details |
|------|-------|------------|---------|
| **4.1** AutomationProperties.Name on all interactive controls | H9 | M | All XAML |
| **4.2** Focus management after navigation + dialog close | H9 | M | All pages |
| **4.3** Keyboard navigation (Tab/Shift-Tab, arrow keys, Enter/Escape) | — | M | All XAML |
| **4.4** High contrast mode support (ThemeResource tokens) | — | S | All XAML |
| **4.5** Text scaling test (125%, 150%, 200%) | — | S | All XAML |
| **4.6** Loading states (shimmer/spinner) on Dashboard, Results, Cleanup | H16 | M | All pages |
| **4.7** Empty states with guidance text | H16 | M | All pages |
| **4.8** Error state banners (InfoBar) on all pages | H16 | M | All pages |
| **4.9** Settings validation (path exists, numeric ranges) | — | S | `SettingsViewModel.cs`, `SettingsPage.xaml` |
| **4.10** Back navigation support | M11 | S | `NavigationService.cs`, `MainWindow.xaml` |
| **4.11** Dark/light theme selector | — | S | `SettingsPage.xaml`, `SettingsViewModel.cs` |
| **4.12** Narrator verification (manual test pass) | — | M | — |
| **4.13** Convert `{Binding}` to `{x:Bind}` where possible | M12 | M | All DataTemplates |

**Estimated effort:** 3–4 weeks

---

## Phase 5 — Visualization (v1.9.0)

**Goal:** Interactive visual exploration of disk usage.

| Task | Complexity | Details |
|------|------------|---------|
| **5.1** WebView2 treemap (D3.js, squarified layout) | XL | New `TreemapPage`, `Assets/treemap.html` |
| **5.2** Treemap drill-down, breadcrumb, right-click context menu | L | Extension of 5.1 |
| **5.3** File type sunburst/donut chart | L | D3.js in WebView2 |
| **5.4** Dashboard drive usage bar chart (native WinUI) | M | `DashboardPage.xaml` |
| **5.5** Scan history timeline (size trend over sessions) | M | `DashboardPage.xaml` |

**Estimated effort:** 4–6 weeks

---

## Phase 6 — Power Features (v2.0.0)

**Goal:** Feature-complete utility matching/exceeding competitors.

| Task | Complexity | Details |
|------|------------|---------|
| **6.1** CLI mode (`--scan`, `--cleanup --dry-run`, `--top-files`) | M | `System.CommandLine` |
| **6.2** Duplicate file detection (size-group → SHA-256 verify) | L | New rule, DB migration adds Hash column |
| **6.3** System tray icon (H.NotifyIcon.WinUI) | M | Disk-usage color coding, right-click menu |
| **6.4** Low-disk-space notifications (toast) | S | `Microsoft.Windows.AppNotifications` |
| **6.5** Windows Task Scheduler integration (scheduled scans) | M | `TaskScheduler` NuGet by dahall |
| **6.6** Export to CSV/JSON | M | `FileSavePicker` + serialization |
| **6.7** First-run experience (welcome card + drive selector) | M | Conditional on no completed sessions |
| **6.8** Update checker (GitHub API, once/day, no telemetry) | M | InfoBar on Dashboard |
| **6.9** In-app what's-new panel | S | Version-gated InfoBar |
| **6.10** ARM64 support (Rust + installer) | M | CI + Inno Setup dual-arch |
| **6.11** Installer: drop admin requirement, add .NET runtime check, safe uninstall (preserve DB option) | M | `StorageMaster.iss` |
| **6.12** Structured logging (Serilog rolling file) | S | Replace Debug logger |
| **6.13** Scan cancellation robustness (only mark Completed on full success) | S | `FileScanner.cs`, `TurboFileScanner.cs` |

**Estimated effort:** 6–8 weeks

---

## Release Timeline

| Release | Phase | Effort | Key deliverables |
|---------|-------|--------|-----------------|
| v1.5.0 | 1 — Hardening | 3–4 wk | All Critical/High bugs fixed, junction safety, thread safety |
| v1.6.0 | 2 — Tests & CI | 3–4 wk | 150+ tests, PR checks, code signing, coverage badge |
| v1.7.0 | 3 — Performance | 2–3 wk | DB indexes, batch updates, benchmarks, 1M file support |
| v1.8.0 | 4 — Accessibility | 3–4 wk | WCAG 2.1 AA, keyboard nav, screen reader, loading/empty/error states |
| v1.9.0 | 5 — Visualization | 4–6 wk | Treemap, sunburst, drive charts, scan history |
| v2.0.0 | 6 — Power features | 6–8 wk | CLI, duplicates, tray, scheduling, export, ARM64, first-run |

**Total to v2.0.0:** ~22–29 weeks of focused engineering.

---

## Engineering Principles (carried forward + additions)

| Principle | Enforcement |
|-----------|-------------|
| Interfaces before implementations | No concrete class across project boundaries |
| No deletion without confirmation | CLI needs `--confirm`; UI needs ContentDialog |
| Additive-only schema migrations | `IF NOT EXISTS` + version stamp inside transaction |
| All async, no blocking | No `Task.Result` or `.Wait()` in app code |
| Structured logging | Every non-trivial op logs structured fields |
| Test before merge | CI runs `dotnet test` + `cargo test` on every PR |
| Audit trail always | Every deletion → CleanupLog, even on error |
| Accessible from day one | AutomationProperties added with each new control |
| Measure before optimizing | BenchmarkDotNet first; no speculative perf work |
| **No bare catch** | Always filter OOM/StackOverflow; always log |
| **Junction-aware** | All filesystem ops check reparse points |
| **WriteLock everywhere** | All DB writes acquire the shared semaphore |
| **COM cleanup in finally** | Every COM object released in finally block |
| **Parameter validation** | Guard clauses on all public/internal API boundaries |

---

## Priority Rules

1. **Data loss bugs first** — anything that can destroy user files (C7), corrupt DB (C4/C5), or lose scan results (C2/C3)
2. **Crash bugs second** — null derefs, unhandled exceptions, thread violations
3. **Correctness third** — wrong results, silent failures, ignored errors
4. **Tests fourth** — coverage must increase monotonically; no phase ships without tests for its changes
5. **Features last** — no new features until hardening is complete
