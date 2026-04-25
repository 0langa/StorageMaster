# StorageMaster — Full Technical Documentation

> **Version:** 1.0.0 | **Date:** 2026-04-25 | **.NET 10 / WinUI 3 / Windows App SDK 1.6**

---

## Table of contents

1. [Getting started](#1-getting-started)
2. [Configuration reference](#2-configuration-reference)
3. [Scanner API](#3-scanner-api)
4. [Cleanup system API](#4-cleanup-system-api)
5. [Storage API](#5-storage-api)
6. [Platform API](#6-platform-api)
7. [UI pages reference](#7-ui-pages-reference)
8. [Dependency injection reference](#8-dependency-injection-reference)
9. [Database reference](#9-database-reference)
10. [Error handling strategy](#10-error-handling-strategy)
11. [Testing guide](#11-testing-guide)
12. [Adding a cleanup rule](#12-adding-a-cleanup-rule)
13. [Adding a scan backend](#13-adding-a-scan-backend)
14. [Troubleshooting](#14-troubleshooting)

---

## 1. Getting started

### Prerequisites

| Requirement | Minimum version | Notes |
|-------------|----------------|-------|
| Windows | 10 1903 (build 18362) | Required by Windows App SDK |
| .NET SDK | 10.0.203 | `global.json` pins this |
| Visual Studio | 2022 17.9+ | For WinUI 3 UI project |
| Windows App SDK | 1.6+ | NuGet restored automatically |

### Clone and build

```powershell
git clone <repo-url>
cd StorageMaster

# Backend + tests only (no VS required)
dotnet build src/StorageMaster.Core/StorageMaster.Core.csproj
dotnet build src/StorageMaster.Storage/StorageMaster.Storage.csproj
dotnet build "src/StorageMaster.Platform.Windows/StorageMaster.Platform.Windows.csproj"
dotnet test  "tests/StorageMaster.Tests/StorageMaster.Tests.csproj"

# Full solution (requires VS 2022 or dotnet with runtime identifier)
dotnet build "src/StorageMaster.UI/StorageMaster.UI.csproj" -r win-x64 -c Release
```

### Database location

The SQLite database is created automatically on first launch at:
```
%LOCALAPPDATA%\StorageMaster\storagemaster.db
```

No installation or database setup is required.

---

## 2. Configuration reference

### AppSettings

All settings are persisted in the SQLite `Settings` table as JSON under the key `AppSettings`.

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `PreferRecycleBin` | `bool` | `true` | Send files to Recycle Bin instead of permanent delete |
| `DryRunByDefault` | `bool` | `false` | Preview cleanup actions without deleting |
| `LargeFileSizeMb` | `int` | `500` | Minimum file size (MB) for "Large Old Files" rule |
| `OldFileAgeDays` | `int` | `365` | Minimum age (days since last-write) for "Large Old Files" rule |
| `DefaultScanPath` | `string` | `C:\` | Pre-filled path in Scan page |
| `ScanParallelism` | `int` | `4` | Concurrent directory workers (increase for SSDs) |
| `ShowHiddenFiles` | `bool` | `false` | Include hidden files in scan results (reserved; not yet plumbed) |
| `SkipSystemFolders` | `bool` | `true` | Skip `C:\Windows\WinSxS` and `C:\Windows\Installer` (reserved) |
| `ExcludedPaths` | `IList<string>` | `[]` | Custom path prefix exclusions (reserved) |

### ScanOptions

Passed programmatically to `IFileScanner.ScanAsync`. Not currently exposed in UI but designed for it.

| Option | Default | Description |
|--------|---------|-------------|
| `RootPath` | required | Root path to scan |
| `MaxParallelism` | `4` | Directory workers (`ScanParallelism` from AppSettings) |
| `DbBatchSize` | `500` | Flush file entries to DB every N entries |
| `ExcludedPaths` | C:\Windows\WinSxS, C:\Windows\Installer | Case-insensitive prefix exclusions |
| `FollowSymlinks` | `false` | Follow reparse points (junctions, symlinks) |

**Parallelism tuning:**
- HDD: keep at `1–4` to avoid random seek thrashing
- SSD/NVMe: `8–16` for maximum throughput
- Network drive: `1–2` to avoid overwhelming the server

---

## 3. Scanner API

### Interface: `IFileScanner`

Location: `StorageMaster.Core/Interfaces/IFileScanner.cs`

#### `ScanAsync`

```csharp
Task<ScanSession> ScanAsync(
    ScanOptions             options,
    IProgress<ScanProgress> progress,
    CancellationToken       cancellationToken = default)
```

Starts a new scan session. This method:
1. Creates a `ScanSession` row in the database
2. Walks the directory tree using bounded parallel BFS
3. Writes file and folder entries to the database in batches
4. Reports progress every 300ms via the `progress` callback
5. Updates the session status to `Completed`, `Cancelled`, or `Failed`
6. Returns the final `ScanSession`

**Thread safety:** Safe to call from any thread. Progress callbacks are invoked from a background timer task. All database writes are protected by the `SqliteConnection` lifetime.

**Cancellation:** Pass a `CancellationToken` linked to a user-visible Cancel button. On cancellation, partial scan data is preserved and the session is marked `Cancelled`.

**Example:**

```csharp
var cts = new CancellationTokenSource();
var options = new ScanOptions
{
    RootPath       = @"C:\Users\Alice",
    MaxParallelism = 4,
    DbBatchSize    = 500,
};

var progress = new Progress<ScanProgress>(p =>
{
    Console.WriteLine($"{p.FilesScanned:N0} files | {p.BytesScanned:N0} bytes");
});

var session = await scanner.ScanAsync(options, progress, cts.Token);
Console.WriteLine($"Scan status: {session.Status}");
Console.WriteLine($"Total: {session.TotalFiles:N0} files, {session.TotalSizeBytes:N0} bytes");
```

---

#### `GetLargestFilesAsync`

```csharp
IAsyncEnumerable<FileEntry> GetLargestFilesAsync(
    long sessionId,
    int  topN = 100,
    CancellationToken cancellationToken = default)
```

Streams the top-N largest files for a session. Can be called while a scan is in progress for incremental display (WAL mode ensures no reader/writer conflict).

---

#### `GetLargestFoldersAsync`

```csharp
IAsyncEnumerable<FolderEntry> GetLargestFoldersAsync(
    long sessionId,
    int  topN = 100,
    CancellationToken cancellationToken = default)
```

Streams the top-N largest folders for a session.

---

### Progress reporting

`IProgress<ScanProgress>` is called from a background timer, approximately every 300ms. Do not perform heavy computation in the callback — post to the UI thread and return immediately.

```csharp
var progress = new Progress<ScanProgress>(p =>
{
    // This runs on the thread pool, NOT the UI thread.
    // For WinUI: DispatcherQueue.TryEnqueue(() => UpdateUI(p));
    // For tests: can read directly.
});
```

In the WinUI ViewModels, `Progress<T>` uses the synchronization context to automatically marshal to the UI thread.

---

## 4. Cleanup system API

### Interface: `ICleanupEngine`

Location: `StorageMaster.Core/Interfaces/ICleanupEngine.cs`

#### `GetSuggestionsAsync`

```csharp
IAsyncEnumerable<CleanupSuggestion> GetSuggestionsAsync(
    long              sessionId,
    AppSettings       settings,
    CancellationToken cancellationToken = default)
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
    CancellationToken cancellationToken = default)
```

**IMPORTANT:** This method MUST only be called after explicit user confirmation. In the WinUI app, this is enforced by the `ContentDialog` confirmation gate in `CleanupPage.xaml.cs`. Do not call this method from background processes or without user interaction.

- Passes each suggestion's `TargetPaths` to `IFileDeleter.DeleteManyAsync`
- Returns a `CleanupResult` for each suggestion (success, partial, failed, or skipped)
- Logs every result to `ICleanupLogRepository`
- If `dryRun = true`, estimates sizes but deletes nothing

---

### Interface: `ICleanupRule`

Location: `StorageMaster.Core/Interfaces/ICleanupRule.cs`

Implement this interface to add a new cleanup rule. The rule is automatically discovered by `CleanupEngine` if registered in DI.

```csharp
public interface ICleanupRule
{
    string RuleId { get; }            // Stable identifier, e.g. "myapp.orphan-files"
    string DisplayName { get; }       // Human-readable
    CleanupCategory Category { get; }
    IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId, AppSettings settings, CancellationToken ct);
}
```

**Contract:**
- `AnalyzeAsync` MUST be read-only — it must never modify the filesystem
- Each suggestion MUST have a unique `Guid` Id
- `TargetPaths` MUST be absolute paths or the sentinel `"::RecycleBin::"`
- Rules SHOULD check `cancellationToken.ThrowIfCancellationRequested()` periodically

---

### CleanupSuggestion fields

| Field | Purpose |
|-------|---------|
| `Id` | `Guid.NewGuid()` — unique per suggestion instance |
| `RuleId` | Used for grouping, logging, and deduplication |
| `Title` | Short summary shown in the suggestion list |
| `Description` | Detail text (size, path, reason) |
| `Category` | Used for filtering and grouping |
| `Risk` | Shown in UI; `Safe` and `Low` auto-proceed; `Medium`/`High` get warnings |
| `EstimatedBytes` | Shown as "potential savings" before execution |
| `TargetPaths` | List of absolute paths passed to `IFileDeleter` |
| `IsSystemPath` | Set to `true` to show an extra warning in the UI |

---

### Risk levels

| Level | Meaning | Examples |
|-------|---------|---------|
| `Safe` | Cannot cause any issues | Recycle Bin, browser cache |
| `Low` | Very unlikely to cause issues | Temp files, installer files in Downloads |
| `Medium` | Could cause minor issues if wrong | Large files user might need |
| `High` | Could cause significant issues | Reserved for v2 (registry, app data) |

---

## 5. Storage API

### Interface: `IScanRepository`

Full method reference — see CODEMAP.md. Key usage patterns:

**Getting the most recent session:**
```csharp
var sessions = await repo.GetRecentSessionsAsync(count: 1);
var latestSession = sessions.FirstOrDefault();
```

**Streaming results for a session:**
```csharp
var files = await repo.GetLargestFilesAsync(sessionId, topN: 500);
foreach (var f in files)
    Console.WriteLine($"{f.FileName}: {f.SizeBytes:N0} bytes");
```

**Category breakdown:**
```csharp
var breakdown = await repo.GetCategoryBreakdownAsync(sessionId);
foreach (var (cat, (count, bytes)) in breakdown.OrderByDescending(x => x.Value.Bytes))
    Console.WriteLine($"{cat}: {count:N0} files, {bytes:N0} bytes");
```

**Deleting a session (and all its data):**
```csharp
await repo.DeleteSessionAsync(sessionId);  // CASCADE deletes FileEntries and FolderEntries
```

---

### StorageDbContext

The `StorageDbContext` singleton manages the single SQLite connection. All repositories receive it via constructor injection.

**Do not** construct repositories without it. **Do not** open multiple `StorageDbContext` instances pointing at the same DB file — WAL shared-cache mode means one connection is the correct pattern for a desktop app.

**Schema migration** runs automatically on first `GetConnectionAsync()`. If the `SchemaVersion` table is missing, the full V1 schema is applied. Future versions add new `V2Statements` arrays.

---

## 6. Platform API

### Interface: `IFileDeleter`

The platform-level deletion abstraction. The Windows implementation (`FileDeleter.cs`) handles:
- Normal file/folder deletion via Recycle Bin
- The special sentinel `"::RecycleBin::"` which empties the entire Recycle Bin
- Dry-run mode (estimate + log, no delete)
- Bounded parallel execution (`SemaphoreSlim(4)`)

**Usage pattern:**
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

**Error handling:** `DeleteManyAsync` never throws for per-file errors. A failed deletion returns a `DeletionOutcome` with `Success = false` and an `Error` message. Only unexpected infrastructure failures (e.g., out-of-memory) propagate as exceptions.

---

### Interface: `IDriveInfoProvider`

```csharp
IReadOnlyList<DriveDetail> GetAvailableDrives()
DriveDetail? GetDrive(string rootPath)
```

Returns only drives with `IsReady = true` and types `Fixed | Network | Removable`. Returns `null` from `GetDrive()` if the path root cannot be read.

---

### Interface: `IRecycleBinInfoProvider`

```csharp
RecycleBinInfo GetRecycleBinInfo()  // { SizeBytes, ItemCount }
```

Returns `(0, 0)` if the Recycle Bin cannot be queried (e.g., insufficient permissions). Check `SizeBytes > 0` before generating a suggestion.

---

## 7. UI pages reference

### Dashboard (`DashboardPage`)

**Purpose:** Application home screen showing disk health and the last scan summary.

**Loads on:** Every navigation to the page (calls `ViewModel.LoadAsync()`).

**Displays:**
- Status message (last scan info or "no scan yet")
- Total scanned size and file count (from last completed session)
- Available drives list with usage progress bar
- Quick-action buttons: Start Scan, View Last Results

**Drive bar formula:** `ProgressBar.Value = UsedBytes`, `Maximum = TotalBytes`

---

### Scan (`ScanPage`)

**Purpose:** Drive/folder selection and active scan control.

**Loads on:** Navigation to the page (calls `ViewModel.Initialize()`).

**Features:**
- Text box for manual path entry
- Quick-select drive buttons (from `IDriveInfoProvider`)
- Browse button (uses `FolderPicker` with HWND association)
- Start/Cancel buttons (mutually exclusive via IsEnabled bindings)
- Live progress display (files scanned, folders, bytes, estimated %)
- InfoBar for success and error states

**Note on FolderPicker:** WinUI 3 requires the window HWND to be passed via `InitializeWithWindow.Initialize(picker, hwnd)`. This is done in `ScanPage.xaml.cs::BrowseButton_Click`.

---

### Results (`ResultsPage`)

**Purpose:** Visualise the contents of a completed scan session.

**Loads on:** Navigation with `long sessionId` parameter, OR from the Dashboard "View Last Results" button.

**Pivot tabs:**
1. **Largest Files** — top 500 files, filterable by path, sorted by size descending
2. **Largest Folders** — top 200 folders, filterable by path, sorted by total size descending
3. **File Types** — category breakdown (all categories, sorted by bytes descending)

**Filter:** Path contains filter applied to both files and folders simultaneously (case-insensitive).

---

### Cleanup (`CleanupPage`)

**Purpose:** Generate and execute cleanup suggestions for a completed scan session.

**Loads on:** Navigation (calls `ViewModel.InitializeAsync()` which populates the session list).

**Flow:**
1. Select a completed session from the `ComboBox`
2. Click **Analyse** → runs all `ICleanupRule` instances → populates suggestion list
3. Review suggestions (checkbox per item, risk badge, estimated size)
4. Toggle **Dry run** checkbox if desired
5. Click **Clean Up Selected…** → `ContentDialog` confirmation appears
6. On confirm → `CleanupEngine.ExecuteAsync()` → results list populates

**Important:** The `ContentDialog` is the hard safety gate. The ViewModel's `ExecuteCleanupCommand` is only called from within the dialog's Primary button handler.

---

### Settings (`SettingsPage`)

**Purpose:** Edit and persist `AppSettings`.

**Loads on:** Navigation (calls `ViewModel.LoadAsync()`).

**Sections:**
- **Deletion Behaviour:** RecycleBin toggle, DryRun default toggle
- **Large & Old File Thresholds:** Sliders for MB threshold and age threshold
- **Scan Options:** Default path, parallelism slider, hidden files toggle, system folders toggle

**Save behaviour:** Settings are immediately written to SQLite on "Save Settings" click. A 3-second confirmation message ("Settings saved.") fades automatically.

---

## 8. Dependency injection reference

### Container composition (`App.xaml.cs::BuildServices`)

The DI container is a `Microsoft.Extensions.DependencyInjection.ServiceCollection` built in `App.xaml.cs`. No separate IoC library is used.

**Singletons** (shared for application lifetime):

```
StorageDbContext           ← manages SQLite connection
IScanRepository            ← ScanRepository
ICleanupLogRepository      ← CleanupLogRepository
ISettingsRepository        ← SettingsRepository
IDriveInfoProvider         ← DriveInfoProvider (Platform.Windows)
IFileDeleter               ← FileDeleter (Platform.Windows)
IRecycleBinInfoProvider    ← RecycleBinInfoProvider (Platform.Windows)
IFileScanner               ← FileScanner (Core)
ICleanupRule               ← RecycleBinCleanupRule (Core) [first in order]
ICleanupRule               ← TempFilesCleanupRule
ICleanupRule               ← DownloadedInstallersRule
ICleanupRule               ← CacheFolderCleanupRule
ICleanupRule               ← LargeOldFilesCleanupRule [last in order]
ICleanupEngine             ← CleanupEngine (receives IEnumerable<ICleanupRule>)
INavigationService         ← NavigationService
MainWindow                 ← (singleton window instance)
```

**Transients** (new instance per `GetRequiredService<T>()` call):

```
DashboardViewModel
ScanViewModel
ResultsViewModel
CleanupViewModel
SettingsViewModel
```

### Adding a new service

```csharp
// In App.xaml.cs BuildServices():
services.AddSingleton<IMyService, MyServiceImpl>();
```

### Service resolution in pages

WinUI 3 pages do not support constructor injection from the framework. Use:

```csharp
public DashboardPage()
{
    ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
    InitializeComponent();
}
```

---

## 9. Database reference

### Connection string

```
Data Source=%LOCALAPPDATA%\StorageMaster\storagemaster.db;Mode=ReadWriteCreate;Cache=Shared
```

### Applied PRAGMAs

```sql
PRAGMA journal_mode=WAL;      -- allows concurrent readers during writes
PRAGMA synchronous=NORMAL;    -- fsync on checkpoint, not every commit
PRAGMA foreign_keys=ON;       -- CASCADE deletes enforced
PRAGMA temp_store=MEMORY;     -- temp tables in RAM
PRAGMA cache_size=-32000;     -- 32 MB page cache
```

### Schema version table

```sql
SELECT MAX(Version) FROM SchemaVersion;
-- Returns 0 (or error) → apply V1Statements → INSERT Version=1
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
-- Top 20 largest files in a session
SELECT FileName, SizeBytes, FullPath
FROM FileEntries
WHERE SessionId = 1
ORDER BY SizeBytes DESC
LIMIT 20;

-- Space used by file type
SELECT Category, COUNT(*), SUM(SizeBytes) / 1024.0 / 1024.0 AS TotalMB
FROM FileEntries
WHERE SessionId = 1
GROUP BY Category
ORDER BY SUM(SizeBytes) DESC;

-- Recent cleanup history
SELECT Title, BytesFreed / 1024.0 / 1024.0 AS MB, Status, ExecutedUtc, WasDryRun
FROM CleanupLog
ORDER BY ExecutedUtc DESC
LIMIT 20;

-- All scan sessions with duration
SELECT Id, RootPath, Status, TotalFiles,
       ROUND(TotalSizeBytes / 1073741824.0, 2) AS TotalGB,
       StartedUtc, CompletedUtc
FROM ScanSessions
ORDER BY StartedUtc DESC;
```

---

## 10. Error handling strategy

### Scan errors

File system errors during scanning are handled at three levels:

| Level | Error type | Response |
|-------|-----------|----------|
| Directory enumeration | `UnauthorizedAccessException` | Increment `AccessDeniedCount`, continue |
| Directory enumeration | `IOException`, `SecurityException` | Log at Debug, continue |
| File info read | `IOException`, `UnauthorizedAccessException` | Skip file, log at Debug |
| Session-level | Any uncaught exception | Mark session `Failed`, rethrow, no partial data lost |

The scanner is designed so that a single bad file or folder never stops the entire scan.

### Cleanup errors

Deletion errors are returned as `DeletionOutcome.Success = false`, never thrown:

```csharp
try { /* delete */ }
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to delete {Path}", request.Path);
    return new DeletionOutcome(request.Path, false, 0, ex.Message);
}
```

A suggestion's result status is:
- `Success` — all paths deleted
- `PartialSuccess` — some paths deleted, some failed (bytes freed > 0)
- `Failed` — no paths deleted
- `Skipped` — no target paths

### Database errors

`StorageDbContext.GetConnectionAsync()` throws on DB file access failure. This is treated as a fatal application error. All other DB errors from repositories propagate up to the ViewModel, which sets an error state and displays an `InfoBar`.

### UI error display

ViewModels surface errors via observable properties:
- `HasError: bool` → `InfoBar` visibility
- `ErrorMessage: string` → `InfoBar.Message`
- `StatusMessage: string` → descriptive text in card

---

## 11. Testing guide

### Running tests

```powershell
# All tests
dotnet test "tests/StorageMaster.Tests/StorageMaster.Tests.csproj"

# With verbose output
dotnet test "tests/StorageMaster.Tests/StorageMaster.Tests.csproj" --logger "console;verbosity=normal"

# Specific test
dotnet test --filter "FullyQualifiedName=StorageMaster.Tests.Storage.ScanRepositoryTests.InsertAndQueryFileEntries_RoundTrip"
```

### Test categories

**Unit tests** (`Scanner/`, `Cleanup/`) — use Moq to mock `IScanRepository`:

```csharp
var repoMock = new Mock<IScanRepository>();
repoMock.Setup(r => r.GetLargestFilesAsync(1, 1000, It.IsAny<CancellationToken>()))
        .ReturnsAsync([/* test data */]);

var rule = new LargeOldFilesCleanupRule(repoMock.Object);
```

**Integration tests** (`Storage/`) — use a real SQLite DB in a temp file:

```csharp
var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
var ctx    = new StorageDbContext(dbPath, NullLogger<StorageDbContext>.Instance);
var repo   = new ScanRepository(ctx);
// ... test against real SQLite ...
await ctx.DisposeAsync();
File.Delete(dbPath);
```

**Real filesystem tests** (`FileScannerTests`) — create temp directories:

```csharp
var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "file.txt"), "content");
// ... scan ...
Directory.Delete(root, recursive: true);
```

### Writing a new test

1. Choose the appropriate category (unit / integration / filesystem)
2. If mocking `IScanRepository`, use the standard Moq setup pattern from `FileScannerTests`
3. Use `FluentAssertions` for readable assertions:
   ```csharp
   session.Status.Should().Be(ScanStatus.Completed);
   files.Should().HaveCountGreaterThanOrEqualTo(5);
   suggestions.Should().BeEmpty("system paths must never be suggested");
   ```
4. Clean up any temp files in a `try/finally` or `IAsyncDisposable.DisposeAsync()`

---

## 12. Adding a cleanup rule

Example: a rule that finds log files older than 90 days.

### Step 1: Create the rule class

```csharp
// src/StorageMaster.Core/Cleanup/Rules/OldLogFilesRule.cs

using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

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
            Description    = $"Log files not modified in 90+ days. Estimated: {FormatBytes(logs.Sum(f => f.SizeBytes))}.",
            Category       = Category,
            Risk           = CleanupRisk.Low,
            EstimatedBytes = logs.Sum(f => f.SizeBytes),
            TargetPaths    = logs.Select(f => f.FullPath).ToList(),
        };
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (1L << 20):F1} MB",
        >= 1L << 10 => $"{b / (1L << 10):F1} KB",
        _           => $"{b} B",
    };
}
```

### Step 2: Register in DI

```csharp
// In App.xaml.cs::BuildServices():
services.AddSingleton<ICleanupRule, OldLogFilesRule>();
```

The `CleanupEngine` receives `IEnumerable<ICleanupRule>` — it discovers the new rule automatically.

### Step 3: Write a test

```csharp
[Fact]
public async Task OldLogFilesRule_YieldsLogFiles()
{
    var repoMock = new Mock<IScanRepository>();
    repoMock.Setup(r => r.GetLargestFilesAsync(1, 50_000, default))
            .ReturnsAsync([MakeLogFile(".log", daysOld: 120)]);

    var rule = new OldLogFilesRule(repoMock.Object);
    var suggestions = new List<CleanupSuggestion>();
    await foreach (var s in rule.AnalyzeAsync(1, new AppSettings()))
        suggestions.Add(s);

    suggestions.Should().ContainSingle();
    suggestions[0].Category.Should().Be(CleanupCategory.LogFiles);
}
```

---

## 13. Adding a scan backend

To add a faster scan backend (e.g., NTFS MFT reader):

### Step 1: Create the new scanner

```csharp
// src/StorageMaster.Platform.Windows/MftFileScanner.cs

public sealed class MftFileScanner : IFileScanner
{
    private readonly IScanRepository _repo;
    private readonly ILogger<MftFileScanner> _logger;

    // ... implement ScanAsync using direct MFT enumeration via NtQueryDirectory or
    //     the FSCTL_ENUM_USN_DATA / ReadDirectoryChangesW API
}
```

The new scanner writes the **same** `FileEntry` and `FolderEntry` records via `IScanRepository`. The storage and UI layers see no difference.

### Step 2: Replace the DI registration

```csharp
// Replace:
services.AddSingleton<IFileScanner, FileScanner>();
// With:
services.AddSingleton<IFileScanner, MftFileScanner>();
```

Or register conditionally:

```csharp
if (MftScanner.IsAvailable())
    services.AddSingleton<IFileScanner, MftFileScanner>();
else
    services.AddSingleton<IFileScanner, FileScanner>();
```

---

## 14. Troubleshooting

### "SHEmptyRecycleBin failed" / Recycle Bin not emptying

- Run the application as Administrator (right-click → Run as Administrator)
- Or confirm the user account has permissions to manage the Recycle Bin
- Check Windows Event Viewer → Application log for Shell32 errors

### Scan is very slow

- If scanning an HDD, `MaxParallelism = 1` is often faster (avoids seek thrashing)
- Set `MaxParallelism = 1` in ScanOptions (or add a setting for this in Settings page)
- Exclude known large system folders: `C:\Windows\WinSxS` is already excluded by default

### "Access denied" paths not being scanned

Expected — the scanner increments `AccessDeniedCount` and moves on. You can see the count in the session's `AccessDeniedCount` property. To scan protected paths, run as Administrator.

### Database file locked

This should not happen in normal use (single connection + WAL mode). If it occurs:
1. Ensure only one instance of StorageMaster is running
2. Delete the WAL file (`storagemaster.db-wal`) if the app crashed
3. The DB file is at `%LOCALAPPDATA%\StorageMaster\storagemaster.db`

### WinUI 3 app fails to launch (Windows App SDK missing)

The app is built with `WindowsAppSDKSelfContained=true`, so the runtime is bundled. If it still fails:
- Ensure you are on Windows 10 1903 (build 18362) or later
- Rebuild with `dotnet build -r win-x64` to ensure the correct runtime identifier

### Test failures after schema change

Integration tests create their own temp DB files. If schema changes are made:
1. Delete any cached SQLite files in `%TEMP%` matching `test_*.db`
2. The `StorageDbContext` migration logic will apply the new schema on next test run

### "Category breakdown" shows no data

This query requires at least one completed scan session. Verify:
```sql
SELECT COUNT(*) FROM FileEntries WHERE SessionId = <id>;
```
If zero, the scan may have failed or been cancelled before any data was written.
