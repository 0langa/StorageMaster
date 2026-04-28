# StorageMaster — Full Technical Documentation

> **Version:** 1.3.0 | **Date:** 2026-04-28 | **.NET 8 / WinUI 3 / Windows App SDK 1.6**

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
| Windows | 10 1809 (build 17763) | Required by Windows App SDK 1.6 |
| .NET SDK | 8.0.x | `global.json` pins this |
| Visual Studio | 2022 17.9+ | For building the WinUI 3 UI project |
| Rust | stable | For building `turbo-scanner.exe` from source |
| Windows App SDK | 1.6+ | NuGet restored automatically |

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

---

## 2. Configuration reference

### AppSettings

All settings are persisted in the SQLite `Settings` table as JSON under the key `AppSettings`. Changes are applied immediately on "Save" in the Settings page.

#### Scanner settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `DefaultScanPath` | `string` | `C:\` | Pre-filled path in Scan page |
| `ScanParallelism` | `int` | `4` | Concurrent directory workers (increase for SSDs) |
| `ShowHiddenFiles` | `bool` | `false` | Include hidden files in results (reserved; not yet plumbed) |
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
| `CleanProgramLeftovers` | `false` | UninstalledProgramLeftoversRule (medium risk — off by default) |
| `CleanLargeOldFiles` | `false` | LargeOldFilesCleanupRule (medium risk — off by default) |

#### Large file thresholds (used by LargeOldFilesCleanupRule)

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `LargeFileSizeMb` | `int` | `500` | Minimum file size in MB |
| `OldFileAgeDays` | `int` | `365` | Minimum age in days since last-write |

### ScanOptions

Passed programmatically to `IFileScanner.ScanAsync`.

| Option | Default | Description |
|--------|---------|-------------|
| `RootPath` | required | Root path to scan |
| `MaxParallelism` | `4` | Directory workers |
| `DbBatchSize` | `500` | Flush file entries to DB every N entries |
| `ExcludedPaths` | `C:\Windows\WinSxS`, `C:\Windows\Installer` | Case-insensitive prefix exclusions |
| `FollowSymlinks` | `false` | Follow reparse points |
| `DeepScan` | `false` | When true: empty exclusions, uses max CPU parallelism |

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

**Thread safety:** Safe to call from any thread. Progress callbacks should marshal to the UI thread via `DispatcherQueue.TryEnqueue()` in WinUI 3 unpackaged apps (no SynchronizationContext).

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
IAsyncEnumerable<FileEntry>   GetLargestFilesAsync(long sessionId, int topN = 100, CancellationToken ct = default)
IAsyncEnumerable<FolderEntry> GetLargestFoldersAsync(long sessionId, int topN = 100, CancellationToken ct = default)
```

Stream top-N results from the database. Can be called during an in-progress scan (WAL mode ensures no reader/writer conflict).

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

The Rust binary outputs one JSON line per file/folder on stdout. Errors go to stderr as `WARN: <message>`.

```json
{"path":"C:\\Users\\Alice\\file.txt","size":12345,"modified_unix":1700000000,"created_unix":1690000000,"is_dir":false}
{"path":"C:\\Users\\Alice","size":0,"modified_unix":1700000000,"created_unix":1690000000,"is_dir":true}
```

### CLI usage (standalone)

```powershell
turbo-scanner.exe --path "C:\Users\Alice" --threads 8
turbo-scanner.exe --path "D:\" --min-size 1048576  # only files ≥ 1 MB
turbo-scanner.exe --path "C:\Projects" --skip-hidden
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
Task<IReadOnlyList<SmartCleanGroup>> AnalyzeAsync(
    IProgress<string>? progress = null,
    CancellationToken  ct = default)
```

Scans all known junk locations directly on the filesystem and returns a list of `SmartCleanGroup` objects.

```csharp
record SmartCleanGroup(
    string Category,
    string Description,
    string IconGlyph,
    long   EstimatedBytes,
    IReadOnlyList<string> Paths,
    bool   IsSelected = true)
```

**Scanned sources:** Temp files, browser caches (Chrome/Edge/Firefox/Brave/Opera), Windows Update cache, Windows Error Reports, Delivery Optimization, thumbnail and shader caches, Recycle Bin.

---

#### `CleanAsync`

```csharp
Task<long> CleanAsync(
    IReadOnlyList<SmartCleanGroup> groups,
    DeletionMethod                 method,
    IProgress<string>?             progress = null,
    CancellationToken              ct = default)
```

Deletes the files in all provided groups using the specified deletion method. Returns total bytes freed.

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
- Reads from the database (never touches the filesystem)
- Yields suggestions in rule registration order
- Stream is lazily evaluated — rules run one at a time

---

#### `ExecuteAsync`

```csharp
Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
    IReadOnlyList<CleanupSuggestion> suggestions,
    bool              dryRun,
    CancellationToken ct = default)
