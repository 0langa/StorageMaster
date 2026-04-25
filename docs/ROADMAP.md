# StorageMaster — Enterprise Roadmap

> **Baseline:** v1.0.0 (2026-04-25) — functional, safe, production-ready foundation
> **Target:** Enterprise-grade, commercially shippable, premium Windows utility

This document maps the path from the current v1 foundation to a fully enterprise-ready product. Each phase is self-contained and delivers real user value. No phase requires discarding prior work — every milestone builds directly on the contracts and interfaces established in v1.

---

## How to read this document

- **Phase** = a coherent release milestone (major feature set)
- **Milestone** = a specific deliverable inside a phase
- **Complexity** = estimated engineering effort [S = 1-3 days | M = 1-2 weeks | L = 2-6 weeks | XL = 2+ months]
- **Impact** = user-visible value [Low | Medium | High | Critical]
- **Dependency** = other milestone that must exist first

---

## Phase overview

```
v1.0  Foundation                           ← CURRENT (complete)
  │
v1.1  Polish & Hardening                   ← Fix known v1 gaps, no new features
  │
v2.0  Performance & Intelligence           ← MFT scan, duplicate detection, folder trees
  │
v2.5  Visualization & UX                   ← Treemap, sunburst, improved UI
  │
v3.0  Background & Scheduling              ← Windows Service, scheduled scans, tray
  │
v3.5  Enterprise Features                  ← Multi-user, reporting, policy
  │
v4.0  Ecosystem                            ← Plugin API, telemetry, cloud, installer
  │
v5.0  Premium Commercial                   ← Licensing, updates, telemetry portal
```

---

## Phase 1 — Polish & Hardening (v1.1)

**Goal:** Fix all known v1 gaps and make the app feel complete and stable. No new major features. Ship-quality release.

---

### M-1.1 — Delete placeholder Class1.cs files

**Complexity:** S | **Impact:** Low

Remove the auto-generated `Class1.cs` from `StorageMaster.Core`, `StorageMaster.Platform.Windows`, and `StorageMaster.Storage`. These are harmless but unprofessional in production code.

---

### M-1.2 — Implement Downloads path via SHGetKnownFolderPath

**Complexity:** S | **Impact:** Low
**File:** `DownloadedInstallersRule.cs`

Replace the profile-path fallback with the proper Windows API:

```csharp
// Add to Shell32Interop.cs:
[LibraryImport("shell32.dll")]
internal static partial int SHGetKnownFolderPath(
    ref Guid rfid, uint dwFlags, IntPtr hToken,
    out nint ppszPath);

// FOLDERID_Downloads = {374DE290-123F-4565-9164-39C4925E467B}
private static readonly Guid FOLDERID_Downloads =
    new("374DE290-123F-4565-9164-39C4925E467B");
```

This correctly handles redirected Downloads folders (enterprise environments often redirect to network shares).

---

### M-1.3 — Implement folder size ancestor propagation

**Complexity:** M | **Impact:** High
**File:** `ScanRepository.cs` or new `FolderSizeAggregator.cs`

The v1 `FolderEntry.TotalSizeBytes` equals `DirectSizeBytes` only (files directly in the folder). Real folder sizes must include all descendants.

**Algorithm:** After all `FolderEntry` rows are written for a session, run a bottom-up propagation pass in a single SQL transaction:

```sql
-- Iterative SQL or application-side tree walk
-- For each folder, TotalSizeBytes = DirectSizeBytes + SUM(children.TotalSizeBytes)
-- Repeat until no rows updated (convergence in O(depth) iterations)
```

Or do a single-pass DFS in C# using the already-stored `FullPath` hierarchy.

**Why this matters:** The "Largest Folders" result is incorrect without this. Users expect `C:\Users\Alice` to show the full tree size.

---

### M-1.4 — NTFS FileId-based symlink cycle detection

