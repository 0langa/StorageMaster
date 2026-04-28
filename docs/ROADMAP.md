# StorageMaster — Development Roadmap

> **Baseline:** v1.3.0 (2026-04-28) — parallel scanning, Rust Turbo Scanner, 10 cleanup rules, Smart Cleaner, admin elevation, CI/CD pipeline, Inno Setup installer.
> **Target:** v1.5.0 — a feature-complete, polished, accessible, and robust Windows utility.

---

## How to read this document

- **Phase** = a coherent release milestone delivering real user value
- **Milestone** = a specific deliverable inside a phase
- **Complexity** = estimated engineering effort: S (hours–1 day) | M (2–5 days) | L (1–2 weeks) | XL (2–4 weeks)
- **Impact** = `Low | Medium | High | Critical`
- **Dependencies** = other milestones that must be complete first

---

## Phase overview

```
v1.3.0  Current release                        ← SHIPPED
  │
v1.3.x  Bug fixes & quick wins                 ← immediate
  │
v1.4.0  Results & Interaction depth            ← next major milestone
  │
v1.4.x  Hardening, compat, accessibility      ← stabilization
  │
v1.5.0  Visualization, scheduling, CLI        ← feature complete
```

---

## Phase 0 — Quick Wins & Bug Fixes (v1.3.x patches)

**Goal:** Immediately actionable improvements that take < 1 day each. Ship as rapid patch releases.

---

### P0-1 — Log Smart Cleaner deletions to CleanupLog

**Complexity:** S | **Impact:** Medium

`SmartCleanerService.CleanAsync()` currently calls `IFileDeleter.DeleteManyAsync()` directly without logging to `CleanupLog`. This means Smart Cleaner operations leave no audit trail.

**Fix:** Inject `ICleanupLogRepository` into `SmartCleanerService`. After cleanup completes, write a synthetic `CleanupLogEntry` per group. Use a synthetic `SuggestionId = Guid.NewGuid()` and `RuleId = "smart-cleaner.<category>"`.

**Files:** `SmartCleanerService.cs`, `ISmartCleanerService.cs` (constructor update), `App.xaml.cs` (DI update)

---

### P0-2 — Wire the Errors tab in ResultsPage

**Complexity:** S | **Impact:** Medium

The `ResultsViewModel` already loads `ScanErrors` from `IScanErrorRepository` and populates `ScanErrors` + `ErrorCount`. The XAML `ResultsPage` needs the fourth PivotItem for the Errors tab.

**XAML to add after the "File Types" PivotItem:**
```xml
<PivotItem>
    <PivotItem.Header>
        <StackPanel Orientation="Horizontal" Spacing="6">
            <TextBlock Text="Errors"/>
            <Border Background="{ThemeResource SystemFillColorCriticalBackgroundBrush}"
                    CornerRadius="8" Padding="6,1"
                    Visibility="{x:Bind ViewModel.HasErrors, Mode=OneWay, Converter={StaticResource BoolVis}}">
                <TextBlock Text="{x:Bind ViewModel.ErrorCount, Mode=OneWay}"
                           Style="{StaticResource CaptionTextBlockStyle}"/>
            </Border>
        </StackPanel>
    </PivotItem.Header>
    <ListView ItemsSource="{x:Bind ViewModel.ScanErrors}" SelectionMode="None" Margin="0,8,0,0">
        <ListView.ItemTemplate>
            <DataTemplate x:DataType="models:ScanError">
                <Grid ColumnDefinitions="*,160,240" Padding="0,4" ColumnSpacing="12">
                    <TextBlock Grid.Column="0" Text="{x:Bind Path}" TextTrimming="CharacterEllipsis"
                               Style="{StaticResource CaptionTextBlockStyle}"/>
                    <TextBlock Grid.Column="1" Text="{x:Bind ErrorType}" HorizontalAlignment="Center"
                               Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8"/>
                    <TextBlock Grid.Column="2" Text="{x:Bind Message}" TextTrimming="CharacterEllipsis"
                               Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.7"/>
                </Grid>
            </DataTemplate>
        </ListView.ItemTemplate>
    </ListView>
</PivotItem>
```