```

**IMPORTANT:** Call only after explicit user confirmation (the `ContentDialog` in `CleanupPage.xaml.cs`).

- Passes each suggestion's `TargetPaths` to `IFileDeleter.DeleteManyAsync`
- Returns a `CleanupResult` per suggestion (Success, PartialSuccess, Failed, Skipped)
- Logs every result to `ICleanupLogRepository`
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
- `TargetPaths` MUST be absolute paths or the sentinel `"::RecycleBin::"`
- Rules SHOULD call `ct.ThrowIfCancellationRequested()` periodically

### Risk levels

| Level | Meaning | Examples |
|-------|---------|---------|
| `Safe` | Cannot cause any issues | Recycle Bin, browser cache |
| `Low` | Very unlikely to cause issues | Temp files, installer files in Downloads |
| `Medium` | Could cause minor issues | Large files user might need, program leftovers |
| `High` | Could cause significant issues | Reserved; not used in v1.3 |

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

The `StorageDbContext` singleton manages the single SQLite connection. All repositories receive it via constructor injection.

**Do not** open multiple `StorageDbContext` instances pointing at the same DB file — single connection + WAL mode is the correct pattern for a desktop app.

**Schema migration** runs automatically on first `GetConnectionAsync()`.

---

## 8. Platform API

### Interface: `IFileDeleter`

The platform-level deletion abstraction. The Windows implementation handles:
- Batch RecycleBin deletion via `SHFileOperation` (one call for all paths)
- Permanent deletion via `File.Delete` / `Directory.Delete`
- The special sentinel `"::RecycleBin::"` which calls `SHEmptyRecycleBin`
- Dry-run mode (estimate + log, no delete)

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

**Error handling:** `DeleteManyAsync` never throws for per-file errors. A failed deletion returns `DeletionOutcome { Success = false, Error = "message" }`.

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
- Live progress display (files, folders, bytes, estimated %)
- InfoBar for success and error states

**Note on FolderPicker:** WinUI 3 requires the window HWND to be passed via `InitializeWithWindow.Initialize(picker, hwnd)`. Done in `ScanPage.xaml.cs::BrowseButton_Click`.

---

### Results (`ResultsPage`)

**Purpose:** Visualise the contents of a completed scan session.

**Loaded with:** `long sessionId` parameter.

**Pivot tabs:**
1. **Largest Files** — top 500 files, filterable, sorted by size descending
2. **Largest Folders** — top 200 folders with correct total sizes (post-aggregation), filterable
3. **File Types** — category breakdown sorted by bytes
4. **Errors** — per-path scan errors with error type and message; badge shows count

**Filter:** Case-insensitive path-contains filter applied to files and folders simultaneously.

---

### Cleanup (`CleanupPage`)

**Purpose:** Session-based cleanup with per-category control.

**Flow:**
1. Select a completed session from the ComboBox
2. Review/toggle **Cleanup Options** — 10 category toggles (ToggleSwitch per category)
3. Toggle **Deletion mode** (Recycle Bin vs. permanent)
4. Optionally toggle **Clear entire Downloads folder** (separate switch)
5. Click **Analyse** → suggestions populate
6. Review suggestions (checkbox per item, risk badge, size)
7. Click **Clean Up Selected…** → `ContentDialog` confirmation
8. On confirm → execution results appear with per-suggestion status

**Important:** The `ContentDialog` is the hard safety gate. The ViewModel's `ExecuteCleanupCommand` is only called from within the dialog's Primary button handler.

---

### Smart Cleaner (`SmartCleanerPage`)

**Purpose:** One-click junk scan and removal — no prior scan session required.

**Flow:**
1. Optionally toggle **Send to Recycle Bin** (recommended; on by default)
2. Click **Scan & Analyse** → 8 junk sources scanned directly
3. Review group list: each card shows category, description, icon, estimated size, checkbox
4. Total size of selected groups shown in a summary bar
5. Click **Clean Selected** → `ContentDialog` confirmation
6. Success InfoBar appears with bytes freed

**Key difference from Cleanup page:** Does not create a scan session in the database. Results are not historically browsable. Suitable for quick routine cleanup without a full scan.

---

### Settings (`SettingsPage`)

**Purpose:** Edit and persist `AppSettings`.

**Sections:**
- **Scan Options:** Default path, parallelism slider, system folders toggle, Turbo Scanner toggle, excluded paths (future)
- **Deletion Behaviour:** RecycleBin toggle, dry-run default toggle
- **Cleanup Options:** All 10 rule enable/disable toggles, Downloads full-clear toggle
- **Large & Old File Thresholds:** Size (MB) and age (days) sliders
- **About:** App version (1.3.0), description

**Save behaviour:** All settings written to SQLite on "Save Settings" click.

---

## 10. Dependency injection reference

### Singletons

```
StorageDbContext               ← SQLite connection lifecycle
IScanRepository                ← ScanRepository
IScanErrorRepository           ← ScanErrorRepository
ICleanupLogRepository          ← CleanupLogRepository
ISettingsRepository            ← SettingsRepository
IDriveInfoProvider             ← DriveInfoProvider
IFileDeleter                   ← FileDeleter
IRecycleBinInfoProvider        ← RecycleBinInfoProvider
IAdminService                  ← AdminService
IInstalledProgramProvider      ← InstalledProgramProvider
FileScanner                    ← concrete managed scanner
TurboFileScanner               ← concrete Rust-backed scanner (wraps FileScanner as fallback)
IFileScanner                   ← FileScanner (ScanViewModel selects turbo dynamically)
ICleanupRule (×10)             ← all rules in order
ICleanupEngine                 ← CleanupEngine
ISmartCleanerService           ← SmartCleanerService
INavigationService             ← NavigationService
MainWindow
```

### Transients (new instance per navigate)

```
DashboardViewModel
ResultsViewModel
CleanupViewModel
SettingsViewModel
SmartCleanerViewModel
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
Data Source=%LOCALAPPDATA%\StorageMaster\storagemaster.db;Mode=ReadWriteCreate;Cache=Shared
```

### Applied PRAGMAs

```sql
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA foreign_keys=ON;
PRAGMA temp_store=MEMORY;
PRAGMA cache_size=-32000;
```

### Full table schemas

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
    IsReparsePoint INTEGER NOT NULL DEFAULT 0
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
    UNIQUE (SessionId, FullPath)
);

CREATE TABLE ScanErrors (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId   INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
    Path        TEXT    NOT NULL,
    ErrorType   TEXT    NOT NULL,
    Message     TEXT    NOT NULL,
    OccurredUtc TEXT    NOT NULL
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
    ErrorMessage TEXT
);

CREATE TABLE Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
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
SELECT Path, ErrorType, Message, OccurredUtc
FROM ScanErrors WHERE SessionId = 1 ORDER BY OccurredUtc;

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
| Turbo Scanner stderr | Any `WARN:` line | Log at Debug, continue processing stdout |
| Session-level | Any uncaught exception | Mark session `Failed`, rethrow |

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
- `PartialSuccess` — some deleted, some failed (bytes freed > 0)
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

### Running tests

```powershell
# All tests
dotnet test "tests/StorageMaster.Tests/StorageMaster.Tests.csproj"