**Complexity:** M | **Impact:** Medium
**File:** `FileScanner.cs`

Replace the path-based `HashSet<string>` with proper NTFS `FileId` tracking:

```csharp
[StructLayout(LayoutKind.Sequential)]
private struct FILE_ID_INFO
{
    public ulong VolumeSerialNumber;
    public FILE_ID_128 FileId;
}

// Use GetFileInformationByHandleEx(FILE_INFO_BY_HANDLE_CLASS.FileIdInfo)
// Store (VolumeSerialNumber, FileId) in a HashSet<(ulong, FILE_ID_128)>
```

This handles the rare-but-real edge case of hard-linked directories that appear under different paths on the same volume.

---

### M-1.5 — Scan result pagination in UI (virtualization)

**Complexity:** M | **Impact:** High
**File:** `ResultsPage.xaml`, `ResultsViewModel.cs`

The current `ListView` loads up to 500 files eagerly. On large scans this causes jank. Replace with:
- `ItemsRepeater` with virtualization
- Load-on-scroll (incremental `IAsyncEnumerable<FileEntry>` consumption)
- Or `AdvancedCollectionView` from CommunityToolkit.WinUI for sort/filter

---

### M-1.6 — Improved error reporting and scan log

**Complexity:** S | **Impact:** Medium

- Add a `ScanErrorLog` table to the schema for per-path errors
- Show error count on Dashboard
- Add "View scan errors" panel in Results

---

### M-1.7 — Settings: excluded paths editor

**Complexity:** M | **Impact:** Medium

The `AppSettings.ExcludedPaths` list is persisted but has no UI. Add a simple editor:
- `ListView` of excluded paths
- Add path button (FolderPicker)
- Remove selected button
- Pre-populated with sensible defaults

---

### M-1.8 — Test coverage expansion

**Complexity:** M | **Impact:** Medium

Current coverage: 13 tests. Target for v1.1: 40+.

Add tests for:
- `CacheFolderCleanupRule`
- `DownloadedInstallersRule`
- `RecycleBinCleanupRule`
- `CleanupEngine.GetSuggestionsAsync` orchestration
- `CleanupEngine.ExecuteAsync` with partial failure
- `StorageDbContext` migration (starting from v0)
- `SettingsRepository` round-trip
- `FileDeleter` dry-run (with a mock or in-process stub)

---

## Phase 2 — Performance & Intelligence (v2.0)

**Goal:** Make StorageMaster significantly faster and smarter than v1. The scanner should feel near-instant on SSDs. Duplicate detection becomes a reality.

---

### M-2.1 — NTFS MFT-based fast scanner

**Complexity:** XL | **Impact:** Critical
**Dependency:** M-1.4

The current BFS scanner reads every directory via Win32 API calls — 10–30 seconds for a full C: drive on an SSD, several minutes on an HDD.

The NTFS Master File Table (MFT) can be read directly, yielding all file records in under 2 seconds even on an HDD. This is how tools like `Everything` and `WizTree` achieve instant results.

**Implementation approach:**

```csharp
// New interface:
public interface IMftScanner : IFileScanner
{
    bool IsAvailable { get; }  // requires admin + NTFS volume
}

// Implementation steps:
// 1. Open volume handle with FSCTL_GET_NTFS_VOLUME_DATA
// 2. Use FSCTL_ENUM_USN_DATA to iterate USN change journal
//    OR read MFT directly via FSCTL_GET_RETRIEVAL_POINTERS + direct sector I/O
// 3. Map MFT records to FileEntry/FolderEntry
// 4. Fall back to BFS scanner if MFT is unavailable (FAT, network, non-admin)
```

**Key considerations:**
- Requires administrator elevation (or Volume Shadow Copy Service for non-admin)
- Only works on NTFS volumes (not exFAT, FAT32, ReFS, network)
- MFT parsing requires P/Invoke or a native helper
- Path reconstruction from parent FRN (File Reference Number) requires a lookup table