**Files:** `ResultsPage.xaml`

---

### P0-3 — Excluded paths editor in Settings

**Complexity:** M | **Impact:** Medium

`AppSettings.ExcludedPaths` is persisted and passed to the scanner, but the Settings page has no UI to edit it. Add a simple editor:
- `ListView` showing current excluded paths
- "Add Path" button → `FolderPicker` (with HWND init)
- "Remove" button per item
- Pre-populated with defaults on first open

**Files:** `SettingsPage.xaml`, `SettingsViewModel.cs`

---

### P0-4 — Session deletion from Dashboard/Results

**Complexity:** S | **Impact:** Medium

Allow users to delete old scan sessions from the Dashboard (or a "Manage Sessions" section in Settings). `IScanRepository.DeleteSessionAsync` already exists; it just needs a UI surface.

- Add a "Delete this session" button in the Results page header
- Or a "Manage scan history" section in Settings showing all sessions with a delete button

**Files:** `DashboardViewModel.cs`, `DashboardPage.xaml`, or `SettingsPage.xaml`

---

### P0-5 — Fix Turbo Scanner's FolderEntry DirectSizeBytes

**Complexity:** M | **Impact:** Medium

When `turbo-scanner.exe` emits a directory entry (`is_dir=true`), `TurboFileScanner.cs` sets `DirectSizeBytes = 0`. The `FolderSizeAggregator` then computes `TotalSizeBytes` correctly from file entries, but `DirectSizeBytes` stays 0 for all folders in Turbo-scanned results.

**Fix:** During JSONL parsing, accumulate `Size` from file records into a `Dictionary<string, long>` keyed by parent folder path, then patch `FolderEntry.DirectSizeBytes` before inserting. This mirrors what the managed `FileScanner` does in `ProcessDirectory`.

**Files:** `TurboFileScanner.cs`

---

## Phase 1 — Results Depth & Interaction (v1.4.0)

**Goal:** Make the Results and Cleanup pages genuinely useful for power users. Users should be able to act directly on what they find, not just view it.

---

### M-1.1 — Open in Explorer from Results

**Complexity:** S | **Impact:** High

Add an "Open folder" icon button to each row in the Largest Files and Largest Folders ListViews.

**Implementation:**
```csharp
// In ResultsViewModel:
[RelayCommand]
private static void OpenFileInExplorer(FileEntry file)
{
    var dir = Path.GetDirectoryName(file.FullPath) ?? file.FullPath;
    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\"") { UseShellExecute = true });
}

[RelayCommand]
private static void OpenFolderInExplorer(FolderEntry folder)
{
    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.FullPath}\"") { UseShellExecute = true });
}
```

**XAML:** Add a 4th column to the DataTemplate grid with a `TransparentButton` bound to `ViewModel.OpenFileInExplorerCommand` / `CommandParameter="{Binding}"`.

**Files:** `ResultsViewModel.cs`, `ResultsPage.xaml`

---

### M-1.2 — Copy path to clipboard from Results

**Complexity:** S | **Impact:** Medium

Right-click context menu on file/folder rows:
- "Copy full path"
- "Open in Explorer" (from M-1.1)

**Implementation:** `MenuFlyout` attached to each `ListViewItem` via `RightTapped` event or `ContextFlyout` property. Use `DataPackage` to write to clipboard.

**Files:** `ResultsPage.xaml`

---

### M-1.3 — Sortable columns in Results

**Complexity:** M | **Impact:** High

Allow clicking column headers to sort the Largest Files and Largest Folders lists without re-querying the database.

**Implementation:** `AdvancedCollectionView` from `CommunityToolkit.WinUI.UI` wraps the `ObservableCollection`. Column headers get `Tapped` handlers that call `acv.SortDescriptions.ReplaceWith(...)`.

**NuGet:** `CommunityToolkit.WinUI.UI 8.x`

**Files:** `ResultsViewModel.cs`, `ResultsPage.xaml`

---

### M-1.4 — Results page: path filter improvements

