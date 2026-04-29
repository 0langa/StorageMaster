# StorageMaster — Development Roadmap

> **Baseline:** v1.4.0 (2026-04-29) — parallel scanning, Rust Turbo Scanner (fixed DirectSizeBytes), 10 cleanup rules, Smart Cleaner, admin elevation, sortable/filterable Results with folder tree, 60+ tests, CI/CD pipeline, Inno Setup installer.
> **Target:** v1.5.0 — a feature-complete, polished, accessible, and robust Windows utility.

---

## How to read this document

- **Phase** = a coherent release milestone delivering real user value
- **Milestone** = a specific deliverable inside a phase
- **Complexity** = estimated engineering effort: S (hours–1 day) | M (2–5 days) | L (1–2 weeks) | XL (2–4 weeks)
- **Impact** = `Low | Medium | High | Critical`
- ✅ = shipped

---

## Phase overview

```
v1.3.x  Bug fixes & quick wins                 ← SHIPPED (v1.3.3)
  │
v1.4.0  Results & Interaction depth            ← SHIPPED
  │
v1.4.x  Hardening, compat, accessibility      ← next milestone
  │
v1.5.0  Visualization, scheduling, CLI        ← feature complete
```

---

## Phase 0 — Quick Wins & Bug Fixes (v1.3.x patches)

**Goal:** Immediately actionable improvements that take < 1 day each. Ship as rapid patch releases.

---

### ✅ P0-2 — Wire the Errors tab in ResultsPage
**Shipped:** v1.3.1 — `ResultsPage.xaml` Errors PivotItem with count badge; ViewModel loads `ScanErrors` from `IScanErrorRepository`.

---

### ✅ P0-3 — Excluded paths editor in Settings
**Shipped:** v1.3.1 — `SettingsPage.xaml` has a ListView + "Add Folder…" + per-row Remove button. ViewModel persists via `AppSettings.ExcludedPaths`.

---

### ✅ P0-1 — Log Smart Cleaner deletions to CleanupLog
**Shipped:** v1.3.3 — `SmartCleanerService` injects `ICleanupLogRepository`; writes synthetic `CleanupResult` + `CleanupSuggestion` per category group with `RuleId = "smart-cleaner.<slug>"`.

---

### ✅ P0-4 — Session deletion from Results page
**Shipped:** v1.3.3 — `DeleteSessionCommand` in `ResultsViewModel` (confirmation `ContentDialog` → `_repo.DeleteSessionAsync` → navigate to Dashboard); trash icon in Results summary card.

---

### ✅ P0-5 — Fix Turbo Scanner's FolderEntry DirectSizeBytes
**Shipped:** v1.4.0 — Accumulates file sizes per parent path during JSONL parsing; patches `FolderEntry.DirectSizeBytes` before `FolderSizeAggregator.Compute`, producing correct recursive totals. Also adds finalization progress reporting to TurboFileScanner.

---

## Phase 1 — Results Depth & Interaction (v1.4.0)

**Goal:** Make Results and Cleanup genuinely useful for power users. Users should act directly on findings, not just view them.

---

### ✅ M-1.1 — Open in Explorer from Results
**Shipped:** v1.3.1 — Explorer button on every file row (`/select,"<path>"`); folder rows open the folder directly.

---

### ✅ M-1.2 — Copy path to clipboard from Results
**Shipped:** v1.3.3 — Right-click `ContextFlyout` on file and folder rows: "Copy full path" (Clipboard), "Open in Explorer", "Send to Recycle Bin" (files only).

---

### M-1.3 — Sortable columns in Results ✅
**Shipped:** v1.3.2 — clickable column headers sort Largest Files by Size/Modified/Type and Largest Folders by Size/Files; ▼/▲ indicators.

---

### ✅ M-1.4 — Results page: filter improvements
**Shipped:** v1.4.0 — 300 ms debounce on filter input, clear (×) button, "Showing N of M files" count label below the filter box.