**Result:** Full C: drive scan in under 3 seconds on SSD, under 15 seconds on HDD.

---

### M-2.2 — USN Journal incremental scan

**Complexity:** L | **Impact:** High
**Dependency:** M-2.1

Instead of rescanning the full drive after the first MFT scan, use the NTFS USN (Update Sequence Number) Change Journal to get only what changed since the last scan.

```csharp
public interface IIncrementalScanner
{
    Task<ScanSession> IncrementalScanAsync(
        long            baseSessionId,
        ScanOptions     options,
        IProgress<ScanProgress> progress,
        CancellationToken ct);
}
```

The change journal records every file creation, modification, rename, and deletion with a USN sequence number. By storing the last USN in the session, subsequent scans only need to process the delta.

**Result:** After the first full MFT scan, subsequent scans complete in <1 second for typical daily changes.

---

### M-2.3 — SHA-256 duplicate file detection

**Complexity:** L | **Impact:** High
**Dependency:** M-1.3 (folder sizes), M-2.1 (fast scan preferred)

The v1 cleanup rule interface already has `DuplicateFiles` as a `CleanupCategory`. v2 implements it properly.

**Two-phase algorithm:**
1. **Size grouping:** Group all `FileEntry` records by `SizeBytes`. Files with unique sizes cannot be duplicates. This is already possible from the DB.
2. **Hash verification:** For each size-group with ≥ 2 members, compute SHA-256 hashes and group by hash. Only confirmed hash-matches are duplicates.

```csharp
// New rule:
public sealed class DuplicateFilesCleanupRule : ICleanupRule
{
    // Yield one suggestion per duplicate group
    // Group title: "3 copies of vacation-photo.jpg (42 MB each)"
    // All paths shown; user keeps one, deletes the rest
    // Risk: Medium (user must choose which copy to keep)
}
```

**UI consideration:** Duplicate suggestions need a special UI — the user must select which copy to keep and which to delete. This may require a dedicated `DuplicatesPage`.

---

### M-2.4 — File content hash index in database

**Complexity:** M | **Impact:** Medium
**Dependency:** M-2.3

Add `Hash` column to `FileEntries`:

```sql
-- Schema v2 migration:
ALTER TABLE FileEntries ADD COLUMN Hash TEXT;
CREATE INDEX IF NOT EXISTS IX_FileEntries_Hash ON FileEntries(Hash) WHERE Hash IS NOT NULL;
```

Populate lazily: compute hashes only for files in size-groups with potential duplicates, not all files (would be slow and unnecessary).

---

### M-2.5 — Folder tree with size bars

**Complexity:** M | **Impact:** High
**Dependency:** M-1.3 (real folder sizes)

Replace the current flat `LargestFolders` list with an interactive folder tree:

```
C:\                              [████████████████░░░░] 450 GB / 512 GB
  └─ Users                       [████████░░░░░░░░░░░░] 120 GB
       └─ Alice                  [███████░░░░░░░░░░░░░] 115 GB
            ├─ Documents         [███░░░░░░░░░░░░░░░░░]  40 GB
            ├─ Downloads         [██░░░░░░░░░░░░░░░░░░]  28 GB
            └─ Videos            [██░░░░░░░░░░░░░░░░░░]  22 GB
```

Use `TreeView` (WinUI 3) with lazy-loaded children. The `FolderEntry` hierarchy is already in the database; just query children of a parent path.

---

### M-2.6 — Smart cleanup rule: Empty folders

**Complexity:** S | **Impact:** Medium

A new rule that identifies empty directories (FileCount=0, SubFolderCount=0) and suggests removing them. Safe by definition.

---

### M-2.7 — Smart cleanup rule: Large video files in unexpected places

**Complexity:** S | **Impact:** Medium