**Complexity:** S | **Impact:** Medium

Current filter: path-contains, case-insensitive. Improvements:
- Filter debounce (300ms) so it doesn't fire on every keystroke
- Clear filter button (×) inside the TextBox when filter text is non-empty
- Show filtered count: "Showing 47 of 500 files"
- Option to filter by extension (e.g., `.mp4` only)

**Files:** `ResultsViewModel.cs`, `ResultsPage.xaml`

---

### M-1.5 — Interactive folder tree view

**Complexity:** L | **Impact:** High
**Depends on:** P0-5 (DirectSizeBytes fix), accurate folder totals

Replace the flat "Largest Folders" list with an expandable tree:

```
C:\                                [████████████████░░░░] 347 GB
  └─ Users\                         [████████░░░░░░░░░░░░] 120 GB
       ├─ Alice\                     [███████░░░░░░░░░░░░░]  98 GB
       │    ├─ Videos\               [████░░░░░░░░░░░░░░░░]  42 GB
       │    ├─ Downloads\            [██░░░░░░░░░░░░░░░░░░]  28 GB
       │    └─ Documents\            [█░░░░░░░░░░░░░░░░░░░]  18 GB
       └─ Public\                    [█░░░░░░░░░░░░░░░░░░░]  11 GB
```

**Implementation:** `TreeView` (WinUI 3) with `IsVirtualized=true`. Root nodes load their children lazily from `IScanRepository.GetLargestFoldersAsync(sessionId, topN: 50)` filtered by parent path.

**Files:** `ResultsPage.xaml`, `ResultsViewModel.cs`, new `FolderTreeItem.cs` model

---

### M-1.6 — Cleanup page: analyse without session (direct path)

**Complexity:** M | **Impact:** Medium

Currently the Cleanup page requires selecting a previous scan session. Add an option to run cleanup suggestions against a freshly-scanned path directly (one-shot scan → analyse → clean) without navigating to the Smart Cleaner.

**Files:** `CleanupViewModel.cs`, `CleanupPage.xaml`

---

### M-1.7 — Delete directly from Results

**Complexity:** M | **Impact:** High
**Depends on:** M-1.1 (Open in Explorer, same button infrastructure)

Add a delete icon button next to "Open in Explorer" in the file list. Clicking it:
1. Shows a confirmation `ContentDialog` listing the file
2. On confirm: `IFileDeleter.DeleteAsync(path, RecycleBin)` 
3. Removes the row from the `ObservableCollection` (no DB update needed; session data is historical)
4. Shows a brief "Sent to Recycle Bin" toast

**Do not** re-query the database after deletion — the row is simply removed from the in-memory collection.

**Files:** `ResultsViewModel.cs`, `ResultsPage.xaml`

---

## Phase 2 — Hardening, Compatibility & Accessibility (v1.4.x)

**Goal:** Make StorageMaster stable on all supported Windows builds and accessible to all users. No new features — only quality and robustness.

---

### M-2.1 — Windows 10 compatibility matrix test

**Complexity:** M | **Impact:** Critical

Test on the minimum supported Windows 10 build (1809, build 17763) and every major feature update through Windows 11 24H2. Document any regressions.

**Focus areas:**
- `DisplayArea.GetFromWindowId()` — availability varies by Windows App SDK version
- `SHGetKnownFolderPath` flags — behaviour differences on older builds
- WinUI 3 XAML feature availability (e.g., `InfoBar`, `ProgressRing` styles)
- Turbo Scanner: MSVC CRT requirements for `turbo-scanner.exe` on older Windows

**Deliverable:** Compatibility matrix in this file + any necessary runtime version guards.

---

### M-2.2 — ARM64 support

**Complexity:** M | **Impact:** Medium

The C# app already lists `arm64` in `RuntimeIdentifiers`. The Rust binary needs an ARM64 build.

**CI changes (`release.yml`):**
```yaml
- name: Setup Rust (stable, ARM64)
  uses: dtolnay/rust-toolchain@stable
  with:
    targets: aarch64-pc-windows-msvc

- name: Build Turbo Scanner (ARM64)
  run: cargo build --release --manifest-path turbo-scanner/Cargo.toml --target aarch64-pc-windows-msvc
```