---

### ✅ M-1.5 — Interactive folder tree view
**Shipped:** v1.4.0 — New "Folder Tree" PivotItem in Results shows the loaded folder hierarchy in a WinUI 3 `TreeView`. Nodes are expandable, sorted largest-first at every level. Built from `FolderTreeNode` POCOs in the ViewModel; mapped to `TreeViewNode` in code-behind. Depends on P0-5 for correct sizes.

---

### M-1.6 — Cleanup page: analyse without session (direct path)

**Complexity:** M | **Impact:** Medium

Currently the Cleanup page requires a previous scan session. Add an option to run suggestions against a freshly-scanned path directly.

**Files:** `CleanupViewModel.cs`, `CleanupPage.xaml`

---

### M-1.7 — Delete directly from Results ✅
**Shipped:** v1.3.2 — delete button on every file row; confirmation `ContentDialog`; item removed from collection on success.

---

## Phase 2 — Hardening, Compatibility & Accessibility (v1.4.x)

**Goal:** Stable on all supported Windows builds; accessible to all users. No new features — only quality.

---

### M-2.1 — Windows 10 compatibility matrix test

**Complexity:** M | **Impact:** Critical

Test on build 17763 (Windows 10 1809) through Windows 11 24H2. Document regressions in the compatibility table at the bottom of this file.

**Focus:** `DisplayArea.GetFromWindowId()`, `SHGetKnownFolderPath`, WinUI 3 control availability, Turbo Scanner MSVC CRT requirements.

---

### M-2.2 — ARM64 support

**Complexity:** M | **Impact:** Medium

Add `aarch64-pc-windows-msvc` Rust target to CI. Update Inno Setup script for both architectures.

**Files:** `.github/workflows/release.yml`, `installer/StorageMaster.iss`

---

### M-2.3 — Keyboard navigation & focus management

**Complexity:** M | **Impact:** High (accessibility)

Full keyboard operability: Tab/Shift+Tab, arrow keys in lists, Enter activates, Escape closes dialogs, focus lands on the first interactive element after navigation.

**Files:** All XAML page files

---

### M-2.4 — Screen reader support (MSAA / UIA)

**Complexity:** M | **Impact:** High (accessibility)

`AutomationProperties.Name` and `.HelpText` on all interactive controls. Scan completion announces via `LiveSetting="Assertive"`. Verify with Windows Narrator.

**Files:** All XAML page files

---

### M-2.5 — High contrast mode support

**Complexity:** S | **Impact:** Medium (accessibility)

Ensure all custom colors use `ThemeResource` tokens. Verify with Windows "High Contrast Black" and "High Contrast White".

---

### M-2.6 — Text size scaling (large font accessibility)

**Complexity:** S | **Impact:** Medium (accessibility)

Test at 125%, 150%, 200% text scaling. No clipped text, no overlapping elements. 44×44 px minimum touch targets.

---

### ✅ M-2.7 — Expand test coverage to 60+ tests
**Shipped:** v1.4.0 — 60 tests (was 41). Added `CleanupEngineTests` (9 engine/orchestration tests), 4 `FolderSizeAggregatorTests` edge cases, 2 `LargeOldFilesRuleTests`, and 3 additional `ScanRepositoryTests`.

---

### M-2.8 — Structured logging (Serilog)

**Complexity:** S | **Impact:** Medium (diagnostics)

Replace `Microsoft.Extensions.Logging.Debug` with Serilog rolling file sink:
- Path: `%LOCALAPPDATA%\StorageMaster\logs\sm-{Date}.log`
- Max 5 files × 5 MB, `Information` in release / `Debug` in debug builds

**NuGet:** `Serilog.Extensions.Hosting`, `Serilog.Sinks.File`

---

### M-2.9 — Scan cancellation robustness

**Complexity:** S | **Impact:** Medium