Finds `.mp4`, `.mkv`, `.avi` files > 500 MB outside of `Videos`, `Desktop`, `Downloads` (e.g., buried in AppData, project folders, temp). Risk: Medium.

---

## Phase 2.5 — Visualization & UX (v2.5)

**Goal:** Make StorageMaster visually impressive and immediately useful on first launch. Compete with WizTree, SpaceSniffer, and DiskSavvy.

---

### M-2.5.1 — Treemap visualization

**Complexity:** XL | **Impact:** Critical

The treemap is the defining visual of a disk space analyzer. Render a hierarchical rectangle packing where each rectangle's area is proportional to the folder/file size.

**Options:**
1. **WinUI 3 Canvas + Direct2D** — custom rendering; high performance; hardest to implement
2. **WinUI 3 Canvas + SkiaSharp** — cross-platform rendering; easier; `SkiaSharp.Views.WinUI` NuGet
3. **WebView2 + D3.js treemap** — easiest to implement; excellent out-of-the-box interactivity
4. **Win2D** — Microsoft-maintained; hardware-accelerated; WinUI 3 compatible

**Recommended approach for v2.5:** WebView2 + D3.js (fastest to ship, best interactive behavior, zero custom renderer).

The treemap is interactive:
- Hover → show tooltip (path, size, % of parent)
- Click → drill down into folder
- Breadcrumb bar → navigate back up
- Right-click → "Open in Explorer", "Add to Cleanup"

---

### M-2.5.2 — Sunburst chart for file types

**Complexity:** L | **Impact:** High

Replace the flat category breakdown list with a sunburst (ring) chart.
- Inner ring: categories (Document, Video, Image, etc.)
- Outer ring: top file extensions within each category
- Same tech recommendation: WebView2 + D3.js

---

### M-2.5.3 — Welcome / first-run experience

**Complexity:** M | **Impact:** High

On first launch (no scan sessions in DB):
1. Full-screen welcome with app name and tagline
2. Drive selector (largest drive pre-selected)
3. Single "Analyse Now" call-to-action
4. Immediate scan start with animated progress

---

### M-2.5.4 — Dashboard redesign

**Complexity:** M | **Impact:** High
**Dependency:** M-2.5.1, M-2.5.2

Redesign the Dashboard to be a command center:
- Top: drive space bar (free / used / scanned)
- Left: treemap preview (top-level folders)
- Right: file-type donut chart + top-5 largest files
- Bottom: recent sessions, quick actions

---

### M-2.5.5 — Results page: sortable columns

**Complexity:** S | **Impact:** Medium

Add column header click to sort the file/folder list. Use `AdvancedCollectionView` from `CommunityToolkit.WinUI` for client-side sort without re-querying the DB.

---

### M-2.5.6 — Right-click context menus

**Complexity:** S | **Impact:** Medium

On `ResultsPage` file list:
- Open file
- Open containing folder in Explorer
- Copy path
- Add to cleanup

On folder tree:
- Scan this folder
- Copy path
- Open in Explorer

---

### M-2.5.7 — Dark mode / light mode support

**Complexity:** S | **Impact:** High

WinUI 3 supports dark/light via `Application.RequestedTheme`. Ensure all colors use `ThemeResource` tokens (already started in XAML). Add a theme selector to `SettingsPage`.

---

### M-2.5.8 — Animations and transitions

**Complexity:** M | **Impact:** Medium

- Scan progress: animated fill bar
- Page transitions: `NavigationThemeTransition` (already available in WinUI 3)
- Suggestion list: `AddDeleteThemeTransition` when items appear/disappear
- Cleanup execution: per-item completion animation

---

## Phase 3 — Background & Scheduling (v3.0)

**Goal:** StorageMaster runs silently in the background, keeps disk health data fresh, and alerts users proactively.

---

### M-3.1 — System tray icon

**Complexity:** M | **Impact:** High