**Installer:** Update `StorageMaster.iss` to include both `x86_64` and `aarch64` binaries, or create separate installer targets.

**Files:** `.github/workflows/release.yml`, `installer/StorageMaster.iss`

---

### M-2.3 — Keyboard navigation & focus management

**Complexity:** M | **Impact:** High (accessibility)

Ensure the entire application is usable without a mouse:

- All buttons, toggles, and list items are reachable via Tab / Shift+Tab
- All `ListView` rows support arrow key navigation
- Enter key activates the focused button/row
- Escape key closes dialogs
- Focus is managed on page navigation (focus first interactive element)
- `FocusManager.TryMoveFocus()` or `AutomationProperties.Name` where needed

**Files:** All XAML page files

---

### M-2.4 — Screen reader support (MSAA / UIA)

**Complexity:** M | **Impact:** High (accessibility)

Add `AutomationProperties` to all interactive elements:

```xml
<Button AutomationProperties.Name="Scan drive C"
        AutomationProperties.HelpText="Start scanning the selected drive for files" />

<ProgressRing AutomationProperties.Name="Scan in progress"
              AutomationProperties.LiveSetting="Assertive"
              IsActive="{x:Bind ViewModel.IsScanning}" />
```

Key areas:
- Scan progress should announce completion via `LiveSetting="Assertive"`
- Drive list items should announce name, size, and usage percentage
- Cleanup suggestion list items should announce category, risk, and size
- InfoBar messages should be announced on appearance

**Tools:** Verify with Windows Narrator (`Win+Ctrl+Enter`) and Accessibility Insights for Windows.

---

### M-2.5 — High contrast mode support

**Complexity:** S | **Impact:** Medium (accessibility)

WinUI 3 automatically respects the system high-contrast theme for most controls. Verify and fix:
- All custom colors use `ThemeResource` tokens, not hardcoded hex values
- Icon glyphs (Segoe MDL2) remain visible in high contrast
- Progress bars and InfoBars remain distinguishable
- Any custom `Canvas` drawing uses system colors

Test with Windows "High Contrast Black" and "High Contrast White" themes.

---

### M-2.6 — Text size scaling (large font accessibility)

**Complexity:** S | **Impact:** Medium (accessibility)

Test the application at 125%, 150%, and 200% text scaling (`Settings → Accessibility → Text size`). Ensure:
- No text is clipped or overlapped
- Cards and panels expand appropriately
- Minimum touch target sizes maintained (44×44 px per WinUI guidelines)

---

### M-2.7 — Expand test coverage to 60+ tests

**Complexity:** M | **Impact:** High (engineering quality)

Current test count: varies. Target: 60 tests with these additions:

| New test class | Tests to add |
|---------------|-------------|
| `BrowserCacheRuleTests` | Yields suggestions for known browser dirs |
| `WindowsUpdateCacheRuleTests` | Correct risk and path handling |
| `WindowsErrorReportingRuleTests` | System path flag is set |
| `DeliveryOptimizationRuleTests` | Correct category |
| `UninstalledProgramLeftoversRuleTests` | Safelist prevents false positives; thresholds respected |
| `CleanupEngineTests` | Orchestration, partial failure, dry-run |
| `SettingsRepositoryTests` | Round-trip for all new settings fields |
| `FolderSizeAggregatorTests` | Correctness for flat, deep, and wide trees |
| `TurboFileScannerTests` | Fallback when binary absent; JSONL parsing |
| `SmartCleanerServiceTests` | AnalyzeAsync returns groups; CleanAsync calls deleter |

---

### M-2.8 — Structured logging (Serilog)

**Complexity:** S | **Impact:** Medium (diagnostics)

Replace `Microsoft.Extensions.Logging.Debug` with `Serilog`:
- Rolling file sink: `%LOCALAPPDATA%\StorageMaster\logs\sm-{Date}.log`
- Max 5 files, 5 MB each
- Log level: `Information` in release, `Debug` in debug builds
- Structured properties: `SessionId`, `RuleId`, `Path`, `ElapsedMs`