# With verbose output
dotnet test "tests/StorageMaster.Tests/StorageMaster.Tests.csproj" --logger "console;verbosity=normal"

# Specific test
dotnet test --filter "FullyQualifiedName=StorageMaster.Tests.Storage.ScanRepositoryTests.InsertAndQueryFileEntries_RoundTrip"
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
        var files  = await _repo.GetLargestFilesAsync(sessionId, 50_000, ct);

        var logs = files
            .Where(f => f.Extension is ".log" or ".etl" or ".evtx"
                     && f.ModifiedUtc < cutoff)
            .ToList();

        if (logs.Count == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Old log files ({logs.Count:N0} files)",
            Description    = $"Log files not modified in 90+ days.",
            Category       = Category,
            Risk           = CleanupRisk.Low,
            EstimatedBytes = logs.Sum(f => f.SizeBytes),
            TargetPaths    = logs.Select(f => f.FullPath).ToList(),
        };
    }
}
```

### Step 2: Register in DI

```csharp
// In App.xaml.cs::BuildServices():
services.AddSingleton<ICleanupRule, OldLogFilesRule>();
```

### Step 3: Add a CleanupCategoryOption

In `CleanupViewModel.BuildCategoryOptions()`, add a new `CleanupCategoryOption` entry so the user can toggle the rule in the UI.

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
Copy-Item turbo-scanner\target\release\turbo-scanner.exe `
    src\StorageMaster.UI\bin\Debug\net8.0-windows10.0.19041.0\win-x64\
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

Expected — the scanner increments `AccessDeniedCount` and moves on. Enable **Deep Scan** + admin elevation to scan protected paths. The Errors tab in Results shows all access-denied paths.

### Folder sizes show 0 or wrong values

The `TotalSizeBytes` is computed in a post-scan aggregation pass. If the scan was interrupted (Cancelled or Failed) before the aggregation ran, folder totals will be zero. Re-run a complete scan to fix this.

### Database file locked

Single connection + WAL mode means this should not happen. If it does:
1. Ensure only one instance of StorageMaster is running
2. Delete `storagemaster.db-wal` if the app crashed
3. DB file: `%LOCALAPPDATA%\StorageMaster\storagemaster.db`

### Test failures after schema change

Integration tests create temp DB files. If schema changes are made:
1. Delete `%TEMP%\test_*.db` files
2. The migration logic in `StorageDbContext` will apply the new schema on next run

### "Category breakdown" shows no data

This query requires at least one completed scan session:
```sql
SELECT COUNT(*) FROM FileEntries WHERE SessionId = <id>;
```
If zero, the scan failed or was cancelled before any data was written.

### WinUI 3 app fails to launch

The installer deploys Windows App SDK dependencies. If launching the raw exe:
- Ensure Windows 10 1809 (build 17763) or later
- Install the Windows App SDK runtime from [aka.ms/windowsappsdk](https://aka.ms/windowsappsdk)
- Or use the published folder output which includes framework-dependent dependencies