A tray icon that:
- Shows disk space % used (color-coded: green < 70%, orange < 90%, red > 90%)
- Right-click menu: Open StorageMaster, Quick Scan, Exit
- Double-click: bring main window to front (or restore)
- Balloon notification when last scan is stale (> 7 days)

**Implementation:** WinUI 3 does not have native tray support. Use `NotifyIcon` from the legacy `System.Windows.Forms` assembly (available in .NET 10 for Windows), or the `H.NotifyIcon.WinUI` NuGet package.

---

### M-3.2 — Background Windows Service

**Complexity:** XL | **Impact:** High

A separate project `StorageMaster.Service` implementing a Windows Service:

```
StorageMaster.Service/
    StorageMaster.Service.csproj   (Worker Service template)
    Worker.cs                       IHostedService implementation
    ScheduledScanJob.cs            Cron-like scheduling
    ServiceInstaller.cs            Install/uninstall service
```

The service:
- Runs scheduled scans using the same `IFileScanner` interface
- Writes to the same SQLite database (shared via named pipe or same DB file)
- Reports disk space alerts to the notification system
- Uses the USN Journal (M-2.2) for efficient incremental scans

**Communication between UI and service:** Named pipe or REST API (hosted by the service on localhost).

---

### M-3.3 — Scheduled scan configuration

**Complexity:** M | **Impact:** High
**Dependency:** M-3.2

Add a "Schedule" section to SettingsPage:
- Enable/disable scheduled scans
- Select drives to scan
- Frequency: Daily / Weekly / On idle
- Time picker
- Last scan status + next scheduled time

Scheduled scan jobs are stored in the SQLite database:

```sql
-- Schema v3 migration:
CREATE TABLE ScheduledScans (
    Id          INTEGER PRIMARY KEY,
    RootPath    TEXT NOT NULL,
    CronExpr    TEXT NOT NULL,
    IsEnabled   INTEGER NOT NULL DEFAULT 1,
    LastRunUtc  TEXT,
    NextRunUtc  TEXT
);
```

---

### M-3.4 — Windows Task Scheduler integration

**Complexity:** M | **Impact:** Medium
**Dependency:** M-3.3

As an alternative to a Windows Service, use Windows Task Scheduler to launch the app in headless mode:

```
StorageMaster.exe --headless --scan C:\ --schedule daily
```

This approach requires no service installation (easier for end-users), but is less reliable for wake-from-sleep scenarios.

---

### M-3.5 — Low-disk-space notifications

**Complexity:** S | **Impact:** High
**Dependency:** M-3.1 (tray icon)

Monitor disk free space in a background `PeriodicTimer`. When any monitored drive drops below configurable thresholds (default: 15% and 5%):
- Show a toast notification (using `Microsoft.Windows.AppNotifications` from Windows App SDK)
- Tray icon turns orange/red
- Optional: auto-launch cleanup suggestions for that drive

---

## Phase 3.5 — Enterprise Features (v3.5)

**Goal:** Make StorageMaster deployable in corporate environments. IT admins can configure policies; enterprise licenses cover multiple seats.

---

### M-3.5.1 — Group Policy / ADMX template

**Complexity:** L | **Impact:** High (enterprise)

Create an ADMX/ADML administrative template that allows IT to:
- Pre-configure excluded paths (e.g., exclude mapped network drives)
- Enforce RecycleBin-only mode (never permanent delete)
- Disable cleanup of specific categories
- Set maximum parallelism
- Force scheduled scan intervals

Settings from Group Policy override AppSettings with lowest priority (user settings win unless forced by GPO).

---

### M-3.5.2 — Multi-user scan history

**Complexity:** M | **Impact:** Medium

Add `UserSid` or `UserPrincipalName` to `ScanSessions` to support multi-user environments where the database is shared:

```sql
-- Schema migration:
ALTER TABLE ScanSessions ADD COLUMN UserSid TEXT;
```

Add user filter to the session picker in the Cleanup page.

---