**NuGet:** `Serilog.Extensions.Hosting`, `Serilog.Sinks.File`

**Files:** `App.xaml.cs` (logging configuration)

---

### M-2.9 — Scan cancellation robustness

**Complexity:** S | **Impact:** Medium

Currently, if the scan is cancelled after `FolderSizeAggregator` starts, the session is marked as complete with partial folder totals. Fix:
- Wrap the aggregation in a `try/catch (OperationCanceledException)`
- On cancellation: mark session `Cancelled`; do not run aggregation
- On completion: session marked `Completed` only after aggregation succeeds

**Files:** `FileScanner.cs`, `TurboFileScanner.cs`

---

## Phase 3 — Visualization, Scheduling & CLI (v1.5.0)

**Goal:** Match and exceed the feature set of competing tools (WizTree, SpaceSniffer). Add proactive disk health monitoring and power-user CLI support.

---

### M-3.1 — Treemap visualization

**Complexity:** XL | **Impact:** Critical

The treemap is the defining visual of a disk space analyzer. Each rectangle's area is proportional to folder/file size.

**Recommended approach:** WebView2 + D3.js treemap
- Fastest to ship; excellent interactive behavior out of the box
- `Microsoft.Web.WebView2` NuGet package
- D3.js loaded from bundled HTML (no external CDN)

**Interactions:**
- Hover → tooltip (path, size, % of parent)
- Click → drill into folder (breadcrumb bar updates)
- Breadcrumb click → navigate back up
- Right-click → context menu: "Open in Explorer", "Send to Cleanup"

**Data format:** `ResultsViewModel` provides a hierarchical JSON object from `FolderEntries` that the WebView renders:
```json
{
  "name": "C:\\",
  "size": 372000000000,
  "children": [
    { "name": "Users", "size": 120000000000, "children": [...] }
  ]
}
```

**Files:** New `TreemapPage.xaml` + `TreemapViewModel.cs`; new `Assets/treemap.html` + `Assets/d3.min.js`

---

### M-3.2 — File type sunburst / donut chart

**Complexity:** L | **Impact:** High
**Depends on:** M-3.1 (WebView2 infrastructure)

Replace the flat "File Types" tab with an interactive donut/sunburst chart:
- Inner ring: major file categories (Video, Image, Document, etc.)
- Outer ring: top extensions within each category
- Click on a segment → filter the file list below
- Legend below the chart

Reuse the same WebView2 + D3.js infrastructure from M-3.1.

**Files:** Update `ResultsPage.xaml` (File Types tab); update `ResultsViewModel.cs`

---

### M-3.3 — System tray icon

**Complexity:** M | **Impact:** High

A tray icon that:
- Shows disk space usage (color-coded: green < 70%, orange < 90%, red > 90%)
- Right-click menu: Open StorageMaster, Quick Smart Clean, Exit
- Double-click: bring main window to foreground (or restore from minimized)
- Balloon notification when the most-used drive exceeds configurable threshold

**Implementation:** `H.NotifyIcon.WinUI` NuGet (WinUI 3 compatible). Monitor disk space with a `PeriodicTimer` on a background thread.

**NuGet:** `H.NotifyIcon.WinUI`

**Files:** `MainWindow.xaml.cs`, new `TrayIconService.cs`

---

### M-3.4 — Low-disk-space notifications

**Complexity:** S | **Impact:** High
**Depends on:** M-3.3 (tray icon or standalone)

Use `Microsoft.Windows.AppNotifications` (Windows App SDK) to show toast notifications when any drive drops below configurable thresholds (default: 15% and 5% free).

```csharp
var notification = new AppNotificationBuilder()
    .AddText("Low disk space on C:")
    .AddText("Only 12.3 GB remaining (4.8%)")
    .AddButton(new AppNotificationButton("Run Smart Cleaner")
        .AddArgument("action", "smart-clean"))
    .BuildNotification();

AppNotificationManager.Default.Show(notification);
```

