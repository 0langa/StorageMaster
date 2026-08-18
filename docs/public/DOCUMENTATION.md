# StorageMaster — Full Technical Documentation

> **Version:** 2.2.1 | **Current-state review:** 2026-08-18 | **.NET 8 / WinUI 3 / Windows App SDK 1.6**
> **Current storage/safety state:** schema v12 uses independent SQLite connection leases, normalized file/folder identity, exact UTC timestamp reads, duplicate recovery journals, and nullable stable scan-time file identity. Historical identity-less rows stay readable but require rescan before destructive scan-backed actions.

---

## Table of contents

1. [Getting started](#1-getting-started)
2. [Configuration reference](#2-configuration-reference)
3. [Scanner API](#3-scanner-api)
4. [Turbo Scanner](#4-turbo-scanner)
5. [Smart Cleaner API](#5-smart-cleaner-api)
6. [Cleanup system API](#6-cleanup-system-api)
7. [Storage API](#7-storage-api)
8. [Platform API](#8-platform-api)
9. [UI pages reference](#9-ui-pages-reference)
10. [Dependency injection reference](#10-dependency-injection-reference)
11. [Database reference](#11-database-reference)
12. [Error handling strategy](#12-error-handling-strategy)
13. [Testing guide](#13-testing-guide)
14. [Adding a cleanup rule](#14-adding-a-cleanup-rule)
15. [Adding a scan backend](#15-adding-a-scan-backend)
16. [Troubleshooting](#16-troubleshooting)

---

## 1. Getting started

### Prerequisites

| Requirement | Minimum version | Notes |
|-------------|----------------|-------|
| Windows | Configured minimum: 10 1809 (build 17763) | The full supported-build matrix still requires release/lab validation |
| .NET SDK | 8.0.x | `global.json` pins this |
| .NET Desktop Runtime | 8.0.x x64 | Required on installed machines; setup blocks with guidance when missing |
| Visual Studio | 2022 17.9+ | For building the WinUI 3 UI project |
| Rust | stable | For building `turbo-scanner.exe` from source |
| Windows App SDK | 1.6 runtime | NuGet restored for builds; release installer stages the x64 runtime MSIX |

### Clone and build

```powershell
git clone <repo-url>
cd StorageMaster

# Backend + tests (no VS required)
dotnet build src/StorageMaster.Core/StorageMaster.Core.csproj
dotnet build src/StorageMaster.Storage/StorageMaster.Storage.csproj
dotnet build "src/StorageMaster.Platform.Windows/StorageMaster.Platform.Windows.csproj"
dotnet test  "tests/StorageMaster.Tests/StorageMaster.Tests.csproj"

# Turbo Scanner binary
cargo build --release --manifest-path turbo-scanner/Cargo.toml

# Full UI build (requires VS 2022 MSBuild)
dotnet publish src/StorageMaster.UI/StorageMaster.UI.csproj /p:PublishProfile=win-x64 -c Release
```

### Database location

The SQLite database is created automatically on first launch at:
```
%LOCALAPPDATA%\StorageMaster\storagemaster.db
```

Crash logs (unhandled exceptions) are written to:
```
%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log
```

### CLI and headless commands

```powershell
StorageMaster.UI.exe --cli scan --path <abs-path> [--turbo] [--deep] [--json <file>]
StorageMaster.UI.exe --cli report last-scan [--json <file>] [--csv <file>]
StorageMaster.UI.exe --cli dedupe scan --session <id> --methods exact,text,image,video --min-size <mb> [--json <file>]
StorageMaster.UI.exe --cli cleanup analyze --session <id> [--json <file>]
StorageMaster.UI.exe --cli cleanup execute --session <id> --rules <csv> --recycle-bin|--quarantine --confirm
StorageMaster.UI.exe --cli health report [--json <file>]
StorageMaster.UI.exe --headless jobs run --id <job-id>
```

`health report` exits with `1` when a critical drive health status is reported, so scheduled or scripted checks can fail loudly.

---

## 2. Configuration reference

### AppSettings

All settings are persisted in the SQLite `Settings` table as JSON under the key `AppSettings`. Changes are applied immediately on "Save" in the Settings page.

#### Scanner settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `DefaultScanPath` | `string` | `C:\` | Pre-filled path in Scan page |
| `ScanParallelism` | `int` | `4` | Concurrent directory workers (increase for SSDs) |
| `ShowHiddenFiles` | `bool` | `false` | Include hidden files in scans and duplicate analysis |
| `SkipSystemFolders` | `bool` | `true` | Skip `C:\Windows` etc. (overridden by DeepScan) |
| `ExcludedPaths` | `IList<string>` | `[]` | Custom path prefix exclusions |
| `UseTurboScanner` | `bool` | `false` | Use Rust-backed scanner when binary available |

#### Deletion behaviour

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `PreferRecycleBin` | `bool` | `true` | Send files to Recycle Bin instead of permanent delete |
| `DryRunByDefault` | `bool` | `false` | Preview cleanup actions without deleting |

#### Cleanup rule toggles

| Setting | Default | Rule enabled |
|---------|---------|-------------|
| `CleanRecycleBin` | `true` | RecycleBinCleanupRule |
| `CleanTempFiles` | `true` | TempFilesCleanupRule |
| `CleanDownloadedInstallers` | `true` | DownloadedInstallersRule |
| `ClearEntireDownloads` | `false` | Clear entire Downloads folder (not just installers) |
| `CleanCacheFolders` | `true` | CacheFolderCleanupRule |
| `CleanBrowserCache` | `true` | BrowserCacheCleanupRule |
| `CleanWindowsUpdateCache` | `true` | WindowsUpdateCacheRule |
| `CleanDeliveryOptimization` | `true` | DeliveryOptimizationRule |
| `CleanWindowsErrorReports` | `true` | WindowsErrorReportingRule |
| `CleanProgramLeftovers` | `false` | UninstalledProgramLeftoversRule (high-risk heuristic; opt-in) |
| `CleanLargeOldFiles` | `false` | LargeOldFilesCleanupRule (medium risk — off by default) |
| `CleanThumbnailCache` | `true` | ThumbnailCacheRule |
| `CleanIconCache` | `true` | IconCacheRule |
| `CleanFontCache` | `false` | FontCacheRule (opt-in) |
| `CleanDnsCache` | `true` | DnsClientCacheRule |
| `CleanPrefetchFiles` | `false` | PrefetchFilesRule (medium risk; elevation required) |
| `CleanStoreLogs` | `true` | MicrosoftStoreLogsRule |

#### Large file thresholds (used by LargeOldFilesCleanupRule)

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `LargeFileSizeMb` | `int` | `500` | Minimum file size in MB |
| `OldFileAgeDays` | `int` | `365` | Minimum age in days since last-write |

#### Tray and health notifications

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `EnableLowDiskNotifications` | `bool` | `true` | Tray notification when free space crosses warning/critical thresholds |
| `EnableDriveHealthNotifications` | `bool` | `true` | Tray notification when drive health is warning or critical |
| `LowDiskWarningPercent` | `int` | `15` | Warning threshold by percent free |
| `LowDiskCriticalPercent` | `int` | `5` | Critical threshold by percent free |

### ScanOptions

Passed programmatically to `IFileScanner.ScanAsync`.

| Option | Default | Description |
|--------|---------|-------------|
| `RootPath` | required | Root path to scan |
| `MaxParallelism` | `4` | Directory workers |
| `DbBatchSize` | `500` | Flush file entries to DB every N entries |
| `ExcludedPaths` | Windows `WinSxS` and `Installer` under the actual Windows folder | Case-insensitive, normalized, boundary-aware exclusions |
| `FollowSymlinks` | `false` | Follow reparse points |
| `IncludeHiddenFiles` | `false` | Include entries marked hidden |
| `DeepScan` | `false` | When true: empty exclusions and include hidden/system entries; configured parallelism still applies |

**Parallelism tuning:**
- HDD: `1–4` to avoid random-seek thrashing
- SSD/NVMe: `8–16` for maximum throughput
- Network drive: `1–2` to avoid overwhelming the server

---

## 3. Scanner API

### Interface: `IFileScanner`

Location: `StorageMaster.Core/Interfaces/IFileScanner.cs`

Two implementations exist:
- `FileScanner` — managed C# BFS parallel walker (always available)
- `TurboFileScanner` — Rust-backed (available when `turbo-scanner.exe` is present)

#### `ScanAsync`

```csharp
Task<ScanSession> ScanAsync(
    ScanOptions             options,
    IProgress<ScanProgress> progress,
    CancellationToken       cancellationToken = default)
```

Starts a new scan session:
1. Creates a `ScanSession` row in the database
2. Walks the directory tree using bounded parallel BFS (or Rust jwalk)
3. Writes file and folder entries to the database in batches
4. Runs a post-scan `FolderSizeAggregator` pass for accurate folder totals
5. Reports progress every 300ms via the `progress` callback
6. Returns the final `ScanSession`

**Thread safety:** Scanner implementations are designed to be called from any thread. `Program.Main()` installs a `DispatcherQueueSynchronizationContext` for the WinUI thread; `Progress<T>` created there posts back to it, and the scan ViewModel also uses explicit `DispatcherQueue.TryEnqueue()` marshalling.

**Example:**

```csharp
var dq = DispatcherQueue.GetForCurrentThread();
var progress = new Progress<ScanProgress>(p =>
{
    dq.TryEnqueue(() => StatusText = $"{p.FilesScanned:N0} files scanned");
});

var session = await scanner.ScanAsync(
    new ScanOptions { RootPath = @"C:\Users\Alice", MaxParallelism = 4 },
    progress, cts.Token);
```

---

#### `GetLargestFilesAsync` / `GetLargestFoldersAsync`

```csharp
Task<IReadOnlyList<FileEntry>> GetLargestFilesAsync(long sessionId, int topN, CancellationToken ct = default)
Task<IReadOnlyList<FolderEntry>> GetLargestFoldersAsync(long sessionId, int topN, CancellationToken ct = default)
```

Return deterministic top-N snapshots from the database. The UI uses separate paged search/count APIs for incremental result browsing. Reads can run while a scan writes; WAL reduces reader/writer interference but does not make every read a final scan snapshot.

---

## 4. Turbo Scanner

### Overview

`TurboFileScanner` (in `StorageMaster.Platform.Windows`) implements `IFileScanner` by spawning the native `turbo-scanner.exe` Rust binary as a hidden background subprocess. The subprocess enumerates files using **jwalk**'s work-stealing Rayon thread pool and writes JSONL records to stdout.

### Availability check

```csharp
bool available = TurboFileScanner.IsAvailable;
// true when turbo-scanner.exe exists in AppContext.BaseDirectory
```

### Automatic fallback

If `turbo-scanner.exe` is absent, `TurboFileScanner.ScanAsync()` immediately delegates to the injected `_fallback` (managed `FileScanner`). The caller receives a valid `ScanSession` — the fallback is completely transparent.

### Output format

The Rust binary outputs one JSON line per file/folder on stdout. Errors go to stderr as `WARN: <message>`; the managed host drains and persists those warnings to `ScanErrors` when a scan-error repository is available.

```json
{"path":"C:\\Users\\Alice\\file.txt","size":12345,"modified_unix":1700000000,"created_unix":1690000000,"modified_utc_ticks":638355968000000000,"created_utc_ticks":638269568000000000,"attributes":32,"volume_serial":305419896,"file_index":123456789,"is_dir":false,"is_hidden":false}
{"path":"C:\\Users\\Alice","size":0,"modified_unix":1700000000,"created_unix":1690000000,"modified_utc_ticks":638355968000000000,"created_utc_ticks":638269568000000000,"attributes":16,"volume_serial":null,"file_index":null,"is_dir":true,"is_hidden":false}
```

Contract v3 preserves exact Windows UTC ticks, raw attributes, and stable volume/file identity. File metadata and identity come from one handle. Link following is off unless `--follow-links` is passed; the managed host also validates and, where Windows permissions permit, locks every root ancestor against replacement for the scan lifetime.

### CLI usage (standalone)

```powershell
turbo-scanner.exe --path "C:\Users\Alice" --threads 8
turbo-scanner.exe --path "D:\" --min-size 1048576  # only files ≥ 1 MB
turbo-scanner.exe --path "C:\Projects" --skip-hidden
turbo-scanner.exe --path "C:\Projects" --follow-links  # explicit opt-in
```

### Building from source

```powershell
cargo build --release --manifest-path turbo-scanner/Cargo.toml --target x86_64-pc-windows-msvc
# Output: turbo-scanner/target/x86_64-pc-windows-msvc/release/turbo-scanner.exe
```

---

## 5. Smart Cleaner API

### Interface: `ISmartCleanerService`

Location: `StorageMaster.Core/Interfaces/ISmartCleanerService.cs`

The Smart Cleaner provides a one-click scan-and-clean path that does **not** require a prior database scan session.

#### `AnalyzeAsync`

```csharp
Task<SmartCleanAnalysisResult> AnalyzeAsync(
    IProgress<string>? progress = null,
    CancellationToken  ct = default)
```

Scans all known junk locations directly on the filesystem and returns groups plus path-specific warnings. `IsPartial` is true when any required source/branch could not be safely inspected.

```csharp
record SmartCleanGroup(
    SmartCleanSource Source,
    string Category,
    string Description,
    string IconGlyph,
    long   EstimatedBytes,
    IReadOnlyList<string> Paths,
    IReadOnlyDictionary<string, FileSnapshot> ExpectedFileSnapshots,
    bool   IsSelected = true)
```

Analysis requests strong no-follow guards from each trusted source boundary through the enumerated branch. A weak guard is reported as partial and cannot authorize cleanup; required boundary/scan-root reparse points are explicit warnings, and descendant reparses are skipped. Every returned path has a source identifier and an identity-bearing analysis-time snapshot.

**Scanned sources:** canonical `%WINDIR%\Temp` and `%LOCALAPPDATA%\Temp` (never redirected `TMP`/`TEMP`), browser caches (Chrome/Edge/Firefox/Brave/Opera), Windows Update cache, Windows Error Reports, Delivery Optimization, thumbnail cache, and DirectX shader cache.

---

#### `CleanAsync`

```csharp
Task<SmartCleanResult> CleanAsync(
    IReadOnlyList<SmartCleanGroup> groups,
    DeletionMethod                 method,
    IProgress<string>?             progress = null,
    CancellationToken              ct = default)
```

Before deletion, each path must still be a regular file beneath the allow-listed root for its source. Smart Cleaner holds a no-follow ancestry lease, requires exact stable identity/metadata, and submits one guarded deletion at a time. The result includes logical bytes processed, bytes actually reported freed, successful-path count, cancellation/error state, per-path failures, and audit-write warnings. Recycle Bin moves report zero reclaimed bytes because allocation remains used until the bin is emptied.

**IMPORTANT:** Call this only after explicit user confirmation (the Smart Cleaner page uses a `ContentDialog` for this).

---

## 6. Cleanup system API

### Interface: `ICleanupEngine`

Location: `StorageMaster.Core/Interfaces/ICleanupEngine.cs`

The session-based cleanup workflow. Requires a completed `ScanSession` to operate.

#### `GetSuggestionsAsync`

```csharp
IAsyncEnumerable<CleanupSuggestion> GetSuggestionsAsync(
    long              sessionId,
    AppSettings       settings,
    CancellationToken ct = default)
```

Runs all registered `ICleanupRule` instances against the given session.
- Analysis is read-only, but a rule may inspect persisted scan rows, the live filesystem, the registry, or another platform query.
- Suggestions are yielded in rule-registration order; rules run one at a time.
- A rule exception is logged and isolated so later rules still run. The API does not currently return an explicit partial-analysis marker, so an empty result alone does not prove every rule completed.

---

#### `ExecuteAsync`

```csharp
Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
    IReadOnlyList<CleanupSuggestion> suggestions,
    bool                            dryRun,
    DeletionMethod                  deletionMethod,
    IProgress<CleanupProgress>?     progress = null,
    CancellationToken               ct = default)
```

**IMPORTANT:** Interactive callers require explicit user confirmation. The unattended scheduler path requires current, versioned destructive consent and a successful headless-policy check instead of a live dialog.

- Passes each suggestion's `TargetPaths` to `IFileDeleter.DeleteManyAsync`
- Returns a `CleanupResult` per suggestion (Success, PartialSuccess, Failed, Skipped)
- Attempts to log every result to `ICleanupLogRepository`; an audit-write failure is returned as a warning/partial outcome rather than hidden
- If `dryRun = true`: estimates sizes, logs intended actions, does not touch the filesystem

---

### Interface: `ICleanupRule`

```csharp
public interface ICleanupRule
{
    string RuleId { get; }             // Stable ID, e.g. "core.temp-files"
    string DisplayName { get; }
    CleanupCategory Category { get; }
    IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId, AppSettings settings, CancellationToken ct);
}
```

**Contract:**
- `AnalyzeAsync` MUST be read-only — never modify the filesystem
- Each suggestion MUST have a unique `Guid`
- `TargetPaths` MUST be absolute paths or one of the recognized execution sentinels: `"::RecycleBin::"` or `"::DnsFlush::"`
- Rules SHOULD call `ct.ThrowIfCancellationRequested()` periodically

### Risk levels

| Level | Meaning | Examples |
|-------|---------|---------|
| `Safe` | Generated/recreatable data with the lowest expected impact | Selected application-cache suggestions |
| `Low` | Usually recreatable or narrowly scoped, but still reviewable | Temp files, browser cache, downloaded installers, thumbnail/icon/font cache, DNS cache, Store logs |
| `Medium` | Material state or performance impact is plausible | Permanent Recycle Bin emptying, large/old user files, Prefetch files |
| `High` | Heuristic targeting could affect application/user state | Uninstalled-program leftovers; permanent delete is blocked by the generic engine |

---

## 7. Storage API

### Interface: `IScanRepository`

Key usage patterns:

```csharp
// Most recent session
var sessions = await repo.GetRecentSessionsAsync(count: 1);
var latest = sessions.FirstOrDefault();

// Top 500 files
var files = await repo.GetLargestFilesAsync(sessionId, topN: 500);

// Category breakdown
var breakdown = await repo.GetCategoryBreakdownAsync(sessionId);
foreach (var (cat, (count, bytes)) in breakdown.OrderByDescending(x => x.Value.Bytes))
    Console.WriteLine($"{cat}: {count:N0} files, {bytes:N0} bytes");

// Delete session (CASCADE removes FileEntries, FolderEntries, ScanErrors)
await repo.DeleteSessionAsync(sessionId);
```

### Interface: `IScanErrorRepository`

```csharp
// Log errors from a scan
await errorRepo.LogErrorsAsync(session.Id, errors, ct);

// Retrieve errors for display
var errors = await errorRepo.GetErrorsForSessionAsync(sessionId, ct);
```

### StorageDbContext

The `StorageDbContext` singleton manages initialization, migration, and database-wide write coordination. Each repository operation obtains its own open, configured SQLite connection lease and disposes it after use.

Multiple contexts in one process coordinate initialization and writes by canonical database path. Independent processes rely on SQLite's immediate writer reservation and bounded 30-second timeout; migration re-reads the schema version after acquiring that reservation.

**Schema migration** runs automatically on first `GetConnectionAsync()`.

---

## 8. Platform API

### Interface: `IFileDeleter`

The platform-level deletion abstraction. The Windows implementation handles:
- Batch Recycle Bin deletion via `IFileOperation` with `FOFX_RECYCLEONDELETE`
- Permanent file deletion plus handle-bound, no-follow recursive directory deletion
- The special sentinel `"::RecycleBin::"`, accepted only for explicit permanent emptying
- Dry-run mode (estimate only, no delete); orchestrators such as `CleanupEngine` own audit logging
- Canonical root rejection and optional expected-snapshot revalidation before mutation

```csharp
var requests = new List<DeletionRequest>
{
    new(@"C:\Users\Alice\Downloads\setup.exe", DeletionMethod.RecycleBin, DryRun: false),
    new(@"C:\Temp\leftover.tmp",              DeletionMethod.RecycleBin, DryRun: false),
};

await foreach (var outcome in deleter.DeleteManyAsync(requests))
{
    if (!outcome.Success)
        logger.LogWarning("Failed: {Path} — {Error}", outcome.Path, outcome.Error);
}
```

**Error handling:** `DeleteManyAsync` returns per-file failures as `DeletionOutcome { Success = false, Error = "message" }`; cancellation still propagates as `OperationCanceledException`.

---

### Interface: `IAdminService`

```csharp
bool IsRunningAsAdmin { get; }
void RestartAsAdmin(bool enableDeepScan)
// → ProcessStartInfo { Verb = "runas", Arguments = "--deep-scan" }
```

Used by `ScanViewModel` when the user enables Deep Scan but is not running as admin.

---

### Interface: `IDriveInfoProvider`

```csharp
IReadOnlyList<DriveDetail> GetAvailableDrives()   // Fixed + Network + Removable, IsReady = true
DriveDetail? GetDrive(string rootPath)             // null if drive not found or not ready
```

---

### Interface: `IInstalledProgramProvider`

```csharp
IReadOnlyList<InstalledProgramInfo> GetInstalledPrograms()
// Reads HKLM + HKCU uninstall registry keys (32 + 64 bit views)
// Skips SystemComponent=1 entries (OS components)
```

Used by `UninstalledProgramLeftoversRule` to cross-reference AppData folders against installed programs.

---

## 9. UI pages reference

v2 adds shared visual tokens/styles, reusable page/state/gauge/badge/card controls, grouped shell navigation, Mica fallback, and a Scan Workspace route. Scan completion now opens the workspace, which loads persisted overview, files, folders, duplicate-run summary, and errors from existing repositories without schema changes.

### Dashboard (`DashboardPage`)

**Purpose:** Application home screen showing disk health and last scan summary.

**Displays:**
- Status message (last scan info or "no scan yet")
- Total scanned size and file count
- Available drives list with usage progress bar
- Quick-action buttons: Start Scan, View Last Results

---

### Scan (`ScanPage`)

**Purpose:** Drive/folder selection and active scan control.

**Features:**
- Text box for manual path entry
- Quick-select drive buttons
- Browse button (FolderPicker with HWND association)
- **Turbo Scanner toggle** — uses Rust binary when available; greyed out with InfoBar warning when `turbo-scanner.exe` not found
- **Deep Scan toggle** — includes system directories; shows elevation prompt when not running as admin
- Start/Cancel buttons
- Live progress display (files, folders, bytes, errors, elapsed time, sample speed, conservative ETA)
- InfoBar for success and error states
- Scan completion action opens the unified Scan Workspace.

**Note on FolderPicker:** WinUI 3 requires the window HWND to be passed via `InitializeWithWindow.Initialize(picker, hwnd)`. Done in `ScanPage.xaml.cs::BrowseButton_Click`.

---

### Results (`ResultsPage`)

**Purpose:** Visualise the contents of a completed scan session.

**Loaded with:** `long sessionId` parameter.

**Pivot tabs:**
1. **Largest Files** — 200-row pages, filterable/sortable, with explicit load-more state
2. **Largest Folders** — 100-row pages with post-aggregation totals, filterable/sortable, with explicit load-more state
3. **File Types** — category breakdown sorted by bytes
4. **Errors** — 100-row pages of recorded scan/backend warnings and errors; badge shows total count

**Filter:** Case-insensitive path-contains filter applied to files and folders simultaneously.

---

### Cleanup (`CleanupPage`)

**Purpose:** Session-based cleanup with per-category control.

**Flow:**
1. Select a completed session from the ComboBox
2. Review/toggle **Cleanup Options** — 16 rule-category toggles grouped by Windows/system, browser, application, downloads, and large files
3. Toggle **Deletion mode** (Recycle Bin vs. permanent)
4. Optionally toggle **Clear entire Downloads folder** (separate switch)
5. Click **Analyse** → suggestions populate
6. Review suggestions (checkbox per item, risk badge, size); only Safe/Low suggestions that support Recycle Bin start selected
7. Click **Clean Up Selected…** → `ContentDialog` confirmation
8. On confirm → execution results appear with per-suggestion status. A dry-run unlocks an immediate real-delete follow-up only if every selected preview result succeeded without failures or warnings

**Important:** The `ContentDialog` is the interactive safety gate. The command also rejects a real run unless it follows the expected review/preview state; scheduled cleanup is a separate, consent-versioned headless path.

---

### Smart Cleaner (`SmartCleanerPage`)

**Purpose:** One-click junk scan and removal — no prior scan session required.

**Flow:**
1. Optionally toggle **Send to Recycle Bin** (recommended; on by default)
2. Click **Scan & Analyse** → 7 allow-listed junk sources scanned directly without following reparse points
3. Review group list: each card shows category, description, icon, estimated size, checkbox
4. Total size of selected groups shown in a summary bar
5. Click **Clean Selected** → `ContentDialog` confirmation
6. Result InfoBar distinguishes logical bytes processed/moved from permanently reclaimed bytes and warns about partial scans, skipped/failed paths, cancellation, or audit-write failure

**Key difference from Cleanup page:** Does not create a scan session in the database. Results are not historically browsable. Suitable for quick routine cleanup without a full scan.

---

### Settings (`SettingsPage`)

**Purpose:** Edit and persist `AppSettings`.

**Sections:**
- **Scan Options:** Default path, parallelism slider, hidden/system-folder controls, Turbo Scanner toggle, and editable excluded paths
- **Deletion Behaviour:** RecycleBin toggle, dry-run default toggle
- **Cleanup Options:** All 16 registered rule enable/disable toggles and the Downloads full-clear sub-option
- **Large & Old File Thresholds:** Size (MB) and age (days) sliders
- **About:** Current app version from informational assembly metadata (`2.2.1`), diagnostics export, update settings

**Save behaviour:** All settings written to SQLite on "Save Settings" click.

If settings cannot be loaded, editing and save/destructive maintenance commands remain disabled; command failures are surfaced in the page instead of escaping through `AsyncRelayCommand`. A new unattended cleanup job starts disabled. Enabling it opens a dedicated confirmation containing the target, rules, frequency, time, and Recycle Bin behavior, then persists the current destructive-consent contract version. Headless execution refuses missing or stale consent.

---

## 10. Dependency injection reference

### Singletons

```
StorageDbContext               ← migration/configuration plus independent connection leases
IScanRepository                ← ScanRepository
IScanErrorRepository           ← ScanErrorRepository
ICleanupLogRepository          ← CleanupLogRepository
ISettingsRepository            ← SettingsRepository
IDriveInfoProvider             ← DriveInfoProvider
IFileDeleter                   ← FileDeleter
IRecycleBinInfoProvider        ← RecycleBinInfoProvider
IAdminService                  ← AdminService
IInstalledProgramProvider      ← InstalledProgramProvider
IFileIdentityProvider          ← FileIdentityProvider
IFileSnapshotProvider          ← FileSnapshotProvider
INoFollowFileEnumerator        ← NoFollowFileEnumerator
FileScanner                    ← concrete managed scanner
TurboFileScanner               ← concrete Rust-backed scanner (wraps FileScanner as fallback)
IFileScanner                   ← FileScanner (ScanViewModel selects turbo dynamically)
ICleanupRule (×16)             ← active session-cleanup rules in registration order
ICleanupEngine                 ← CleanupEngine
ISmartCleanerService           ← SmartCleanerService
INavigationService             ← NavigationService
IScheduledTaskService          ← ScheduledTaskService
MainWindow
```

### Transients (new instance per navigate)

```
DashboardViewModel
ScanWorkspaceViewModel
ResultsViewModel
DuplicatesViewModel
CleanupViewModel
SettingsViewModel
SmartCleanerViewModel
SpaceMapViewModel
DriveHealthViewModel
```

### Special registrations

**ScanViewModel** is registered as a **Singleton** via a factory lambda:
```csharp
services.AddSingleton<ScanViewModel>(sp => new ScanViewModel(
    sp.GetRequiredService<FileScanner>(),       // managed scanner
    sp.GetRequiredService<TurboFileScanner>(),  // Rust-backed scanner
    sp.GetRequiredService<IDriveInfoProvider>(),
    sp.GetRequiredService<INavigationService>(),
    sp.GetRequiredService<IAdminService>(),
    sp.GetRequiredService<ISettingsRepository>()));
```

**DownloadedInstallersRule** uses a factory lambda to inject `KnownFolders.GetDownloadsPath`:
```csharp
services.AddSingleton<ICleanupRule>(sp => new DownloadedInstallersRule(
    sp.GetRequiredService<IScanRepository>(),
    KnownFolders.GetDownloadsPath));
```

**UninstalledProgramLeftoversRule** receives `IInstalledProgramProvider`:
```csharp
services.AddSingleton<ICleanupRule>(sp => new UninstalledProgramLeftoversRule(
    sp.GetRequiredService<IInstalledProgramProvider>()));
```

---

## 11. Database reference

### Connection string

```
Data Source=%LOCALAPPDATA%\StorageMaster\storagemaster.db;Mode=ReadWriteCreate;Cache=Private;Pooling=True;Default Timeout=30
```

### Applied PRAGMAs

```sql
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA foreign_keys=ON;
PRAGMA temp_store=MEMORY;
PRAGMA cache_size=-32000;
```

### Selected current table schemas

These are current post-migration shapes for primary scan/audit tables. `StorageMaster.Storage/Schema/DatabaseSchema.cs` remains authoritative and also defines duplicate, quarantine, operation-journal, space-map, and drive-health tables.

```sql
CREATE TABLE ScanSessions (
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    RootPath          TEXT    NOT NULL,
    StartedUtc        TEXT    NOT NULL,
    CompletedUtc      TEXT,
    Status            TEXT    NOT NULL DEFAULT 'Running',
    TotalSizeBytes    INTEGER NOT NULL DEFAULT 0,
    TotalFiles        INTEGER NOT NULL DEFAULT 0,
    TotalFolders      INTEGER NOT NULL DEFAULT 0,
    AccessDeniedCount INTEGER NOT NULL DEFAULT 0,
    ErrorMessage      TEXT
);

CREATE TABLE FileEntries (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId     INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
    FullPath      TEXT    NOT NULL,
    FileName      TEXT    NOT NULL,
    Extension     TEXT    NOT NULL DEFAULT '',
    SizeBytes     INTEGER NOT NULL DEFAULT 0,
    CreatedUtc    TEXT    NOT NULL,
    ModifiedUtc   TEXT    NOT NULL,
    AccessedUtc   TEXT    NOT NULL,
    Attributes    INTEGER NOT NULL DEFAULT 0,
    Category      TEXT    NOT NULL DEFAULT 'Unknown',
    IsReparsePoint INTEGER NOT NULL DEFAULT 0,
    NormalizedFullPath TEXT,
    IdentityVolumeSerial TEXT,
    IdentityFileIndex TEXT
);

CREATE TABLE FolderEntries (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId       INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
    FullPath        TEXT    NOT NULL,
    FolderName      TEXT    NOT NULL,
    DirectSizeBytes INTEGER NOT NULL DEFAULT 0,
    TotalSizeBytes  INTEGER NOT NULL DEFAULT 0,
    FileCount       INTEGER NOT NULL DEFAULT 0,
    SubFolderCount  INTEGER NOT NULL DEFAULT 0,
    IsReparsePoint  INTEGER NOT NULL DEFAULT 0,
    WasAccessDenied INTEGER NOT NULL DEFAULT 0,
    NormalizedFullPath TEXT NOT NULL,
    UNIQUE (SessionId, NormalizedFullPath)
);

CREATE TABLE ScanErrors (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId   INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
    Path        TEXT    NOT NULL,
    ErrorType   TEXT    NOT NULL,
    Message     TEXT    NOT NULL,
    OccurredAt  TEXT    NOT NULL
);

CREATE TABLE CleanupLog (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    SuggestionId TEXT    NOT NULL,
    RuleId       TEXT    NOT NULL,
    Title        TEXT    NOT NULL,
    BytesFreed   INTEGER NOT NULL DEFAULT 0,
    WasDryRun    INTEGER NOT NULL DEFAULT 0,
    Status       TEXT    NOT NULL,
    ExecutedUtc  TEXT    NOT NULL,
    ErrorMessage TEXT,
    AuditDataJson TEXT
);

CREATE TABLE Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

CREATE TABLE DriveHealthSnapshots (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    DriveName          TEXT    NOT NULL,
    VolumeLabel        TEXT    NOT NULL DEFAULT '',
    DriveFormat        TEXT    NOT NULL DEFAULT '',
    TotalBytes         INTEGER NOT NULL DEFAULT 0,
    FreeBytes          INTEGER NOT NULL DEFAULT 0,
    FreePercent        INTEGER NOT NULL DEFAULT 0,
    Status             TEXT    NOT NULL,
    Source             TEXT    NOT NULL DEFAULT '',
    Message            TEXT    NOT NULL DEFAULT '',
    Model              TEXT    NOT NULL DEFAULT '',
    SerialNumber       TEXT    NOT NULL DEFAULT '',
    MediaType          TEXT    NOT NULL DEFAULT '',
    TemperatureCelsius INTEGER,
    WearPercent        INTEGER,
    CapturedUtc        TEXT    NOT NULL
);
```

### Common queries

```sql
-- Top 20 largest files
SELECT FileName, SizeBytes, FullPath
FROM FileEntries WHERE SessionId = 1
ORDER BY SizeBytes DESC LIMIT 20;

-- Space by file type
SELECT Category, COUNT(*), SUM(SizeBytes)/1024.0/1024.0 AS TotalMB
FROM FileEntries WHERE SessionId = 1
GROUP BY Category ORDER BY SUM(SizeBytes) DESC;

-- Cleanup audit trail
SELECT Title, BytesFreed/1024.0/1024.0 AS MB, Status, ExecutedUtc, WasDryRun
FROM CleanupLog ORDER BY ExecutedUtc DESC LIMIT 20;

-- Scan errors for a session
SELECT Path, ErrorType, Message, OccurredAt
FROM ScanErrors WHERE SessionId = 1 ORDER BY OccurredAt;

-- All sessions with duration
SELECT Id, RootPath, Status, TotalFiles,
       ROUND(TotalSizeBytes/1073741824.0, 2) AS TotalGB,
       StartedUtc, CompletedUtc
FROM ScanSessions ORDER BY StartedUtc DESC;
```

---

## 12. Error handling strategy

### Scan errors

| Level | Error type | Response |
|-------|-----------|----------|
| Directory enumeration | `UnauthorizedAccessException` | Increment `AccessDeniedCount`, continue |
| Directory enumeration | `IOException`, `SecurityException` | Log at Debug, continue |
| File info read | `IOException`, `UnauthorizedAccessException` | Skip file, log at Debug |
| Turbo Scanner stderr | Any `WARN:` line | Log at Debug, persist as a scan error, continue processing stdout |
| Session-level | Any uncaught exception | Attempt to flush discovered rows and aggregate partial folder totals, mark session `Failed`, then rethrow |

### Cleanup errors

Deletion errors are returned as `DeletionOutcome.Success = false`:

```csharp
try { /* delete */ }
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to delete {Path}", request.Path);
    return new DeletionOutcome(request.Path, false, 0, ex.Message);
}
```

A suggestion's result status:
- `Success` — all paths deleted
- `PartialSuccess` — at least one path succeeded but another path or post-mutation recovery/audit step failed
- `Failed` — no paths deleted
- `Skipped` — no target paths

### Crash logging

Unhandled exceptions in `App.UnhandledException`, `AppDomain.CurrentDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` are written to `%LOCALAPPDATA%\StorageMaster\logs\startup-errors.log`. The log file grows indefinitely (append-only); manual cleanup may be needed if the app is crashing frequently.

### UI error display

ViewModels surface errors via:
- `HasError: bool` → `InfoBar` visibility
- `ErrorMessage: string` → `InfoBar.Message`
- `StatusMessage: string` → descriptive text

---

## 13. Testing guide

The exact test count changes as regression coverage grows; use the command below for the current total. The .NET test project deliberately does not reference `StorageMaster.UI`, so WinUI page/ViewModel lifecycle behavior is build-reviewed and verified on an interactive desktop rather than loaded into the non-WinUI test host. Core scheduling policy and non-UI services remain directly testable.

### Running tests

```powershell
# All tests
dotnet test "tests/StorageMaster.Tests/StorageMaster.Tests.csproj"

# With verbose output
dotnet test "tests/StorageMaster.Tests/StorageMaster.Tests.csproj" --logger "console;verbosity=normal"

# Specific test
dotnet test "tests/StorageMaster.Tests/StorageMaster.Tests.csproj" `
    --filter "FullyQualifiedName=StorageMaster.Tests.Storage.ScanRepositoryTests.InsertAndQueryFileEntries_RoundTrip"
```

### Test patterns

**Unit tests** — mock `IScanRepository`:
```csharp
var repoMock = new Mock<IScanRepository>();
repoMock.Setup(r => r.GetLargestFilesAsync(1, 1000, It.IsAny<CancellationToken>()))
        .ReturnsAsync([/* test data */]);
var rule = new LargeOldFilesCleanupRule(repoMock.Object);
```

**Integration tests** — real SQLite in a temp file:
```csharp
var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
var ctx    = new StorageDbContext(dbPath, NullLogger<StorageDbContext>.Instance);
var repo   = new ScanRepository(ctx);
// ... test against real SQLite ...
await ctx.DisposeAsync();
File.Delete(dbPath);
```

**Filesystem tests** — real temp directories:
```csharp
var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "test.txt"), "content");
// ... scan ...
Directory.Delete(root, recursive: true);
```

---

## 14. Adding a cleanup rule

### Step 1: Create the rule class

```csharp
// src/StorageMaster.Core/Cleanup/Rules/OldLogFilesRule.cs

public sealed class OldLogFilesRule : ICleanupRule
{
    private readonly IScanRepository _repo;

    public string RuleId      => "core.old-log-files";
    public string DisplayName => "Old Log Files";
    public CleanupCategory Category => CleanupCategory.LogFiles;

    public OldLogFilesRule(IScanRepository repo) => _repo = repo;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var files  = await ScanFilePager.LoadAllAsync(_repo, sessionId, ct);

        var logs = files
            .Where(f => (f.Extension is ".log" or ".etl" or ".evtx")
                     && f.ModifiedUtc < cutoff
                     && f.Identity is not null)
            .ToList();

        foreach (var file in logs)
        {
            ct.ThrowIfCancellationRequested();
            yield return new CleanupSuggestion
            {
                Id             = Guid.NewGuid(),
                RuleId         = RuleId,
                Title          = $"Old log file: {file.FileName}",
                Description    = $"Not modified in 90+ days: {file.FullPath}",
                Category       = Category,
                Risk           = CleanupRisk.Low,
                EstimatedBytes = file.SizeBytes,
                TargetPaths    = [file.FullPath],
                ExpectedFileSnapshots = new Dictionary<string, FileSnapshot>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [file.FullPath] = new(
                        file.FullPath,
                        file.Identity,
                        file.SizeBytes,
                        file.ModifiedUtc,
                        file.Attributes),
                },
            };
        }
    }
}
```

### Step 2: Register in DI

```csharp
// In src/StorageMaster.UI/ServiceBootstrapper.cs::BuildServices():
services.AddSingleton<ICleanupRule, OldLogFilesRule>();
```

### Step 3: Add a CleanupCategoryOption

In `CleanupViewModel.BuildCategoryGroups()`, add the category through `AddItem(...)` so the user can toggle the rule in the correct group.

### Step 4: Add to AppSettings

Add a `CleanOldLogFiles` property to `AppSettings` and wire it in `SettingsViewModel` and `SettingsPage.xaml`.

---

## 15. Adding a scan backend

To add a faster scan backend:

### Step 1: Implement `IFileScanner`

```csharp
public sealed class MftFileScanner : IFileScanner
{
    private readonly IScanRepository _repo;

    // Implement ScanAsync using NTFS MFT enumeration (FSCTL_ENUM_USN_DATA)
    // Write FileEntry / FolderEntry records via _repo — same as FileScanner
}
```

### Step 2: Register

```csharp
services.AddSingleton<MftFileScanner>(sp => new MftFileScanner(
    sp.GetRequiredService<IScanRepository>(),
    sp.GetRequiredService<FileScanner>()   // fallback
));
```

### Step 3: Select in ScanViewModel

Add a third `_mftScanner` field and an `IsMftAvailable` property. Update the active scanner selection:

```csharp
var activeScanner = UseMft && IsMftAvailable ? _mftScanner
                 : UseTurboScanner && TurboScannerAvailable ? _turboScanner
                 : _scanner;
```

---

## 16. Troubleshooting

### Turbo Scanner not available

The InfoBar in ScanPage warns when `turbo-scanner.exe` is missing. This is normal for local F5 debug builds without a publish step. To enable it locally:

```powershell
cargo build --release --manifest-path turbo-scanner/Cargo.toml
dotnet build src/StorageMaster.UI/StorageMaster.UI.csproj -c Debug `
    -p:Platform=x64 -p:RuntimeIdentifier=win-x64
$uiOutput = "src\StorageMaster.UI\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64"
Copy-Item turbo-scanner\target\release\turbo-scanner.exe `
    (Join-Path $uiOutput "turbo-scanner.exe")
```

Or run a full publish:
```powershell
dotnet publish src/StorageMaster.UI/StorageMaster.UI.csproj /p:PublishProfile=win-x64 -c Release
```

### Scan is very slow

- If scanning an HDD, set `ScanParallelism = 1` in Settings (avoids seek thrashing)
- Turbo Scanner may actually be slower on HDDs due to non-sequential I/O — try disabling it
- Exclude known large system folders (WinSxS is excluded by default)

### "Access denied" paths

Expected — the scanner increments `AccessDeniedCount` and moves on. Enable **Deep Scan** + admin elevation to scan protected paths. The Errors tab shows persisted per-path errors/warnings; the count can exceed the currently loaded 100-row page.

### Folder sizes show 0 or wrong values

`TotalSizeBytes` is computed in a post-scan aggregation pass. Cancelled and failed scans now attempt a final flush and partial aggregation, but the result remains incomplete by definition and an unexpected terminal failure can still prevent finalization. Re-run a successful complete scan for authoritative totals.

### Database busy or locked

SQLite WAL normally allows readers alongside one writer; SQLite still serializes writers and some schema/checkpoint activity can wait. StorageMaster uses independent connection leases, in-process write coordination, immediate transactions for atomic mutations/migrations, and a bounded 30-second busy timeout. If an operation still times out:
1. Close other StorageMaster UI/CLI instances and retry.
2. Check filesystem permissions and free space for `%LOCALAPPDATA%\StorageMaster`.
3. Preserve `storagemaster.db`, `storagemaster.db-wal`, and `storagemaster.db-shm` together for diagnosis; do not delete WAL/SHM files while any process may have the database open.

### Test failures after schema change

Integration tests create uniquely named temporary databases. Do not bulk-delete `%TEMP%\test_*.db`, which may belong to another process/project. Capture the exact disposable path from the failing test, ensure no test process still owns it, and remove only that file if cleanup is genuinely required. Migration tests should exercise the intended old schema through `StorageDbContext` rather than relying on a stale shared fixture.

### "Category breakdown" shows no data

This query requires at least one completed scan session:
```sql
SELECT COUNT(*) FROM FileEntries WHERE SessionId = <id>;
```
If zero, the session may be an empty scan or may have ended before files were persisted. Inspect `ScanSessions.Status`, `ErrorMessage`, and the Errors view before deciding which case applies.

### WinUI 3 app fails to launch

The installer deploys the Windows App SDK 1.6 runtime dependency. If launching the raw exe:
- The project is configured for Windows 10 1809 (build 17763) or later; consult the release compatibility matrix because the full build range is not yet lab-validated
- Install the Windows App SDK 1.6 runtime, or use the release installer so `Microsoft.WindowsAppRuntime.1.6.msix` is installed first
- Use the published folder output only on machines with the matching runtime already installed