If the scan is cancelled mid-aggregation, the session is currently marked `Completed` with partial folder totals. Wrap post-scan aggregation in `OperationCanceledException` handling; only mark `Completed` after full success.

**Files:** `FileScanner.cs`, `TurboFileScanner.cs`

---

## Phase 3 — Visualization, Scheduling & CLI (v1.5.0)

**Goal:** Match and exceed competing tools. Add proactive monitoring and power-user CLI.

---

### M-3.1 — Treemap visualization

**Complexity:** XL | **Impact:** Critical

WebView2 + D3.js treemap. Area ∝ folder/file size. Hover tooltip, click-to-drill, breadcrumb bar, right-click context menu ("Open in Explorer", "Send to Cleanup").

**NuGet:** `Microsoft.Web.WebView2`

**Files:** New `TreemapPage.xaml`, `TreemapViewModel.cs`, `Assets/treemap.html`, `Assets/d3.min.js`

---

### M-3.2 — File type sunburst / donut chart

**Complexity:** L | **Impact:** High
**Depends on:** M-3.1 (WebView2 infrastructure)

Replace the flat File Types tab with an interactive D3.js donut chart. Inner ring: major categories. Outer ring: top extensions per category. Click segment → filter the file list.

---

### M-3.3 — System tray icon

**Complexity:** M | **Impact:** High

Tray icon with disk-usage color coding (green/orange/red). Right-click: Open, Quick Smart Clean, Exit. Double-click: bring window forward. Balloon notification on low disk.

**NuGet:** `H.NotifyIcon.WinUI`

---

### M-3.4 — Low-disk-space notifications

**Complexity:** S | **Impact:** High
**Depends on:** M-3.3

Toast notifications via `Microsoft.Windows.AppNotifications` when any drive drops below configurable thresholds (default: 15% / 5% free). Action button: "Run Smart Cleaner".

---

### M-3.5 — Windows Task Scheduler integration (scheduled scans)

**Complexity:** M | **Impact:** High

Schedule automatic scans from the Settings page (daily / weekly, time picker). Creates a Task Scheduler entry that launches `StorageMaster.UI.exe --headless --scan <drive> --no-window`.

**NuGet:** `TaskScheduler` by dahall

---

### M-3.6 — CLI mode (scan + report)

**Complexity:** M | **Impact:** High (power users, scripting)

```powershell
StorageMaster.UI.exe --cli scan --path C:\ --output report.json
StorageMaster.UI.exe --cli top-files --count 20
StorageMaster.UI.exe --cli cleanup --rules temp,browser-cache --dry-run
StorageMaster.UI.exe --cli cleanup --rules temp,browser-cache --confirm
```

**NuGet:** `System.CommandLine`

---

### M-3.7 — Duplicate file detection

**Complexity:** L | **Impact:** High

Two-phase: size-group SQL query → SHA-256 hash verification for candidates. New "Duplicates" PivotItem in Results. DB schema migration adds `FileEntries.Hash` column.

---

### M-3.8 — Export to CSV / JSON

**Complexity:** M | **Impact:** High

"Export…" button in Results header. `FileSavePicker` → write file list, folder list, or cleanup history as CSV or JSON.

---

### M-3.9 — First-run experience

**Complexity:** M | **Impact:** High

On first launch (no completed sessions): welcome card + drive selector + single "Analyse Now" CTA. Dismissed once the first scan completes.

---

### M-3.10 — Dark / light theme selector

**Complexity:** S | **Impact:** High

Theme picker in Settings (System / Light / Dark). Persisted in `AppSettings.AppTheme`. Applied via `MainWindow.SetTheme()`.

---

### M-3.11 — In-app what's-new panel

**Complexity:** S | **Impact:** Medium

On first launch after an update: slide-in InfoBar listing new features. Dismissed once; never shown again for the same version.

---

### M-3.12 — Update check

**Complexity:** M | **Impact:** High