**Files:** New `DiskMonitorService.cs`; `App.xaml.cs` (AppNotificationManager init + activation)

---

### M-3.5 — Windows Task Scheduler integration (scheduled scans)

**Complexity:** M | **Impact:** High

Allow users to schedule automatic scans without a background service:

**UI (SettingsPage, new "Scheduled Scans" section):**
- Enable/disable switch
- Drive selector (default: all fixed drives)
- Frequency: Daily / Weekly
- Time picker

**Implementation:** Use `Microsoft.Win32.TaskScheduler` NuGet (or raw `COM` via `System.Runtime.InteropServices`) to create a Task Scheduler entry that launches:
```
StorageMaster.UI.exe --headless --scan C:\ --no-window
```

**Headless mode:** When `--headless` arg is present, the app runs a scan without showing the window, writes results to the database, then exits. `ScanViewModel` and the DI container still initialize, but `MainWindow` is not shown.

**NuGet:** `TaskScheduler` by dahall

**Files:** New `ScheduledScanService.cs`; `SettingsPage.xaml`; `SettingsViewModel.cs`; `App.xaml.cs` (headless mode detection)

---

### M-3.6 — CLI mode (scan + report)

**Complexity:** M | **Impact:** High (power users, scripting)

```powershell
# Scan a drive and get a summary
StorageMaster.UI.exe --cli scan --path C:\ --output report.json

# List top 20 largest files from last session
StorageMaster.UI.exe --cli top-files --count 20

# Run cleanup rules (dry run)
StorageMaster.UI.exe --cli cleanup --rules temp,browser-cache --dry-run

# Run cleanup (requires --confirm to prevent accidental deletion)
StorageMaster.UI.exe --cli cleanup --rules temp,browser-cache --confirm
```

**Implementation:**
- Detect `--cli` argument in `App.xaml.cs`; if present, skip `MainWindow` and run a headless `CliRunner`
- `CliRunner` resolves services from DI, runs the requested operation, writes to stdout, exits
- Use `System.CommandLine` for argument parsing (Microsoft, stable API)

**Output formats:** Plain text (default), JSON (`--json`), CSV (`--csv`)

**Safety:** `--cleanup` without `--confirm` prints the plan and exits with code 1. `--confirm` executes.

**NuGet:** `System.CommandLine`

**Files:** New `Cli/CliRunner.cs`, `Cli/CliCommands.cs`; `App.xaml.cs`

---

### M-3.7 — Duplicate file detection

**Complexity:** L | **Impact:** High

Find files with identical content using a two-phase algorithm:

**Phase 1 — Size grouping (fast, in DB):**
```sql
SELECT SizeBytes, COUNT(*) as cnt FROM FileEntries
WHERE SessionId = $id AND SizeBytes > 1048576  -- ignore files < 1 MB
GROUP BY SizeBytes HAVING cnt > 1
ORDER BY SizeBytes * cnt DESC;
```

**Phase 2 — SHA-256 hash verification (on demand, for matched size groups):**
```csharp
using var sha = SHA256.Create();
var hash = sha.ComputeHash(File.OpenRead(path));
```

Group confirmed duplicates; yield one `CleanupSuggestion` per group.

**UI:** A new "Duplicates" PivotItem in `ResultsPage` showing groups of duplicates. The user picks which copy to keep; the others are added to cleanup.

**DB schema addition (migration V2):**
```sql
ALTER TABLE FileEntries ADD COLUMN Hash TEXT;
CREATE INDEX IX_FileEntries_Hash ON FileEntries(Hash) WHERE Hash IS NOT NULL;
```

**Files:** New `DuplicateFilesCleanupRule.cs`; `ResultsPage.xaml`; `ResultsViewModel.cs`; `DatabaseSchema.cs`

---

### M-3.8 — Export to CSV / JSON

**Complexity:** M | **Impact:** High