### M-3.5.3 — Export reports

**Complexity:** M | **Impact:** High

Export scan results and cleanup history in multiple formats:
- **CSV** — full file list, folder list, or category breakdown
- **JSON** — machine-readable for integration with monitoring tools
- **HTML** — self-contained report with charts (use embedded Chart.js)
- **PDF** — via `SkiaSharp` or `PdfSharp`

Report types:
- Disk usage summary (per-folder size tree)
- File type breakdown
- Cleanup history (audit trail)
- Large file inventory

---

### M-3.5.4 — Network path scanning

**Complexity:** M | **Impact:** Medium

The current scanner works on network paths already (via UNC). Improvements:
- Detect network paths (`\\server\share`) and set `MaxParallelism = 1` automatically
- Show latency warning for network scans
- Exclude network paths from disk-space bar (no reliable free-space API for network shares)

---

### M-3.5.5 — Command-line interface

**Complexity:** M | **Impact:** High (power users, scripting)

```
StorageMaster.exe scan --path C:\ --output report.csv
StorageMaster.exe cleanup --session 42 --rule core.temp-files --dry-run
StorageMaster.exe info --drive C
```

The CLI reuses the same `Core`, `Storage`, and `Platform.Windows` layers. Only the presentation layer differs. Use `System.CommandLine` (Microsoft) for argument parsing.

**Important:** CLI execution of cleanup requires `--confirm` flag to prevent accidental batch deletions.

---

### M-3.5.6 — Centralized logging (Serilog)

**Complexity:** S | **Impact:** Medium

Replace `Microsoft.Extensions.Logging.Debug` with Serilog:
- Rolling file appender (`%LOCALAPPDATA%\StorageMaster\logs\sm-*.log`)
- Structured log fields (SessionId, RuleId, path)
- Configurable level (Debug in dev, Info in release)
- Log viewer in the app (simple text viewer in an About/Diagnostics page)

---

## Phase 4 — Ecosystem (v4.0)

**Goal:** StorageMaster becomes a platform. Third parties can extend it. Cloud storage is aware. Telemetry enables data-driven improvements.

---

### M-4.1 — Plugin API

**Complexity:** XL | **Impact:** High

Enable third-party cleanup rules via a plugin system:

```csharp
// Public plugin contract (separate NuGet package: StorageMaster.Plugin.Abstractions):
public interface IStorageMasterPlugin
{
    string Name { get; }
    string Version { get; }
    IReadOnlyList<ICleanupRule> GetCleanupRules();
}
```

Plugin loading:
- Scan `%LOCALAPPDATA%\StorageMaster\Plugins\*.dll` on startup
- Use `AssemblyLoadContext` for isolation
- Verify plugin signature (optional, enterprise only)
- Show plugin manager in Settings

Example plugins:
- Steam game asset cleanup (removes download cache, shader cache)
- Visual Studio cleanup (bin/obj, .vs, nuget packages)
- Docker cleanup (dangling images, stopped containers)
- Zoom/Teams meeting recording cleanup

---

### M-4.2 — Cloud storage awareness

**Complexity:** L | **Impact:** High

Detect cloud-synced folders and handle them specially:

| Cloud Service | Detection Method |
|--------------|-----------------|
| OneDrive | `%USERPROFILE%\OneDrive` + registry `SOFTWARE\Microsoft\OneDrive` |
| Google Drive | `%LOCALAPPDATA%\Google\Drive\user_default` |
| Dropbox | `%APPDATA%\Dropbox\info.json` |
| iCloud Drive | `%USERPROFILE%\iCloudDrive` |

For cloud-synced files:
- Show a cloud icon in the file list
- Mark them "online only" vs "locally available" (OneDrive NTFS sparse file attributes)
- Never suggest deleting "always available" cloud files (they would be re-downloaded)
- Offer to "free up space" by marking files as online-only (OneDrive `StorageProviderSyncRootManager` API)