On startup (max once per day): `GET https://api.github.com/repos/0langa/StorageMaster/releases/latest`. If newer than current assembly version: unobtrusive "Update available" InfoBar on Dashboard with "Download" button (opens GitHub). No auto-download, no telemetry.

---

## Next sprint (v1.4.x targets)

Priority order for the next patch/minor release:

1. **M-1.6** — Cleanup page: analyse without a prior session (direct path)
2. **M-2.1** — Windows 10 1809 compatibility matrix test
3. **M-2.8** — Structured logging via Serilog rolling file sink
4. **M-2.9** — Scan cancellation robustness (OperationCancelledException during post-scan aggregation)
5. **M-2.3** — Keyboard navigation & focus management (accessibility foundation)

---

## Compatibility matrix

| Windows version | Build | Status (v1.3) | Target (v1.5) |
|----------------|-------|--------------|--------------|
| Windows 10 1809 | 17763 | Not tested | Verified (M-2.1) |
| Windows 10 21H2 | 19044 | Not tested | Verified |
| Windows 10 22H2 | 19045 | Primary dev target | Verified |
| Windows 11 22H2 | 22621 | Not tested | Verified |
| Windows 11 23H2 | 22631 | Not tested | Verified |
| Windows 11 24H2 | 26100 | Not tested | Verified |
| ARM64 (any) | — | Not tested | Verified (M-2.2) |

---

## Accessibility compliance targets

By v1.5.0, StorageMaster should meet:

| Standard | Target |
|----------|--------|
| WCAG 2.1 AA | Color contrast, text alternatives, keyboard access |
| Windows UI Guidelines | Touch targets ≥ 44×44 px; font scaling; focus indicators |
| Narrator | Full navigation without mouse |
| High Contrast | All visual elements remain distinguishable |

---

## Engineering principles

| Principle | Enforcement |
|-----------|-------------|
| **Interfaces before implementations** | No concrete class referenced across project boundaries |
| **No deletion without confirmation** | CLI needs `--confirm`; UI needs ContentDialog |
| **Additive-only schema migrations** | Migration runner enforces; PR review gate |
| **All async, no blocking** | No `Task.Result` or `.Wait()` in application code |
| **Structured logging** | Every non-trivial operation logs with structured fields |
| **Test before merge** | CI runs `dotnet test` on every pull request |
| **Audit trail always** | Every deletion → CleanupLog row, even on error |
| **Accessible from day one** | AutomationProperties added with each new control |
| **Measure before optimizing** | Benchmark first; no speculative performance work |

---

## Release timeline (estimated, 1 developer)

| Release | Phase | Estimated effort | Key deliverables |
|---------|-------|-----------------|----------------|
| v1.3.3 | P0 + P1 | days | Smart Cleaner audit log, session deletion, copy-path context menu |
| v1.4.0 | Phase 1 | 3–5 weeks | Filter improvements, folder tree view, cleanup-without-session |
| v1.4.x | Phase 2 | 3–4 weeks | ARM64, accessibility, 60+ tests, Serilog |
| v1.5.0 | Phase 3 | 6–10 weeks | Treemap, tray, scheduled scans, CLI, duplicate detection, exports |

**Total to v1.5.0:** approximately 3–5 months of focused engineering.

---

## Next sprint (v1.3.3)

1. **P0-1:** Smart Cleaner audit log — `SmartCleanerService.CleanAsync` logs each cleaned group to `CleanupLog`
2. **P0-4:** Session deletion — "Delete scan" button in Results page header, confirmation dialog
3. **M-1.2:** Copy path to clipboard — right-click context menu on file/folder rows

After v1.3.3, move to v1.4.0:
4. **P0-5:** Fix `TurboFileScanner` `DirectSizeBytes = 0` — required before folder tree view
5. **M-1.4:** Filter debounce + clear button + count display
6. **M-1.5:** Interactive folder tree view (depends on P0-5)
7. **M-2.7:** Expand tests to 60+