Export scan results from the Results page:
- **File list → CSV**: FileName, FullPath, SizeBytes, SizeFriendly, Category, ModifiedUtc
- **Folder list → CSV**: FolderName, FullPath, TotalSizeBytes, SizeFriendly, FileCount
- **Cleanup history → CSV**: ExecutedUtc, Title, BytesFreed, Status, WasDryRun

Add "Export..." button in the Results page header (or per-tab). Use `FileSavePicker` to let the user choose the destination.

**Files:** `ResultsPage.xaml`, `ResultsViewModel.cs`, new `ExportService.cs`

---

### M-3.9 — First-run experience

**Complexity:** M | **Impact:** High

On first launch (no completed scan sessions in DB):
1. Full-width welcome card with StorageMaster logo and tagline
2. Drive selector: pre-selects the largest fixed drive
3. Single "Analyse Now" call-to-action button
4. Immediately starts a scan on click

Dismiss condition: first completed scan session exists.

**Files:** `DashboardPage.xaml`, `DashboardViewModel.cs`

---

### M-3.10 — Dark / light theme selector

**Complexity:** S | **Impact:** High

WinUI 3 supports `Application.RequestedTheme`. All colors already use `ThemeResource` tokens. Add:
- Theme selector in `SettingsPage` (System / Light / Dark)
- Persisted in `AppSettings.AppTheme`
- Applied on settings save via `MainWindow.SetTheme()`

**Files:** `AppSettings.cs`, `SettingsViewModel.cs`, `SettingsPage.xaml`, `MainWindow.xaml.cs`

---

### M-3.11 — In-app what's-new panel

**Complexity:** S | **Impact:** Medium

On first launch after an update (version in AppSettings < current assembly version):
- Show a non-modal slide-in panel (or InfoBar) listing new features in this version
- "What's new in v1.5.0" → bullet list of highlights
- Dismiss button; panel never shows again for this version

**Files:** New `WhatsNewPanel.xaml`; `MainWindow.xaml.cs`

---

### M-3.12 — Update check

**Complexity:** M | **Impact:** High

On startup (max once per day):
1. `HttpClient.GetAsync("https://api.github.com/repos/0langa/StorageMaster/releases/latest")`
2. Parse `tag_name` (e.g. `v1.5.0`) and compare to `Assembly.GetExecutingAssembly().GetName().Version`
3. If newer: show unobtrusive InfoBar at the bottom of the Dashboard — "Update available: v1.5.0"
4. Button: "Download" → opens GitHub release page in browser

No auto-download; no background service. Simple, one-click, no tracking.

**Files:** New `UpdateCheckService.cs`; `DashboardPage.xaml`, `DashboardViewModel.cs`

---

## Compatibility matrix

| Windows version | Build | Status (v1.3) | Target (v1.5) |
|----------------|-------|--------------|--------------|
| Windows 10 1809 | 17763 | Not tested | Verified |
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

These must be maintained across all phases:

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

| Release | Phase | Estimated effort | Key deliverable |
|---------|-------|-----------------|----------------|
| v1.3.1 | P0 patches | 1–2 weeks | Audit log for Smart Cleaner, Errors tab, excluded paths editor |
| v1.4.0 | Phase 1 | 3–5 weeks | Open in Explorer, sortable columns, folder tree, delete from Results |
| v1.4.x | Phase 2 | 3–4 weeks | ARM64, accessibility, 60 tests, Serilog |
| v1.5.0 | Phase 3 | 6–10 weeks | Treemap, tray, scheduled scans, CLI, duplicate detection, exports |

**Total to v1.5.0:** approximately 3–5 months of focused engineering.

---

## Immediate next steps (this sprint)

1. **P0-2: Wire Errors tab** in `ResultsPage.xaml` — 30 minutes
2. **P0-1: Log Smart Cleaner to CleanupLog** — 1 hour
3. **M-1.1: Open in Explorer** — 2 hours (highest user impact per effort)
4. **P0-5: Fix FolderEntry DirectSizeBytes in TurboScanner** — 2 hours
5. **P0-3: Excluded paths editor** — half day
6. **M-2.7: Expand tests to 60+** — 1–2 days

These six items transform v1.3.0 from a technically complete release into a polished, fully functional one.