---

### M-4.3 — Opt-in telemetry

**Complexity:** M | **Impact:** Medium (product intelligence)

Collect anonymous usage data with explicit opt-in on first launch:

```csharp
// Events:
// ScanCompleted(driveSizeGb, filesCount, durationSec)
// CleanupExecuted(category, bytesFreed, ruleId, wasDryRun)
// FeatureUsed(featureName)
// ErrorOccurred(errorType, component)  -- never include paths or content
```

Telemetry back-end:
- Use Application Insights (Azure) or PostHog (self-hosted)
- Anonymized: no paths, no file names, no user identity
- Configurable opt-out at any time in Settings
- GDPR compliant: no PII collected

---

### M-4.4 — Auto-update mechanism

**Complexity:** M | **Impact:** Critical (commercial)

In-app update check on startup:
1. Check `https://updates.storagemaster.app/releases/latest.json` (or GitHub Releases API)
2. Compare installed version with latest
3. Show unobtrusive notification: "Update available: v2.1.0"
4. Download installer/MSIX in background
5. Prompt to install (with restart)

Use `Squirrel.Windows` or `WinSparkle` for robust auto-update, or the `Microsoft.Windows.AppLifecycle` restart API.

---

### M-4.5 — MSIX packaging and Microsoft Store submission

**Complexity:** L | **Impact:** Critical (distribution)

Currently the app is unpackaged (`WindowsPackageType=None`). For Store submission:
1. Switch to `WindowsPackageType=MSIX`
2. Create Package.appxmanifest with app capabilities
3. Code-sign with an EV certificate (required for Store and enterprise deployment)
4. Submit to Microsoft Store (Windows category: Utilities & Tools)
5. Maintain an enterprise sideload package for GPO deployment

MSIX benefits:
- Clean install/uninstall (no registry residue)
- Automatic updates via Store
- Sandboxing (requires declaring capabilities for filesystem access)
- WinGet installability

---

### M-4.6 — ReFS support

**Complexity:** M | **Impact:** Low-medium

The Resilient File System (ReFS) is used on Windows Server and Storage Spaces. The MFT scanner (M-2.1) does not apply to ReFS. Add a ReFS-compatible fallback scanner mode.

---

## Phase 5 — Premium Commercial (v5.0)

**Goal:** Ship a commercially viable product with professional support, a business model, and the polish of a premium utility.

---

### M-5.1 — Licensing system

**Complexity:** L | **Impact:** Critical (revenue)

**Free tier:**
- Scan and view results (unlimited)
- Recycle Bin and Temp file cleanup
- Single drive

**Pro tier** ($19.99/year):
- All cleanup rules
- Scheduled scans
- Reports export
- Multiple drives simultaneously

**Enterprise tier** (per-seat or site license):
- Group Policy support
- CLI interface
- Plugin API
- Centralized reporting
- Priority support

**Implementation options:**
- Keygen-based (simple; piracy risk)
- Online activation (requires network on activation)
- Hardware-locked (most secure; worst UX for reinstalls)
- Recommendation: Online activation with offline grace period (7 days)

Use `Cryptolens` or `LicenseSpring` for managed license management, or build a simple JWT-based system.

---

### M-5.2 — Professional installer

**Complexity:** M | **Impact:** High

Create a professional installer using:
- **WiX Toolset** (MSI) — enterprise deployable, silent install, GPO compatible
- **OR MSIX** (from M-4.5) — modern, Store compatible

Installer features:
- Custom install directory
- Optional "Launch at startup" checkbox
- Start menu shortcut + Desktop shortcut (optional)
- Uninstaller that cleans DB and logs
- Silent install mode for enterprise: `setup.exe /quiet /norestart`

---

### M-5.3 — Help documentation system

**Complexity:** M | **Impact:** Medium

In-app help:
- F1 key opens context-sensitive help for the current page
- Help panel slides in from right (no browser needed for basics)
- Full documentation as a static HTML site (hosted on GitHub Pages or Vercel)
- "What's new in this version" splash on first run after update

---

### M-5.4 — Localization (i18n)

**Complexity:** L | **Impact:** High (international)

Move all user-facing strings to `.resw` resource files:

```
StorageMaster.UI/Strings/en-US/Resources.resw  (English)
StorageMaster.UI/Strings/de-DE/Resources.resw  (German)
StorageMaster.UI/Strings/ja-JP/Resources.resw  (Japanese)
```

WinUI 3 supports `x:Uid` resource binding natively. Priority languages:
1. English (baseline)
2. German (high PC utility market)
3. Japanese (high premium software market)
4. Simplified Chinese
5. Spanish, French, Portuguese

---

### M-5.5 — Crash reporting and diagnostics

**Complexity:** M | **Impact:** High (quality)

Automated crash reporting:
- Catch unhandled exceptions in `App.OnUnhandledException`
- Collect: stack trace, OS version, app version, anonymized last action
- Upload to crash reporting service (Sentry, Raygun, or Azure App Insights)
- Prompt user to send report (opt-in)
- In-app diagnostics page: app version, DB size, log tail, system info

---

### M-5.6 — Performance benchmarking suite

**Complexity:** M | **Impact:** Medium (engineering quality)

Add a benchmark project using `BenchmarkDotNet`:

```
tests/StorageMaster.Benchmarks/
    ScannerBenchmarks.cs       (scan throughput vs. file count)
    DbInsertBenchmarks.cs      (batch insert performance)
    QueryBenchmarks.cs         (top-N query time vs. DB size)
```

Run benchmarks on each release to detect regressions. Publish results to README.

---

## Engineering principles (all phases)

These principles must be maintained across all phases to avoid technical debt accumulation:

| Principle | How to enforce |
|-----------|---------------|
| **Interfaces before implementations** | Never reference a concrete class across project boundaries |
| **No deletion without confirmation** | CLI needs `--confirm` flag; UI needs ContentDialog; tests verify this |
| **Additive-only schema migrations** | Migration runner enforces this; PR review gate |
| **All async, all the way** | No `Task.Result` or `.Wait()` in application code |
| **Structured logging everywhere** | Every non-trivial operation logs with structured fields |
| **Test before merge** | CI runs `dotnet test` on every pull request |
| **Audit trail always** | Every file deletion → CleanupLog row, always, even on error |
| **Measure before optimizing** | Use benchmarks (M-5.6) to justify performance work |

---

## Suggested release timeline

| Version | Phase | Estimated effort | Key deliverable |
|---------|-------|-----------------|----------------|
| v1.1 | Polish | 2–3 weeks | Ship-quality v1 with folder sizes fixed |
| v2.0 | Performance | 2–3 months | MFT scanner, duplicate detection |
| v2.5 | Visualization | 6–8 weeks | Treemap, folder tree, better UX |
| v3.0 | Background | 2–3 months | Service, scheduling, tray |
| v3.5 | Enterprise | 2–3 months | GPO, CLI, reports |
| v4.0 | Ecosystem | 3–4 months | Plugins, cloud, telemetry, Store |
| v5.0 | Commercial | 1–2 months | Licensing, installer, i18n |

**Total to commercial:** approximately 12–18 months of engineering effort (1 developer).
With a 2-developer team: 8–12 months.

---

## Immediate next steps (this sprint)

1. **Delete `Class1.cs` files** (M-1.1) — 5 minutes
2. **Fix `TotalSizeBytes` propagation** (M-1.3) — most impactful v1 bug
3. **Add excluded paths editor to Settings** (M-1.7) — completes settings page
4. **Expand test coverage to 40+** (M-1.8) — confidence before wider use
5. **Fix Downloads path via SHGetKnownFolderPath** (M-1.2) — correctness on edge cases
